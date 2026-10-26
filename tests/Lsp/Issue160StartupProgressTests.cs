using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Newtonsoft.Json.Linq;
using NSubstitute;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Server;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Issue #160 regression tests.
///
/// Part 1 — Issue160QueuingReceiverTests: messages batched in the same pipe read as the
/// initialize request must be dispatched (kept, in order) instead of being
/// rejected with a swallowed -32002 (requests) or dropped (notifications).
/// After the receiver gate opens, the library's own semantics are unchanged.
///
/// Part 2 — Issue160WorkspaceIndexerProgressTests: the startup scan must
/// report a monotonic 0→100 percentage so clients get an event-driven
/// readiness signal ($/progress begin/report/end).
/// </summary>
public class Issue160QueuingReceiverTests
{
    private static QueuingReceiver CreateReceiver()
        => new(NullLoggerFactory.Instance);

    private static JToken Batch(params object[] messages)
        => JArray.FromObject(messages);

    private static string[] Methods((IEnumerable<Renor> results, bool hasResponse) parsed)
        => parsed.results
            .Select(r => r.IsRequest ? r.Request!.Method
                : r.IsNotification ? r.Notification!.Method
                : r.IsError ? "<error>"
                : "<response>")
            .ToArray();

    [Fact]
    public void PreInit_BatchedInitializeDidOpenHover_AllDispatchedInOrder()
    {
        // The exact issue #160 shape: one OS write containing the handshake
        // plus didOpen and a hover request, all parsed while the gate is
        // still closed. Everything must be kept, in arrival order.
        var receiver = CreateReceiver();
        var parsed = receiver.GetRequests(Batch(
            new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } },
            new { jsonrpc = "2.0", method = "initialized", @params = new { } },
            new { jsonrpc = "2.0", method = "textDocument/didOpen", @params = new { } },
            new { jsonrpc = "2.0", id = 2, method = "textDocument/hover", @params = new { } }));

        Assert.Equal(
            new[] { "initialize", "initialized", "textDocument/didOpen", "textDocument/hover" },
            Methods(parsed));
        Assert.False(parsed.hasResponse);
    }

    [Fact]
    public void PreInit_StandaloneRequest_IsKeptNotRejected()
    {
        // Base LspServerReceiver converts a pre-init request into a
        // ServerNotInitialized error that the output filter then swallows
        // (silent drop). QueuingReceiver must keep the request dispatchable.
        var receiver = CreateReceiver();
        var parsed = receiver.GetRequests(JObject.FromObject(new
        {
            jsonrpc = "2.0",
            id = 7,
            method = "textDocument/documentSymbol",
            @params = new { }
        }));

        var item = Assert.Single(parsed.results);
        Assert.True(item.IsRequest);
        Assert.Equal("textDocument/documentSymbol", item.Request!.Method);
        Assert.Null(item.Error);
    }

    [Fact]
    public void PreInit_ResponsesAndProtocolErrors_ArePreserved()
    {
        var receiver = CreateReceiver();
        var parsed = receiver.GetRequests(Batch(
            new { jsonrpc = "2.0", method = "textDocument/didChange", @params = new { } },
            new { jsonrpc = "2.0", id = "c1", result = new { } },
            new { jsonrpc = "2.0", id = 3, method = "shutdown" }));

        Assert.Equal(3, parsed.results.Count());
        Assert.True(parsed.hasResponse, "The batch contains a client response.");
        Assert.Contains(parsed.results, r => r.IsResponse);
    }

    [Fact]
    public void PostInit_LibrarySemanticsUnchanged_RequestParsedNormally()
    {
        // After the gate opens, behavior must be exactly the library's own
        // (issue #93 semantics): the receiver parses requests normally (the
        // -32002 rejection path only exists pre-init — the same path
        // QueuingReceiver replaces, see the pre-init tests above). Dispatch
        // and any MethodNotFound handling are the input loop's concern, not
        // the receiver's.
        var receiver = CreateReceiver();
        receiver.Initialized();

        var parsed = receiver.GetRequests(JObject.FromObject(new
        {
            jsonrpc = "2.0",
            id = 9,
            method = "textDocument/hover",
            @params = new { }
        }));

        var item = Assert.Single(parsed.results);
        Assert.True(item.IsRequest);
        Assert.Equal("textDocument/hover", item.Request!.Method);
        Assert.Null(item.Error);
    }
}

[Collection("GlobalSymbolIndex Tests")]
public class Issue160WorkspaceIndexerProgressTests : IDisposable
{
    private readonly string _workspace;
    private readonly GlobalSymbolIndex _index = GlobalSymbolIndex.Instance;

    public Issue160WorkspaceIndexerProgressTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "workspace-indexer-progress-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _index.Clear();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
        finally
        {
            _index.Clear();
        }
    }

    private WorkspaceIndexer CreateIndexer()
        => new WorkspaceIndexer(
            Substitute.For<ILogger<WorkspaceIndexer>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            new OpenDocumentStore());

    [Fact]
    public void ScanProgress_StartsAtZeroEndsAtOneHundredMonotonically()
    {
        for (var i = 0; i < 4; i++)
        {
            File.WriteAllText(Path.Combine(_workspace, $"p{i}.mq4"), $"int V{i} = {i};\n");
        }

        var reports = new List<(int? Percentage, string Message)>();
        var indexer = CreateIndexer();
        indexer.ScanProgressNotifier = (pct, msg) => reports.Add((pct, msg));
        indexer.StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.NotEmpty(reports);
        Assert.Equal(0, reports[0].Percentage);
        Assert.Equal(100, reports[^1].Percentage);
        Assert.True(reports.Count >= 6, $"Expected start + per-file + end reports, got {reports.Count}.");

        // Monotonic non-decreasing percentages; all within 0..100.
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.InRange(reports[i].Percentage!.Value, 0, 100);
            Assert.True(reports[i].Percentage >= reports[i - 1].Percentage,
                $"Progress went backwards: {reports[i - 1]} -> {reports[i]}");
        }

        // The last message mirrors the scan-finished summary.
        Assert.Contains("finished", reports[^1].Message);
    }

    [Fact]
    public void ScanProgress_TwoFolders_CoversZeroToOneHundredAcrossBoth()
    {
        var second = Path.Combine(_workspace, "second");
        Directory.CreateDirectory(second);
        File.WriteAllText(Path.Combine(_workspace, "a.mq4"), "int A = 1;\n");
        File.WriteAllText(Path.Combine(second, "b.mq4"), "int B = 2;\n");
        File.WriteAllText(Path.Combine(second, "c.mq4"), "int C = 3;\n");

        var percentages = new List<int?>();
        var indexer = CreateIndexer();
        indexer.ScanProgressNotifier = (pct, _) => percentages.Add(pct);
        indexer.StartIndexingAndWaitForIdle(new[] { _workspace, second });

        Assert.NotEmpty(percentages);
        Assert.Equal(0, percentages[0]);
        Assert.Equal(100, percentages[^1]);
        Assert.All(percentages, p => Assert.InRange(p!.Value, 0, 100));
    }

    [Fact]
    public void ScanProgress_NotifierThrowing_DoesNotBreakScan()
    {
        File.WriteAllText(Path.Combine(_workspace, "x.mq4"), "int X = 1;\n");

        var indexer = CreateIndexer();
        indexer.ScanProgressNotifier = (_, _) => throw new InvalidOperationException("client gone");
        indexer.StartIndexingAndWaitForIdle(new[] { _workspace });

        // The scan completed despite the notifier throwing (best-effort rule).
        Assert.True(_index.FindOccurrences("X").Count >= 1, "File was not indexed.");
    }
}
