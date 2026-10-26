using System.Collections.Generic;

namespace MqlLanguageServer.Models;

/// <summary>
/// Represents an MQL file (.mq4, .mq5, or .mqh)
/// </summary>
public class MqlFile
{
    /// <summary>
    /// File path
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// File content
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Language of the file (MQL4 or MQL5)
    /// </summary>
    public MqlLanguage Language { get; set; } = MqlLanguage.Mql4;

    /// <summary>
    /// Parsed symbols
    /// </summary>
    public List<Mql4Symbol> Symbols { get; set; } = new();

    /// <summary>
    /// Included files
    /// </summary>
    public List<string> Includes { get; set; } = new();

    /// <summary>
    /// Preprocessor macros (extracted via token scanning)
    /// </summary>
    public List<string> Macros { get; set; } = new();

    /// <summary>
    /// Syntax errors detected by the parser during parsing.
    /// </summary>
    public List<SyntaxError> SyntaxErrors { get; set; } = new();

    /// <summary>
    /// Identifier token occurrences captured during the last parse (OCC-01).
    /// Replaced wholesale on every re-parse (OCC-02).
    /// </summary>
    public IReadOnlyList<TokenOccurrence> Occurrences { get; set; } = new List<TokenOccurrence>();

    /// <summary>
    /// Color literal occurrences (C'r,g,b', hex, clr* names) captured during
    /// the last parse (REQ-CP-01..03). Additive, replaced wholesale per parse
    /// (Occurrences pattern), refreshed by didChange re-parsing (REQ-CP-10).
    /// </summary>
    public IReadOnlyList<ColorOccurrence> ColorOccurrences { get; set; } = new List<ColorOccurrence>();

    /// <summary>
    /// OPTIMIZATION: Cached symbol index for fast lookups by name
    /// Built lazily on first access and reused for subsequent lookups
    /// </summary>
    public Dictionary<string, List<Mql4Symbol>>? SymbolIndex { get; set; }

    /// <summary>
    /// Issue #184 (S6): cached function/method declarations of this document
    /// (FilePath matching the document's), used by SymbolScopeResolver to
    /// classify name-index candidates as document-scope vs nested in a
    /// function. Built lazily next to <see cref="SymbolIndex"/> on the first
    /// name lookup.
    /// </summary>
    public List<Mql4Symbol>? ScopeContainerSymbols { get; set; }
}
