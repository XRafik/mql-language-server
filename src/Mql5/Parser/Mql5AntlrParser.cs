using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Antlr4.Runtime;
using System.Threading;
using Mql5Grammar;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Parser;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;

namespace MqlLanguageServer.Mql5.Parser;

/// <summary>
/// MQL5 ANTLR Parser - Wraps the generated Mql5Grammar parser with MQL5-specific functionality.
/// Mirrors Mql4AntlrParser public surface and implements IMqlParser.
/// </summary>
public class Mql5AntlrParser : IMqlParser
{
    private static readonly Mql5Builtins _builtins = new();

    public Mql5AntlrParser()
    {
    }

    /// <summary>
    /// Parse an MQL5 file from its content with cooperative cancellation.
    ///
    /// The guard (size + nesting pre-scan, issue #20) runs before ANTLR
    /// lexing/parsing so pathological inputs produce a catchable syntax
    /// error instead of an uncatchable StackOverflowException; a canceled
    /// token aborts the parse between pre-scan, lexing, and the
    /// recursive-descent pass. The token cannot abort ANTLR mid-recursion,
    /// but the pre-scan bounds how deep that recursion can go.
    /// </summary>
    public MqlFile ParseFile(string content, string filePath, CancellationToken cancellationToken)
    {
        var parsedFile = new MqlFile { Language = MqlLanguage.Mql5 };
        var errorListener = new SyntaxErrorListener(filePath, "MQL5");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Pre-parse guard (issue #20): reject oversized inputs and
            // expression nesting deep enough to blow the call stack.
            var guardError = ParseInputGuard.Check(content, filePath, "MQL5");
            if (guardError != null)
            {
                parsedFile.SyntaxErrors = new List<SyntaxError> { guardError };
                return parsedFile;
            }

            return ParseFile(content, filePath, errorListener, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is cooperative, not an error: return whatever was
            // collected so far so callers can decide what to publish.
            parsedFile.SyntaxErrors = errorListener.Errors;
            return parsedFile;
        }
    }

    /// <summary>
    /// Parse an MQL5 file from its content.
    /// </summary>
    public MqlFile ParseFile(string content, string filePath = "unknown")
    {
        // Non-cancellation entry point: still guarded (issue #20) so
        // parser-internal paths (include chains, tests) can never recurse
        // into pathological input either.
        var parsedFile = new MqlFile { Language = MqlLanguage.Mql5 };
        var errorListener = new SyntaxErrorListener(filePath, "MQL5");

        try
        {
            var guardError = ParseInputGuard.Check(content, filePath, "MQL5");
            if (guardError != null)
            {
                parsedFile.SyntaxErrors = new List<SyntaxError> { guardError };
                return parsedFile;
            }

            return ParseFile(content, filePath, errorListener, CancellationToken.None);
        }
        catch (OperationCanceledException)
        {
            parsedFile.SyntaxErrors = errorListener.Errors;
            return parsedFile;
        }
    }

    /// <summary>
    /// Core parse pipeline shared by all entry points.
    /// </summary>
    private MqlFile ParseFile(
        string content, string filePath, SyntaxErrorListener errorListener, CancellationToken cancellationToken)
    {
        var parsedFile = new MqlFile { Language = MqlLanguage.Mql5 };
        var symbolsByName = new Dictionary<string, List<MqlSymbol>>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Strip a leading UTF-8 BOM (\uFEFF); the grammar has no token for it and
            // it would otherwise surface as a lexer "token recognition error" at 1:0.
            content = content.TrimStart('\uFEFF');

            cancellationToken.ThrowIfCancellationRequested();

            var inputStream = new AntlrInputStream(content);
            var lexer = new Mql5GrammarLexer(inputStream);

            // Route lexer errors through a listener that reports the file and grammar;
            // without this, ANTLR's default ConsoleErrorListener prints an anonymous
            // "token recognition error" with no file context.
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(new LexerErrorListener(filePath, "MQL5"));

            var tokenStream = new CommonTokenStream(lexer);

            // Issue #37: expand user-defined function-like macros (from this
            // file AND its quoted includes) before parsing. The stdlib
            // no-op filter (below) then runs on the expanded stream — the
            // two passes compose: expansion first, stdlib neutralization
            // second (its registry is untouched and Account_Protector.mqh
            // keeps parsing with 0 errors).
            var macroTable = MqlLanguageServer.Parser.MacroTableBuilder.Build(
                tokenStream, new MqlLanguageServer.Parser.MacroTableBuilder.PreTokenTypes(
                    Mql5GrammarLexer.PRE_DEFINE, Mql5GrammarLexer.PRE_IFDEF, Mql5GrammarLexer.PRE_IFNDEF,
                    Mql5GrammarLexer.PRE_ELSE, Mql5GrammarLexer.PRE_ENDIF, Mql5GrammarLexer.PRE_INCLUDE),
                filePath, MqlLanguage.Mql5);
            var expandedTokenSource = MqlLanguageServer.Parser.MacroExpansionFilter.Apply(
                tokenStream, macroTable, MqlLanguage.Mql5,
                new MqlLanguageServer.Parser.ExpansionTokenTypes(
                    Mql5GrammarLexer.IDENTIFIER, Mql5GrammarLexer.LPAREN, Mql5GrammarLexer.RPAREN,
                    Mql5GrammarLexer.COMMA, Mql5GrammarLexer.SEMICOLON));
            if (expandedTokenSource != null)
            {
                tokenStream = new CommonTokenStream(expandedTokenSource);
            }

            // Issue #26: neutralize known stdlib function-like macro invocations
            // (ON_EVENT/EVENT_MAP_* etc. from unresolvable stdlib includes)
            // before parsing, so the grammar sees no-op statements instead of
            // unparseable macro calls.
            var filteredTokenSource = MqlLanguageServer.Parser.StdlibMacroInvocationFilter.Apply(
                tokenStream, MqlLanguageServer.Parser.StdlibMacroInvocationFilter.DefaultMql5);
            if (filteredTokenSource != null)
            {
                tokenStream = new CommonTokenStream(filteredTokenSource);
            }

            var parser = new Mql5GrammarParser(tokenStream);

            parser.RemoveErrorListeners();
            parser.AddErrorListener(errorListener);

            cancellationToken.ThrowIfCancellationRequested();

            var tree = parser.compilationUnit();
            var visitor = new Mql5SymbolVisitor(filePath);
            visitor.Visit(tree);

            parsedFile.Symbols = visitor.Symbols;
            parsedFile.Includes = visitor.Includes;
            parsedFile.FilePath = filePath;
            // Issue #25b: extracted to the shared MacroExtractor collaborator.
            parsedFile.Macros = MacroExtractor.Extract(tokenStream, Mql5GrammarLexer.PRE_DEFINE);

            // OCC-01: capture default-channel IDENTIFIER tokens as occurrences.
            parsedFile.Occurrences = TokenOccurrenceCapture.Collect(
                tokenStream, Mql5GrammarLexer.IDENTIFIER);

            // Issue #30: capture color literals (C'r,g,b', hex, clr* names)
            // from the same token stream (REQ-CP-01..04, REQ-CP-08).
            parsedFile.ColorOccurrences = ColorOccurrenceCapture.Collect(
                tokenStream,
                new ColorTokenTypes(Mql5GrammarLexer.LITERAL_COLOR, Mql5GrammarLexer.HEX, Mql5GrammarLexer.IDENTIFIER),
                MqlLanguageServer.Color.MqlColorRegistry.Instance);

            BuildSymbolIndex(parsedFile, symbolsByName);

            parsedFile.SyntaxErrors = errorListener.Errors;
            return parsedFile;
        }
        catch (Exception ex)
        {
            // Log to stderr, never stdout (issue #21a): stdout is the JSON-RPC
            // transport, so plain text injected here desynchronizes/crashes the
            // client connection. Keep parsing degraded-but-alive: return the file
            // with any symbols collected before the exception.
            Console.Error.WriteLine($"Error parsing MQL5 file: {ex.Message}");
            // Preserve any syntax errors collected before the exception
            parsedFile.SyntaxErrors = errorListener.Errors;
            return parsedFile;
        }
    }

