using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handles textDocument/documentHighlight requests.
/// </summary>
public class DocumentHighlightHandler : LanguageAwareHandlerBase<DocumentHighlightParams, DocumentHighlightContainer?>, IDocumentHighlightHandler
{
    private readonly ILogger<DocumentHighlightHandler> _logger;

    public DocumentHighlightHandler(
        ILogger<DocumentHighlightHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("DocumentHighlightHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DocumentHighlightHandler(ILogger<DocumentHighlightHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<DocumentHighlightContainer?> Handle(DocumentHighlightParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override DocumentHighlightContainer? HandleForLanguage(DocumentHighlightParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing document highlight request for: {DocumentUri} ({Language})", documentUri, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return new DocumentHighlightContainer();
            }

            var parser = ResolveParser(language);
            var uri = documentUri.ToUri();
            // Issue #88 (the #86 pattern): store-first — the stored content is
            // the exact text the stored model was parsed from (the editor
            // buffer), while the disk file can be stale for unsaved edits.
            // Reading disk text against the fresh model desynchronized
            // position-based resolution until a restart (#86).
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);

            var line = request.Position.Line + 1;
            var character = request.Position.Character + 1;

            // Resolve the exact identifier under the cursor (ReferencesHandler/Rename
            // precedent). FindSymbolAtPosition matches by body containment, so a cursor
            // on a use inside a function body resolves to the containing function
            // instead of the local variable the user is actually highlighting (issue #55).
            var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character);
            if (symbol == null)
            {
                // Issue #92: the cursor may sit on a call site of a function declared
                // in an included file (e.g. StopLong from "stop_utils.mqh") — in-file
                // lookup finds nothing. Resolve the identifier against the global
                // index so the same-file textual-occurrence fallback below highlights
                // the call sites (kind Text) instead of returning an empty result.
                symbol = IncludeSymbolResolver.TryResolve(mqlFile, line - 1, character - 1, SymbolIndex.Index);
                if (symbol == null)
                {
                    return new DocumentHighlightContainer();
                }
            }

            var highlights = new List<DocumentHighlight>();

            foreach (var s in mqlFile.Symbols)
            {
                if (s.Name == symbol.Name && s.Range.Start.Line >= 0)
                {
                    DocumentHighlightKind kind;
                    if (s.Kind == SymbolKind.Function)
                        kind = DocumentHighlightKind.Write;
                    else if (s.Kind == SymbolKind.Variable)
                        kind = DocumentHighlightKind.Read;
                    else if (s.Kind == SymbolKind.Property)
                        kind = DocumentHighlightKind.Read;
                    else
                        kind = DocumentHighlightKind.Text;

                    highlights.Add(new DocumentHighlight
                    {
                        Range = s.Range,
                        Kind = kind
                    });
                }
            }

            // Builtin/event-handler fallback (#63): identifier-based resolution
            // (issue #55) returns a builtin pseudo-symbol (e.g. Print) or a name
            // that has no user symbols in this file (event handlers such as
            // OnInit). With zero symbol matches, highlight the same-file textual
            // occurrences of the identifier (kind Text) from the parse-time
            // occurrence index (OCC-01) instead of returning an empty result.
            if (highlights.Count == 0)
            {
                foreach (var o in mqlFile.Occurrences)
                {
                    if (string.Equals(o.Text, symbol.Name, StringComparison.OrdinalIgnoreCase)
                        && o.Line >= 0 && o.Column >= 0)
                    {
                        highlights.Add(new DocumentHighlight
                        {
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(o.Line, o.Column),
                                new Position(o.Line, o.Column + o.Length)),
                            Kind = DocumentHighlightKind.Text
                        });
                    }
                }
            }

            _logger.LogDebug("Found {Count} highlights for symbol '{SymbolName}'", highlights.Count, symbol.Name);

            return new DocumentHighlightContainer(highlights);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing document highlight request for {Uri}", request.TextDocument.Uri);
            return new DocumentHighlightContainer();
        }
    }

    public DocumentHighlightRegistrationOptions GetRegistrationOptions(DocumentHighlightCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentHighlightRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
