using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Mql5Grammar;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace MqlLanguageServer.Mql5.Parser;

/// <summary>
/// ANTLR visitor that walks the Mql5Grammar parse tree and extracts MqlSymbol instances.
/// Mirrors Mql4SymbolVisitor structure and adds MQL5-specific hierarchy support.
/// </summary>
public class Mql5SymbolVisitor : Mql5GrammarBaseVisitor<MqlSymbol?>
{
    public List<MqlSymbol> Symbols { get; } = new List<MqlSymbol>();
    public List<string> Includes { get; } = new List<string>();

    private readonly string _filePath;
    private readonly Dictionary<string, MqlSymbol> _symbolsByName = new(StringComparer.OrdinalIgnoreCase);

    // REQ-SM-02: stack of enclosing class/struct/interface symbols while visiting their bodies.
    // Members discovered inside are attached as Children of the innermost enclosing type at
    // extraction time — never via Range containment (class Range spans only the name token).
    private readonly Stack<MqlSymbol> _currentTypeStack = new();

    public Mql5SymbolVisitor(string filePath)
    {
        _filePath = filePath ?? string.Empty;
    }

    public override MqlSymbol? VisitClassDeclaration([NotNull] Mql5GrammarParser.ClassDeclarationContext context)
    {
        var nameToken = context.IDENTIFIER()?.Symbol;
        if (nameToken == null)
            return base.VisitClassDeclaration(context);

        var name = nameToken.Text;
        var symbol = CreateTypeSymbol(name, nameToken, SymbolType.Class);

        if (context.accessModifier() != null && context.qualifiedName() != null)
        {
            var parentName = context.qualifiedName().GetText();
            symbol.Detail = $"class {name} : {parentName}";
            // Issue #123: structured capture for typeHierarchy supertypes.
            symbol.BaseClass = parentName;
        }

        Symbols.Add(symbol);
        _symbolsByName[name] = symbol;

        _currentTypeStack.Push(symbol);
        try
        {
            var result = base.VisitClassDeclaration(context);
            ResolveHierarchy();
            return result;
        }
        finally
        {
            _currentTypeStack.Pop();
        }
    }

    public override MqlSymbol? VisitStructDeclaration([NotNull] Mql5GrammarParser.StructDeclarationContext context)
    {
        var nameToken = context.IDENTIFIER()?.Symbol;
        if (nameToken == null)
            return base.VisitStructDeclaration(context);

        var name = nameToken.Text;
        var symbol = CreateTypeSymbol(name, nameToken, SymbolType.Struct);

        if (context.accessModifier() != null && context.qualifiedName() != null)
        {
            var parentName = context.qualifiedName().GetText();
            symbol.Detail = $"struct {name} : {parentName}";
            // Issue #123: structured capture for typeHierarchy supertypes.
            symbol.BaseClass = parentName;
        }

        Symbols.Add(symbol);
        _symbolsByName[name] = symbol;

        _currentTypeStack.Push(symbol);
        try
        {
            var result = base.VisitStructDeclaration(context);
            ResolveHierarchy();
            return result;
        }
        finally
        {
            _currentTypeStack.Pop();
        }
    }

    public override MqlSymbol? VisitInterfaceDeclaration([NotNull] Mql5GrammarParser.InterfaceDeclarationContext context)
    {
        var nameToken = context.IDENTIFIER()?.Symbol;
        if (nameToken == null)
            return base.VisitInterfaceDeclaration(context);

        var name = nameToken.Text;
        var symbol = CreateTypeSymbol(name, nameToken, SymbolType.Interface);

        if (context.accessModifier() != null && context.qualifiedName() != null)
        {
            var parentName = context.qualifiedName().GetText();
            symbol.Detail = $"interface {name} : {parentName}";
            // Issue #123: structured capture for typeHierarchy supertypes.
            symbol.BaseClass = parentName;
        }

        Symbols.Add(symbol);
        _symbolsByName[name] = symbol;

        _currentTypeStack.Push(symbol);
        try
        {
            var result = base.VisitInterfaceDeclaration(context);
            ResolveHierarchy();
            return result;
        }
        finally
        {
            _currentTypeStack.Pop();
        }
    }

    public override MqlSymbol? VisitUnionDeclaration([NotNull] Mql5GrammarParser.UnionDeclarationContext context)
    {
        var nameToken = context.IDENTIFIER()?.Symbol;
        if (nameToken == null)
            return base.VisitUnionDeclaration(context);

        var name = nameToken.Text;
        var symbol = CreateTypeSymbol(name, nameToken, SymbolType.Struct);
        symbol.Detail = $"union {name}";

        Symbols.Add(symbol);
        _symbolsByName[name] = symbol;

        _currentTypeStack.Push(symbol);
        try
        {
            return base.VisitUnionDeclaration(context);
        }
        finally
        {
            _currentTypeStack.Pop();
        }
    }

