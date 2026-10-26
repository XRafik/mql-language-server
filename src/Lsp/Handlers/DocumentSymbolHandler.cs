using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Lsp.Server;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for document symbol requests (outline view)
/// </summary>
public class DocumentSymbolHandler : LanguageAwareHandlerBase<DocumentSymbolParams, SymbolInformationOrDocumentSymbolContainer?>, IDocumentSymbolHandler
{
    private readonly ILogger<DocumentSymbolHandler> _logger;

    public DocumentSymbolHandler(
        ILogger<DocumentSymbolHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("DocumentSymbolHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DocumentSymbolHandler(ILogger<DocumentSymbolHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<SymbolInformationOrDocumentSymbolContainer?> Handle(DocumentSymbolParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override SymbolInformationOrDocumentSymbolContainer? HandleForLanguage(DocumentSymbolParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing document symbols for: {DocumentUri} ({Language})", documentUri, language);

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

            var symbols = mqlFile.Symbols
                .Select(ConvertToSymbolInformationOrDocumentSymbol);

            _logger.LogDebug("Found {SymbolCount} symbols in {FilePath}", symbols.Count(), filePath);

            return new SymbolInformationOrDocumentSymbolContainer(symbols);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing document symbols for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    private SymbolInformationOrDocumentSymbol ConvertToSymbolInformationOrDocumentSymbol(MqlSymbol symbol)
    {
        return new SymbolInformationOrDocumentSymbol(new DocumentSymbol
        {
            Name = symbol.Name,
            Kind = symbol.Kind,
            Detail = symbol.Detail,
            Range = symbol.Range,
            SelectionRange = symbol.SelectionRange
        });
    }

    public DocumentSymbolRegistrationOptions GetRegistrationOptions(DocumentSymbolCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentSymbolRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
