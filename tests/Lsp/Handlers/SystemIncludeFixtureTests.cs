using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
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
/// S5 (issue #179, task T4) regression tests over the COMMITTED synthetic
/// system-include fixture under <c>tests/fixtures/system-include/</c>.
///
/// <para>Unlike <see cref="SystemIncludeResolutionTests"/>, which builds a
/// throwaway terminal tree in the temp directory, these tests point the
/// <see cref="SystemIncludeRoots"/> test hook at the fixture root committed to
/// the repository. The fixture reproduces the SHAPE the real MetaTrader
/// Standard Library exercises, using project-authored code only:</para>
///
/// <list type="bullet">
/// <item><c>Trade\TradeLib.mqh</c> — a base class plus a derived class that
/// inherits from it (the CTrade pattern).</item>
/// <item><c>Collections\ArrayLongLib.mqh</c> — a collection-like type (the
/// CArrayLong pattern), reached THROUGH a transitive include chain: the entry
/// header includes <c>Expert\ExpertLib.mqh</c> (same-directory quoted hop),
/// which includes the collection header, so the closure exercises
/// depth &gt; 1.</item>
/// <item><c>Expert\ExpertEntry.mqh</c> — the entry header the consumer
/// document includes through the angle-bracket directive.</item>
/// <item><c>Expert\ExpertQuotedEntry.mqh</c> — the quoted parent-relative
/// entry header (issue #186): reached through the angle-bracket directive,
/// its single nested hop uses the quoted <c>..\</c> form into
/// <c>Collections\</c> — the hop the include closure must admit inside a
/// declared system root.</item>
/// </list>
///
/// <para>Tier 1 contract under test: with system include roots configured,
/// undeclared-symbol (1070/5070) diagnostics for symbols reachable only
/// through the include closure are suppressed, while genuinely-unknown
/// symbols keep firing (no blanket suppression). Without roots nothing is
/// suppressed (fail open). All assertions go through the pull-diagnostics
/// pipeline, not internal storage.</para>
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class SystemIncludeFixtureTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _wsDir;     // workspace folder with consumer docs
    private readonly string _eaMql5Path;
    private readonly string _eaMql4Path;
    private readonly string _eaMql5QuotedPath;
    private readonly string _eaMql4QuotedPath;

    public SystemIncludeFixtureTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "SystemIncludeFixtureTests_" + Guid.NewGuid().ToString("N"));
        _wsDir = Path.Combine(_baseDir, "ws");
        Directory.CreateDirectory(_wsDir);

        // MQL5 consumer: every fixture symbol (CSynthTrade, CSynthObjectBase,
        // CArrayLongSynth) is reached ONLY through the single angle-bracket
        // include of the entry header; MissingStdlibSymbol is genuinely
        // unknown and must keep its diagnostic.
        _eaMql5Path = Path.Combine(_wsDir, "consumer.mq5");
        File.WriteAllText(_eaMql5Path,
            "#include <Expert\\ExpertEntry.mqh>\n" +
            "void OnTick()\n" +
            "{\n" +
            "   CSynthTrade trade;\n" +
            "   trade.Buy(0.1);\n" +
            "   CSynthObjectBase baseObj;\n" +
            "   CArrayLongSynth values;\n" +
            "   int total = values.Total();\n" +
            "   MissingStdlibSymbol();\n" +
            "}\n");

        // MQL4 counterpart: same fixture closure, same suppression contract.
        _eaMql4Path = Path.Combine(_wsDir, "consumer.mq4");
        File.WriteAllText(_eaMql4Path,
            "#include <Expert\\ExpertEntry.mqh>\n" +
            "void OnStart()\n" +
            "{\n" +
            "   CSynthTrade trade;\n" +
            "   trade.Sell(0.1);\n" +
            "   CSynthObjectBase baseObj;\n" +
            "   CArrayLongSynth values;\n" +
            "   int total = values.Total();\n" +
            "   MissingMql4Symbol();\n" +
            "}\n");

        // Issue #186 consumers: the entry header arrives through the angle
        // form, but its single nested hop uses the QUOTED parent-relative
        // form ("..\Collections\ArrayLongLib.mqh"). CArrayLongSynth must be
        // suppressed through that hop; MissingQuoted*Symbol is genuinely
        // unknown and must keep its diagnostic.
        _eaMql5QuotedPath = Path.Combine(_wsDir, "consumer-quoted.mq5");
        File.WriteAllText(_eaMql5QuotedPath,
            "#include <Expert\\ExpertQuotedEntry.mqh>\n" +
            "void OnTick()\n" +
            "{\n" +
            "   CArrayLongSynth values;\n" +
            "   int total = values.Total();\n" +
            "   MissingQuotedStdlibSymbol();\n" +
            "}\n");

        // MQL4 counterpart: same quoted parent-relative hop, same contract.
        _eaMql4QuotedPath = Path.Combine(_wsDir, "consumer-quoted.mq4");
        File.WriteAllText(_eaMql4QuotedPath,
            "#include <Expert\\ExpertQuotedEntry.mqh>\n" +
            "void OnStart()\n" +
            "{\n" +
            "   CArrayLongSynth values;\n" +
            "   int total = values.Total();\n" +
            "   MissingQuotedMql4Symbol();\n" +
            "}\n");

        // Point the session's system include roots at the committed fixture
        // root and wire the resolver provider exactly as Program.OnInitialize
        // does with the systemIncludeRoots initialization option.
        SystemIncludeRoots.Set(new[] { FixtureIncludeRoot() });
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

    /// <summary>
    /// The committed synthetic terminal include tree:
    /// <c>tests/fixtures/system-include/Include</c>. Resolved by walking up to
    /// <c>MqlLanguageServer.sln</c> so it works regardless of the test
    /// runner's working directory (the convention used by tests/FpMeasurement
    /// and the workspace scan tests).
    /// </summary>
    private static string FixtureIncludeRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MqlLanguageServer.sln")))
        {
            dir = dir.Parent;
        }

        if (dir == null)
        {
            throw new InvalidOperationException("Could not locate the repository root");
        }

        return Path.Combine(dir.FullName, "tests", "fixtures", "system-include", "Include");
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
        // (same mechanism as DiagnosticHandlerCrossFileSuppressionTests and
        // SystemIncludeResolutionTests).
        if (diagnostic.Code != "1070" && diagnostic.Code != "5070")
        {
            return false;
        }

        return string.Equals(diagnostic.Data?["symbol"]?.ToString(), symbol, StringComparison.Ordinal);
    }

    // --- Tier 1 suppression through the committed fixture closure ---

    [Fact]
    public async Task Diagnostics_Mql5_CommittedFixture_TransitiveSystemInclude_SuppressesUndeclaredSymbolsAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5Path, "mql5");

        var items = await GetPullDiagnosticsAsync(_eaMql5Path);

        // Derived class (CTrade pattern) and its base are suppressed through
        // the include closure.
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CSynthTrade"));
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CSynthObjectBase"));
        // Collection type (CArrayLong pattern) sits two include hops below the
        // entry header — its suppression proves the depth>1 chain was indexed.
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        // A genuinely-missing symbol keeps its diagnostic (no blanket suppression).
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingStdlibSymbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql4_CommittedFixture_TransitiveSystemInclude_SuppressesUndeclaredSymbolsAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql4Path, "mql4");

        var items = await GetPullDiagnosticsAsync(_eaMql4Path);

        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CSynthTrade"));
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CSynthObjectBase"));
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingMql4Symbol"));
    }

    // --- Issue #186: quoted parent-relative hop inside a declared root ---

    [Fact]
    public async Task Diagnostics_Mql5_CommittedFixture_QuotedParentRelativeInclude_SuppressesUndeclaredSymbolsAsync()
    {
        // Issue #186 RED pin: the quoted parent-relative hop inside the
        // declared root must feed the include closure, so CArrayLongSynth is
        // suppressed exactly like through the angle-form chain; the
        // genuinely-missing symbol keeps its diagnostic (no blanket
        // suppression).
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql5QuotedPath, "mql5");

        var items = await GetPullDiagnosticsAsync(_eaMql5QuotedPath);

        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingQuotedStdlibSymbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql4_CommittedFixture_QuotedParentRelativeInclude_SuppressesUndeclaredSymbolsAsync()
    {
        // MQL4 mirror of the issue #186 quoted parent-relative pin.
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _eaMql4QuotedPath, "mql4");

        var items = await GetPullDiagnosticsAsync(_eaMql4QuotedPath);

        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingQuotedMql4Symbol"));
    }

    // --- Fail-open controls: without roots the closure suppresses nothing ---

    [Fact]
    public async Task Diagnostics_Mql5_NoRoots_CommittedFixtureSymbols_StillFlaggedAsync()
    {
        // Without configured roots the committed headers are unknown: the
        // suppression must NOT fire (pre-feature behavior is kept exactly).
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetPullDiagnosticsAsync(_eaMql5Path);

        Assert.Contains(items, d => IsUnresolvedFor(d, "CSynthTrade"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingStdlibSymbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql4_NoRoots_CommittedFixtureSymbols_StillFlaggedAsync()
    {
        // MQL4 mirror of the fail-open control over the committed fixture.
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetPullDiagnosticsAsync(_eaMql4Path);

        Assert.Contains(items, d => IsUnresolvedFor(d, "CSynthTrade"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingMql4Symbol"));
    }

    [Fact]
    public async Task Diagnostics_Mql5_NoRoots_QuotedParentRelativeInclude_StillFlaggedAsync()
    {
        // Fail-open control (issue #186): without declared roots the quoted
        // consumer suppresses nothing — CArrayLongSynth and the missing
        // symbol both keep their diagnostics.
        SystemIncludeRoots.Clear();
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetPullDiagnosticsAsync(_eaMql5QuotedPath);

        Assert.Contains(items, d => IsUnresolvedFor(d, "CArrayLongSynth"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "MissingQuotedStdlibSymbol"));
    }
}
