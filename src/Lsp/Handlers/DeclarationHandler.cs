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
/// Handles textDocument/declaration requests.
/// </summary>
public class DeclarationHandler : LanguageAwareHandlerBase<DeclarationParams, LocationOrLocationLinks?>, IDeclarationHandler
{
    private readonly ILogger<DeclarationHandler> _logger;

    public DeclarationHandler(
        ILogger<DeclarationHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("DeclarationHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DeclarationHandler(ILogger<DeclarationHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<LocationOrLocationLinks?> Handle(DeclarationParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override LocationOrLocationLinks? HandleForLanguage(DeclarationParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing declaration request for: {DocumentUri} ({Language})", documentUri, language);

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

            var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character);

            if (symbol == null)
            {
                // Issue #92: the cursor may sit on a call site of a function declared
                // in an included file. In-file lookup finds nothing; resolve the
                // identifier against the global index, whose candidate symbols carry
                // the declaration range in the included file.
                symbol = IncludeSymbolResolver.TryResolve(mqlFile, line - 1, character - 1, SymbolIndex.Index);
            }

            if (symbol == null)
            {
                _logger.LogDebug("No symbol found at position {Line}:{Character}", line, character);
                return null;
            }

            // Issue #92: an include-declared symbol (resolved via the global
            // index fallback above) carries the included file's path on its
            // FilePath; point the location there instead of the requesting
            // document. Same-file symbols keep the request URI.
            var declarationUri = !string.IsNullOrEmpty(symbol.FilePath)
                && !string.Equals(symbol.FilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(symbol.FilePath)
                    ? DocumentUri.File(symbol.FilePath)
                    : documentUri;

            var location = new Location
            {
                Uri = declarationUri,
                Range = symbol.Range
            };

            _logger.LogDebug("Found declaration for symbol '{SymbolName}' at {Range}",
                symbol.Name, location.Range);

            return new LocationOrLocationLinks(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing declaration request for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public DeclarationRegistrationOptions GetRegistrationOptions(DeclarationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DeclarationRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