    /// <summary>
    /// Parse an MQL5 file from a file path.
    /// </summary>
    public MqlFile ParseFileFromPath(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"MQL5 file not found: {filePath}");
        }

        // Parser-internal read, NOT client-driven (see IMqlParser): every LSP
        // handler reads the file itself via SourceFileReader.ReadAllText
        // (guarded) and parses content. This path is used by tests on repo
        // fixtures outside any declared workspace root, so it must bypass the
        // client-URI containment guard.
        var content = SourceFileReader.ReadAllTextUncontained(filePath);
        return ParseFile(content, filePath);
    }

    /// <summary>
    /// Parse an MQL5 file and all its included files recursively.
    /// </summary>
    public MqlFile ParseFileWithIncludes(string filePath)
    {
        return ParseFileWithIncludes(filePath, new HashSet<string>());
    }

    private MqlFile ParseFileWithIncludes(string filePath, HashSet<string> processedFiles)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"MQL5 file not found: {filePath}");
        }

        var canonicalPath = Path.GetFullPath(filePath);
        if (processedFiles.Contains(canonicalPath))
        {
            return new MqlFile { FilePath = filePath, Symbols = new List<MqlSymbol>(), Includes = new List<string>(), Language = MqlLanguage.Mql5 };
        }

        processedFiles.Add(canonicalPath);

        var mainFile = ParseFileFromPath(filePath);
        var allSymbols = new List<MqlSymbol>(mainFile.Symbols);

        foreach (var include in mainFile.Includes)
        {
            // Issue #25a: single include-resolution service. Previously this
            // chain re-ran the raw-directive regex over already-extracted
            // stored entries, which never matched, so quoted include merges
            // silently resolved nothing. TryResolveContained now resolves
            // quoted entries relative to the includer (with the PathSecurity
            // containment guard the didOpen path already had) and returns
            // false for angle-bracket system includes — matching the MQL4
            // parser's intentional skip.
            if (IncludePathResolver.TryResolveContained(filePath, include, out var includeFullPath))
            {
                var includedFile = ParseFileWithIncludes(includeFullPath, processedFiles);
                allSymbols.AddRange(includedFile.Symbols);
            }
        }

        return new MqlFile
        {
            FilePath = filePath,
            Symbols = allSymbols,
            Includes = mainFile.Includes,
            Language = MqlLanguage.Mql5
        };
    }

    /// <summary>
    /// Find a symbol at a specific position in the file.
    /// </summary>
    public MqlSymbol? FindSymbolAtPosition(MqlFile file, int line, int column)
    {
        var searchLine = line - 1;
        var searchColumn = column - 1;

        foreach (var symbol in file.Symbols)
        {
            if (IsPositionInRange(searchLine, searchColumn, symbol.Range))
            {
                return symbol;
            }
        }

        return null;
    }

    /// <summary>
    /// Find a symbol definition by extracting the identifier at position and looking it up.
    /// </summary>
    public MqlSymbol? FindSymbolDefinition(MqlFile file, string content, int line, int column)
    {
        var searchLine = line - 1;
        var searchColumn = column - 1;

        var identifier = ExtractIdentifierAtPosition(content, searchLine, searchColumn);
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        // Issue #182: declarations in this document (or its include graph) take
        // precedence over the builtin registry. Lookup is case-insensitive in
        // both directions, so a local "bars" shadows the builtin "Bars()"
        // instead of being answered from the registry.
        var symbols = FindSymbolsByName(file, identifier);
        if (symbols.Any())
        {
            return symbols.First();
        }

        // Fall back to the builtin registry
        if (IsBuiltin(identifier))
        {
            return new MqlSymbol
            {
                Name = identifier,
                Kind = SymbolType.Function.ToLspSymbolKind(),
                SymbolType = SymbolType.Function,
                Detail = "MQL5 Built-in",
                Range = new LspRange(
                    new LspPosition(searchLine, searchColumn),
                    new LspPosition(searchLine, searchColumn + identifier.Length)
                )
            };
        }

        return null;
    }

    /// <summary>
    /// Find all symbols matching a name pattern (case-insensitive).
    /// </summary>
    public IEnumerable<MqlSymbol> FindSymbolsByName(MqlFile file, string name)
    {
        if (file == null || string.IsNullOrEmpty(name))
        {
            return Enumerable.Empty<MqlSymbol>();
        }

        var symbolIndex = EnsureSymbolIndex(file);
        if (symbolIndex.TryGetValue(name, out var symbols))
        {
            return symbols;
        }

        return Enumerable.Empty<MqlSymbol>();
    }

    /// <summary>
    /// Get all symbols in the file.
    /// </summary>
    public IEnumerable<MqlSymbol> GetAllSymbols(MqlFile file)
    {
        return file.Symbols;
    }

    /// <summary>
    /// Get all include directives in the file.
    /// </summary>
    public IEnumerable<string> GetIncludes(MqlFile file)
    {
        return file.Includes;
    }

    /// <summary>
    /// Check if a symbol is a built-in function or variable.
    /// </summary>
    public bool IsBuiltin(string name)
    {
        return _builtins.IsBuiltin(name);
    }

    /// <summary>
    /// Get completion suggestions at a position in the file.
    /// </summary>
    public IEnumerable<string> GetCompletions(MqlFile file, int line, int column)
    {
        var completions = new List<string>();
        completions.AddRange(file.Symbols.Select(s => s.Name));
        return completions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c);
    }

    /// <summary>
    /// Extract the body text of a function from the file content.
    /// </summary>
    public string? GetFunctionBody(string content, MqlSymbol symbol)
    {
        if (string.IsNullOrEmpty(content) || symbol == null || symbol.Range == null)
        {
            return null;
        }

        var lines = content.Split('\n');
        var startLine = symbol.Range.Start.Line;
        var endLine = symbol.Range.End.Line;

        if (startLine < 0 || startLine >= lines.Length || endLine < 0 || endLine >= lines.Length)
        {
            return null;
        }

        if (startLine == endLine)
        {
            return null;
        }

        int bodyStartLine = -1;
        int bodyStartColumn = -1;
        for (int i = startLine; i <= endLine; i++)
        {
            var line = lines[i];
            var braceIndex = line.IndexOf('{');
            if (braceIndex >= 0)
            {
                bodyStartLine = i;
                bodyStartColumn = braceIndex + 1;
                break;
            }
        }

        int bodyEndLine = -1;
        int bodyEndColumn = -1;
        int braceCount = 0;
        for (int i = startLine; i <= endLine; i++)
        {
            var line = lines[i];
            for (int j = 0; j < line.Length; j++)
            {
                if (line[j] == '{') braceCount++;
                else if (line[j] == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        bodyEndLine = i;
                        bodyEndColumn = j;
                        break;
                    }
                }
            }
            if (bodyEndLine >= 0) break;
        }

        if (bodyStartLine < 0 || bodyEndLine < 0)
        {
            return null;
        }

        if (bodyStartLine == bodyEndLine)
        {
            return lines[bodyStartLine].Substring(bodyStartColumn, bodyEndColumn - bodyStartColumn);
        }

        var bodyLines = new List<string>
        {
            lines[bodyStartLine].Substring(bodyStartColumn)
        };

        for (int i = bodyStartLine + 1; i < bodyEndLine; i++)
        {
            bodyLines.Add(lines[i]);
        }

        bodyLines.Add(lines[bodyEndLine].Substring(0, bodyEndColumn + 1));

        return string.Join("\n", bodyLines);
    }

    /// <summary>
    /// Get the 0-based line number where the function body starts.
    /// </summary>
    public int GetFunctionBodyStartLine(string content, MqlSymbol symbol)
    {
        if (string.IsNullOrEmpty(content) || symbol == null || symbol.Range == null)
        {
            return -1;
        }

        var lines = content.Split('\n');
        var startLine = symbol.Range.Start.Line;

        if (startLine < 0 || startLine >= lines.Length)
        {
            return -1;
        }

        for (int i = startLine; i <= symbol.Range.End.Line; i++)
        {
            var line = lines[i];
            var braceIndex = line.IndexOf('{');
            if (braceIndex >= 0)
            {
                if (braceIndex + 1 < line.Length)
                {
                    return i;
                }
                return i + 1;
            }
        }

        return -1;
    }

    /// <summary>
    /// Get the 0-based line number where the function body ends.
    /// </summary>
    public int GetFunctionBodyEndLine(string content, MqlSymbol symbol)
    {
        if (string.IsNullOrEmpty(content) || symbol == null || symbol.Range == null)
        {
            return -1;
        }

        var lines = content.Split('\n');
        int braceCount = 0;
        int startLine = symbol.Range.Start.Line;

        for (int i = startLine; i <= symbol.Range.End.Line; i++)
        {
            var line = lines[i];
            foreach (var c in line)
            {
                if (c == '{') braceCount++;
                else if (c == '}')
                {
                    braceCount--;
                    if (braceCount == 0)
                    {
                        return i;
                    }
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Get the number of lines in a function body.
    /// </summary>
    public int GetFunctionBodyLineCount(string content, MqlSymbol symbol)
    {
        var startLine = GetFunctionBodyStartLine(content, symbol);
        var endLine = GetFunctionBodyEndLine(content, symbol);

        if (startLine < 0 || endLine < 0 || startLine > endLine)
        {
            return -1;
        }

        return endLine - startLine + 1;
    }

    private string? ExtractIdentifierAtPosition(string content, int line, int column)
    {
        var lines = content.Split('\n');

        if (line < 0 || line >= lines.Length)
        {
            return null;
        }

        var currentLine = lines[line];

        if (column < 0 || column >= currentLine.Length)
        {
            return null;
        }

        if (!char.IsLetterOrDigit(currentLine[column]) && currentLine[column] != '_')
        {
            return null;
        }

        var start = column;
        while (start > 0 && (char.IsLetterOrDigit(currentLine[start - 1]) || currentLine[start - 1] == '_'))
        {
            start--;
        }

        var end = column;
        while (end < currentLine.Length && (char.IsLetterOrDigit(currentLine[end]) || currentLine[end] == '_'))
        {
            end++;
        }

        if (end <= start)
        {
            return null;
        }

        return currentLine.Substring(start, end - start);
    }

    private Dictionary<string, List<MqlSymbol>> EnsureSymbolIndex(MqlFile file)
    {
        if (file.SymbolIndex != null)
        {
            return file.SymbolIndex;
        }

        lock (file)
        {
            if (file.SymbolIndex == null)
            {
                CreateSymbolIndex(file, out var newIndex);
                file.SymbolIndex = newIndex;
            }
            return file.SymbolIndex;
        }
    }

    private void CreateSymbolIndex(MqlFile file, out Dictionary<string, List<MqlSymbol>> symbolsByName)
    {
        symbolsByName = new Dictionary<string, List<MqlSymbol>>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in file.Symbols)
        {
            if (!symbolsByName.TryGetValue(symbol.Name, out var list))
            {
                list = new List<MqlSymbol>();
                symbolsByName[symbol.Name] = list;
            }

            list.Add(symbol);
        }
    }

    private void BuildSymbolIndex(MqlFile file, Dictionary<string, List<MqlSymbol>> symbolsByName)
    {
        symbolsByName.Clear();

        foreach (var symbol in file.Symbols)
        {
            if (!symbolsByName.TryGetValue(symbol.Name, out var list))
            {
                list = new List<MqlSymbol>();
                symbolsByName[symbol.Name] = list;
            }

            list.Add(symbol);
        }
    }


    private bool IsPositionInRange(int line, int column, LspRange range)
    {
        if (line < range.Start.Line || line > range.End.Line)
            return false;

        if (line == range.Start.Line && column < range.Start.Character)
            return false;

        if (line == range.End.Line && column > range.End.Character)
            return false;

        return true;
    }
}
