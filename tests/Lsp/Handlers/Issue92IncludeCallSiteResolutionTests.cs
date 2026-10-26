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
/// Issue #92: cursor resolution at call sites of include-declared functions.
///
/// A call to a function whose declaration lives in an included file (e.g.
/// StopLong in main.mq4, declared in stop_utils.mqh) failed to resolve the
/// called symbol in hover (fell back to the enclosing function), definition
/// (null), references (null), documentHighlight (empty) and declaration
/// (null), even with both files indexed. The document model contains only the
/// consumer file's symbols, so FindSymbolDefinition's in-file lookup missed
/// the include-declared declaration; the tests below pin the GlobalSymbolIndex
/// fallback (IncludeSymbolResolver) for all five handler paths.
///
/// Runs inside the shared GlobalSymbolIndex Tests collection: the fixture
/// uses GlobalSymbolIndex.Instance.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue92IncludeCallSiteResolutionTests : IDisposable
{
    private const string IncludeContent =
        "// Stop/take utilities\n" +
        "void StopLong(double entryPrice, double stopLossPips)\n" +
        "{\n" +
        "    double stop = entryPrice - stopLossPips * 0.0001;\n" +
        "    Print(\"stop set: \", stop);\n" +
        "}\n";

    private const string MainContent =
        "#include \"stop_utils.mqh\"\n" +
        "\n" +
        "void OpenPendingOrder_Caesar()\n" +
        "{\n" +
        "    double stopLossPips = 40;\n" +
        "    StopLong(Bid, stopLossPips);\n" +
        "}\n" +
        "\n" +
        "void ManageOrder_Caesar()\n" +
        "{\n" +
        "    double stopLossPips = 50;\n" +
        "    StopLong(Bid, stopLossPips);\n" +
        "}\n" +
        "\n" +
        "void CloseOrder_Caesar()\n" +
        "{\n" +
        "    double stopLossPips = 30;\n" +
        "    StopLong(Bid, stopLossPips);\n" +
        "}\n";

    // 0-based line of the first StopLong call site in MainContent, and the
    // column of the identifier on that line.
    private const int CallSiteLine = 5;
    private const int CallSiteColumn = 7; // inside "StopLong" (column 4..12)

    // 0-based line/column of the StopLong declaration in IncludeContent
    // (the function Range starts at the name token).
    private const int DefinitionLine = 1;
    private const int DefinitionColumn = 5;

    private readonly string _dir;
    private readonly string _includePath;
    private readonly string _mainPath;

    public Issue92IncludeCallSiteResolutionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue92Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _includePath = Path.Combine(_dir, "stop_utils.mqh");
        _mainPath = Path.Combine(_dir, "main.mq4");
        File.WriteAllText(_includePath, IncludeContent);
        File.WriteAllText(_mainPath, MainContent);
    }

    public void Dispose()
    {
        GlobalSymbolIndex.Instance.Clear();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort on temp files
        }
    }

    /// <summary>
    /// Simulate didOpen: parse the consumer file and its include, index both
    /// into the GlobalSymbolIndex (symbols + token occurrences) and place the
    /// consumer's model in the document store.
    /// </summary>
    private OpenDocumentStore OpenAndIndex(MqlLanguage language, IMqlParser parser)
    {
        var mainFile = parser.ParseFile(MainContent, _mainPath);
        var includeFile = parser.ParseFile(IncludeContent, _includePath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_mainPath, language, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, _mainPath, language));
        GlobalSymbolIndex.Instance.AddFile(_includePath, language, includeFile.Symbols,
            SymbolOccurrenceMapper.Map(includeFile, _includePath, language));

        var store = new OpenDocumentStore();
        var uri = DocumentUri.FromFileSystemPath(_mainPath);
        store.AddOrUpdate(uri.ToUri(), mainFile, MainContent, language);
        return store;
    }

    private static IMqlBuiltins[] CreateBuiltins() =>
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() };

    [Fact]
    public async Task Hover_AtIncludeDeclaredCallSite_ShowsCalledSymbolAsync()
    {
        var parser = new Mql4AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql4, parser);
        var handler = new HoverHandler(
            Substitute.For<ILogger<HoverHandler>>(),
            new MqlLanguageService(parser, new Mql5AntlrParser()),
            store,
            CreateBuiltins());

        var request = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn)
        };

        var hover = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(hover);
        var content = hover!.Contents?.MarkupContent?.Value ?? string.Empty;
        Assert.Contains("## StopLong", content);
        Assert.Contains("stop_utils.mqh", content);
        // The enclosing-function fallback (pre-fix symptom) must NOT win.
        Assert.DoesNotContain("OpenPendingOrder_Caesar", content);
    }

    [Fact]
    public async Task Definition_AtIncludeDeclaredCallSite_ReturnsIncludeDeclarationAsync()
    {
        var parser = new Mql4AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql4, parser);
        var handler = new DefinitionHandler(
            Substitute.For<ILogger<DefinitionHandler>>(),
            new MqlLanguageService(parser, new Mql5AntlrParser()),
            store,
            CreateBuiltins(),
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(_includePath, location!.Uri.GetFileSystemPath());
        Assert.Equal(DefinitionLine, location.Range.Start.Line);
        Assert.Equal(DefinitionColumn, location.Range.Start.Character);
    }

    [Fact]
    public async Task References_AtIncludeDeclaredCallSite_ReturnsAllCallSitesAndDefinitionAsync()
    {
        var parser = new Mql4AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql4, parser);
        var handler = new ReferencesHandler(
            Substitute.For<ILogger<ReferencesHandler>>(),
            new MqlLanguageService(parser, new Mql5AntlrParser()),
            store,
            CreateBuiltins(),
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

        var request = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn),
            Context = new ReferenceContext { IncludeDeclaration = true }
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var locations = result!.ToList();
        // The three call sites in main.mq4 (0-based lines 5, 11, 17).
        Assert.Contains(locations, l =>
            l.Uri.GetFileSystemPath() == _mainPath && l.Range.Start.Line == CallSiteLine);
        Assert.Contains(locations, l =>
            l.Uri.GetFileSystemPath() == _mainPath && l.Range.Start.Line == 11);
        Assert.Contains(locations, l =>
            l.Uri.GetFileSystemPath() == _mainPath && l.Range.Start.Line == 17);
        // The definition in the included file.
        Assert.Contains(locations, l =>
            l.Uri.GetFileSystemPath() == _includePath && l.Range.Start.Line == DefinitionLine);
    }

    [Fact]
    public async Task DocumentHighlight_AtIncludeDeclaredCallSite_HighlightsCallSitesAsync()
    {
        var parser = new Mql4AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql4, parser);
        var handler = new DocumentHighlightHandler(
            Substitute.For<ILogger<DocumentHighlightHandler>>(),
            new MqlLanguageService(parser, new Mql5AntlrParser()),
            store,
            CreateBuiltins());

        var request = new DocumentHighlightParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var highlights = result!.ToList();
        Assert.NotEmpty(highlights);
        // At least the occurrence under the cursor (kind Text, per #63 precedent).
        Assert.Contains(highlights, h => h.Range.Start.Line == CallSiteLine);
        // All three call sites in main.mq4 are highlighted.
        Assert.Contains(highlights, h => h.Range.Start.Line == 11);
        Assert.Contains(highlights, h => h.Range.Start.Line == 17);
    }

    [Fact]
    public async Task Declaration_AtIncludeDeclaredCallSite_PointsAtIncludeFileAsync()
    {
        var parser = new Mql4AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql4, parser);
        var handler = new DeclarationHandler(
            Substitute.For<ILogger<DeclarationHandler>>(),
            new MqlLanguageService(parser, new Mql5AntlrParser()),
            store,
            CreateBuiltins());

        var request = new DeclarationParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(_includePath, location!.Uri.GetFileSystemPath());
        Assert.Equal(DefinitionLine, location.Range.Start.Line);
    }

    [Fact]
    public async Task Definition_AtIncludeDeclaredCallSite_Mql5_ReturnsIncludeDeclarationAsync()
    {
        // The issue reports the same defect on the shared MQL5 handler paths.
        var parser = new Mql5AntlrParser();
        var store = OpenAndIndex(MqlLanguage.Mql5, parser);
        var handler = new DefinitionHandler(
            Substitute.For<ILogger<DefinitionHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), parser),
            store,
            CreateBuiltins(),
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSiteLine, CallSiteColumn)
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(_includePath, location!.Uri.GetFileSystemPath());
        Assert.Equal(DefinitionLine, location.Range.Start.Line);
    }
}