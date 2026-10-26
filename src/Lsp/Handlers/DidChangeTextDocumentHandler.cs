using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MediatR;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for didChange text document notification
/// </summary>
public class DidChangeTextDocumentHandler : LanguageAwareHandlerBase<DidChangeTextDocumentParams, Unit>, IDidChangeTextDocumentHandler
{
    private readonly ILogger<DidChangeTextDocumentHandler> _logger;
    private readonly DiagnosticPublisher? _diagnosticPublisher;

    public DidChangeTextDocumentHandler(
        ILogger<DidChangeTextDocumentHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore openFiles,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null,
        DiagnosticPublisher? diagnosticPublisher = null)
        : base(languageService, openFiles, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnosticPublisher = diagnosticPublisher;
        _logger.LogInformation("DidChangeTextDocumentHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DidChangeTextDocumentHandler(
        ILogger<DidChangeTextDocumentHandler> logger,
        Mql4AntlrParser parser,
        OpenDocumentStore openFiles,
        GlobalSymbolIndex globalSymbolIndex)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               openFiles,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() },
               new GlobalSymbolIndexAccessor(globalSymbolIndex))
    {
    }

    public TextDocumentChangeRegistrationOptions GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentChangeRegistrationOptions
        {
            SyncKind = TextDocumentSyncKind.Full,
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }

    public Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Task.FromResult(HandleCore(request, cancellationToken));
    }

    /// <summary>
    /// Issue #116: synchronous forwarding entry point used by the built-in
    /// TextDocumentSync handler registered in Program.cs. OmniSharp 0.19.9
    /// routes each textDocument/didChange notification to EXACTLY ONE of the two
    /// handlers registered for the method, and which one wins is decided per
    /// process; this exposes the same synchronous core so the real logic runs
    /// regardless of routing (see DidOpenTextDocumentHandler.HandleSync).
    /// </summary>
    public void HandleSync(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        HandleCore(request, cancellationToken);
    }

    private Unit HandleCore(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return HandleForLanguage(request, language, cancellationToken);
    }

    protected override Unit HandleForLanguage(DidChangeTextDocumentParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri.ToUri();
            var changes = request.ContentChanges;

            _logger.LogDebug("Processing document changes for: {DocumentUri} ({Language})", documentUri, language);

            var newContent = GetFullContent(changes);

            if (!string.IsNullOrEmpty(newContent))
            {
                // Issue #36: the document is still open here, so check the
                // live entry only (no LRU involvement on didChange). When the
                // content is unchanged since the last parse, the live model is
                // already indexed and current: skip the redundant full parse.
                if (_documentStore.TryGetValue(documentUri, out var existingFile, out var existingContent, out var existingLanguage) &&
                    existingLanguage == language &&
                    string.Equals(existingContent, newContent, StringComparison.Ordinal))
                {
                    _logger.LogDebug("Skipping didChange re-parse for {DocumentUri}: content unchanged", documentUri);
                    return Unit.Value;
                }

                var filePath = documentUri.AbsolutePath ?? "unknown";

                var parser = ResolveParser(language);
                // Issue #20: propagate the request's CancellationToken into
                // the parse so pathological input can be aborted between
                // pre-scan / lexing / recursive-descent phases.
                var newMqlFile = parser.ParseFile(newContent, filePath, cancellationToken);

                _documentStore.AddOrUpdate(documentUri, newMqlFile, newContent, language);

                // OCC-03: re-index with the fresh parse's token occurrences so
                // the wholesale per-file occurrence replacement swaps old for
                // new instead of purging scan-indexed entries with an empty
                // list (CRITICAL-2 fix; identical mapping to the workspace scan).
                SymbolIndex.Index.AddFile(
                    filePath, language, newMqlFile.Symbols,
                    SymbolOccurrenceMapper.Map(newMqlFile, filePath, language));

                UpdateIncludes(newMqlFile, filePath);

                _logger.LogDebug("Re-parsed {SymbolCount} symbols after document change", newMqlFile.Symbols.Count);

                // Issue #91: push-model diagnostics mirror of the didOpen path —
                // publish the fresh state (including an empty set, so clients clear
                // stale squiggles after a fix). Null in legacy/test constructors.
                _diagnosticPublisher?.Publish(
                    documentUri, filePath, newMqlFile, newContent, language, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling didChange for {Uri}", request.TextDocument.Uri);
        }

        return Unit.Value;
    }

    private string GetFullContent(Container<TextDocumentContentChangeEvent> changes)
    {
        var changeList = changes.ToArray();
        if (changeList.Length > 0)
        {
            var lastChange = changeList[changeList.Length - 1];
            if (lastChange.Text != null)
            {
                return lastChange.Text;
            }
        }
        return string.Empty;
    }

    private void UpdateIncludes(MqlFile file, string filePath)
    {
        // Issue #25a: the previous copy re-ran the raw-directive include regex
        // over the visitors' already-extracted stored entries, which never
        // matched. Path resolution and indexing for includes is delegated to
        // DidOpen / workspace scan, so the loop body stays empty — kept as an
        // explicit delegation note (see DidOpenTextDocumentHandler).
        foreach (var _ in file.Includes)
        {
            // Path resolution and indexing delegated to DidOpen / workspace scan.
        }
    }
}
