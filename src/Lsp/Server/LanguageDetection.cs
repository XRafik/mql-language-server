using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MqlLanguageServer.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Detects whether a document is MQL4 or MQL5.
/// </summary>
public static class LanguageDetection
{
    /// <summary>
    /// MQL5-exclusive tokens used for content sniffing on .mqh files.
    /// F9: `using` and `final` are intentionally excluded because they are valid in MQL4.
    /// </summary>
    /// <remarks>
    /// Tradeoff: a genuinely-MQL5 .mqh that uses none of these tokens falls back to the
    /// MQL4 default. This is an inherent ambiguity of content sniffing; issue #16 Phase 2
    /// mitigates it by routing .mqh files by their includer. The predefined variables
    /// `_Digits`, `_Point`, `_Symbol`, and `_Period` are excluded here on purpose: they are
    /// valid in both MQL4 (build 600+) and MQL5, so they must not be treated as MQL5 markers.
    /// Issue #124: when sniffing .mqh content, tokens inside comments no longer flip the
    /// dialect — the token scan runs over a comment-stripped copy of the content (see
    /// <see cref="StripCommentsForSniffing"/>). This list itself is unchanged and remains
    /// the single source of truth for the LanguageMisuseRule (issue #28), which still scans
    /// raw document text.
    /// </remarks>
    private static readonly string[] Mql5Tokens =
    {
        "nullptr",
        "#resource",
        "union",
        "pack(",
        "enum class"
    };

    /// <summary>
    /// Read-only exposure of <see cref="Mql5Tokens"/> for consumers that need
    /// the MQL5-exclusive marker list without duplicating it (issue #28:
    /// LanguageMisuseRule flags these tokens in MQL4 documents). Single
    /// source of truth for both content sniffing and semantic diagnostics.
    /// </summary>
    public static IReadOnlyList<string> Mql5Markers => Mql5Tokens;

    /// <summary>
    /// Detect the language of a document from client languageId, URI, and content.
    /// </summary>
    public static MqlLanguage Detect(Uri uri, string? languageId, string content)
    {
        if (languageId == Constants.Languages.Mql5)
            return MqlLanguage.Mql5;

        if (languageId == Constants.Languages.Mql4)
            return MqlLanguage.Mql4;

        // Unknown or null languageId: fall through to extension/content sniff.
        // A non-null but unrecognized languageId (e.g. "mqh") must NOT short-circuit
        // to the MQL4 default; the extension and content carry the real signal.
        var extension = Path.GetExtension(uri.AbsolutePath);

        if (extension.Equals(".mq5", StringComparison.OrdinalIgnoreCase))
            return MqlLanguage.Mql5;

        if (extension.Equals(".mq4", StringComparison.OrdinalIgnoreCase))
            return MqlLanguage.Mql4;

        // Only sniff content for include files (.mqh) regardless of languageId.
        // Issue #124: sniff the comment-stripped copy so a lone MQL5-exclusive token
        // in a comment (a signature note, a disabled helper, prose) does not flip the
        // whole header to the MQL5 pipeline. Tokens in code and string literals still do.
        if (extension.Equals(".mqh", StringComparison.OrdinalIgnoreCase))
        {
            var sniffable = StripCommentsForSniffing(content);
            foreach (var token in Mql5Tokens)
            {
                if (sniffable.Contains(token, StringComparison.Ordinal))
                    return MqlLanguage.Mql5;
            }
        }

        return MqlLanguage.Mql4; // default per REQ-LD-03
    }

    /// <summary>
    /// Issue #124: produce a copy of <paramref name="content"/> with line (//) and
    /// block (/* */) comments replaced by a single space, so content sniffing never
    /// reacts to MQL5-exclusive tokens that only appear inside comments. String and
    /// character literals are tracked so comment markers inside them (e.g. a URL in
    /// a string) are not mistaken for comments, and comment markers can never hide
    /// real code on the same line. Unterminated comments degrade gracefully: the
    /// remainder of the file is treated as comment (matching compiler behavior).
    /// The input is returned unmodified when it contains no comment start marker.
    /// </summary>
    private static string StripCommentsForSniffing(string content)
    {
        if (string.IsNullOrEmpty(content))
            return content;

        if (!content.Contains("//", StringComparison.Ordinal) &&
            !content.Contains("/*", StringComparison.Ordinal))
            return content;

        var buffer = new char[content.Length];
        var write = 0;
        var i = 0;

        while (i < content.Length)
        {
            var c = content[i];

            if (c == '"' || c == '\'')
            {
                // Copy the literal verbatim, honoring backslash escapes, so a
                // "//" or "/*" inside it is never treated as a comment.
                var quote = c;
                buffer[write++] = c;
                i++;
                while (i < content.Length)
                {
                    buffer[write++] = content[i];
                    if (content[i] == '\\' && i + 1 < content.Length)
                    {
                        buffer[write++] = content[i + 1];
                        i += 2;
                        continue;
                    }

                    var closed = content[i] == quote;
                    i++;
                    if (closed)
                        break;
                }

                continue;
            }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '/')
            {
                // Line comment: collapse to one space, keep the newline so line
                // structure (and the test above for tokens after comments) holds.
                buffer[write++] = ' ';
                while (i < content.Length && content[i] != '\n')
                    i++;
                continue;
            }

            if (c == '/' && i + 1 < content.Length && content[i + 1] == '*')
            {
                // Block comment: collapse to one space; unterminated runs to EOF.
                buffer[write++] = ' ';
                i += 2;
                while (i < content.Length &&
                       !(content[i] == '*' && i + 1 < content.Length && content[i + 1] == '/'))
                    i++;
                i = Math.Min(i + 2, content.Length);
                continue;
            }

            buffer[write++] = c;
            i++;
        }

        return new string(buffer, 0, write);
    }

    /// <summary>
    /// Resolve the LSP languageId string ("mql4" or "mql5") for a document URI based on
    /// its file extension only. Used by the text-document sync registration to route
    /// documents to the correct language without inspecting content.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="Path.GetExtension(string)"/> rather than <see cref="string.EndsWith(string)"/>
    /// so that a file like <c>backup.mq5.bak</c> is NOT misclassified as MQL5 — only a real
    /// <c>.mq5</c> extension routes to MQL5. The <c>.mqh</c> include extension and any other
    /// extension default to MQL4; <c>.mqh</c> content sniffing is handled separately by
    /// <see cref="Detect(Uri, string?, string)"/> once the document is opened.
    /// </remarks>
    public static string GetLanguageIdFromUri(Uri uri)
    {
        var extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".mq5", StringComparison.OrdinalIgnoreCase)
            ? Constants.Languages.Mql5
            : Constants.Languages.Mql4;
    }
}
