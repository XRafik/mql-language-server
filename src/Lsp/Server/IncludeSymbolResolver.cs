using System;
using System.IO;
using System.Linq;

using MqlLanguageServer.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Resolves a cursor identifier to a symbol declared in an included file (issue #92).
///
/// <para><see cref="Parser.IMqlParser.FindSymbolDefinition(MqlFile, string, int, int)"/>
/// extracts the identifier under the cursor and looks it up in the requesting
/// document's own symbol list only. A call site of a function declared in an
/// included file (e.g. <c>StopLong</c> in <c>main.mq4</c> with the declaration
/// in <c>stop_utils.mqh</c>) therefore resolves to nothing, and the position
/// handlers (hover, definition, references, documentHighlight, declaration)
/// degrade: hover falls back to the enclosing function and the others return
/// empty results.</para>
///
/// <para>The symbol is not missing from the workspace, though: didOpen's
/// include resolution (and the workspace scan) index include-declared symbols
/// into the <see cref="GlobalSymbolIndex"/> under the includer's language.
/// This resolver extracts the exact identifier at the cursor from the
/// parse-time occurrence index (OCC-01 — model text, never stale disk text,
/// per the issue #86 buffer-vs-disk rule) and looks it up in the global
/// index, skipping candidates declared in the requesting document itself:
/// in-file names were already covered (case-insensitively) by the failing
/// lookup, so a same-file candidate here could only shadow the cross-file
/// definition.</para>
/// </summary>
public static class IncludeSymbolResolver
{
    /// <summary>
    /// Resolve the identifier at the cursor (0-based line/column) to a
    /// symbol declared outside the requesting document, or null when the
    /// position is not on an identifier or no indexed candidate exists.
    /// </summary>
    public static MqlSymbol? TryResolve(
        MqlFile file, int line0, int character0, GlobalSymbolIndex index)
    {
        if (file == null || index == null)
        {
            return null;
        }

        var identifier = TypeDeclarationResolver.GetCursorIdentifier(file, line0, character0);
        if (string.IsNullOrEmpty(identifier))
        {
            return null;
        }

        foreach (var candidate in index.FindSymbol(identifier))
        {
            if (candidate?.Symbol == null || string.IsNullOrEmpty(candidate.FilePath))
            {
                continue;
            }

            // In-file declarations were already covered by the failed
            // in-file lookup; only cross-file (include-declared) candidates
            // carry new information here.
            if (string.Equals(candidate.FilePath, file.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!File.Exists(candidate.FilePath))
            {
                continue;
            }

            return candidate.Symbol;
        }

        return null;
    }
}