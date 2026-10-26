using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for hover requests (display symbol information on mouse hover)
/// </summary>
public class HoverHandler : LanguageAwareHandlerBase<HoverParams, Hover?>, IHoverHandler
{
    private readonly ILogger<HoverHandler> _logger;

    public HoverHandler(
        ILogger<HoverHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("HoverHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public HoverHandler(ILogger<HoverHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override Hover? HandleForLanguage(HoverParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N")[..8];
        _logger.LogDebug("[{CorrelationId}] Processing hover request at position {Line}:{Character}",
            correlationId, request.Position.Line, request.Position.Character);

        try
        {
            if (request.TextDocument == null || request.TextDocument.Uri == null!)
            {
                _logger.LogWarning("[{CorrelationId}] Invalid request: documentUri is null", correlationId);
                return null;
            }

            var documentUri = request.TextDocument.Uri;
            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("[{CorrelationId}] Invalid file path from URI: {DocumentUri}", correlationId, documentUri);
                return null;
            }

            var uri = documentUri.ToUri();
            var parser = ResolveParser(language);
            var builtins = ResolveBuiltins(language);

            string content;
            MqlFile? mqlFile;
            try
            {
                // Issue #88 (the #86 pattern): store-first — the stored
                // content is the exact text the stored model was parsed from
                // (the editor buffer); disk text can be stale for unsaved
                // edits, desynchronizing hover resolution until a restart.
                TryGetDocumentContent(uri, filePath, parser, language, out mqlFile, out content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Failed to read file: {FilePath}", correlationId, filePath);
                return null;
            }

            var line = request.Position.Line + 1;
            var character = request.Position.Character + 1;

            if (line < 1 || character < 1)
            {
                _logger.LogWarning("[{CorrelationId}] Invalid position: line={Line}, character={Character}", correlationId, line, character);
                return null;
            }

            MqlSymbol? symbol;
            try
            {
                symbol = parser.FindSymbolAtPosition(mqlFile!, line, character);

                // Issue #92: FindSymbolAtPosition tends to return the containing declaration (e.g.
                // the enclosing function) when the cursor is inside a function body. Try
                // FindSymbolDefinition as a refinement: it extracts the exact identifier
                // at the position and resolves builtins/references more precisely. Only
                // override when FindSymbolDefinition finds something different.
                if (symbol != null
                    && (symbol.Kind == SymbolKind.Function || symbol.Kind == SymbolKind.Method))
                {
                    var refined = parser.FindSymbolDefinition(mqlFile!, content, line, character);
                    if (refined != null && refined.Name != symbol.Name)
                    {
                        symbol = refined;
                    }
                    else if (refined == null)
                    {
                        // Issue #92: the identifier at the cursor may resolve into a
                        // symbol declared in an included file (e.g. a call site of
                        // StopLong declared in "stop_utils.mqh"). In-file lookup found
                        // nothing, so the refinement above keeps the enclosing function —
                        // resolve the cursor identifier against the global index, which
                        // didOpen's include resolution keeps populated.
                        var includeSymbol = IncludeSymbolResolver.TryResolve(
                            mqlFile!, line - 1, character - 1, SymbolIndex.Index);
                        if (includeSymbol != null && includeSymbol.Name != symbol.Name)
                        {
                            symbol = includeSymbol;
                        }
                    }
                }
                symbol ??= parser.FindSymbolDefinition(mqlFile!, content, line, character);
                // Issue #92: same fallback when no containing symbol was found at all.
                symbol ??= IncludeSymbolResolver.TryResolve(mqlFile!, line - 1, character - 1, SymbolIndex.Index);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Error finding symbol at position {Line}:{Character}", correlationId, line, character);
                return null;
            }

            // Issue #180: the builtin registry documents tokens, not only resolved
            // symbols. When the identifier under the cursor is a registered builtin
            // of this dialect but resolution produced a different symbol (a covering
            // declaration of non-function kind) or nothing at all, answer from the
            // registry instead of returning the wrong symbol or null. User symbols
            // named exactly like the token keep the existing resolution path.
            // Issue #182: the comparison is case-insensitive, matching the parsers'
            // own lookup — a user declaration named "Ask" also covers a use spelled
            // "ask" and must not be hijacked by the registry.
            var token = ExtractIdentifierAt(content, line - 1, character - 1);
            if (!string.IsNullOrEmpty(token)
                && builtins.IsBuiltin(token)
                && (symbol == null || !string.Equals(symbol.Name, token, StringComparison.OrdinalIgnoreCase)))
            {
                return BuildBuiltinHover(token, builtins, language, line - 1, character - 1);
            }

            if (symbol == null)
            {
                _logger.LogDebug("[{CorrelationId}] No symbol found at position {Line}:{Character}", correlationId, line, character);
                return null;
            }

            try
            {
                // Issue #182: only pseudo-symbols synthesized from the registry are
                // "predefined". A declaration that happens to share a builtin name
                // keeps its non-empty FilePath and is rendered as user defined, so
                // a shadowing local never inherits the builtin type/signature text.
                var isPredefined = builtins.IsBuiltin(symbol.Name) && string.IsNullOrEmpty(symbol.FilePath);
                var symbolType = isPredefined ? $"MQL{(language == MqlLanguage.Mql5 ? "5" : "4")} Built-in" : "User Defined";

                // Issue #182: registry-provided signature/description text belongs to
                // builtin tokens only; a user declaration that shadows a builtin name
                // keeps its own detail and never inherits the registry prose.
                var signature = isPredefined ? builtins.GetBuiltinFunctionSignature(symbol.Name) : null;

                // Issue #180: surface the registered variable description for
                // builtin variables; the legacy pseudo-symbol path resolved them
                // with Kind=Function and never read the variable descriptions.
                // Issue #182: a user declaration that shadows a builtin name must
                // not inherit the registry's variable description either.
                var isBuiltinVariable = isPredefined && !builtins.IsBuiltinFunction(symbol.Name);
                var builtinDescription = isPredefined ? builtins.GetBuiltinVariableDescription(symbol.Name) : null;
                var markdownContent = BuildHoverContent(symbol, signature, isPredefined, symbolType, language, builtinDescription, isBuiltinVariable);

                var hover = new Hover
                {
                    Contents = new MarkedStringsOrMarkupContent(
                        new MarkupContent { Kind = MarkupKind.Markdown, Value = markdownContent.ToString() }
                    ),
                    Range = symbol.Range
                };

                _logger.LogDebug("[{CorrelationId}] Successfully created hover for symbol: {SymbolName}", correlationId, symbol.Name);
                return hover;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{CorrelationId}] Error building hover content for symbol: {SymbolName}", correlationId, symbol?.Name ?? "unknown");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[{CorrelationId}] Hover request cancelled", correlationId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{CorrelationId}] Unexpected error processing hover request", correlationId);
            return null;
        }
    }

    private StringBuilder BuildHoverContent(MqlSymbol symbol, string? signature, bool isPredefined, string symbolType, MqlLanguage language,
        string? builtinDescription = null, bool isBuiltinVariable = false)
    {
        var markdownContent = new StringBuilder();
        markdownContent.AppendLine($"## {symbol.Name}");

        if (!string.IsNullOrEmpty(signature))
        {
            var fence = language == MqlLanguage.Mql5 ? "mql5" : "mql4";
            markdownContent.AppendLine($"```{fence}");
            markdownContent.AppendLine(signature);
            markdownContent.AppendLine("```");
            markdownContent.AppendLine();
        }

        markdownContent.AppendLine($"**Type:** {symbolType}");
        markdownContent.AppendLine($"**Kind:** {(isBuiltinVariable ? SymbolKind.Variable : symbol.Kind)}");

        if (!string.IsNullOrEmpty(builtinDescription))
        {
            markdownContent.AppendLine($"**Description:** {builtinDescription}");
        }

        if (!string.IsNullOrEmpty(symbol.Detail))
        {
            markdownContent.AppendLine($"**Detail:** {symbol.Detail}");
        }

        if (!isPredefined && !string.IsNullOrEmpty(symbol.FilePath))
        {
            var relativePath = Path.GetFileName(symbol.FilePath);
            markdownContent.AppendLine($"**File:** {relativePath}");
        }

        if (isPredefined && !string.IsNullOrEmpty(signature))
        {
            markdownContent.AppendLine();
            markdownContent.AppendLine("### Example Usage");

            var example = GetExampleUsage(symbol.Name, language);
            if (!string.IsNullOrEmpty(example))
            {
                var fence = language == MqlLanguage.Mql5 ? "mql5" : "mql4";
                markdownContent.AppendLine($"```{fence}");
                markdownContent.AppendLine(example);
                markdownContent.AppendLine("```");
            }
        }

        return markdownContent;
    }

    /// <summary>
    /// Issue #180: build a hover for a registered builtin token straight from
    /// the dialect registry — signature for functions, description for
    /// variables — with the range covering the identifier occurrence under
    /// the cursor. Formatting mirrors the resolved-symbol builtin path.
    /// </summary>
    private Hover BuildBuiltinHover(string token, IMqlBuiltins builtins, MqlLanguage language, int zeroBasedLine, int zeroBasedCharacter)
    {
        var symbolType = $"MQL{(language == MqlLanguage.Mql5 ? "5" : "4")} Built-in";
        var isFunction = builtins.IsBuiltinFunction(token);
        var signature = builtins.GetBuiltinFunctionSignature(token);
        var description = builtins.GetBuiltinVariableDescription(token);
        var range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
            new Position(zeroBasedLine, zeroBasedCharacter),
            new Position(zeroBasedLine, zeroBasedCharacter + token.Length));

        var pseudoSymbol = new MqlSymbol
        {
            Name = token,
            Kind = isFunction ? SymbolKind.Function : SymbolKind.Variable,
            Detail = symbolType,
            Range = range
        };

        var markdownContent = BuildHoverContent(
            pseudoSymbol, signature, isPredefined: true, symbolType, language,
            description, isBuiltinVariable: !isFunction);

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(
                new MarkupContent { Kind = MarkupKind.Markdown, Value = markdownContent.ToString() }
            ),
            Range = range
        };
    }

    /// <summary>
    /// Issue #180: extract the identifier token covering the cursor position
    /// (0-based line/character), tolerating a cursor on the token boundary or
    /// on the whitespace right after it. Returns null when no identifier is
    /// under the cursor.
    /// </summary>
    private static string? ExtractIdentifierAt(string content, int zeroBasedLine, int zeroBasedCharacter)
    {
        if (string.IsNullOrEmpty(content) || zeroBasedLine < 0 || zeroBasedCharacter < 0)
        {
            return null;
        }

        var lines = content.Split('\n');
        if (zeroBasedLine >= lines.Length)
        {
            return null;
        }

        var text = lines[zeroBasedLine].TrimEnd('\r');
        if (text.Length == 0)
        {
            return null;
        }

        var index = Math.Min(zeroBasedCharacter, text.Length - 1);
        if (!IsIdentifierCharacter(text[index]))
        {
            if (index == 0 || !IsIdentifierCharacter(text[index - 1]))
            {
                return null;
            }

            index--;
        }

        var start = index;
        while (start > 0 && IsIdentifierCharacter(text[start - 1]))
        {
            start--;
        }

        var end = index;
        while (end < text.Length - 1 && IsIdentifierCharacter(text[end + 1]))
        {
            end++;
        }

        return text.Substring(start, end - start + 1);
    }

    private static bool IsIdentifierCharacter(char c) => char.IsLetterOrDigit(c) || c == '_';

    private string GetExampleUsage(string functionName, MqlLanguage language)
    {
        if (language == MqlLanguage.Mql5)
        {
            return functionName.ToLowerInvariant() switch
            {
                "ordersend" => @"MqlTradeRequest request;
MqlTradeResult result;
request.action = TRADE_ACTION_DEAL;
request.symbol = _Symbol;
request.volume = 0.1;
request.type = ORDER_TYPE_BUY;
OrderSend(request, result);",
                "positiongetsymbol" => @"if(PositionSelect(_Symbol))
{
   long ticket = PositionGetInteger(POSITION_TICKET);
}",
                "print" => @"Print(""Message: "", _Symbol, "" Price: "", Ask);",
                "oninit" => @"int OnInit()
{
    Print(""EA initialized"");
    return(INIT_SUCCEEDED);
}",
                "ontick" => @"void OnTick()
{
    if(Bars < 100) return;
}",
                _ => string.Empty
            };
        }

        return functionName.ToLowerInvariant() switch
        {
            "ordersend" => @"// Market Order
OrderSend(Symbol(), OP_BUY, 1.0, Ask, 3, 0, 0, ""comment"", 0, 0, clrBlue);

// Limit Order
OrderSend(Symbol(), OP_BUYLIMIT, 1.0, Ask * 1.01, 3, 0, 0, ""limit"", 0, 0, clrGreen);",
            "print" => @"Print(""Message: "", Symbol(), "" Price: "", Bid);",
            "ask" => @"double currentPrice = Ask; // Current Ask price
Print(""Ask price is: "", Ask);",
            "bid" => @"double currentPrice = Bid; // Current Bid price
Print(""Bid price is: "", Bid);",
            "oninit" => @"int OnInit()
{
    Print(""EA initialized"");
    return(INIT_SUCCEEDED);
}",
            "ontick" => @"void OnTick()
{
    if(Bars < 100) return;
    // Trading logic here
}",
            "comment" => @"void OnTick()
{
    Comment(""Ask: "", Ask, ""\n"", ""Bid: "", Bid);
}",
            "sleep" => @"Sleep(5000); // Sleep for 5 seconds",
            _ => string.Empty
        };
    }

    public HoverRegistrationOptions GetRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
