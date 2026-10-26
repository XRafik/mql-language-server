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
/// End-to-end integration tests for the MQL5 LSP lifecycle.
/// Exercises didOpen, hover, definition, completion, and diagnostics in a single
/// chained scenario using the same infrastructure as the production server.
/// Uses the MQL5 test collection so the shared GlobalSymbolIndex singleton stays isolated.
/// </summary>
[Collection("Mql5 Handler Tests")]
public class Mql5IntegrationTests
{
    private readonly Mql5TestCollectionFixture _fixture;

    public Mql5IntegrationTests(Mql5TestCollectionFixture fixture)
    {
        _fixture = fixture;
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

    private const string Mql5Source =
        "class TradeContext {\n" +
        "public:\n" +
        "    long Ticket;\n" +
        "};\n" +
        "\n" +
        "void OnTick()\n" +
        "{\n" +
        "    long ticket = PositionGetTicket(0);\n" +
        "    TradeContext context;\n" +
        "    context.Ticket = ticket;\n" +
        "    int _internalVar = 1;\n" +
        "    UnkownFunction();\n" +
        "}\n";

    private string CreateSourceFile()
    {
        return _fixture.CreateTempFile("integration.mq5", Mql5Source);
    }

    private static Uri PathToUri(string path)
    {
        return DocumentUri.File(path).ToUri();
    }

    [Fact]
    public async Task FullLifecycle_didOpen_Hover_Definition_Completion_DiagnosticsAsync()
    {
        // Arrange: open a .mq5 file through DidOpenTextDocumentHandler
        var path = CreateSourceFile();
        var uri = PathToUri(path);

        var store = new OpenDocumentStore();
        var languageService = CreateLanguageService();
        var builtins = CreateBuiltins();

        var didOpenHandler = new DidOpenTextDocumentHandler(
            MockLogger<DidOpenTextDocumentHandler>(),
            languageService,
            store,
            builtins);

        var didOpenRequest = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = DocumentUri.File(path),
                LanguageId = "mql5",
                Version = 1,
                Text = Mql5Source
            }
        };

        await didOpenHandler.Handle(didOpenRequest, CancellationToken.None);

        Assert.True(store.TryGetLanguage(uri, out var storedLanguage));
        Assert.Equal(MqlLanguage.Mql5, storedLanguage);

        // Act/Assert 1: hover on a MQL5 builtin returns markup content with the builtin name
        var hoverHandler = new HoverHandler(
            MockLogger<HoverHandler>(),
            languageService,
            store,
            builtins);

        var hoverRequest = new HoverParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.File(path)),
            Position = new Position(7, 21) // on PositionGetTicket
        };

        var hover = await hoverHandler.Handle(hoverRequest, CancellationToken.None);
        Assert.NotNull(hover);
        var hoverContent = hover!.Contents?.MarkupContent?.Value ?? string.Empty;
        Assert.Contains("PositionGetTicket", hoverContent);
        Assert.Contains("MQL5 Built-in", hoverContent);

        // Act/Assert 2: definition on a user-defined symbol resolves to its declaration
        var definitionHandler = new DefinitionHandler(
            MockLogger<DefinitionHandler>(),
            languageService,
            store,
            builtins);

        var definitionRequest = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.File(path)),
            Position = new Position(8, 10) // on context.Ticket reference
        };

        var definitionResult = await definitionHandler.Handle(definitionRequest, CancellationToken.None);
        Assert.NotNull(definitionResult);
        var definitionLocations = definitionResult!.Select(l => l.Location).Where(l => l != null).ToList();
        Assert.NotEmpty(definitionLocations);
        var definitionLocation = definitionLocations.First()!;
        Assert.Contains("integration", Path.GetFileName(definitionLocation.Uri.GetFileSystemPath()));

        // Act/Assert 3: completion at a position returns MQL5 builtins
        var completionHandler = new CompletionHandler(
            MockLogger<CompletionHandler>(),
            languageService,
            store,
            builtins);

        var completionRequest = new CompletionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.File(path)),
            Position = new Position(6, 25) // after PositionGetTicket(
        };

        var completionList = await completionHandler.Handle(completionRequest, CancellationToken.None);
        Assert.NotNull(completionList);
        Assert.NotEmpty(completionList.Items);
        Assert.Contains(completionList.Items, i => i.Label.Contains("PositionGetSymbol", StringComparison.OrdinalIgnoreCase));

        // Act/Assert 4: diagnostics use MQL5xxx codes
        var diagnosticHandler = new DiagnosticHandler(
            MockLogger<DiagnosticHandler>(),
            languageService,
            store,
            builtins);

        var diagnosticRequest = new DocumentDiagnosticParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.File(path))
        };

        var diagnostics = await diagnosticHandler.Handle(diagnosticRequest, CancellationToken.None);
        Assert.NotNull(diagnostics);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(diagnostics);
        Assert.NotNull(report.Items);
        Assert.NotEmpty(report.Items);

        foreach (var diagnostic in report.Items)
        {
            var code = diagnostic.Code?.String ?? "0";
            Assert.True(int.TryParse(code, out var numericCode), $"Diagnostic code '{code}' should be numeric");
            Assert.InRange(numericCode, 5000, 5999);
        }

        Assert.Contains(report.Items, d => d.Code?.String == "5001"); // typo diagnostic
        Assert.DoesNotContain(report.Items, d => d.Code?.String == "5003"); // underscore hint removed (issue #109 fourth sweep)
    }
}
