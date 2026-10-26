using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.JsonRpc;
using OmniSharp.Extensions.JsonRpc.Server;
using OmniSharp.Extensions.LanguageServer.Server;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Replaces the library's <see cref="LspServerReceiver"/> pre-initialization
/// filter so messages batched in the same pipe read as the
/// <c>initialize</c> request are dispatched instead of silently dropped
/// (issue #160).
///
/// <para>OmniSharp 0.19.9's <see cref="LspServerReceiver"/> rejects every
/// pre-initialization request with a <c>ServerNotInitialized</c> (-32002)
/// error and drops every pre-initialization notification. The error is an
/// <c>ErrorMessage</c>, which <c>LspServerOutputFilter.ShouldOutput</c> does
/// not recognize as an <c>OutgoingResponse</c>, so it is swallowed with a
/// "will be sent later" log and NEVER reaches the client: the client sees a
/// silent drop and can only fall back to timeouts (exactly the agent-lsp
/// cold-start symptom from the issue). This matters even though issue #93
/// opened the receiver gate early: messages batched in the same OS pipe read
/// as <c>initialize</c> (agent MCP relays commonly pipeline
/// <c>didOpen</c> + queries right after the handshake) are parsed while the
/// gate is still closed.</para>
///
/// <para>The fix: before the gate opens, parse the batch with the plain
/// <see cref="Receiver"/> semantics — no LSP filtering. The input loop
/// dispatches the returned items in arrival order, so a batched request is
/// dispatched after the batched <c>initialize</c>/<c>initialized</c>, by
/// which point the initialization pipeline (handler registration) has run in
/// the same batch. After the gate opens this class adds nothing: behavior is
/// 100% the library's own (issue #93 semantics).</para>
///
/// <para>Spec note: LSP says pre-initialization requests should be answered
/// with -32002 — but the library then swallows that error, which is strictly
/// worse for the client (silence). Dispatching to the already-registered
/// handlers is the deterministic behavior the issue asks for.</para>
/// </summary>
public sealed class QueuingReceiver : LspServerReceiver
{
    public QueuingReceiver(ILoggerFactory loggerFactory)
        : base(loggerFactory.CreateLogger<LspServerReceiver>())
    {
    }

    public override (IEnumerable<Renor> results, bool hasResponse) GetRequests(JToken container)
    {
        if (_initialized)
        {
            // Gate open: the library handles everything (issue #93 semantics).
            return base.GetRequests(container);
        }

        // Gate closed: parse with the plain Receiver semantics — keep every
        // message (requests, notifications, responses, errors) in arrival
        // order instead of rejecting/dropping the non-lifecycle ones.
        var results = new List<Renor>();
        if (container is JArray array)
        {
            results.AddRange(array.Select(GetRenor));
        }
        else
        {
            results.Add(GetRenor(container));
        }

        return (results, results.Any(z => z.IsResponse));
    }
}
