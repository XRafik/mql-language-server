using System;
using System.Collections.Generic;
using System.IO;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Issue #157: an immutable matcher parsed from a project-local
/// <c>.mqlignore</c> file. The workspace scan (<see cref="WorkspaceIndexer"/>)
/// loads one per workspace folder and prunes matching subtrees and files
/// entirely — a third prune source alongside the built-in
/// <c>ExcludedDirectoryNames</c> and the dot-directory rule.
///
/// <para>Gitignore (gitwildmatch) subset, last-match-wins:</para>
/// <list type="bullet">
/// <item><c>#</c> comments and blank lines are skipped.</item>
/// <item>A trailing <c>/</c> makes the pattern directory-only (it never
/// matches a same-named file).</item>
/// <item>A leading <c>/</c> (or any embedded separator) anchors the pattern
/// to the workspace-folder root; otherwise the pattern matches the entry's
/// name at any depth.</item>
/// <item>A leading <c>!</c> negates: it re-includes a path matched by an
/// earlier <c>.mqlignore</c> pattern. Negation cannot resurrect the built-in
/// exclusions (v1 decision): <c>ExcludedDirectoryNames</c>, dot-directories,
/// and non-overrideable enumeration rules always win; and because a matched
/// directory is pruned before descending, a negation cannot re-include
/// children of a pruned directory either (same limitation as git).</item>
/// <item><c>*</c> and <c>?</c> match within a single path segment; <c>**</c>
/// as a full segment matches zero or more segments (a trailing
/// <c>/**</c> matches everything <em>inside</em>, not the directory itself).</item>
/// </list>
///
/// <para>Robustness (WI-05 spirit): malformed lines are skipped silently and
/// parsing never throws; CRLF line endings and a UTF-8 BOM are handled (the
/// file is typically authored on Windows next to MQL sources). Matching is
/// case-insensitive, consistent with the indexer's path handling
/// (OrdinalIgnoreCase throughout).</para>
///
/// <para>Documented scope (issue #157, analysis points 1–4):</para>
/// <list type="bullet">
/// <item><b>Index-only</b>: the ignore governs the workspace index, never the
/// document pipeline — <c>didOpen</c> on an ignored file still receives full
/// document-level analysis (the client buffer is authoritative).</item>
/// <item><b>Ignored .mqh</b>: an ignored header is not indexed and
/// contributes no includer-language evidence to the scan's pass B; live
/// sources that include it will surface unresolved-symbol diagnostics for
/// its declarations. That is the intended semantics: "ignored" means
/// "outside the index".</item>
/// <item><b>Dependency edges</b>: a live file's resolved include into an
/// ignored directory is still recorded as a dependency-graph edge (to a
/// non-indexed target) — harmless: consumers (rename reachability guard,
/// include-closure correlation) find no symbols there and degrade gracefully.</item>
/// <item><b>No watcher</b>: the file is read once per scan at startup;
/// editing <c>.mqlignore</c> requires a server restart.</item>
/// </list>
/// </summary>
public sealed class MqlIgnoreSpec
{
    private readonly List<Pattern> _patterns;

    private MqlIgnoreSpec(List<Pattern> patterns) => _patterns = patterns;

    /// <summary>Number of effective (non-comment, non-blank) patterns.</summary>
    public int PatternCount => _patterns.Count;

    /// <summary>
    /// Parse the <c>.mqlignore</c> file at <paramref name="path"/>. Returns
    /// false (fail-open, spec = null) when the file cannot be read — the
    /// caller then behaves exactly as if the file were absent. Malformed
    /// lines are skipped silently inside an otherwise valid file.
    /// </summary>
    public static bool TryParseFile(string path, out MqlIgnoreSpec? spec)
    {
        spec = null;
        try
        {
            // ReadAllLines detects and strips the UTF-8 BOM and splits
            // uniformly on CRLF/LF.
            spec = Parse(File.ReadAllLines(path));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable .mqlignore behaves as if absent (WI-05 spirit):
            // never throw from the scan path.
            return false;
        }
    }

