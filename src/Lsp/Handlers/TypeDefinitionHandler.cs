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
/// Handles textDocument/typeDefinition requests.
/// </summary>
public class TypeDefinitionHandler : LanguageAwareHandlerBase<TypeDefinitionParams, LocationOrLocationLinks?>, ITypeDefinitionHandler
{
    private readonly ILogger<TypeDefinitionHandler> _logger;

    public TypeDefinitionHandler(
        ILogger<TypeDefinitionHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null)
        : base(languageService, documentStore, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("TypeDefinitionHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public TypeDefinitionHandler(ILogger<TypeDefinitionHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<LocationOrLocationLinks?> Handle(TypeDefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override LocationOrLocationLinks? HandleForLanguage(TypeDefinitionParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing type definition request for: {DocumentUri} ({Language})", documentUri, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
            }

            var parser = ResolveParser(language);
            var uri = documentUri.ToUri();

            // Issue #86: prefer the open-document store over disk — the stored
            // content is the exact text the stored model was parsed from (the
            // editor buffer), while the disk file can be stale for unsaved edits.
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
                // Issue #86: a cursor on a type-usage identifier can find no
                // case-insensitive in-file declaration match (e.g. after a
                // buffer edit renamed the variable). Generalize the #64
                // exact-case correction to the no-symbol-found case:
                // GetCursorIdentifier is parse-time occurrence-index based
                // (model text), never raw disk text, so the #86
                // buffer-vs-disk divergence cannot poison it.
                var usageIdentifier = TypeDeclarationResolver.GetCursorIdentifier(mqlFile, line - 1, character - 1);
                if (!string.IsNullOrEmpty(usageIdentifier) &&
                    TypeDeclarationResolver.TryResolveTypeLocation(usageIdentifier, mqlFile, documentUri, SymbolIndex.Index, out var usageLocation))
                {
                    _logger.LogDebug("Resolved type usage '{Identifier}' at {Range}", usageIdentifier, usageLocation.Range);
                    return new LocationOrLocationLinks(usageLocation);
                }

                _logger.LogDebug("No symbol found at position {Line}:{Character}", line, character);
                return null;
            }

            // Issue #64: real type resolution. A variable (instance) resolves to
            // its declared class/struct/interface/enum — same-file first, then
            // cross-file via the workspace index (e.g. the included .mqh).
            Location? location = null;

            if (TypeDeclarationResolver.IsTypeCandidate(symbol))
            {
                // Cursor on a type name: its own declaration is the type definition.
                location = new Location
                {
                    Uri = documentUri,
                    Range = symbol.Range
                };
            }
            // Issue #64: trigger on DeclaredType presence, not SymbolType — the
            // MQL4 parser keeps SymbolType null (CCR-05 tolerance) while both
            // parsers capture DeclaredType for variables and parameters.
            else if (!string.IsNullOrEmpty(symbol.DeclaredType))
            {
                var typeName = TypeDeclarationResolver.NormalizeDeclaredType(symbol.DeclaredType);
                if (typeName != null &&
                    TypeDeclarationResolver.TryResolveTypeLocation(typeName, mqlFile, documentUri, SymbolIndex.Index, out var typeLocation))
                {
                    location = typeLocation;
                }
                else
                {
                    // Exact-case correction: the parser resolves case-insensitively,
                    // so a cursor on a class usage ("Person") can resolve to a
                    // same-file variable ("person"). Prefer a type symbol with the
                    // exact cursor name from the workspace index.
                    var cursorIdentifier = TypeDeclarationResolver.GetCursorIdentifier(mqlFile, line - 1, character - 1);
                    if (!string.IsNullOrEmpty(cursorIdentifier) &&
                        !string.Equals(cursorIdentifier, symbol.Name, StringComparison.Ordinal) &&
                        TypeDeclarationResolver.TryResolveTypeLocation(cursorIdentifier, mqlFile, documentUri, SymbolIndex.Index, out var exactType))
                    {
                        location = exactType;
                    }
                }
            }

            // Preserved behavior for functions/methods/builtins: the resolved
            // symbol's own declaration range in the current document.
            location ??= new Location
            {
                Uri = documentUri,
                Range = symbol.Range
            };

            _logger.LogDebug("Found type definition for symbol '{SymbolName}' at {Range}",
                symbol.Name, location.Range);

            return new LocationOrLocationLinks(location);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing type definition request for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public TypeDefinitionRegistrationOptions GetRegistrationOptions(TypeDefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeDefinitionRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
