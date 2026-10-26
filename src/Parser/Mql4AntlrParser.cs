using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Mql4Grammar;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using LspSymbolKind = OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind;

namespace MqlLanguageServer.Parser
{
    /// <summary>
    /// MQL4 ANTLR Parser - Wraps the generated ANTLR parser with MQL4-specific functionality
    /// </summary>
    public class Mql4AntlrParser : IMqlParser
    {
        public Mql4AntlrParser()
        {
            // No state - parser is now stateless and thread-safe
        }

        /// <summary>
        /// Parse an MQL4 file from its content with cooperative cancellation.
        ///
        /// The guard (size + nesting pre-scan, issue #20) runs before ANTLR
        /// lexing/parsing so pathological inputs produce a catchable syntax
        /// error instead of an uncatchable StackOverflowException; a canceled
        /// token aborts the parse between pre-scan, lexing, and the
        /// recursive-descent pass. The token cannot abort ANTLR mid-recursion,
        /// but the pre-scan bounds how deep that recursion can go.
        /// </summary>
        public Mql4File ParseFile(string content, string filePath, CancellationToken cancellationToken)
        {
            var parsedFile = new Mql4File();
            var errorListener = new SyntaxErrorListener(filePath, "MQL4");

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Pre-parse guard (issue #20): reject oversized inputs and
                // expression nesting deep enough to blow the call stack.
                var guardError = ParseInputGuard.Check(content, filePath, "MQL4");
                if (guardError != null)
                {
                    parsedFile.SyntaxErrors = new List<SyntaxError> { guardError };
                    return parsedFile;
                }

                return ParseFile(content, filePath, errorListener, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is cooperative, not an error: return whatever
                // was collected so far so callers can decide what to publish.
                parsedFile.SyntaxErrors = errorListener.Errors;
                return parsedFile;
            }
        }

        /// <summary>
        /// Parse an MQL4 file from its content
        /// </summary>
        /// <param name="content">File content to parse</param>
        /// <param name="filePath">File path (for error reporting)</param>
        /// <returns>Parsed Mql4File with symbols</returns>
        public Mql4File ParseFile(string content, string filePath = "unknown")
        {
            // Non-cancellation entry point: still guarded (issue #20) so
            // parser-internal paths (include chains, tests) can never recurse
            // into pathological input either.
            var parsedFile = new Mql4File();
            var errorListener = new SyntaxErrorListener(filePath, "MQL4");

            try
            {
                var guardError = ParseInputGuard.Check(content, filePath, "MQL4");
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
        private Mql4File ParseFile(
            string content, string filePath, SyntaxErrorListener errorListener, CancellationToken cancellationToken)
        {
            var parsedFile = new Mql4File();
            var symbolsByName = new Dictionary<string, List<Mql4Symbol>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                // Strip a leading UTF-8 BOM (\uFEFF); the grammar has no token for it and
                // it would otherwise surface as a lexer "token recognition error" at 1:0.
                content = content.TrimStart('\uFEFF');

                cancellationToken.ThrowIfCancellationRequested();

                // Create input stream from content
                var inputStream = new AntlrInputStream(content);

                // Create lexer
                var lexer = new Mql4GrammarLexer(inputStream);

                // Route lexer errors through a listener that reports the file and grammar;
                // without this, ANTLR's default ConsoleErrorListener prints an anonymous
                // "token recognition error" with no file context.
                lexer.RemoveErrorListeners();
                lexer.AddErrorListener(new LexerErrorListener(filePath, "MQL4"));

                // Create token stream
                var tokenStream = new CommonTokenStream(lexer);

                // Issue #37: expand user-defined function-like macros (from
                // this file AND its quoted includes) before parsing. The
                // stdlib no-op filter (below) then runs on the expanded
                // stream — the two passes compose: expansion first, stdlib
                // neutralization second (its registry is untouched and
                // Account_Protector.mqh keeps parsing with 0 errors).
                var macroTable = MacroTableBuilder.Build(
                    tokenStream, new MacroTableBuilder.PreTokenTypes(
                        Mql4GrammarLexer.PRE_DEFINE, Mql4GrammarLexer.PRE_IFDEF, Mql4GrammarLexer.PRE_IFNDEF,
                        Mql4GrammarLexer.PRE_ELSE, Mql4GrammarLexer.PRE_ENDIF, Mql4GrammarLexer.PRE_INCLUDE),
                    filePath, MqlLanguage.Mql4);
                var expandedTokenSource = MacroExpansionFilter.Apply(
                    tokenStream, macroTable, MqlLanguage.Mql4,
                    new ExpansionTokenTypes(
                        Mql4GrammarLexer.IDENTIFIER, Mql4GrammarLexer.LPAREN, Mql4GrammarLexer.RPAREN,
                        Mql4GrammarLexer.COMMA, Mql4GrammarLexer.SEMICOLON));
                if (expandedTokenSource != null)
                {
                    tokenStream = new CommonTokenStream(expandedTokenSource);
                }

                // Issue #26: neutralize known stdlib function-like macro
                // invocations (ON_EVENT/EVENT_MAP_* etc. from unresolvable
                // stdlib includes) before parsing, so the grammar sees no-op
                // statements instead of unparseable macro calls.
                var filteredTokenSource = StdlibMacroInvocationFilter.Apply(
                    tokenStream, MqlLanguageServer.Parser.StdlibMacroInvocationFilter.DefaultMql4);
                if (filteredTokenSource != null)
                {
                    tokenStream = new CommonTokenStream(filteredTokenSource);
                }

                // Create parser
                var parser = new Mql4GrammarParser(tokenStream);

                // Add error listener
                parser.RemoveErrorListeners();
                parser.AddErrorListener(errorListener);

                cancellationToken.ThrowIfCancellationRequested();

                // Parse the compilation unit
                var tree = parser.compilationUnit();

                // Create visitor to extract symbols
                var visitor = new Mql4SymbolVisitor(filePath);
                visitor.Visit(tree);

                // Extract symbols and includes from visitor
                parsedFile.Symbols = visitor.Symbols;
                parsedFile.Includes = visitor.Includes;
                parsedFile.FilePath = filePath;

                // NEW: Extract macros using token scanning (Channel 1 - PREPROCESSOR)
                // This is needed because #define, #ifdef, etc. are hidden from the parser.
                // Issue #25b: extracted to the shared MacroExtractor collaborator.
                parsedFile.Macros = MacroExtractor.Extract(tokenStream, Mql4GrammarLexer.PRE_DEFINE);

                // OCC-01: capture default-channel IDENTIFIER tokens as occurrences.
                parsedFile.Occurrences = TokenOccurrenceCapture.Collect(
                    tokenStream, Mql4GrammarLexer.IDENTIFIER);

                // Issue #30: capture color literals (C'r,g,b', hex, clr* names)
                // from the same token stream (REQ-CP-01..04, REQ-CP-08).
                parsedFile.ColorOccurrences = ColorOccurrenceCapture.Collect(
                    tokenStream,
                    new ColorTokenTypes(Mql4GrammarLexer.LITERAL_COLOR, Mql4GrammarLexer.HEX, Mql4GrammarLexer.IDENTIFIER),
                    MqlLanguageServer.Color.MqlColorRegistry.Instance);

                // Build index of symbols by name
                BuildSymbolIndex(parsedFile, symbolsByName);

                parsedFile.SyntaxErrors = errorListener.Errors;
                return parsedFile;
            }
            catch (Exception ex)
            {
                // Log to stderr, never stdout (issue #21a): stdout is the JSON-RPC
                // transport, so plain text injected here desynchronizes/crashes the
                // client connection. Keep parsing degraded-but-alive: return the file
                // with any symbols found before the error.
                Console.Error.WriteLine($"Error parsing MQL4 file: {ex.Message}");
                // Preserve any syntax errors collected before the exception
                parsedFile.SyntaxErrors = errorListener.Errors;
                return parsedFile;
            }
        }

        /// <summary>
        /// Parse an MQL4 file from a file path
        /// </summary>
        /// <param name="filePath">Path to the MQL4 file</param>
        /// <returns>Parsed Mql4File with symbols</returns>
        public Mql4File ParseFileFromPath(string filePath)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"MQL4 file not found: {filePath}");
            }

