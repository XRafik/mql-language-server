using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// Handles textDocument/signatureHelp requests.
/// </summary>
public class SignatureHelpHandler : LanguageAwareHandlerBase<SignatureHelpParams, SignatureHelp?>, ISignatureHelpHandler
{
    private readonly ILogger<SignatureHelpHandler> _logger;

    public SignatureHelpHandler(
        ILogger<SignatureHelpHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("SignatureHelpHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public SignatureHelpHandler(ILogger<SignatureHelpHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override SignatureHelp? HandleForLanguage(SignatureHelpParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing signature help request for: {DocumentUri} ({Language})", documentUri, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
            }

            var parser = ResolveParser(language);
            var content = SourceFileReader.ReadAllText(filePath);
            var uri = documentUri.ToUri();

            MqlFile? mqlFile = null;
            if (!_documentStore.TryGetValue(uri, out mqlFile) || mqlFile == null)
            {
                mqlFile = parser.ParseFile(content, filePath);
                _documentStore.AddOrUpdate(uri, mqlFile, content, language);
            }

            var line = request.Position.Line + 1;
            var character = request.Position.Character + 1;

            // Signature help must return the signature of the CALLED function,
            // never the enclosing one (issue #77). Resolution order:
            //   1. Identifier under the cursor via FindSymbolDefinition
            //      (current-file symbols + builtins — preserves issue #55
            //      behavior for a cursor on a plain callee name).
            //   2. When step 1 misses (or is not callable), extract the dotted
            //      call-target chain from the source line (e.g. "person.Greet"
            //      or "Print") and resolve it: plain names go through the
            //      workspace index or the builtin registry; "receiver.member"
            //      resolves the receiver's declared type and looks the member
            //      up among that type's children.
            // The former FindSymbolAtPosition body-containment fallback was
            // removed: inside a call's argument list it resolved to the
            // ENCLOSING function, and returning a wrong signature is worse
            // than returning none — an unresolvable call target yields null.
            var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character);
            if (!IsCallable(symbol))
            {
                var callTarget = ExtractCallTarget(content, line, character);
                symbol = callTarget != null ? ResolveCallTarget(callTarget, parser, mqlFile, language) : null;
            }

            if (!IsCallable(symbol))
            {
                _logger.LogDebug("No callable call target for signature help at {Line}:{Character}", line, character);
                return null;
            }

