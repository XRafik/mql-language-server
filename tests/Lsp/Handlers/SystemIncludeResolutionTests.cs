using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// T3 regression tests: system-include resolution for the MQL4/MQL5 Standard
/// Library. Angle-bracket includes (<c>#include &lt;Trade\Trade.mqh&gt;</c>)
/// resolve against server-configured system include roots (the terminal's
/// MQL5/Include / MQL4/Include directories, delivered via the
/// <c>systemIncludeRoots</c> initialization option). Resolved headers are
/// lazily parsed and indexed into <see cref="GlobalSymbolIndex"/>, so the
/// existing Tier 1/1b cross-file suppression, definition and hover work
/// unchanged.
///
/// <para>Fail-open contract: with no roots configured or an unresolvable
/// header, today's behavior is kept exactly (header never indexed, unresolved
/// diagnostics keep firing).</para>
///
/// Runs inside the shared "GlobalSymbolIndex Tests" collection (the fixture
/// uses <see cref="GlobalSymbolIndex.Instance"/>) and resets it per test.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class SystemIncludeResolutionTests : IDisposable
{
    // 0-based position of "CTrade" inside the MQL5 document ("   CTrade trade;").
    private const int CTradeLine = 3;
    private const int CTradeColumn = 5;

    private readonly string _baseDir;
    private readonly string _rootDir;   // fake terminal MQL5/Include root
    private readonly string _wsDir;     // workspace folder with consumer docs
    private readonly string _tradeHeaderPath;
    private readonly string _eaMql5Path;
    private readonly string _eaMql4Path;
    private readonly string _arrayObjHeaderPath;

    public SystemIncludeResolutionTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "SystemIncludeFixture_" + Guid.NewGuid().ToString("N"));
        _rootDir = Path.Combine(_baseDir, "Include");
        _wsDir = Path.Combine(_baseDir, "ws");
        Directory.CreateDirectory(Path.Combine(_rootDir, "Trade"));
        Directory.CreateDirectory(Path.Combine(_rootDir, "Arrays"));
        Directory.CreateDirectory(_wsDir);

        _tradeHeaderPath = Path.Combine(_rootDir, "Trade", "Trade.mqh");
        File.WriteAllText(_tradeHeaderPath,
            "// Trade.mqh (test stdlib header)\n" +
            "class CTrade\n" +
            "{\n" +
            "public:\n" +
            "   bool Buy(double volume);\n" +
            "   bool Sell(double volume);\n" +
            "};\n");

        _arrayObjHeaderPath = Path.Combine(_rootDir, "Arrays", "ArrayObj.mqh");
        File.WriteAllText(_arrayObjHeaderPath,
            "// ArrayObj.mqh (test stdlib header)\n" +
            "class CArrayObj\n" +
            "{\n" +
            "public:\n" +
            "   int Total();\n" +
            "};\n" +
            "int ArrayResizeHelper(int size);\n");

        _eaMql5Path = Path.Combine(_wsDir, "ea.mq5");
        File.WriteAllText(_eaMql5Path,
            "#include <Trade\\Trade.mqh>\n" +
            "void OnTick()\n" +
            "{\n" +
            "   CTrade trade;\n" +
            "   trade.Buy(0.1);\n" +
            "   MissingStdlibSymbol();\n" +
            "}\n");

        _eaMql4Path = Path.Combine(_wsDir, "ea.mq4");
        File.WriteAllText(_eaMql4Path,
            "#include <Arrays\\ArrayObj.mqh>\n" +
            "void OnStart()\n" +
            "{\n" +
            "   CArrayObj list;\n" +
            "   ArrayResizeHelper(5);\n" +
            "   MissingMql4Symbol();\n" +
            "}\n");

        // Configure the fake terminal roots + the resolver provider, exactly
        // as Program.OnInitialize does with the systemIncludeRoots option.
        SystemIncludeRoots.Set(new[] { _rootDir });
        IncludePathResolver.SetSystemIncludeRootsProvider(() => SystemIncludeRoots.Current);
    }

    public void Dispose()
    {
        IncludePathResolver.SetSystemIncludeRootsProvider(null);
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch
        {
            // best effort on temp files
        }
    }

    private DidOpenTextDocumentHandler CreateOpenHandler(OpenDocumentStore store) => new(
        Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
        store,
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
        new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

    private static async Task DidOpenAsync(DidOpenTextDocumentHandler handler, string path, string languageId)
    {
        await handler.Handle(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = DocumentUri.FromFileSystemPath(path),
                LanguageId = languageId,
                Version = 1,
                Text = File.ReadAllText(path)
            }
        }, CancellationToken.None);
    }

    private static async Task<List<Diagnostic>> GetPullDiagnosticsAsync(string filePath)
    {
        var logger = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(logger, serviceProvider, documentStore);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(uri) };
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(
            await handler.Handle(request, CancellationToken.None));

        return report.Items.ToList();
    }

    private static bool IsUnresolvedFor(Diagnostic diagnostic, string symbol)
    {
        // DiagnosticCode compares against strings via implicit conversion
        // (same mechanism as DiagnosticHandlerCrossFileSuppressionTests).
        if (diagnostic.Code != "1070" && diagnostic.Code != "5070")
        {
            return false;
        }

        return string.Equals(diagnostic.Data?["symbol"]?.ToString(), symbol, StringComparison.Ordinal);
    }

    // --- Lazy header indexing ---

    [Fact]
    public async Task DidOpen_Mql5_SystemHeader_LazyIndexesIntoGlobalSymbolIndexAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var openHandler = CreateOpenHandler(store);

        await DidOpenAsync(openHandler, _eaMql5Path, "mql5");

        var headerSymbols = GlobalSymbolIndex.Instance.GetFileSymbols(_tradeHeaderPath, MqlLanguage.Mql5);
        Assert.NotNull(headerSymbols);
        Assert.Contains(headerSymbols!, s => s.Name == "CTrade");

        // The dependency edge root → header feeds the #89/#90 reachability guards.
        var deps = GlobalSymbolIndex.Instance.GetDependencies(_eaMql5Path);
        Assert.Contains(deps, d => string.Equals(d, _tradeHeaderPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DidOpen_Mql4_SystemHeader_LazyIndexesIntoGlobalSymbolIndexAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var openHandler = CreateOpenHandler(store);

        await DidOpenAsync(openHandler, _eaMql4Path, "mql4");

        var headerSymbols = GlobalSymbolIndex.Instance.GetFileSymbols(_arrayObjHeaderPath, MqlLanguage.Mql4);
        Assert.NotNull(headerSymbols);
        Assert.Contains(headerSymbols!, s => s.Name == "CArrayObj");
    }

    [Fact]
    public async Task DidOpen_SystemHeader_AlreadyIndexed_IsNotReparsedAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var openHandler = CreateOpenHandler(store);

        await DidOpenAsync(openHandler, _eaMql5Path, "mql5");

        // Mutate the on-disk header after the first index: the dedup contract
        // keeps the already-indexed symbols (no re-parse).
        File.WriteAllText(_tradeHeaderPath,
            "// Trade.mqh (mutated after first index)\n" +
            "class CNewTrade\n" +
            "{\n" +
            "public:\n" +
            "   void NewMethod();\n" +
            "};\n");

        var store2 = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store2), _eaMql5Path, "mql5");

        var headerSymbols = GlobalSymbolIndex.Instance.GetFileSymbols(_tradeHeaderPath, MqlLanguage.Mql5);
        Assert.NotNull(headerSymbols);
        Assert.Contains(headerSymbols!, s => s.Name == "CTrade");
        Assert.DoesNotContain(headerSymbols!, s => s.Name == "CNewTrade");
    }

    [Fact]
    public async Task DidOpen_NoRootsConfigured_HeaderStaysUnindexedAsync()
    {
        // Fail open: without configured roots today's behavior is exact.
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5Path, "mql5");

        Assert.Null(GlobalSymbolIndex.Instance.GetFileSymbols(_tradeHeaderPath, MqlLanguage.Mql5));
    }

    // --- Tier 1/1b suppression over the system header ---

    [Fact]
    public async Task Diagnostics_Mql5_SystemHeaderSymbol_IsSuppressedAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5Path, "mql5");

        var items = await GetPullDiagnosticsAsync(_eaMql5Path);

        // Header-declared class and the include-reachable name are suppressed.
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CTrade"));
        // A genuinely-missing symbol keeps its diagnostic (no blanket suppression).
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingStdlibSymbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql4_SystemHeaderSymbol_IsSuppressedAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql4Path, "mql4");

        var items = await GetPullDiagnosticsAsync(_eaMql4Path);

        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "ArrayResizeHelper"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingMql4Symbol"));
    }

    [Fact]
    public async Task Diagnostics_NoRootsConfigured_SystemHeaderSymbol_StillFlaggedAsync()
    {
        // Without roots the header is unknown: the suppression must NOT fire
        // (fail open keeps today's diagnostics).
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetPullDiagnosticsAsync(_eaMql5Path);

        Assert.Contains(items, d => IsUnresolvedFor(d, "CTrade"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingStdlibSymbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql4_NoRoots_SystemHeaderSymbol_StillFlaggedAsync()
    {
        // MQL4 mirror of the fail-open control: without roots the header is
        // unknown and its declarations stay flagged (pre-feature behavior).
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetPullDiagnosticsAsync(_eaMql4Path);

        Assert.Contains(items, d => IsUnresolvedFor(d, "ArrayResizeHelper"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingMql4Symbol"));
    }

    // --- Definition / hover reachability over the indexed system header ---

    [Fact]
    public async Task Definition_AtSystemHeaderClass_FindsHeaderLocationAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5Path, "mql5");

        var handler = new DefinitionHandler(
            Substitute.For<ILogger<DefinitionHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            store,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_eaMql5Path)),
            Position = new Position(CTradeLine, CTradeColumn)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(_tradeHeaderPath, location!.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task Hover_AtSystemHeaderClass_ShowsHeaderSymbolAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5Path, "mql5");

        var handler = new HoverHandler(
            Substitute.For<ILogger<HoverHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            store,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

        var request = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_eaMql5Path)),
            Position = new Position(CTradeLine, CTradeColumn)
        };

        var hover = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(hover);
        var content = hover!.Contents?.MarkupContent?.Value ?? string.Empty;
        Assert.Contains("CTrade", content);
        Assert.Contains("Trade.mqh", content);
    }
}
