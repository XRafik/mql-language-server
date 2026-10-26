using System.Collections.Generic;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Models;

/// <summary>
/// Represents a symbol in MQL code (function, variable, class, struct, etc.)
/// </summary>
public class MqlSymbol
{
    /// <summary>
    /// Name of the symbol
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Kind of symbol (Function, Variable, etc.) for LSP responses
    /// </summary>
    public SymbolKind Kind { get; set; }

    /// <summary>
    /// MQL-language-level type classification (Class, Struct, Interface, Enum, Function, Variable, Method, Property, Constructor, Destructor, Template)
    /// </summary>
    public SymbolType? SymbolType { get; set; }

    /// <summary>
    /// Declared type of variables, fields, and parameters (e.g. "CTrade" for "CTrade trade;"),
    /// as written in the source. Null for non-variable symbols.
    /// </summary>
    public string? DeclaredType { get; set; }

    /// <summary>
    /// Base class/interface name for class, struct (MQL5) and interface
    /// declarations, as written in the source (issue #123: captured from the
    /// grammar's `COLON accessModifier qualifiedName` clause). Single
    /// inheritance only — MQL has no multiple inheritance. Null when the
    /// declaration has no base or the dialect's grammar does not parse one.
    /// </summary>
    public string? BaseClass { get; set; }

    /// <summary>
    /// Range within the document (full symbol including body for functions)
    /// </summary>
    public Range Range { get; set; } = new();

    /// <summary>
    /// Selection range (just the declaration name)
    /// </summary>
    public Range SelectionRange { get; set; } = new();

    /// <summary>
    /// Detailed description
    /// </summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Children symbols (for hierarchical structure)
    /// </summary>
    public List<MqlSymbol> Children { get; set; } = new();

    /// <summary>
    /// Parent symbol
    /// </summary>
    public MqlSymbol? ParentSymbol { get; set; }

    /// <summary>
    /// Is this a predefined MQL4 symbol?
    /// </summary>
    public bool IsPredefined { get; set; }

    /// <summary>
    /// Source file path
    /// </summary>
    public string FilePath { get; set; } = string.Empty;
}
