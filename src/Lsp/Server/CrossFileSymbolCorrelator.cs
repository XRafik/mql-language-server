using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using MqlLanguageServer.Analysis.Rules;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Issue #44: workspace-correlated suppression tier for unresolved-symbol
/// diagnostics (architecture B — handler-level filter). The semantic rule
/// stays strictly document-local (REQ-IA-02, <see cref="UnresolvedSymbolRule"/>);
/// this correlator runs downstream in <c>DiagnosticHandler</c>, mirroring how
/// <c>CodeActionHandler</c> already correlates via the structured
/// <c>Data</c> payload (<see cref="UnresolvedSymbolRule.UnresolvedSymbolData"/>,
/// camelCase JSON, <c>symbol</c> name — never text extraction).
///
/// <para><b>Tier 1 — include closure</b>: the document's <see cref="MqlFile.Includes"/>
/// entries are walked through <see cref="IncludePathResolver.TryResolveContained"/>
/// (quoted includes) and, for angle-bracket system includes, through
/// <see cref="IncludePathResolver.TryResolveSystemInclude"/> (resolved against
/// the configured system include roots — the MQL4/MQL5 Standard Library
/// headers that the lazy didOpen indexing has already put into the index;
/// entries no root can resolve degrade silently, as before). Each resolved header's own <c>#include</c> entries are extracted with a
/// lightweight line scan over the raw text via
/// <see cref="IncludePathResolver.ExtractFromDirective"/> (no full parse),
/// recursing with a visited set (cycle guard) and a depth cap of 8. A
/// diagnostic whose payload symbol is declared in any closure file (per
/// <see cref="GlobalSymbolIndex.GetFileSymbols(string, MqlLanguage)"/>) is suppressed.</para>
///
/// <para><b>Tier 2 — workspace-correlated</b>: otherwise the symbol is looked
/// up in the same-language secondary index via
/// <see cref="GlobalSymbolIndex.FindSymbol(string, MqlLanguage)"/>; a non-empty
/// result is suppressed.</para>
///
/// <para><b>Fallback (fail open)</b>: when the index is unavailable or empty,
/// the document path is null or not a filesystem path, or a header cannot be
/// read/resolved, the diagnostics are returned unchanged (the current
/// document-local behavior). Results grow monotonically per D5: suppression
/// improves as the workspace scan progresses. A malformed or absent
/// <c>Data</c> payload keeps the diagnostic. The method is read-only over the
/// index and filesystem and honors the handler's cancellation token
/// throughout the walk.</para>
/// </summary>
public static class CrossFileSymbolCorrelator
{
    /// <summary>Maximum include levels walked below the document (matches the macro table's depth cap).</summary>
    private const int MaxIncludeDepth = 8;

    /// <summary>
    /// Returns the diagnostics with workspace-correlated unresolved-symbol
    /// false positives removed. Unresolved-symbol diagnostics are recognized
    /// by the base+70 code for <paramref name="language"/> and by their
    /// structured <c>Data</c> payload; everything else passes through.
    /// </summary>
    public static IReadOnlyList<Diagnostic> SuppressWorkspaceResolvable(
        IReadOnlyList<Diagnostic>? diagnostics,
        MqlFile? mqlFile,
        string? documentPath,
        MqlLanguage language,
        GlobalSymbolIndex? index,
        CancellationToken token)
    {
        if (diagnostics == null || diagnostics.Count == 0 || index == null)
        {
            return diagnostics ?? Array.Empty<Diagnostic>();
        }

        // Non-file documents (untitled buffers, virtual URIs) have no include
        // closure and no workspace anchor: fail open.
        if (string.IsNullOrWhiteSpace(documentPath) || !Path.IsPathRooted(documentPath))
        {
            return diagnostics;
        }

        // Empty index (cold open, scan not started): nothing can correlate.
        if (index.GetIndexedFileKeys().Count == 0)
        {
            return diagnostics;
        }

        var unresolvedCode = ((language == MqlLanguage.Mql5 ? 5000 : 1000)
                              + UnresolvedSymbolRule.UnresolvedSymbolOffset).ToString();
        if (!diagnostics.Any(d => d.Code == unresolvedCode))
        {
            return diagnostics;
        }

        // Tier 1: names declared anywhere in the document's include closure.
        var (closureNames, closurePaths) = CollectIncludeClosureDeclaredNames(mqlFile, documentPath, language, index, token);

        var result = new List<Diagnostic>(diagnostics.Count);
        foreach (var diagnostic in diagnostics)
        {
            token.ThrowIfCancellationRequested();

            // Fail open: not an unresolved-symbol code, or payload unreadable.
            if (diagnostic.Code != unresolvedCode || !TryGetSymbolName(diagnostic, out var symbolName))
            {
                result.Add(diagnostic);
                continue;
            }

            // Tier 1 — declared in an included header.
            if (closureNames.Contains(symbolName))
            {
                continue;
            }

            // Tier 1b (issue #126) — declared in an included header even though
            // the symbol tree does not model it: enum members and enum type
            // names are not captured as symbols by the dialect visitors, but
            // they ARE lexer identifier occurrences in the header (indexed by
            // the workspace scan). A name with any occurrence inside the
            // closure files is resolvable from the document's point of view.
            // Tradeoff (accepted, mirrors the doc-local rule's own flattening):
            // an identifier that only ever appears inside a header function
            // body also suppresses. Comments/strings never yield identifier
            // occurrences, so prose cannot silence diagnostics.
            if (HasOccurrenceInClosure(index, symbolName, language, closurePaths))
            {
                continue;
            }

            // Tier 2 — declared elsewhere in the indexed workspace (same language).
            if (index.FindSymbol(symbolName, language).Count > 0)
            {
                continue;
            }

            result.Add(diagnostic);
        }

        return result;
    }