    public override MqlSymbol? VisitEnumDeclaration([NotNull] Mql5GrammarParser.EnumDeclarationContext context)
    {
        // Issue #126 follow-up: the enum node was captured but its members never
        // were, and an anonymous enum contributed nothing — enum constants then
        // surfaced as unresolved-symbol false positives in including documents.
        // Members attach as Children (Kind EnumMember) so AddFile can flatten
        // them into the workspace name index; an anonymous enum contributes
        // top-level members (its constants are still file-scope).
        var nameToken = context.IDENTIFIER()?.Symbol;
        MqlSymbol? enumSymbol = null;
        if (nameToken != null)
        {
            var name = nameToken.Text;
            var range = CreateRangeFromToken(nameToken);
            var symbol = new MqlSymbol
            {
                Name = name,
                Kind = SymbolType.Enum.ToLspSymbolKind(),
                SymbolType = SymbolType.Enum,
                Range = range,
                SelectionRange = range,
                Detail = context.K_CLASS() != null ? $"enum class {name}" : $"enum {name}",
                FilePath = _filePath
            };

            Symbols.Add(symbol);
            _symbolsByName[name] = symbol;
            enumSymbol = symbol;
        }

        foreach (var member in context.enumMember())
        {
            var memberToken = member.IDENTIFIER()?.Symbol;
            if (memberToken == null)
            {
                continue;
            }

            var memberSymbol = new MqlSymbol
            {
                Name = memberToken.Text,
                Kind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.EnumMember,
                Range = CreateRangeFromToken(memberToken),
                SelectionRange = CreateRangeFromToken(memberToken),
                Detail = enumSymbol != null ? $"enum member of {enumSymbol.Name}" : "enum member (anonymous enum)",
                FilePath = _filePath
            };

            if (enumSymbol != null)
            {
                if (!enumSymbol.Children.Contains(memberSymbol))
                {
                    enumSymbol.Children.Add(memberSymbol);
                }
            }
            else
            {
                Symbols.Add(memberSymbol);
            }
        }

        return base.VisitEnumDeclaration(context);
    }

    public override MqlSymbol? VisitFunctionDeclaration([NotNull] Mql5GrammarParser.FunctionDeclarationContext context)
    {
        var name = context.qualifiedName()?.GetText();
        if (!string.IsNullOrEmpty(name))
        {
            var lastToken = context.qualifiedName().IDENTIFIER(context.qualifiedName().IDENTIFIER().Length - 1);
            var selectionRange = CreateRangeFromToken(lastToken.Symbol);
            var fullRange = CreateFullFunctionRange(context, lastToken.Symbol);

            // A function declared inside a class/struct/interface body is a method.
            var enclosingType = _currentTypeStack.Count > 0 ? _currentTypeStack.Peek() : null;
            var symbolType = enclosingType != null ? SymbolType.Method : SymbolType.Function;

            var symbol = new MqlSymbol
            {
                Name = name,
                Kind = symbolType.ToLspSymbolKind(),
                SymbolType = symbolType,
                Range = fullRange,
                Detail = $"function {context.type().GetText()} {name}",
                SelectionRange = selectionRange,
                FilePath = _filePath
            };

            Symbols.Add(symbol);
            AttachMember(symbol);
        }

        return base.VisitFunctionDeclaration(context);
    }

    public override MqlSymbol? VisitVariableDeclaration([NotNull] Mql5GrammarParser.VariableDeclarationContext context)
    {
        // Issue #109 (follow-up): a declaration can carry multiple declarators
        // ("uchar src[], dst[], key[32];") — every declarator declares a
        // name. Only the first was emitted, so the rest became unresolved-
        // symbol false positives whenever they were referenced later.
        var declarators = context.variableDeclarator();
        if (declarators != null && declarators.Length > 0)
        {
            var modifierTokens = new List<string>();
            for (int i = 0; i < context.modifiers().Length; i++)
            {
                var modifiersContext = context.modifiers(i);
                if (modifiersContext.K_INPUT() != null) modifierTokens.Add("input");
                if (modifiersContext.K_EXTERN() != null) modifierTokens.Add("extern");
                if (modifiersContext.K_STATIC() != null) modifierTokens.Add("static");
                if (modifiersContext.K_CONST() != null) modifierTokens.Add("const");
                if (modifiersContext.K_SINPUT() != null) modifierTokens.Add("sinput");
                if (modifiersContext.K_VIRTUAL() != null) modifierTokens.Add("virtual");
                if (modifiersContext.K_FINAL() != null) modifierTokens.Add("final");
            }

            string modifier = string.Join(" ", modifierTokens);
            var typeText = context.type().GetText();

            foreach (var declarator in declarators)
            {
                var nameToken = declarator.IDENTIFIER();
                if (nameToken == null)
                {
                    continue;
                }

                var name = nameToken.GetText();
                var range = CreateRangeFromToken(nameToken.Symbol);
                string detail = string.IsNullOrEmpty(modifier)
                    ? $"{typeText} {name}"
                    : $"{modifier} {typeText} {name}";

                var symbol = new MqlSymbol
                {
                    Name = name,
                    Kind = SymbolType.Variable.ToLspSymbolKind(),
                    SymbolType = SymbolType.Variable,
                    Range = range,
                    SelectionRange = range,
                    Detail = detail,
                    DeclaredType = typeText,
                    FilePath = _filePath
                };

                Symbols.Add(symbol);
                AttachMember(symbol);
            }
        }

        return base.VisitVariableDeclaration(context);
    }

