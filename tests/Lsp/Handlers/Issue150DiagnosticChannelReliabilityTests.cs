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
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
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
/// Issue #150 regression: the diagnostics channel silently reported "no
/// problems" for documents whose full rule-set generation overran the pull
/// handler's budget. In 2.5.1 the pull channel answered an EMPTY report
/// (indistinguishable from a clean document) while the push channel kept
/// publishing the real findings — for one real-world 682KB project the two
/// channels diverged as push=289 / pull=0.
///
/// Contracts pinned here:
/// 1. A generation budget overrun must NEVER answer empty when the parse
///    produced syntax errors — it degrades to parse-only diagnostics.
/// 2. The generation budget is separate from (and larger than) the parse
///    budget: a parse that already succeeded must not be thrown away.
/// 3. Push and pull are ONE rule set over the same document state: for a
///    given didOpen, the publisher's diagnostics and the pull handler's
///    items must agree (issue #91 "one rule set, two channels").
///
/// These tests are deliberately CI-enabled (no SkippableFact, no timeouts):
/// the CI-skipped diagnostics tests are exactly the gap that let #150 ship.
/// </summary>
public class Issue150DiagnosticChannelReliabilityTests
{
    private static MqlLanguageService CreateLanguageService()
        => new(new Mql4AntlrParser(), new Mql5AntlrParser());

    private static IMqlBuiltins[] CreateBuiltins()
        => new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() };

    private static ILogger<T> MockLogger<T>() where T : class
        => Substitute.For<ILogger<T>>();

    private const string ContentWithSyntaxError =
        "void OnTick()\n{\n  int x = = 5 ;;;\n  OrderSend(\"EURUSD\",OP_BUY,0.1,Ask,3,Ask-10*Point,Ask+10*Point);\n";

    [Fact]
    public async Task GenerationBudget_IsSeparateAndLargerThanParseBudget()
    {
        // The 2.5.1 regression: generation shared the 2s parse budget and the
        // overrun was answered with an empty report. The two budgets must be
        // distinct and generation must have strictly more headroom.
        Assert.True(
            DiagnosticHandler.GenerationTimeoutMilliseconds > DiagnosticHandler.ParseTimeoutMilliseconds,
            $"Generation budget ({DiagnosticHandler.GenerationTimeoutMilliseconds}ms) must exceed " +
            $"parse budget ({DiagnosticHandler.ParseTimeoutMilliseconds}ms)");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Pull_GenerationOverrun_ReturnsParseOnlyDiagnostics_NeverEmpty()
    {
        var store = new OpenDocumentStore();
        var uri = DocumentUri.FromFileSystemPath("/tmp/issue150-gen-overrun.mq4");

        // Simulate the post-didOpen state: the parse ALREADY succeeded and is
        // in the store. The pull must build on it, not re-parse.
        var parser = new Mql4AntlrParser();
        var parsed = parser.ParseFile(ContentWithSyntaxError, "issue150-gen-overrun.mq4", CancellationToken.None);
        Assert.NotEmpty(parsed.SyntaxErrors); // precondition: the document really has parse errors
        store.AddOrUpdate(uri.ToUri(), parsed, ContentWithSyntaxError, MqlLanguage.Mql4);

        var handler = new DiagnosticHandler(
            MockLogger<DiagnosticHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());

        // A cancelled request token deterministically forces the generation
        // path through its budget-overrun branch (no wall-clock dependency).
        using var cancelledCts = new CancellationTokenSource();
        await cancelledCts.CancelAsync();

        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(uri) };
        var result = await handler.Handle(request, cancelledCts.Token);

        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.NotNull(report.ResultId);
        Assert.NotEmpty(report.Items);

        // The degraded answer is the parse-level signal: syntax errors in the
        // MQL4 range (1100). An empty report here is the regression itself.
        Assert.Contains(report.Items, d => d.Code?.String == "1100");
        Assert.All(report.Items, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task Pull_ParseBudgetOverrun_WhenNothingIsParsed_ReturnsEmptyReport()
    {
        // Different contract: when even the fallback parse is cancelled there
        // is no model at all, so an empty report is the honest answer (issue
        // #20 semantics preserved). Pinned so the two overrun paths stay
        // distinguishable.
        var store = new OpenDocumentStore();
        var handler = new DiagnosticHandler(
            MockLogger<DiagnosticHandler>(),
            CreateLanguageService(),
            store,
            CreateBuiltins());

        using var cancelledCts = new CancellationTokenSource();
        await cancelledCts.CancelAsync();

        // Not in store and not on disk: content is empty, the handler answers
        // empty without ever parsing — the same observable shape as the parse
        // budget overrun, deterministic.
        var request = new DocumentDiagnosticParams
        {
            TextDocument = new TextDocumentIdentifier(
                DocumentUri.FromFileSystemPath("/nonexistent/issue150-never-parsed.mq4"))
        };
        var result = await handler.Handle(request, cancelledCts.Token);

        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.Null(report.ResultId);
        Assert.Empty(report.Items);
    }

    [Fact]
    public async Task Push_And_Pull_Agree_ForSameDocumentState()
    {
        // The #150 observed divergence, pinned as a contract: for one didOpen,
        // the push channel's published diagnostics and the pull channel's
        // items must come from the same rule set over the same document state
        // (issue #91: one rule set, two channels). In 2.5.1 a real project saw
        // push=289 / pull=0.
        var store = new OpenDocumentStore();
        var tempFile = Path.Combine(Path.GetTempPath(), "issue150-push-pull-" + Guid.NewGuid().ToString("N") + ".mq4");
        await File.WriteAllTextAsync(tempFile, ContentWithSyntaxError);

        try
        {
            var languageService = CreateLanguageService();

            var captured = new System.Collections.Generic.List<PublishDiagnosticsParams>();
            var textDocumentServer = Substitute.For<ITextDocumentLanguageServer>();
            textDocumentServer
                .When(t => t.PublishDiagnostics(Arg.Any<PublishDiagnosticsParams>()))
                .Do(ci => captured.Add(ci.Arg<PublishDiagnosticsParams>()));

            var publisher = new DiagnosticPublisher(
                MockLogger<DiagnosticPublisher>(),
                textDocumentServer);

            var didOpenHandler = new DidOpenTextDocumentHandler(
                MockLogger<DidOpenTextDocumentHandler>(),
                languageService,
                store,
                CreateBuiltins(),
                diagnosticPublisher: publisher);

            var uri = DocumentUri.FromFileSystemPath(tempFile);
            await didOpenHandler.Handle(
                new DidOpenTextDocumentParams
                {
                    TextDocument = new TextDocumentItem
                    {
                        Uri = uri,
                        LanguageId = "mql4",
                        Version = 1,
                        Text = ContentWithSyntaxError
                    }
                },
                CancellationToken.None);

            // Push channel: didOpen must publish exactly one batch, non-empty,
            // with the syntax errors visible.
            var pushBatch = Assert.Single(captured);
            Assert.NotEmpty(pushBatch.Diagnostics);

            // Pull channel: same document state must produce the same rule set.
            var pullHandler = new DiagnosticHandler(
                MockLogger<DiagnosticHandler>(),
                languageService,
                store,
                CreateBuiltins());
            var result = await pullHandler.Handle(
                new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(uri) },
                CancellationToken.None);
            var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);

            Assert.Equal(
                pushBatch.Diagnostics.Count(),
                report.Items.Count());
            // And the syntax error is on both channels, with the same code.
            Assert.Contains(pushBatch.Diagnostics, d => d.Code?.String == "1100");
            Assert.Contains(report.Items, d => d.Code?.String == "1100");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
