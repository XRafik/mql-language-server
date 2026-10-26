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
/// Handles textDocument/implementation requests.
/// </summary>
public class ImplementationHandler : LanguageAwareHandlerBase<ImplementationParams, LocationOrLocationLinks?>, IImplementationHandler
{
    private readonly ILogger<ImplementationHandler> _logger;

    public ImplementationHandler(
        ILogger<ImplementationHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("ImplementationHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public ImplementationHandler(ILogger<ImplementationHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<LocationOrLocationLinks?> Handle(ImplementationParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override LocationOrLocationLinks? HandleForLanguage(ImplementationParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing implementation request for: {DocumentUri} ({Language})", documentUri, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
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
            // inside a function body resolves to the containing function instead of the
            // local variable or parameter the user is actually on (issue #55).
            var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character);

            if (symbol == null)
            {
                _logger.LogDebug("No symbol found at position {Line}:{Character}", line, character);
                return null;
            }

            var implementations = new List<Location>();

            foreach (var s in mqlFile.Symbols)
            {
                if (s.Name == symbol.Name && s.Kind == SymbolKind.Function)
                {
                    implementations.Add(new Location
                    {
                        Uri = documentUri,
                        Range = s.Range
                    });
                }
            }

            if (implementations.Count == 0)
            {
                _logger.LogDebug("No implementations found for symbol '{SymbolName}'", symbol.Name);
                return null;
            }

            if (implementations.Count == 1)
            {
                return new LocationOrLocationLinks(implementations[0]);
            }

            return new LocationOrLocationLinks(implementations.Select(l => new LocationOrLocationLink(new LocationLink
            {
                OriginSelectionRange = symbol.Range,
                TargetUri = l.Uri,
                TargetRange = l.Range
            })));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing implementation request for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public ImplementationRegistrationOptions GetRegistrationOptions(ImplementationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new ImplementationRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