            var signature = new SignatureInformation
            {
                // Issue #77: the label must identify the callee. Detail alone
                // (e.g. "Function returning string") omits the member name, so
                // prefix it; fall back to the bare name when no detail exists.
                Label = string.IsNullOrEmpty(symbol.Detail) ? symbol.Name : $"{symbol.Name}: {symbol.Detail}",
                Documentation = new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = symbol.Detail ?? $"Function {symbol.Name}"
                },
                Parameters = new List<ParameterInformation>().ToArray()
            };

            var signatureHelp = new SignatureHelp
            {
                Signatures = new[] { signature },
                ActiveSignature = 0,
                ActiveParameter = 0
            };

            _logger.LogDebug("Returning signature help for: {SymbolName}", symbol.Name);

            return signatureHelp;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing signature help for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    /// <summary>
    /// True when the symbol is something the cursor could be calling:
    /// a free function or a class method (issue #77).
    /// </summary>
    private static bool IsCallable([NotNullWhen(true)] MqlSymbol? symbol) =>
        symbol != null &&
        (symbol.Kind == SymbolKind.Function || symbol.Kind == SymbolKind.Method);

    private const int MaxCallTargetSegments = 3;

    /// <summary>Dotted identifier chain: a.b.c — each segment a valid identifier.</summary>
    private static readonly Regex CallTargetPattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    /// <summary>
    /// Extract the dotted call-target chain (e.g. "person.Greet") whose open
    /// paren the cursor is on or inside, from the cursor's source line.
    /// Returns null when no such chain can be located. <paramref name="line"/>
    /// and <paramref name="character"/> are the 1-based request coordinates.
    /// </summary>
    private static string? ExtractCallTarget(string content, int line, int character)
    {
        var lines = content.Split('\n');
        var line0 = line - 1;
        if (line0 < 0 || line0 >= lines.Length)
        {
            return null;
        }

        var text = lines[line0];
        var col0 = Math.Min(character - 1, text.Length);

        // Callee end: the index just past the callee's last character.
        int calleeEnd;
        if (col0 < text.Length && IsIdentifierChar(text[col0]))
        {
            var identifierEnd = col0;
            while (identifierEnd < text.Length && IsIdentifierChar(text[identifierEnd]))
            {
                identifierEnd++;
            }

            var next = identifierEnd;
            while (next < text.Length && char.IsWhiteSpace(text[next]))
            {
                next++;
            }

            calleeEnd = next < text.Length && text[next] == '('
                ? identifierEnd
                : FindCallOpenParen(text, col0);
        }
        else
        {
            calleeEnd = FindCallOpenParen(text, col0);
        }

        if (calleeEnd <= 0)
        {
            return null;
        }

        // Walk backwards over the dotted identifier chain ending at calleeEnd.
        var chainStart = calleeEnd;
        while (chainStart > 0 && IsChainChar(text[chainStart - 1]))
        {
            chainStart--;
        }

        var chain = text[chainStart..calleeEnd];
        if (chain.Length == 0 || chain.Count(c => c == '.') + 1 > MaxCallTargetSegments)
        {
            return null;
        }

        return CallTargetPattern.IsMatch(chain) ? chain : null;
    }

    /// <summary>
    /// Index of the callee end for the nearest '(' at or left of <paramref
    /// name="col0"/> (whitespace between callee and paren is skipped), or -1.
    /// </summary>
    private static int FindCallOpenParen(string text, int col0)
    {
        var paren = Math.Min(col0, text.Length - 1);
        while (paren >= 0 && text[paren] != '(')
        {
            paren--;
        }

        if (paren < 0)
        {
            return -1;
        }

        var end = paren;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end;
    }

    private static bool IsIdentifierChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c == '_';

    private static bool IsChainChar(char c) =>
        IsIdentifierChar(c) || c == '.';

    /// <summary>
    /// Resolve an extracted call-target chain to a callable symbol, or null.
    /// Single segments resolve through the workspace index and the builtin
    /// registry; "receiver.member" resolves through the receiver's declared
    /// type. Longer chains are unsupported (issue #77).
    /// </summary>
    private MqlSymbol? ResolveCallTarget(string callTarget, IMqlParser parser, MqlFile mqlFile, MqlLanguage language)
    {
        var segments = callTarget.Split('.');
        return segments.Length switch
        {
            1 => ResolvePlainCallTarget(segments[0], parser, language),
            2 => ResolveMemberCallTarget(segments[0], segments[1], parser, mqlFile),
            _ => null
        };
    }

    /// <summary>
    /// Resolve a plain (unqualified) callee name: workspace-indexed definition
    /// first (with declaration-shape validation), builtin pseudo-symbol second.
    /// </summary>
    private MqlSymbol? ResolvePlainCallTarget(string name, IMqlParser parser, MqlLanguage language)
    {
        foreach (var candidate in SymbolIndex.Index.FindSymbol(name))
        {
            if (!IsCallable(candidate.Symbol) ||
                string.IsNullOrEmpty(candidate.FilePath) ||
                !File.Exists(candidate.FilePath) ||
                !ValidatesAsDeclaration(candidate, name))
            {
                continue;
            }

            return candidate.Symbol;
        }

        if (parser.IsBuiltin(name))
        {
            // Builtin pseudo-symbol: makes the "(" and "," trigger characters
            // useful for builtin calls (issue #77).
            var builtins = ResolveBuiltins(language);
            return new MqlSymbol
            {
                Name = name,
                Kind = SymbolKind.Function,
                Detail = builtins.GetBuiltinFunctionSignature(name)
                    ?? $"MQL{(language == MqlLanguage.Mql5 ? "5" : "4")} Built-in"
            };
        }

        return null;
    }

    /// <summary>
    /// Declaration-shape validation mirroring DefinitionHandler: the indexed
    /// symbol's line must start with the name followed by an optional space
    /// and an open paren or brace, so occurrences of the same name cannot
    /// satisfy a call-target lookup.
    /// </summary>
    private static bool ValidatesAsDeclaration(SymbolLocation candidate, string name)
    {
        try
        {
            var content = SourceFileReader.ReadAllText(candidate.FilePath);
            var lines = content.Split('\n');
            var defLine = candidate.Symbol!.Range.Start.Line;
            var defChar = candidate.Symbol.Range.Start.Character;

            if (defLine < 0 || defLine >= lines.Length)
            {
                return false;
            }

            var lineText = lines[defLine];
            if (defChar < 0 || defChar >= lineText.Length)
            {
                return false;
            }

            return Regex.IsMatch(lineText[defChar..], @"^\b" + Regex.Escape(name) + @"\b\s*[({]");
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolve "receiver.member" to a callable method of the receiver's
    /// declared type, or null when the receiver or type cannot be resolved.
    /// </summary>
    private MqlSymbol? ResolveMemberCallTarget(string receiverName, string memberName, IMqlParser parser, MqlFile mqlFile)
    {
        var receiver = parser.FindSymbolsByName(mqlFile, receiverName)
            .FirstOrDefault(s => !string.IsNullOrEmpty(s.DeclaredType));
        if (receiver == null)
        {
            return null;
        }

        var typeName = TypeDeclarationResolver.NormalizeDeclaredType(receiver.DeclaredType);
        if (typeName == null)
        {
            // Primitive receivers have no user type to look members up in.
            return null;
        }

        var typeSymbol = ResolveTypeSymbol(typeName, mqlFile);
        if (typeSymbol == null)
        {
            return null;
        }

        return typeSymbol.Children.FirstOrDefault(c =>
                string.Equals(c.Name, memberName, StringComparison.Ordinal) && IsCallable(c))
            ?? typeSymbol.Children.FirstOrDefault(c =>
                string.Equals(c.Name, memberName, StringComparison.OrdinalIgnoreCase) && IsCallable(c));
    }

    /// <summary>
    /// Locate a type symbol by name: same-file (exact, then case-insensitive)
    /// first, then the workspace index — mirroring
    /// TypeDeclarationResolver.TryResolveTypeLocation but returning the symbol
    /// itself so class members remain reachable (issue #77).
    /// </summary>
    private MqlSymbol? ResolveTypeSymbol(string typeName, MqlFile mqlFile)
    {
        var sameFile = mqlFile.Symbols.FirstOrDefault(s =>
                string.Equals(s.Name, typeName, StringComparison.Ordinal) && TypeDeclarationResolver.IsTypeCandidate(s))
            ?? mqlFile.Symbols.FirstOrDefault(s =>
                string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase) && TypeDeclarationResolver.IsTypeCandidate(s));

        if (sameFile != null)
        {
            return sameFile;
        }

        foreach (var candidate in SymbolIndex.Index.FindSymbol(typeName))
        {
            if (!TypeDeclarationResolver.IsTypeCandidate(candidate.Symbol) ||
                string.IsNullOrEmpty(candidate.FilePath) ||
                !File.Exists(candidate.FilePath))
            {
                continue;
            }

            return candidate.Symbol;
        }

        return null;
    }

    public SignatureHelpRegistrationOptions GetRegistrationOptions(SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector(),
            TriggerCharacters = new[] { "(", "," }
        };
    }
}
