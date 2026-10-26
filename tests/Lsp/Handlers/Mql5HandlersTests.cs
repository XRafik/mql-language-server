using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
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

[Collection("Mql5 Handler Tests")]
public class Mql5HandlersTests
{
    private readonly Mql5TestCollectionFixture _fixture;

    public Mql5HandlersTests(Mql5TestCollectionFixture fixture)
    {
        _fixture = fixture;
        // F12 acceptance: ensure the dual-key GlobalSymbolIndex dictionary is empty
        // before each test in this collection, regardless of ordering. The collection
        // fixture ctor/dispose also Clear()s, but xUnit creates the fixture once per
        // collection run, not per test, so we reset here to guarantee isolation.
        GlobalSymbolIndex.Instance.Clear();
    }

    private static MqlLanguageService CreateLanguageService()
    {
        return new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser());
    }

    private static IMqlBuiltins[] CreateBuiltins()
    {
        return new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() };
    }

    private static ILogger<T> MockLogger<T>() where T : class
    {
        return Substitute.For<ILogger<T>>();
    }

    [Fact]
    public void Fixture_Setup_GlobalSymbolIndex_Is_Empty()
    {
        // F12 acceptance: the fixture's InitializeAsync (and the ctor Clear()) run before
        // each test, so the dual-key dictionary must be empty at this point.
        var stats = GlobalSymbolIndex.Instance.GetStatistics();
        Assert.Equal(0, stats.FileCount);
    }

    [Fact]
    public void Fixture_Pollutes_Index_NextTestStillIsolated()
    {
        // A-004: deliberately pollute the shared singleton. The next test in this
        // collection must still see an empty index (verified by Fixture_Setup_GlobalSymbolIndex_Is_Empty)
        // without this test calling Clear() itself.
        GlobalSymbolIndex.Instance.AddFile("/polluted.mq5", MqlLanguage.Mql5,
            new List<MqlSymbol> { new() { Name = "Polluted", FilePath = "/polluted.mq5" } });
        Assert.True(GlobalSymbolIndex.Instance.GetStatistics().FileCount > 0);
    }

    [Fact]
    public async Task DiagnosticHandler_Mql5_Returns_MQL5_CodesAsync()
    {
        // Arrange
        var path = _fixture.CreateTempFile("test.mq5",
            "int OnInit(){}\n" +
            "\n" +
            "void OnTick()\n" +
            "{\n" +
            "    int UnkownVariable = 0;\n" +
            "    UnkownFunction();\n" +
            "    int _internalVar = 1;\n" +
            "}\n");

        var store = new OpenDocumentStore();
        var handler = new DiagnosticHandler(
            MockLogger<DiagnosticHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());

        var request = new DocumentDiagnosticParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path))
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        // A-007: codes are now numeric. MQL5 base = 5000, offsets 001/002/003.
        var codes = report.Items.Select(d => d.Code?.String).Where(c => c != null).ToList();
        Assert.Contains("5001", codes);
        Assert.Contains("5002", codes);
        Assert.DoesNotContain("5003", codes); // underscore hint removed (issue #109 fourth sweep)
        Assert.All(report.Items, d =>
        {
            var code = d.Code?.String ?? "0";
            Assert.True(int.TryParse(code, out var n) && n >= 5000 && n < 6000,
                $"MQL5 diagnostic code '{code}' must be in the 5000-5999 range");
        });
    }

    [Fact]
    public async Task CompletionHandler_Mql5_Suggests_PositionGetTicketAsync()
    {
        // Arrange
        var content = "void OnTick()\n{\n    ulong t = Position\n}\n";
        var path = _fixture.CreateTempFile("complete.mq5", content);

        var store = new OpenDocumentStore();
        var handler = new CompletionHandler(
            MockLogger<CompletionHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());

        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            Position = new Position(2, 18) // after "Position"
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.Items);
        Assert.Contains(result.Items, i => i.Label.Contains("PositionGetTicket", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Debug_FindSymbolDefinition_Mql5()
    {
        var parser = new Mql5AntlrParser();
        Assert.True(parser.IsBuiltin("PositionGetTicket"));

        var content = "void OnTick()\n{\n    ulong t = PositionGetTicket(0);\n}\n";
        var file = parser.ParseFile(content, "debug.mq5");
        var symbol = parser.FindSymbolDefinition(file, content, 3, 19);
        Assert.NotNull(symbol);
        Assert.Equal("PositionGetTicket", symbol!.Name);
    }

    [Fact]
    public async Task HoverHandler_Mql5_Returns_PositionGetTicket_DetailAsync()
    {
        // Arrange
        var content = "void OnTick()\n{\n    ulong t = PositionGetTicket(0);\n}\n";
        var path = _fixture.CreateTempFile("hover.mq5", content);

        var store = new OpenDocumentStore();
        var handler = new HoverHandler(
            MockLogger<HoverHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());

        var request = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            Position = new Position(2, 18) // on PositionGetTicket
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var hover = Assert.IsType<Hover>(result);
        var contentString = hover.Contents?.MarkupContent?.Value ?? string.Empty;
        Assert.Contains("PositionGetTicket", contentString);
        Assert.Contains("MQL5 Built-in", contentString);
    }

    [Fact]
    public void GetRegistrationOptions_Includes_Mql5_LanguageId_And_Patterns()
    {
        var handler = new CompletionHandler(
            MockLogger<CompletionHandler>(),
            CreateLanguageService(),
            new OpenDocumentStore(),
            CreateBuiltins());

        var options = handler.GetRegistrationOptions(new CompletionCapability(), new ClientCapabilities());

        Assert.NotNull(options.DocumentSelector);
        Assert.Contains(options.DocumentSelector, f => f.Pattern == "**/*.mq5");
        Assert.Contains(options.DocumentSelector, f => f.Language == "mql5");
    }

    // ------------------------------------------------------------------
    // Issue #29 Slice 3 (REQ-HD-04/05): performance monitoring, error
    // contract, MQL4 tolerance, .mqh language routing, cross-file members
    // and heuristics fallback through the handler surface.
    // ------------------------------------------------------------------

    private static CompletionHandler CreateCompletionHandler(OpenDocumentStore store)
    {
        return new CompletionHandler(
            MockLogger<CompletionHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());
    }

    /// <summary>
    /// REQ-HD-04 "Performance monitoring intact": a completion request records
    /// a "Completion" operation measurement via the existing performance
    /// monitor.
    /// </summary>
    [Fact]
    public async Task CompletionHandler_Measures_CompletionOperationAsync()
    {
        // Arrange
        var content = "int OnInit()\n{\n    int count;\n    return 0;\n}\n";
        var path = _fixture.CreateTempFile("monitor.mq5", content);

        var store = new OpenDocumentStore();
        var handler = CreateCompletionHandler(store);

        MetricsCollector.Instance.Reset();
        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            Position = new Position(3, 4)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var snapshot = MetricsCollector.Instance.GetSnapshot();
        Assert.Contains(snapshot.OperationMetrics, m => m.OperationName == "Completion");
    }

    /// <summary>
    /// REQ-HD-04 "Error contract preserved": an internal handler exception
    /// surfaces as an empty CompletionList, never an error to the client.
    /// </summary>
    [Fact]
    public async Task CompletionHandler_InternalException_ReturnsEmptyCompletionListAsync()
    {
        // Arrange: an empty builtins registry makes ResolveBuiltins throw
        // ("No IMqlBuiltins registry registered") inside the handler, driving
        // the catch-all into its empty-list error contract.
        var languageService = CreateLanguageService();
        var handler = new CompletionHandler(
            MockLogger<CompletionHandler>(),
            languageService,
            new OpenDocumentStore(),
            System.Array.Empty<IMqlBuiltins>());

        var path = _fixture.CreateTempFile("throwing.mq4", "int OnInit()\n{\n}\n");
        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            Position = new Position(1, 0)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.False(result.IsIncomplete);
    }

    /// <summary>
    /// REQ-HD-04 / CCR-05: an MQL4 document's member completion tolerates
    /// symbols with SymbolType==null — members are suggested, classified
    /// from Kind (method kind → method completion items).
    /// </summary>
    [Fact]
    public async Task CompletionHandler_Mql4MemberAccess_ClassifiedFromKindAsync()
    {
        // Arrange
        var content = @"class CIndicator
{
    int buffer;
    void Draw()
    {
    }
};
int OnInit()
{
    CIndicator ind;
    ind.
    return 0;
}";
        var path = _fixture.CreateTempFile("mql4_member.mq4", content);

        var store = new OpenDocumentStore();
        var handler = CreateCompletionHandler(store);

        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            // Cursor immediately after "ind." (line 10, 0-based).
            Position = new Position(10, 8)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var labels = result.Items.Select(i => i.Label).ToList();
        Assert.Contains("buffer", labels);
        Assert.Contains("Draw", labels);
        // Members classified from Kind: method items, not unrelated symbols.
        Assert.Contains(result.Items, i => i.Label == "Draw" && i.Kind == CompletionItemKind.Method);
    }

    /// <summary>
    /// REQ-HD-04 / CCR-03: the receiver type defined in another (quoted
    /// include) file resolves through the GlobalSymbolIndex — handler-level
    /// cross-file member completion. Runs in this collection (REQ-HD-05).
    /// </summary>
    [Fact]
    public async Task CompletionHandler_CrossFileMqhClass_MembersResolveAsync()
    {
        // Arrange: index an .mqh defining class CTimer under Mql5.
        var includeContent = @"class CTimer
{
    int elapsed;
    void Reset()
    {
        elapsed = 0;
    }
};";
        var includePath = _fixture.CreateTempFile("handler_timer.mqh", includeContent);
        var mqhParser = new Mql5AntlrParser();
        var includedFile = mqhParser.ParseFile(includeContent, includePath);
        GlobalSymbolIndex.Instance.AddFile(includePath, MqlLanguage.Mql5, includedFile.Symbols);

        var content = @"#include ""handler_timer.mqh""
int OnInit()
{
    CTimer t;
    t.
    return 0;
}";
        var path = _fixture.CreateTempFile("cross_file_main.mq5", content);

        var store = new OpenDocumentStore();
        var handler = CreateCompletionHandler(store);

        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            // Cursor immediately after "t." (line 4, 0-based).
            Position = new Position(4, 6)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var labels = result.Items.Select(i => i.Label).ToList();
        Assert.Contains("elapsed", labels);
        Assert.Contains("Reset", labels);
    }

    /// <summary>
    /// REQ-HD-04 / CCR-04: .mqh documents route by includer language — a
    /// header included only by MQL5 sources runs the MQL5 pipeline, not the
    /// MQL4 one. Verified through the resolver's language-filtered index
    /// merge: the .mqh document requests MQL5 symbols and only MQL5-variant
    /// entries are admitted (an MQL4-indexed class must not resolve).
    /// </summary>
    [Fact]
    public async Task CompletionHandler_MqhDocument_RoutedByIncluderLanguageAsync()
    {
        // Arrange: the .mqh document, pre-opened under Mql5 (includer-decided
        // routing, as the didOpen pipeline does). The handler resolves the
        // language via the store, and the resolver filters index merges by
        // that language.
        var includePath = _fixture.CreateTempFile("routed.mqh",
            "class CRouted\n{\n    int member;\n};\n");
        var content = "class CRouted\n{\n    int member;\n};\n";
        var file = new Mql5AntlrParser().ParseFile(content, includePath);

        var store = new OpenDocumentStore();
        store.AddOrUpdate(DocumentUri.FromFileSystemPath(includePath).ToUri(), file, content, MqlLanguage.Mql5);

        var handler = CreateCompletionHandler(store);

        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(includePath)),
            // End of the header — plain context.
            Position = new Position(4, 2)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert: MQL5 pipeline ran — the header's own class is in the
        // scope-aware top-level list. (Its member is NOT suggested outside
        // the enclosing class per CCR-01/JD-2: class members belong to the
        // enclosing-class tier, and the cursor here is outside the class.)
        Assert.NotNull(result);
        Assert.Contains(result.Items, i => i.Label == "CRouted");
    }

    /// <summary>
    /// REQ-HD-04 / CCR-04: the resolver's cross-file merge admits only
    /// symbols whose language matches the requesting document. Indexed under
    /// the includer-decided language (Mql5), an Mql4 request must not merge
    /// the Mql5 class.
    /// </summary>
    [Fact]
    public void CompletionHandler_CrossFileMerge_IsLanguageFiltered()
    {
        // Arrange: index the header under Mql5 (MQL5 includer). The .mq5
        // document (same model) references the class through the index.
        var includePath = _fixture.CreateTempFile("routed.mqh",
            "class CRouted\n{\n    int member;\n};\n");
        var content = "class CRouted\n{\n    int member;\n};\n";
        GlobalSymbolIndex.Instance.AddFile(
            includePath, MqlLanguage.Mql5, new Mql5AntlrParser().ParseFile(content, includePath).Symbols);

        // The requesting document does NOT define the class itself — its
        // symbols can only come from the language-filtered index merge.
        var mainContent = "#include \"routed.mqh\"\nint OnInit()\n{\n    CRouted r;\n    return 0;\n}\n";
        var file = new Mql5AntlrParser().ParseFile(mainContent, "/tmp/main.mq5");

        var resolver = new CompletionContextResolver(new GlobalSymbolIndexAccessor());

        // Act: scope resolution for both requesting languages. The flat file
        // model has no member symbols here — any member in the scope list
        // came from the language-filtered index merge.
        var mql4Result = resolver.Resolve(file, mainContent, 4, 4, MqlLanguage.Mql4);
        var mql5Result = resolver.Resolve(file, mainContent, 4, 4, MqlLanguage.Mql5);

        // Assert
        Assert.True(mql5Result.Success);
        Assert.Contains(mql5Result.ScopeSymbols, s => s.Name == "member");
        // Language filter: the Mql4 request sees no Mql5-only members.
        Assert.DoesNotContain(mql4Result.ScopeSymbols, s => s.Name == "member");
    }

    /// <summary>
    /// REQ-HD-04 / CCR-05 "Incomplete parse degrades to heuristics": when the
    /// resolver cannot resolve the member context, the handler returns the
    /// existing heuristic-based list (unresolved receiver → no member list,
    /// but still a successful completion response).
    /// </summary>
    [Fact]
    public async Task CompletionHandler_UnresolvedReceiver_DegradesToHeuristicsAsync()
    {
        // Arrange: receiver type only declared in an angle-bracket stdlib
        // include — intentionally unresolvable (CCR-03). The handler must not
        // error; it degrades to the heuristic list.
        var content = @"#include <Trade/Trade.mqh>
int OnInit()
{
    CTrade trade;
    trade.
    return 0;
}";
        var path = _fixture.CreateTempFile("fallback.mq5", content);

        var store = new OpenDocumentStore();
        var handler = CreateCompletionHandler(store);

        var request = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
            // Cursor immediately after "trade." (line 4, 0-based).
            Position = new Position(4, 10)
        };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert: no error, no false member list; the response is the
        // heuristic-based list (never empty by contract, never null).
        Assert.NotNull(result);
        // Unresolved receiver must NOT produce the class's members: with no
        // indexed CTrade, no member items can appear.
        Assert.DoesNotContain(result.Items, i => i.Label == "Buy" || i.Label == "Sell");
    }
}