            // Parser-internal read, NOT client-driven (see IMqlParser): every
            // LSP handler reads the file itself via SourceFileReader.ReadAllText
            // (guarded) and parses content. This path is used by tests on repo
            // fixtures outside any declared workspace root, so it must bypass
            // the client-URI containment guard.
            var content = SourceFileReader.ReadAllTextUncontained(filePath);
            return ParseFile(content, filePath);
        }

        /// <summary>
        /// Parse an MQL4 file and all its included files (.mqh)
        /// Resolves includes recursively and returns combined symbols
        /// </summary>
        /// <param name="filePath">Path to the main MQL4 file</param>
        /// <returns>Parsed Mql4File with symbols from main file and all includes</returns>
        public Mql4File ParseFileWithIncludes(string filePath)
        {
            return ParseFileWithIncludes(filePath, new HashSet<string>());
        }

        /// <summary>
        /// Parse an MQL4 file and all its included files (recursive)
        /// </summary>
        /// <param name="filePath">Path to the MQL4 file</param>
        /// <param name="processedFiles">Set of already processed files to avoid circular dependencies</param>
        /// <returns>Parsed Mql4File with symbols from main file and all includes</returns>
        private Mql4File ParseFileWithIncludes(string filePath, HashSet<string> processedFiles)
        {
            // Check if file exists
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"MQL4 file not found: {filePath}");
            }

            // Avoid circular dependencies
            var canonicalPath = Path.GetFullPath(filePath);
            if (processedFiles.Contains(canonicalPath))
            {
                // Return empty file for circular include
                return new Mql4File { FilePath = filePath, Symbols = new List<Mql4Symbol>(), Includes = new List<string>() };
            }

            // Add current file to processed set
            processedFiles.Add(canonicalPath);

            // Parse the main file
            var mainFile = ParseFileFromPath(filePath);
            var allSymbols = new List<Mql4Symbol>(mainFile.Symbols);

            // Parse all included files recursively
            foreach (var include in mainFile.Includes)
            {
                // `Includes` entries are stored by VisitDirective as the extracted
                // path: a bare relative path for `#include "file.mqh"` (e.g.
                // "Account_Protector.mqh") or an angle-bracket-wrapped path for
                // `#include <file.mqh>` (e.g. "<WinUser32.mqh>"). System includes
                // (angle-bracket form) reference the MQL4 standard library which
                // is not present in the fixture tree, so they are skipped here;
                // only local quoted includes can be resolved relative to the
                // including file's directory.
                // Issue #25a: resolution goes through the single
                // IncludePathResolver service (it returns null for angle-bracket
                // system includes, preserving the skip behavior).
                var includeFullPath = IncludePathResolver.Resolve(filePath, include);
                if (includeFullPath != null && File.Exists(includeFullPath))
                {
                    // Parse included file and merge symbols
                    var includedFile = ParseFileWithIncludes(includeFullPath, processedFiles);
                    allSymbols.AddRange(includedFile.Symbols);
                }
            }

            // Return combined file
            return new Mql4File
            {
                FilePath = filePath,
                Symbols = allSymbols,
                Includes = mainFile.Includes
            };
        }

        /// <summary>
        /// Find a symbol at a specific position in the file
        /// </summary>
        /// <param name="file">Parsed Mql4File to search in</param>
        /// <param name="line">Line number (1-based)</param>
        /// <param name="column">Column number (1-based)</param>
        /// <returns>Symbol at position, or null if not found</returns>
        public Mql4Symbol? FindSymbolAtPosition(Mql4File file, int line, int column)
        {
            // Convert from 1-based (LSP) to 0-based (internal)
            var searchLine = line - 1;
            var searchColumn = column - 1;

            // First, check if position is within a symbol's range (for clicking on declarations)
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
        /// Find a symbol definition by extracting the identifier at position and looking it up
        /// </summary>
        /// <param name="file">Parsed Mql4File to search in</param>
        /// <param name="content">File content</param>
        /// <param name="line">Line number (1-based)</param>
        /// <param name="column">Column number (1-based)</param>
        /// <returns>Symbol definition at position, or null if not found</returns>
        public Mql4Symbol? FindSymbolDefinition(Mql4File file, string content, int line, int column)
        {
            // Convert from 1-based to 0-based
            var searchLine = line - 1;
            var searchColumn = column - 1;

            // Extract identifier at position
            var identifier = ExtractIdentifierAtPosition(content, searchLine, searchColumn);

            if (string.IsNullOrEmpty(identifier))
            {
                return null;
            }

            // Issue #182: declarations in this document (or its include graph) take
            // precedence over the builtin registry. Lookup is case-insensitive in
            // both directions, so a local "ask" shadows the builtin "Ask" instead
            // of being answered from the registry.
            // Issue #184: the document match is scope-aware — an enclosing
            // declaration wins (S1), a document-scope declaration beats one
            // nested in a foreign function (S2), included symbols compete at
            // document scope (S4), and the registry stays the last fallback (S3).
            var resolved = ResolveInScope(file, identifier, searchLine, searchColumn);
            if (resolved != null)
            {
                return resolved;
            }

            // Fall back to the builtin registry
            if (IsBuiltin(identifier))
            {
                // For builtins, return a pseudo-symbol with the identifier name
                return new Mql4Symbol
                {
                    Name = identifier,
                    Kind = LspSymbolKind.Function,
                    Detail = "MQL4 Built-in",
                    Range = new LspRange(
                        new LspPosition(searchLine, searchColumn),
                        new LspPosition(searchLine, searchColumn + identifier.Length)
                    )
                };
            }

            return null;
        }

        /// <summary>
        /// Extract identifier at a specific position in the file content
        /// </summary>
        /// <param name="content">File content</param>
        /// <param name="line">Line number (0-based)</param>
        /// <param name="column">Column number (0-based)</param>
        /// <returns>Identifier at position, or null if not found</returns>
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

            // Check if position is within an identifier
            if (!char.IsLetterOrDigit(currentLine[column]) && currentLine[column] != '_')
            {
                return null;
            }

            // Find start of identifier
            var start = column;
            while (start > 0 && (char.IsLetterOrDigit(currentLine[start - 1]) || currentLine[start - 1] == '_'))
            {
                start--;
            }

            // Find end of identifier
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

        /// <summary>
        /// Find all symbols matching a name pattern (OPTIMIZED version)
        /// Uses cached symbol index to avoid rebuilding dictionary on every lookup
        /// </summary>
        /// <param name="file">Parsed Mql4File to search in</param>
        /// <param name="name">Name pattern (case-insensitive)</param>
        /// <returns>List of matching symbols</returns>
        public IEnumerable<Mql4Symbol> FindSymbolsByName(Mql4File file, string name)
        {
            if (file == null || string.IsNullOrEmpty(name))
            {
                return Enumerable.Empty<Mql4Symbol>();
            }

            // OPTIMIZATION 1: Check if file has cached symbol index
            // This avoids rebuilding the dictionary on every lookup
            var symbolIndex = EnsureSymbolIndex(file);

            // OPTIMIZATION 2: Use TryGetValue for O(1) lookup instead of ContainsKey + indexer
            if (symbolIndex.TryGetValue(name, out var symbols))
            {
                return symbols;
            }

            return Enumerable.Empty<Mql4Symbol>();
        }

        /// <summary>
        /// Ensure the file has a cached symbol index for fast lookups
        /// </summary>
        /// <param name="file">Mql4File to index</param>
        /// <returns>Symbol index dictionary</returns>
        private Dictionary<string, List<Mql4Symbol>> EnsureSymbolIndex(Mql4File file)
        {
            // OPTIMIZATION: Use lazy initialization with double-check locking pattern
            // This ensures thread-safe lazy initialization without locks on every access

            // First check without lock (fast path)
            if (file.SymbolIndex != null && file.ScopeContainerSymbols != null)
            {
                return file.SymbolIndex;
            }

            // Second check with lock (slow path)
            lock (file)
            {
                if (file.SymbolIndex == null)
                {
                    // Build and cache the index
                    CreateSymbolIndex(file, out var newIndex);
                    file.SymbolIndex = newIndex;
                }

                // Issue #184 (S6): cache this document's function/method
                // declarations next to the name index — one pass over Symbols,
                // built once, reused by every subsequent scope-aware lookup.
                file.ScopeContainerSymbols ??= SymbolScopeResolver.CollectScopeContainers(file);

                return file.SymbolIndex;
            }
        }

        /// <summary>
        /// Issue #184: choose the declaration of the identifier that is visible
        /// at the lookup position (see SymbolScopeResolver for the S1/S2/S4/S5
        /// precedence), or null when nothing in the document encloses the
        /// position or is declared at document scope.
        /// </summary>
        private Mql4Symbol? ResolveInScope(Mql4File file, string identifier, int searchLine, int searchColumn)
        {
            var symbolIndex = EnsureSymbolIndex(file);
            if (!symbolIndex.TryGetValue(identifier, out var candidates) || candidates.Count == 0)
            {
                return null;
            }

            var containers = file.ScopeContainerSymbols ?? SymbolScopeResolver.CollectScopeContainers(file);
            return SymbolScopeResolver.SelectCandidate(candidates, containers, file.FilePath, searchLine, searchColumn);
        }

        /// <summary>
        /// Get all symbols in the file
        /// </summary>
        /// <param name="file">Parsed Mql4File</param>
        /// <returns>All symbols</returns>
        public IEnumerable<Mql4Symbol> GetAllSymbols(Mql4File file)
        {
            return file.Symbols;
        }

        /// <summary>
        /// Get all include directives
        /// </summary>
        /// <param name="file">Parsed Mql4File</param>
        /// <returns>All includes</returns>
        public IEnumerable<string> GetIncludes(Mql4File file)
        {
            return file.Includes;
        }

        /// <summary>
        /// Check if a symbol is a builtin function or variable
        /// </summary>
        /// <param name="name">Symbol name</param>
        /// <returns>True if builtin</returns>
        public bool IsBuiltin(string name)
        {
            return Mql4Builtins.BuiltInFunctions.ContainsKey(name) ||
                   Mql4Builtins.BuiltInVariables.ContainsKey(name);
        }

        /// <summary>
        /// Get completion suggestions at a position
        /// </summary>
        /// <param name="file">Parsed Mql4File</param>
        /// <param name="line">Line number</param>
        /// <param name="column">Column number</param>
        /// <returns>List of completion items</returns>
        public IEnumerable<string> GetCompletions(Mql4File file, int line, int column)
        {
            var completions = new List<string>();

            // Add builtin functions
            completions.AddRange(Mql4Builtins.BuiltInFunctions.Keys);

            // Add builtin variables
            completions.AddRange(Mql4Builtins.BuiltInVariables.Keys);

            // Add local symbols (excluding built-ins to avoid duplicates)
            var builtinNames = new HashSet<string>(Mql4Builtins.BuiltInFunctions.Keys.Concat(Mql4Builtins.BuiltInVariables.Keys),
                StringComparer.OrdinalIgnoreCase);
            completions.AddRange(file.Symbols.Select(s => s.Name).Where(name => !builtinNames.Contains(name)));

            // Remove duplicates and return
            return completions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c);
        }

        // Legacy overloads for backward compatibility with tests
        public IEnumerable<string> GetCompletions(int line, int column)
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        public Mql4Symbol? FindSymbolAtPosition(int line, int column)
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        public IEnumerable<Mql4Symbol> FindSymbolsByName(string name)
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        public IEnumerable<Mql4Symbol> GetAllSymbols()
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        public IEnumerable<string> GetIncludes()
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        public Mql4Symbol? FindSymbolDefinition(string content, int line, int column)
        {
            throw new NotSupportedException("This overload is deprecated. Parse the file first and pass it as the first parameter.");
        }

        private bool IsPositionInRange(int line, int column, LspRange range)
        {
            // Simple range check - a symbol is at position if:
            // - Position is on the same line and column >= start
            // - OR position line is between start and end lines
            if (line < range.Start.Line || line > range.End.Line)
                return false;

            if (line == range.Start.Line && column < range.Start.Character)
                return false;

            if (line == range.End.Line && column > range.End.Character)
                return false;

            return true;
        }


        /// <summary>
        /// Create and build symbol index for fast lookups (optimized version with out parameter)
        /// </summary>
        /// <param name="file">Mql4File to index</param>
        /// <param name="symbolsByName">Output dictionary for symbol index</param>
        private void CreateSymbolIndex(Mql4File file, out Dictionary<string, List<Mql4Symbol>> symbolsByName)
        {
            symbolsByName = new Dictionary<string, List<Mql4Symbol>>(StringComparer.OrdinalIgnoreCase);

            foreach (var symbol in file.Symbols)
            {
                if (!symbolsByName.TryGetValue(symbol.Name, out var list))
                {
                    list = new List<Mql4Symbol>();
                    symbolsByName[symbol.Name] = list;
                }

                list.Add(symbol);
            }
        }

        /// <summary>
        /// Build symbol index for fast lookups (existing version for backward compatibility)
        /// </summary>
        /// <param name="file">Mql4File to index</param>
        /// <param name="symbolsByName">Dictionary to populate with symbol index</param>
        private void BuildSymbolIndex(Mql4File file, Dictionary<string, List<Mql4Symbol>> symbolsByName)
        {
            symbolsByName.Clear();

            foreach (var symbol in file.Symbols)
            {
                if (!symbolsByName.TryGetValue(symbol.Name, out var list))
                {
                    list = new List<Mql4Symbol>();
                    symbolsByName[symbol.Name] = list;
                }

                list.Add(symbol);
            }
        }

        /// <summary>
        /// Extract the body text of a function from the file content.
        /// Uses the symbol's Range to determine start and end positions.
        /// </summary>
        /// <param name="content">Full file content</param>
        /// <param name="symbol">Function symbol with Range</param>
        /// <returns>Function body text (from opening { to closing }), or null if extraction fails</returns>
        public string? GetFunctionBody(string content, Mql4Symbol symbol)
        {
            if (string.IsNullOrEmpty(content) || symbol == null || symbol.Range == null)
            {
                return null;
            }

            var lines = content.Split('\n');

            // Check if line numbers are valid
            var startLine = symbol.Range.Start.Line;
            var endLine = symbol.Range.End.Line;

            if (startLine < 0 || startLine >= lines.Length || endLine < 0 || endLine >= lines.Length)
            {
                return null;
            }

            // If it's a single line function or range is just the declaration
            if (startLine == endLine)
            {
                return null; // No body to extract
            }

            // Find the opening brace
            int bodyStartLine = -1;
            int bodyStartColumn = -1;
            for (int i = startLine; i <= endLine; i++)
            {
                var line = lines[i];
                var braceIndex = line.IndexOf('{');
                if (braceIndex >= 0)
                {
                    bodyStartLine = i;
                    bodyStartColumn = braceIndex + 1; // Start after the brace
                    break;
                }
            }

            // Find the closing brace
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
                            bodyEndColumn = j; // End at the brace
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

            // Extract the body text
            var bodyLines = new List<string>();

            if (bodyStartLine == bodyEndLine)
            {
                // Single line body
                return lines[bodyStartLine].Substring(bodyStartColumn, bodyEndColumn - bodyStartColumn);
            }

            // First line (from opening brace to end)
            var firstLine = lines[bodyStartLine].Substring(bodyStartColumn);
            bodyLines.Add(firstLine);

            // Middle lines
            for (int i = bodyStartLine + 1; i < bodyEndLine; i++)
            {
                bodyLines.Add(lines[i]);
            }

            // Last line (from start to closing brace)
            var lastLine = lines[bodyEndLine].Substring(0, bodyEndColumn + 1);
            bodyLines.Add(lastLine);

            return string.Join("\n", bodyLines);
        }

        /// <summary>
        /// Get the line number where a function body starts (line after opening brace).
        /// </summary>
        /// <param name="content">Full file content</param>
        /// <param name="symbol">Function symbol</param>
        /// <returns>0-based line number where body starts, or -1 if not found</returns>
        public int GetFunctionBodyStartLine(string content, Mql4Symbol symbol)
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
                    // Check if there's content after the brace on the same line
                    if (braceIndex + 1 < line.Length)
                    {
                        return i; // Body starts on same line
                    }
                    // Body starts on next line
                    return i + 1;
                }
            }

            return -1;
        }

        /// <summary>
        /// Get the line number where a function body ends (line with closing brace).
        /// </summary>
        /// <param name="content">Full file content</param>
        /// <param name="symbol">Function symbol</param>
        /// <returns>0-based line number where body ends, or -1 if not found</returns>
        public int GetFunctionBodyEndLine(string content, Mql4Symbol symbol)
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
        /// <param name="content">Full file content</param>
        /// <param name="symbol">Function symbol</param>
        /// <returns>Number of lines in function body, or -1 if not found</returns>
        public int GetFunctionBodyLineCount(string content, Mql4Symbol symbol)
        {
            var startLine = GetFunctionBodyStartLine(content, symbol);
            var endLine = GetFunctionBodyEndLine(content, symbol);

            if (startLine < 0 || endLine < 0 || startLine > endLine)
            {
                return -1;
            }

            return endLine - startLine + 1;
        }
    }
    }

    /// <summary>
    /// MQL4 Symbol Visitor - Extracts symbols from ANTLR parse tree.
    /// (The SyntaxErrorListener / LexerErrorListener classes that used to live
    /// after this file's namespace block now live in AntlrErrorListeners.cs —
    /// issue #25b.)
    /// </summary>
    public class Mql4SymbolVisitor : Mql4GrammarBaseVisitor<Mql4Symbol?>
    {
        public List<Mql4Symbol> Symbols { get; } = new List<Mql4Symbol>();
        public List<string> Includes { get; } = new List<string>();

        private readonly string _filePath;

        // REQ-SM-02: stack of enclosing class/struct symbols while visiting their bodies.
        // Members discovered inside are attached as Children of the innermost enclosing type
        // at extraction time — never via Range containment (class Range spans the name token).
        private readonly Stack<Mql4Symbol> _currentTypeStack = new Stack<Mql4Symbol>();

        public Mql4SymbolVisitor(string filePath)
        {
            _filePath = filePath ?? string.Empty;
        }

        public override Mql4Symbol? VisitClassDeclaration([NotNull] Mql4GrammarParser.ClassDeclarationContext context)
        {
            // REQ-SM-02: the MQL4 visitor now emits class symbols (previously none).
            // Tolerance per CCR-05: Kind is set, SymbolType stays null.
            var nameToken = context.IDENTIFIER();
            if (nameToken == null)
                return base.VisitClassDeclaration(context);

            // Issue #123: capture the base class from the grammar's
            // `COLON accessModifier qualifiedName` clause (single inheritance).
            string? baseClass = context.qualifiedName()?.GetText();

            var symbol = new Mql4Symbol
            {
                Name = nameToken.GetText(),
                Kind = LspSymbolKind.Class,
                Range = CreateRangeFromToken(nameToken.Symbol),
                SelectionRange = CreateRangeFromToken(nameToken.Symbol),
                Detail = baseClass != null
                    ? $"class {nameToken.GetText()} : {baseClass}"
                    : $"class {nameToken.GetText()}",
                BaseClass = baseClass,
                FilePath = _filePath
            };

            Symbols.Add(symbol);

            _currentTypeStack.Push(symbol);
            try
            {
                return base.VisitClassDeclaration(context);
            }
            finally
            {
                _currentTypeStack.Pop();
            }
        }

        public override Mql4Symbol? VisitStructDeclaration([NotNull] Mql4GrammarParser.StructDeclarationContext context)
        {
            var nameToken = context.IDENTIFIER();
            if (nameToken == null)
                return base.VisitStructDeclaration(context);

            var symbol = new Mql4Symbol
            {
                Name = nameToken.GetText(),
                Kind = LspSymbolKind.Struct,
                Range = CreateRangeFromToken(nameToken.Symbol),
                SelectionRange = CreateRangeFromToken(nameToken.Symbol),
                Detail = $"struct {nameToken.GetText()}",
                FilePath = _filePath
            };

            Symbols.Add(symbol);

            _currentTypeStack.Push(symbol);
            try
            {
                return base.VisitStructDeclaration(context);
            }
            finally
            {
                _currentTypeStack.Pop();
            }
        }

        public override Mql4Symbol? VisitEnumDeclaration([NotNull] Mql4GrammarParser.EnumDeclarationContext context)
        {
            // Issue #126 follow-up: enums were not visited at all, so neither the
            // enum type name nor its members reached the symbol tree — both surfaced
            // as unresolved-symbol false positives in including documents. Members
            // attach as Children (Kind EnumMember) so AddFile can flatten them into
            // the workspace name index; an anonymous enum contributes top-level
            // members (its constants are still file-scope). Tolerance per CCR-05:
            // Kind is set, SymbolType stays null.
            var nameToken = context.IDENTIFIER();
            Mql4Symbol? enumSymbol = null;
            if (nameToken != null)
            {
                enumSymbol = new Mql4Symbol
                {
                    Name = nameToken.GetText(),
                    Kind = LspSymbolKind.Enum,
                    Range = CreateRangeFromToken(nameToken.Symbol),
                    SelectionRange = CreateRangeFromToken(nameToken.Symbol),
                    Detail = $"enum {nameToken.GetText()}",
                    FilePath = _filePath
                };

                Symbols.Add(enumSymbol);
                AttachMember(enumSymbol);
            }

            foreach (var member in context.enumMember())
            {
                var memberToken = member.IDENTIFIER();
                if (memberToken == null)
                {
                    continue;
                }

                var memberSymbol = new Mql4Symbol
                {
                    Name = memberToken.GetText(),
                    Kind = LspSymbolKind.EnumMember,
                    Range = CreateRangeFromToken(memberToken.Symbol),
                    SelectionRange = CreateRangeFromToken(memberToken.Symbol),
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

        public override Mql4Symbol? VisitFunctionDeclaration([NotNull] Mql4GrammarParser.FunctionDeclarationContext context)
        {
            // Get function name (now uses qualifiedName to support Class::Method syntax)
            var name = context.qualifiedName()?.GetText();
            if (!string.IsNullOrEmpty(name))
            {
                // Get the last token from qualifiedName for position info
                var lastToken = context.qualifiedName().IDENTIFIER(context.qualifiedName().IDENTIFIER().Length - 1);
                var selectionRange = CreateRangeFromToken(lastToken.Symbol);
                var fullRange = CreateFullFunctionRange(context, lastToken.Symbol);

                // A function declared inside a class/struct body is a method (Kind only —
                // MQL4 tolerance keeps SymbolType null, CCR-05).
                var enclosingType = _currentTypeStack.Count > 0 ? _currentTypeStack.Peek() : null;
                var kind = enclosingType != null ? LspSymbolKind.Method : LspSymbolKind.Function;

                var symbol = new Mql4Symbol
                {
                    Name = name,
                    Kind = kind,
                    Range = fullRange,  // Range = full range (declaration + body) per LSP spec
                    Detail = $"Function returning {context.type().GetText()}",
                    SelectionRange = selectionRange,  // SelectionRange = only the declaration name
                    FilePath = _filePath
                };

                Symbols.Add(symbol);
                AttachMember(symbol);
            }

            return base.VisitFunctionDeclaration(context);
        }

        public override Mql4Symbol? VisitVariableDeclaration([NotNull] Mql4GrammarParser.VariableDeclarationContext context)
        {
            // Get variable names from variableDeclarator (new grammar structure)
            // Note: variableDeclarator() returns an array — every declarator
            // declares a name ("uchar src[], dst[], key[32];"): issue #109's
            // unresolved-symbol false positives came from emitting only the first.
            var declarators = context.variableDeclarator();
            if (declarators != null && declarators.Length > 0)
            {
              foreach (var declarator in declarators)
              {
                var nameToken = declarator.IDENTIFIER();
                if (nameToken != null)
                {
                    var name = nameToken.GetText();
                    var range = CreateRangeFromToken(nameToken.Symbol);

                    // Check for modifiers (input, extern, static, etc.)
                    // NEW GRAMMAR: modifiers? type modifiers? (modifiers can appear before AND after type)
                    var modifierTokens = new List<string>();

                    // Check first modifiers (before type)
                    if (context.modifiers(0) != null)
                    {
                        var modifiersContext = context.modifiers(0);
                        if (modifiersContext.K_INPUT() != null) modifierTokens.Add("input");
                        if (modifiersContext.K_EXTERN() != null) modifierTokens.Add("extern");
                        if (modifiersContext.K_STATIC() != null) modifierTokens.Add("static");
                        if (modifiersContext.K_CONST() != null) modifierTokens.Add("const");
                        if (modifiersContext.K_SINPUT() != null) modifierTokens.Add("sinput");
                    }

                    // Check second modifiers (after type) - NEW!
                    if (context.modifiers(1) != null)
                    {
                        var modifiersContext = context.modifiers(1);
                        if (modifiersContext.K_INPUT() != null) modifierTokens.Add("input");
                        if (modifiersContext.K_EXTERN() != null) modifierTokens.Add("extern");
                        if (modifiersContext.K_STATIC() != null) modifierTokens.Add("static");
                        if (modifiersContext.K_CONST() != null) modifierTokens.Add("const");
                        if (modifiersContext.K_SINPUT() != null) modifierTokens.Add("sinput");
                    }

                    string modifier = string.Join(" ", modifierTokens);

                    // Build detail with modifiers and type
                    string detail;
                    var typeText = context.type().GetText();
                    if (!string.IsNullOrEmpty(modifier))
                    {
                        detail = $"{modifier} {typeText} {name}";
                    }
                    else
                    {
                        detail = $"{typeText} {name}";
                    }

                    var symbol = new Mql4Symbol
                    {
                        Name = name,
                        Kind = LspSymbolKind.Variable,
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
            }

            return base.VisitVariableDeclaration(context);
        }

        public override Mql4Symbol? VisitParameter([NotNull] Mql4GrammarParser.ParameterContext context)
        {
            // Parameters are anonymous in some positions ("void f(int)") — only capture named ones.
            var nameToken = context.IDENTIFIER();
            if (nameToken != null)
            {
                var name = nameToken.GetText();
                var range = CreateRangeFromToken(nameToken.Symbol);

                var symbol = new Mql4Symbol
                {
                    Name = name,
                    Kind = LspSymbolKind.Variable,
                    Range = range,
                    SelectionRange = range,
                    Detail = $"{context.type().GetText()} {name}",
                    DeclaredType = context.type().GetText(),
                    FilePath = _filePath
                };

                Symbols.Add(symbol);
                AttachMember(symbol);
            }

            return base.VisitParameter(context);
        }

        /// <summary>
        /// REQ-SM-02: attach class members as Children of the innermost enclosing type symbol.
        /// No-op for top-level declarations.
        /// </summary>
        private void AttachMember(Mql4Symbol member)
        {
            if (_currentTypeStack.Count == 0)
                return;

            var enclosingType = _currentTypeStack.Peek();
            if (!enclosingType.Children.Contains(member))
            {
                enclosingType.Children.Add(member);
            }
        }

        public override Mql4Symbol? VisitDirective([NotNull] Mql4GrammarParser.DirectiveContext context)
        {
            // Handle PRE_INCLUDE, PRE_PROPERTY, PRE_IMPORT tokens
            // These tokens contain the full directive text (e.g., "#include \"file.mqh\"")

            if (context.PRE_INCLUDE() != null)
            {
                // Extract include path from token text
                var tokenText = context.PRE_INCLUDE().GetText(); // e.g., "#include \"file.mqh\""
                var includePath = ExtractIncludePath(tokenText);
                if (!string.IsNullOrEmpty(includePath))
                {
                    Includes.Add(includePath);
                }
            }
            else if (context.PRE_IMPORT() != null)
            {
                // Extract import library name from token text
                var tokenText = context.PRE_IMPORT().GetText(); // e.g., "#import \"library.dll\""
                // Could also track imports if needed for LSP features
            }
            else if (context.PRE_PROPERTY() != null)
            {
                // Extract property from token text
                var tokenText = context.PRE_PROPERTY().GetText(); // e.g., "#property copyright \"Author\""
                // Could track properties if needed for LSP features
            }

            return base.VisitDirective(context);
        }

        private string? ExtractIncludePath(string tokenText)
        {
            // Issue #25a: single include-resolution service (regex extraction
            // of the quoted/angle-bracket path). Token format:
            // #include "file.mqh" or #include <path/file.mqh>.
            // NOTE: fully qualified because this visitor class sits after the
            // closing brace of the file's namespace block (pre-existing brace
            // imbalance), so unqualified lookup would miss it.
            return MqlLanguageServer.Parser.IncludePathResolver.ExtractFromDirective(tokenText);
        }

        private LspRange CreateRangeFromToken(IToken token)
        {
            return new LspRange
            (
                new LspPosition(token.Line - 1, token.Column),
                new LspPosition(token.Line - 1, token.Column + token.Text.Length)
            );
        }

        private LspRange CreateRangeFromContext(ParserRuleContext context)
        {
            return new LspRange
            (
                new LspPosition(context.Start.Line - 1, context.Start.Column),
                new LspPosition(context.Stop.Line - 1, context.Stop.Column + context.Stop.Text.Length)
            );
        }

        /// <summary>
        /// Calculate the full range of a function including its body.
        /// Uses the block's RBRACE token to ensure accurate end position.
        /// ANTLR uses 1-indexed lines, LSP uses 0-indexed, so we subtract 1.
        /// </summary>
        private LspRange CreateFullFunctionRange(Mql4GrammarParser.FunctionDeclarationContext context, IToken nameToken)
        {
            // Start from the function name token (not the return type)
            var startLine = nameToken.Line - 1;
            var startColumn = nameToken.Column;

            // Find the end position - prefer RBRACE from block if available
            var blockContext = context.block();
            if (blockContext != null && blockContext.RBRACE() != null)
            {
                // Use the RBRACE token as the end marker
                var rbrace = blockContext.RBRACE().Symbol;
                return new LspRange(
                    new LspPosition(startLine, startColumn),
                    new LspPosition(rbrace.Line - 1, rbrace.Column + rbrace.Text.Length)
                );
            }

            // Fallback: use context.Stop (may not include full body)
            return new LspRange(
                new LspPosition(startLine, startColumn),
                new LspPosition(context.Stop.Line - 1, context.Stop.Column + context.Stop.Text.Length)
            );
        }
    }

