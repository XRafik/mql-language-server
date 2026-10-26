using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for definition requests (go-to-definition)
/// </summary>
public class DefinitionHandler : LanguageAwareHandlerBase<DefinitionParams, LocationOrLocationLinks?>, IDefinitionHandler
{
    private readonly ILogger<DefinitionHandler> _logger;

    public DefinitionHandler(
        ILogger<DefinitionHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null)
        : base(languageService, documentStore, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("DefinitionHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DefinitionHandler(
        ILogger<DefinitionHandler> logger,
        Mql4AntlrParser parser,
        OpenDocumentStore documentStore,
        GlobalSymbolIndex globalSymbolIndex)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() },
               new GlobalSymbolIndexAccessor(globalSymbolIndex))
    {
    }

    public Task<LocationOrLocationLinks?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override LocationOrLocationLinks? HandleForLanguage(DefinitionParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing definition request for: {DocumentUri} ({Language})", documentUri, language);

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
            // The cross-file target read below stays on disk: that file is
            // legitimately not the open document.
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);

            var line = request.Position.Line + 1;
            var character = request.Position.Character + 1;

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

                // Issue #92: call sites of include-declared functions. In-file lookup
                // (FindSymbolDefinition) searches only the requesting document's own
                // symbols, so a call to a function declared in an included file (e.g.
                // StopLong from "stop_utils.mqh") resolves to nothing above. Resolve
                // the cursor identifier against the global index — populated by
                // didOpen's include resolution and the workspace scan — and fall
                // through to the shared all-definitions validation loop below.
                symbol = IncludeSymbolResolver.TryResolve(mqlFile, line - 1, character - 1, SymbolIndex.Index);

                if (symbol == null)
                {
                    _logger.LogDebug("No symbol found at position {Line}:{Character}", line, character);
                    return null;
                }
            }

            // Issue #64: exact-case correction. The parser resolves the cursor
            // symbol case-insensitively, so a class-usage cursor ("Person") can
            // resolve to a same-file variable ("person"). When a type symbol with
            // the exact cursor name exists in the workspace index, prefer it.
            var cursorIdentifier = TypeDeclarationResolver.GetCursorIdentifier(mqlFile, line - 1, character - 1);
            if (!string.IsNullOrEmpty(cursorIdentifier) &&
                !string.Equals(cursorIdentifier, symbol.Name, StringComparison.Ordinal))
            {
                foreach (var exactCandidate in SymbolIndex.Index.FindSymbol(cursorIdentifier))
                {
                    if (TypeDeclarationResolver.IsTypeCandidate(exactCandidate.Symbol) &&
                        !string.IsNullOrEmpty(exactCandidate.FilePath) && File.Exists(exactCandidate.FilePath))
                    {
                        var exactLocation = new Location
                        {
                            Uri = DocumentUri.File(exactCandidate.FilePath),
                            Range = exactCandidate.Symbol!.Range
                        };
                        _logger.LogDebug("Exact-case type definition for '{Identifier}' at {Range}",
                            cursorIdentifier, exactLocation.Range);
                        return new LocationOrLocationLinks(exactLocation);
                    }
                }
            }

            var allDefinitions = SymbolIndex.Index.FindSymbol(symbol.Name);

            Location? definitionLocation = null;

            foreach (var defLocation in allDefinitions)
            {
                if (!File.Exists(defLocation.FilePath))
                {
                    continue;
                }

                // Type declarations carry no paren/brace requirement: a class or
                // struct candidate is accepted directly (issue #64); other symbols
                // keep the function-like declaration validation below.
                if (TypeDeclarationResolver.IsTypeCandidate(defLocation.Symbol))
                {
                    definitionLocation = new Location
                    {
                        Uri = DocumentUri.File(defLocation.FilePath),
                        Range = defLocation.Symbol!.Range
                    };
                    break;
                }

                try
                {
                    var defContent = SourceFileReader.ReadAllText(defLocation.FilePath);
                    var lines = defContent.Split('\n');
                    var defLine = defLocation.Symbol!.Range.Start.Line;
                    var defChar = defLocation.Symbol.Range.Start.Character;

                    if (defLine >= 0 && defLine < lines.Length &&
                        defChar >= 0 && defChar < lines[defLine].Length)
                    {
                        var lineText = lines[defLine];
                        var remainingText = lineText.Substring(defChar);

                        var match = System.Text.RegularExpressions.Regex.Match(
                            remainingText,
                            @"^\b" + System.Text.RegularExpressions.Regex.Escape(symbol.Name) + @"\b\s*[({]"
                        );

                        if (match.Success)
                        {
                            definitionLocation = new Location
                            {
                                Uri = DocumentUri.File(defLocation.FilePath),
                                Range = defLocation.Symbol.Range
                            };
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reading file for definition: {FilePath}", defLocation.FilePath);
                }
            }

            if (definitionLocation == null)
            {
                definitionLocation = new Location
                {
                    Uri = documentUri,
                    Range = symbol.Range
                };
            }

            _logger.LogDebug("Found definition for symbol '{SymbolName}' at {Range}",
                symbol.Name, definitionLocation.Range);

            return new LocationOrLocationLinks(definitionLocation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing definition request for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public DefinitionRegistrationOptions GetRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
