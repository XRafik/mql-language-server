using System;
using System.Collections.Generic;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace MqlLanguageServer.Models;

/// <summary>
/// Issue #184: scope-aware ordering for the name-index candidates of one
/// lookup position. <see cref="MqlFile.SymbolIndex"/> is a flat, position-blind
/// index: it lists every declaration of a name in document order, and taking
/// the first entry let a local declared in one function shadow a same-named
/// declaration — and, after #182, the builtin registry — in every other
/// function of the document. The precedence implemented here:
///
/// S1 — a candidate whose scope extent encloses the lookup position wins,
///      innermost extent first. For a declaration nested inside a function or
///      method the scope extent is the enclosing function/method's range (the
///      declaration's own range is just its name token); for a document-scope
///      candidate it is the candidate's own range (a function's full body, a
///      global's name token).
/// S2 — with no enclosing candidate, a document-scope candidate (its range is
///      not contained in any function/method declaration's range of the same
///      document) beats a candidate nested inside another function. Foreign
///      nested candidates are skipped entirely: they are not visible at the
///      lookup position, so they must not shadow document declarations or the
///      builtin registry either (S3 keeps the registry as the last fallback).
/// S4 — symbols merged from included files (a FilePath other than the
///      document's) compete in the document-scope class; their ranges are
///      coordinates of another file and can neither enclose a position in
///      this document nor be nested in it.
/// S5 — within one precedence class document order decides, so repeated
///      lookups return the same symbol (the pre-existing behavior).
/// </summary>
public static class SymbolScopeResolver
{
    /// <summary>
    /// True for the declarations whose ranges act as scope containers for the
    /// S2 classification: functions and methods. MQL5 visitors set SymbolType;
    /// MQL4 visitors leave it null (CCR-05 tolerance) and classify through the
    /// LSP Kind instead.
    /// </summary>
    public static bool IsFunctionLike(MqlSymbol symbol)
    {
        if (symbol.SymbolType is SymbolType.Function or SymbolType.Method)
        {
            return true;
        }

        // MQL4 tolerance (CCR-05): Kind is set, SymbolType stays null.
        return symbol.SymbolType is null
            && (symbol.Kind == SymbolKind.Function || symbol.Kind == SymbolKind.Method);
    }

    /// <summary>
    /// S6: collect the function/method declarations of the document itself.
    /// Built once per file next to <see cref="MqlFile.SymbolIndex"/> and cached
    /// on the file, so a lookup never rescans all symbols. Included files'
    /// symbols never act as containers (S4): their ranges are coordinates of
    /// another file.
    /// </summary>
    public static List<MqlSymbol> CollectScopeContainers(MqlFile file)
    {
        var containers = new List<MqlSymbol>();

        foreach (var symbol in file.Symbols)
        {
            if (!IsFunctionLike(symbol))
            {
                continue;
            }

            if (!string.Equals(symbol.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            containers.Add(symbol);
        }

        return containers;
    }

    /// <summary>
    /// Pick the candidate visible at the 0-based (line, column) from the flat
    /// name-index candidate list, following the S1/S2/S4/S5 precedence. Returns
    /// null when no candidate encloses the position and none is declared at
    /// document scope; the caller then falls through to the builtin registry
    /// (S3).
    /// </summary>
    public static MqlSymbol? SelectCandidate(
        IReadOnlyList<MqlSymbol> candidates,
        List<MqlSymbol> scopeContainers,
        string documentPath,
        int line,
        int column)
    {
        MqlSymbol? enclosing = null;
        var enclosingLines = int.MaxValue;
        var enclosingCharacters = int.MaxValue;
        MqlSymbol? documentScope = null;

        foreach (var candidate in candidates)
        {
            LspRange? extent;
            var isDocumentScope = false;

            if (!string.Equals(candidate.FilePath, documentPath, StringComparison.OrdinalIgnoreCase))
            {
                // S4: included symbols compete at document scope, but their
                // ranges are coordinates of another file and never enclose
                // positions in this document.
                extent = null;
                isDocumentScope = true;
            }
            else
            {
                var container = FindInnermostContainer(scopeContainers, candidate);
                if (container != null)
                {
                    // Nested declaration: its scope extent is the enclosing
                    // function/method, not its own name token.
                    extent = container.Range;
                }
                else
                {
                    // Document scope: a function's own range covers its body,
                    // a global's covers its name token.
                    extent = candidate.Range;
                    isDocumentScope = true;
                }
            }

            if (extent != null && ContainsPosition(extent, line, column))
            {
                var extentLines = extent.End.Line - extent.Start.Line;
                var extentCharacters = extent.End.Character - extent.Start.Character;

                // S1: the innermost enclosing declaration wins; the strict
                // comparison keeps the earlier (document-order) candidate on
                // equal extents (S5).
                if (extentLines < enclosingLines
                    || (extentLines == enclosingLines && extentCharacters < enclosingCharacters))
                {
                    enclosingLines = extentLines;
                    enclosingCharacters = extentCharacters;
                    enclosing = candidate;
                }
            }
            else if (isDocumentScope && documentScope == null)
            {
                // S2/S5: the first document-scope candidate in document order.
                documentScope = candidate;
            }

            // else: nested in a foreign function — not visible here; skipped.
        }

        return enclosing ?? documentScope;
    }

    /// <summary>
    /// Innermost same-file function/method whose range contains the candidate's
    /// range, or null when the candidate is not nested in one. A function never
    /// nests inside itself, so the candidate is skipped by reference.
    /// </summary>
    private static MqlSymbol? FindInnermostContainer(List<MqlSymbol> scopeContainers, MqlSymbol candidate)
    {
        MqlSymbol? innermost = null;
        var innermostLines = int.MaxValue;
        var innermostCharacters = int.MaxValue;

        foreach (var container in scopeContainers)
        {
            if (ReferenceEquals(container, candidate))
            {
                continue;
            }

            if (!ContainsRange(container.Range, candidate.Range))
            {
                continue;
            }

            var containerLines = container.Range.End.Line - container.Range.Start.Line;
            var containerCharacters = container.Range.End.Character - container.Range.Start.Character;

            if (containerLines < innermostLines
                || (containerLines == innermostLines && containerCharacters < innermostCharacters))
            {
                innermostLines = containerLines;
                innermostCharacters = containerCharacters;
                innermost = container;
            }
        }

        return innermost;
    }

    /// <summary>
    /// Inclusive position containment, matching the parsers' IsPositionInRange
    /// semantics.
    /// </summary>
    private static bool ContainsPosition(LspRange range, int line, int column)
    {
        if (line < range.Start.Line || line > range.End.Line)
        {
            return false;
        }

        if (line == range.Start.Line && column < range.Start.Character)
        {
            return false;
        }

        if (line == range.End.Line && column > range.End.Character)
        {
            return false;
        }

        return true;
    }

    private static bool ContainsRange(LspRange outer, LspRange inner)
    {
        return StartsAtOrAfter(inner.Start, outer.Start)
            && EndsAtOrBefore(inner.End, outer.End);
    }

    private static bool StartsAtOrAfter(Position position, Position start)
    {
        return position.Line > start.Line
            || (position.Line == start.Line && position.Character >= start.Character);
    }

    private static bool EndsAtOrBefore(Position position, Position end)
    {
        return position.Line < end.Line
            || (position.Line == end.Line && position.Character <= end.Character);
    }
}