    /// <summary>
    /// Parse <c>.mqlignore</c> content line by line. See the type
    /// documentation for the supported syntax subset.
    /// </summary>
    public static MqlIgnoreSpec Parse(IEnumerable<string>? lines)
    {
        var patterns = new List<Pattern>();
        foreach (var raw in lines ?? Array.Empty<string>())
        {
            var line = raw?.TrimEnd() ?? string.Empty;
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue; // blank line or comment

            var negated = false;
            if (line.StartsWith("!", StringComparison.Ordinal))
            {
                negated = true;
                line = line[1..];
            }

            var directoryOnly = false;
            if (line.EndsWith("/", StringComparison.Ordinal))
            {
                directoryOnly = true;
                line = line.TrimEnd('/');
            }

            if (line.Length == 0)
                continue; // malformed ("!" or "/" alone) — skip silently

            // Git semantics: a separator at the start (or middle) anchors the
            // pattern to the folder root; a bare name matches at any depth.
            var anchored = line.Contains('/');
            line = line.TrimStart('/');

            var segments = line.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue; // malformed — skip silently

            patterns.Add(new Pattern(segments, directoryOnly, negated, anchored));
        }

        return new MqlIgnoreSpec(patterns);
    }

    /// <summary>
    /// Returns whether <paramref name="relativePath"/> (relative to the
    /// workspace-folder root, '/'-separated) matches the spec. The last
    /// matching pattern wins; no match means "not ignored". Directory-only
    /// patterns never match files and vice versa.
    /// </summary>
    public bool IsMatch(string relativePath, bool isDirectory)
    {
        if (_patterns.Count == 0 || string.IsNullOrEmpty(relativePath))
            return false;

        var normalized = relativePath.Replace('\\', '/').Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var ignored = false;
        foreach (var pattern in _patterns)
        {
            if (pattern.DirectoryOnly && !isDirectory)
                continue;

            var matched = pattern.Anchored
                ? MatchSegments(pattern.Segments, 0, segments, 0)
                : GlobMatch(pattern.Segments[0], segments[^1]);

            if (matched)
                ignored = !pattern.Negated;
        }

        return ignored;
    }

    /// <summary>
    /// Segment-wise match of an anchored pattern against a path.
    /// <c>**</c> as a full segment matches zero or more segments (a trailing
    /// <c>/**</c> requires at least one segment inside — "src/**" does not
    /// ignore "src" itself); all other segments match one-for-one via
    /// <see cref="GlobMatch"/>.
    /// </summary>
    private static bool MatchSegments(string[] patternSegs, int pi, string[] pathSegs, int si)
    {
        while (pi < patternSegs.Length)
        {
            var segment = patternSegs[pi];
            if (segment == "**")
            {
                if (pi + 1 == patternSegs.Length)
                    return si < pathSegs.Length; // trailing '/**': everything inside

                for (var i = si; i <= pathSegs.Length; i++)
                {
                    if (MatchSegments(patternSegs, pi + 1, pathSegs, i))
                        return true;
                }

                return false;
            }

            if (si >= pathSegs.Length || !GlobMatch(segment, pathSegs[si]))
                return false;

            pi++;
            si++;
        }

        return si == pathSegs.Length;
    }

    /// <summary>
    /// Single-segment wildcard match. <c>*</c> and <c>?</c> never cross
    /// '/' (segments are already split); consecutive asterisks within a
    /// segment (gitwildmatch's invalid form) degrade to a regular '*'.
    /// Case-insensitive, consistent with the indexer's path handling.
    /// </summary>
    private static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, starP = -1, starT = -1;
        while (t < text.Length)
        {
            if (p < pattern.Length &&
                (pattern[p] == '?' ||
                 char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
            p++;

        return p == pattern.Length;
    }

    /// <summary>One parsed .mqlignore line.</summary>
    private sealed record Pattern(string[] Segments, bool DirectoryOnly, bool Negated, bool Anchored);
}