    public override MqlSymbol? VisitParameter([NotNull] Mql5GrammarParser.ParameterContext context)
    {
        // Parameters are anonymous in some positions ("void f(int)") — only capture named ones.
        var nameToken = context.IDENTIFIER();
        if (nameToken != null)
        {
            var name = nameToken.GetText();
            var range = CreateRangeFromToken(nameToken.Symbol);

            var symbol = new MqlSymbol
            {
                Name = name,
                Kind = SymbolType.Variable.ToLspSymbolKind(),
                SymbolType = SymbolType.Variable,
                Range = range,
                SelectionRange = range,
                Detail = $"{context.type().GetText()} {name}",
                DeclaredType = context.type().GetText(),
                FilePath = _filePath
            };

            Symbols.Add(symbol);
        }

        return base.VisitParameter(context);
    }

    public override MqlSymbol? VisitDirective([NotNull] Mql5GrammarParser.DirectiveContext context)
    {
        if (context.PRE_INCLUDE() != null)
        {
            var tokenText = context.PRE_INCLUDE().GetText();
            var includePath = ExtractIncludePath(tokenText);
            if (!string.IsNullOrEmpty(includePath))
            {
                Includes.Add(includePath);
            }
        }

        return base.VisitDirective(context);
    }

    private void AttachMember(MqlSymbol member)
    {
        // REQ-SM-02: attach class members as Children of the innermost enclosing type symbol.
        if (_currentTypeStack.Count == 0)
            return;

        var enclosingType = _currentTypeStack.Peek();
        if (!enclosingType.Children.Contains(member))
        {
            enclosingType.Children.Add(member);
        }
    }

    private void ResolveHierarchy()
    {
        // Re-scan class/struct/interface declarations to wire ParentSymbol/Children.
        foreach (var symbol in Symbols.Where(s =>
            s.SymbolType == SymbolType.Class ||
            s.SymbolType == SymbolType.Struct ||
            s.SymbolType == SymbolType.Interface))
        {
            if (string.IsNullOrEmpty(symbol.Detail))
                continue;

            var parentName = ExtractParentName(symbol.Detail);
            if (!string.IsNullOrEmpty(parentName) && _symbolsByName.TryGetValue(parentName, out var parent))
            {
                symbol.ParentSymbol = parent;
                if (!parent.Children.Contains(symbol))
                {
                    parent.Children.Add(symbol);
                }
            }
        }
    }

    private static string? ExtractParentName(string detail)
    {
        var colonIndex = detail.IndexOf(':');
        if (colonIndex < 0)
            return null;

        var afterColon = detail.Substring(colonIndex + 1).Trim();
        var parts = afterColon.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[parts.Length - 1] : null;
    }

    private MqlSymbol CreateTypeSymbol(string name, IToken nameToken, SymbolType symbolType)
    {
        var range = CreateRangeFromToken(nameToken);
        return new MqlSymbol
        {
            Name = name,
            Kind = symbolType.ToLspSymbolKind(),
            SymbolType = symbolType,
            Range = range,
            SelectionRange = range,
            Detail = symbolType.ToString().ToLowerInvariant() + " " + name,
            FilePath = _filePath
        };
    }

    private string? ExtractIncludePath(string tokenText)
    {
        // Issue #25a: single include-resolution service (regex extraction of
        // the quoted/angle-bracket path from the directive token).
        return IncludePathResolver.ExtractFromDirective(tokenText);
    }

    private static LspRange CreateRangeFromToken(IToken token)
    {
        return new LspRange
        (
            new LspPosition(token.Line - 1, token.Column),
            new LspPosition(token.Line - 1, token.Column + token.Text.Length)
        );
    }

    private static LspRange CreateFullFunctionRange(Mql5GrammarParser.FunctionDeclarationContext context, IToken nameToken)
    {
        var startLine = nameToken.Line - 1;
        var startColumn = nameToken.Column;

        var blockContext = context.block();
        if (blockContext != null && blockContext.RBRACE() != null)
        {
            var rbrace = blockContext.RBRACE().Symbol;
            return new LspRange(
                new LspPosition(startLine, startColumn),
                new LspPosition(rbrace.Line - 1, rbrace.Column + rbrace.Text.Length)
            );
        }

        return new LspRange(
            new LspPosition(startLine, startColumn),
            new LspPosition(context.Stop.Line - 1, context.Stop.Column + context.Stop.Text.Length)
        );
    }
}