    /// <summary>
    /// Issue #126: true when the index holds at least one identifier occurrence
    /// of <paramref name="symbolName"/> in a file belonging to the document's
    /// include closure. Read-only index query (O(1) name bucket); the closure
    /// set is built once per diagnostics request.
    /// </summary>
    private static bool HasOccurrenceInClosure(
        GlobalSymbolIndex index,
        string symbolName,
        MqlLanguage language,
        HashSet<string> closurePaths)
    {
        foreach (var occurrence in index.FindOccurrences(symbolName, language))
        {
            if (closurePaths.Contains(occurrence.FilePath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the document's include closure (quoted includes plus angle-bracket
    /// includes that resolve against the configured system include roots,
    /// visited-set cycle guard, depth cap of 8) and collects the names of every
    /// symbol the index knows for the closure files in <paramref name="language"/>.
    /// Unreadable or unresolvable headers degrade silently (fail open).
    /// </summary>
    private static (HashSet<string> Names, HashSet<string> Paths) CollectIncludeClosureDeclaredNames(
        MqlFile? mqlFile,
        string documentPath,
        MqlLanguage language,
        GlobalSymbolIndex index,
        CancellationToken token)
    {
        // MQL identifiers are case-sensitive (like C++): a call site whose
        // casing differs from the declaration is a genuine error and must
        // still surface. Filesystem paths stay case-insensitive (Windows).
        var names = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Seed: the document's own stored include entries (canonical
        // MqlFile.Includes shape — bare path for quoted, <...> for system).
        var frontier = new List<(string Path, int Depth)>();
        if (mqlFile?.Includes != null)
        {
            foreach (var entry in mqlFile.Includes)
            {
                if (IncludePathResolver.TryResolveContained(documentPath, entry, out var headerPath)
                    || IncludePathResolver.TryResolveSystemInclude(entry, out headerPath))
                {
                    frontier.Add((headerPath, 1));
                }
            }
        }

        for (var i = 0; i < frontier.Count; i++)
        {
            token.ThrowIfCancellationRequested();

            var (headerPath, depth) = frontier[i];
            if (depth > MaxIncludeDepth)
            {
                continue;
            }

            // Cycle guard / duplicate-header dedup.
            if (!visited.Add(headerPath))
            {
                continue;
            }

            paths.Add(headerPath);

            var declared = index.GetFileSymbols(headerPath, language);
            if (declared != null)
            {
                foreach (var symbol in declared)
                {
                    if (!string.IsNullOrEmpty(symbol.Name))
                    {
                        names.Add(symbol.Name);
                    }
                }
            }

            // Expand only while under the depth cap.
            if (depth >= MaxIncludeDepth)
            {
                continue;
            }

            foreach (var entry in ExtractIncludesFromRawText(headerPath, token))
            {
                if (IncludePathResolver.TryResolveContained(headerPath, entry, out var nestedPath)
                    || IncludePathResolver.TryResolveSystemInclude(entry, out nestedPath))
                {
                    frontier.Add((nestedPath, depth + 1));
                }
            }
        }

        return (names, paths);
    }

    /// <summary>
    /// Lightweight line scan of a header's raw text for <c>#include</c>
    /// directives (no full parse). Returns the canonical stored-entry shape
    /// produced by <see cref="IncludePathResolver.ExtractFromDirective"/>;
    /// angle-bracket entries pass through and are rejected later by
    /// <see cref="IncludePathResolver.TryResolveContained"/>. Unreadable files
    /// yield nothing (fail open).
    /// </summary>
    private static IEnumerable<string> ExtractIncludesFromRawText(string headerPath, CancellationToken token)
    {
        string text;
        try
        {
            text = SourceFileReader.ReadAllText(headerPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            yield break;
        }

        foreach (var line in text.Split('\n'))
        {
            token.ThrowIfCancellationRequested();

            if (!line.Contains("#include", StringComparison.Ordinal))
            {
                continue;
            }

            var entry = IncludePathResolver.ExtractFromDirective(line);
            if (entry != null)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Extract the symbol name from the diagnostic's structured Data payload
    /// (REQ-HD-06 discipline: never text extraction). Returns false for
    /// missing or malformed payloads — the caller keeps the diagnostic (D6:
    /// defensive parse, mirrors CodeActionHandler).
    /// </summary>
    private static bool TryGetSymbolName(Diagnostic diagnostic, out string symbolName)
    {
        symbolName = string.Empty;
        var data = diagnostic.Data;
        if (data == null)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(data.ToString() ?? string.Empty);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("symbol", out var symbol) &&
                symbol.ValueKind == JsonValueKind.String)
            {
                var name = symbol.GetString();
                if (!string.IsNullOrEmpty(name))
                {
                    symbolName = name;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // Malformed payload: fail open — keep the diagnostic.
        }

        return false;
    }
}
