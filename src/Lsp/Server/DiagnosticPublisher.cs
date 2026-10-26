using System;
using System.Threading;

using Microsoft.Extensions.Logging;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Pushes document diagnostics over <c>textDocument/publishDiagnostics</c>
/// after didOpen/didChange (issue #91).
///
/// <para>The server declared the PULL model (<c>diagnosticProvider</c>) and
/// implemented <c>IDocumentDiagnosticHandler</c>, but push-only clients — the
/// majority of editors and both MCP servers evaluated in the issue — never
/// receive anything on the pull channel. This publisher reuses the shared
/// <see cref="DocumentDiagnostics"/> rule set (identical to the pull model's)
/// and publishes the current document state right after the parse that
/// didOpen/didChange already perform — no extra parse, and empty result
/// sets are published too so clients clear stale squiggles on fix.</para>
/// </summary>
public sealed class DiagnosticPublisher
{
    private readonly ILogger<DiagnosticPublisher> _logger;
    // The TextDocument facade (not ILanguageServer) is injected on purpose:
    // OmniSharp registers ILanguageServer with a resolution condition (only as
    // a resolution root, or once InstanceHasStarted.Started is true), so any
    // dependency on it here fails at handler-construction time and DryIo
    // silently falls back to the parameter default (null publisher). The
    // facade has no resolution condition and routes publishDiagnostics
    // through the IResponseRouter directly.
    private readonly ITextDocumentLanguageServer _textDocumentServer;
    private readonly SemanticAnalyzer? _semanticAnalyzer;
    private readonly GlobalSymbolIndexAccessor _symbolIndex;

    public DiagnosticPublisher(
        ILogger<DiagnosticPublisher> logger,
        ITextDocumentLanguageServer textDocumentServer,
        GlobalSymbolIndexAccessor? symbolIndex = null,
        SemanticAnalyzer? semanticAnalyzer = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _textDocumentServer = textDocumentServer ?? throw new ArgumentNullException(nameof(textDocumentServer));
        _symbolIndex = symbolIndex ?? new GlobalSymbolIndexAccessor();
        _semanticAnalyzer = semanticAnalyzer;
    }

    /// <summary>
    /// Generate and push diagnostics for one parsed document state.
    /// Best-effort by design: a publish failure is logged and swallowed —
    /// it must never break the didOpen/didChange notification handling.
    /// </summary>
    public void Publish(
        DocumentUri documentUri,
        string? documentPath,
        MqlFile? mqlFile,
        string content,
        MqlLanguage language,
        CancellationToken token)
    {
        try
        {
            var diagnostics = DocumentDiagnostics.Generate(
                _logger, mqlFile, content, language, token, documentPath,
                _semanticAnalyzer, _symbolIndex.Index);

            // An empty result set is published on purpose: LSP push semantics
            // require clearing previously shown diagnostics when the document
            // is now clean.
            _textDocumentServer.PublishDiagnostics(new PublishDiagnosticsParams
            {
                Uri = documentUri,
                Diagnostics = new Container<Diagnostic>(diagnostics)
            });

            _logger.LogDebug("Published {Count} diagnostics for {DocumentUri}", diagnostics.Count, documentUri);
        }
        catch (OperationCanceledException)
        {
            // The client went away / the request was cancelled: nothing to do.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to publish diagnostics for {DocumentUri}; push notification skipped", documentUri);
        }
    }
}