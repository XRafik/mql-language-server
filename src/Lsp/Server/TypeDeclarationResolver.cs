using System;
using System.Collections.Generic;
using System.Linq;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Shared declared-type normalization and type-declaration lookup (issue #64).
///
/// <para>Backs <c>textDocument/typeDefinition</c> and <c>definition</c> on
/// instances: a variable with a declared class type must resolve to the
/// class/struct/interface/enum declaration — in the same file when present,
/// otherwise in the file that declares it via the global symbol index
/// (workspace-indexed, e.g. an included .mqh header).</para>
/// </summary>
public static class TypeDeclarationResolver
{
    /// <summary>Builtin value types: typeDefinition has no user declaration to point at.</summary>
    private static readonly HashSet<string> Primitives = new(StringComparer.OrdinalIgnoreCase)
    {
        "void", "int", "short", "long", "char", "uchar", "ushort", "uint", "ulong",
        "bool", "color", "datetime", "string", "double", "float", "long long", "unsigned"
    };

    /// <summary>True for Class/Struct/Interface/Enum symbols (MQL5-classified), or MQL4 legacy class symbols whose SymbolType stays null but whose LSP Kind is Class (CCR-05 tolerance).</summary>
    public static bool IsTypeCandidate(MqlSymbol? symbol) =>
        symbol != null &&
        (symbol.SymbolType is SymbolType.Class or SymbolType.Struct or SymbolType.Interface or SymbolType.Enum
         || (symbol.SymbolType == null && symbol.Kind == SymbolKind.Class));

    public static bool IsTypeCandidate(SymbolType? symbolType) =>
        symbolType is SymbolType.Class or SymbolType.Struct or SymbolType.Interface or SymbolType.Enum;

    /// <summary>
    /// Exact identifier text captured at the cursor position (0-based) from the
    /// parse-time occurrence index, or null when no identifier token covers it.
    /// </summary>
    public static string? GetCursorIdentifier(MqlFile file, int line0, int col0) =>
        file.Occurrences.FirstOrDefault(o => o.Line == line0 && o.Column <= col0 && col0 < o.Column + o.Length)?.Text;

    /// <summary>
    /// Normalize a declared type to a plain type name: strips pointer/reference
    /// marks, whitespace, and trailing array brackets; returns null for
    /// primitives (int, string, …), where no user type declaration exists.
    /// </summary>
    public static string? NormalizeDeclaredType(string? declaredType)
    {
        if (string.IsNullOrWhiteSpace(declaredType))
            return null;

        var name = declaredType.Trim();
        while (name.Length > 0 && (name[^1] == '*' || name[^1] == '&'))
        {
            name = name[..^1].TrimEnd();
        }

        if (name.Length > 0 && name[^1] == ']')
        {
            var open = name.LastIndexOf('[');
            if (open > 0)
            {
                name = name[..open].TrimEnd();
            }
        }

        name = name.Trim();
        if (name.Length == 0 || Primitives.Contains(name))
            return null;

        return name;
    }

    /// <summary>
    /// Locate the declaration of a type by name: exact-case same-file symbol
    /// first, then same-file case-insensitive, then the global symbol index
    /// (cross-file). Only Class/Struct/Interface/Enum candidates qualify.
    /// <paramref name="location"/>'s Uri points at the declaring file.
    /// </summary>
    public static bool TryResolveTypeLocation(
        string typeName, MqlFile currentFile, DocumentUri currentDocumentUri, GlobalSymbolIndex index, out Location location)
    {
        location = new Location();

        if (string.IsNullOrWhiteSpace(typeName))
            return false;

        var sameFile = currentFile.Symbols.FirstOrDefault(s =>
                string.Equals(s.Name, typeName, StringComparison.Ordinal) && IsTypeCandidate(s))
            ?? currentFile.Symbols.FirstOrDefault(s =>
                string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase) && IsTypeCandidate(s));

        if (sameFile != null)
        {
            location = new Location { Uri = currentDocumentUri, Range = sameFile.Range };
            return true;
        }

        // Cross-file: the global index keys are the captured symbol names
        // (case-sensitive), so an exact query avoids the same-file
        // variable/class case shadowing on purpose.
        //
        // Issue #89 reachability guard: a type-name identifier with no in-file
        // declaration must not resolve to a same-named type in an UNRELATED
        // file (the reporter's case: free function Format(string) in an included
        // helper + a foreign class Format in a never-included Utils.mqh — the
        // pre-guard fallback answered the foreign class instead of null).
        // Candidates are restricted to files linked to the queried document
        // through the include graph (either direction). When the queried
        // document has no include edges at all (degraded graph — no scan, never
        // opened), the guard is skipped: name-keyed resolution is the documented
        // tier-2 behavior, and never resolving would regress #64.
        var hasEdges = index.GetDependencies(currentFile.FilePath).Count > 0
                    || index.GetDependentFiles(currentFile.FilePath).Count > 0;

        foreach (var candidate in index.FindSymbol(typeName))
        {
            if (!IsTypeCandidate(candidate.Symbol) || string.IsNullOrEmpty(candidate.FilePath))
            {
                continue;
            }

            if (hasEdges && !IsReachableThroughIncludes(currentFile.FilePath, candidate.FilePath, index))
            {
                continue;
            }

            if (!System.IO.File.Exists(candidate.FilePath))
            {
                continue;
            }

            location = new Location
            {
                Uri = DocumentUri.File(candidate.FilePath),
                Range = candidate.Symbol!.Range
            };
            return true;
        }

        return false;
    }

    /// <summary>
    /// Issue #89: true when the two files are linked through the include graph
    /// (A includes B, or B includes A). The graph is populated by didOpen's
    /// include loop and by the workspace scan (issue #90). Direct edges only —
    /// the MQL include chains this guard needs are one hop (consumer → header).
    /// </summary>
    public static bool IsReachableThroughIncludes(string filePathA, string filePathB, GlobalSymbolIndex index)
    {
        if (string.IsNullOrEmpty(filePathA) || string.IsNullOrEmpty(filePathB) || index == null)
        {
            return false;
        }

        return index.GetDependencies(filePathA).Contains(filePathB, StringComparer.OrdinalIgnoreCase)
            || index.GetDependentFiles(filePathA).Contains(filePathB, StringComparer.OrdinalIgnoreCase);
    }
}