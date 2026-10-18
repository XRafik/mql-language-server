using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using MqlLanguageServer.Lsp.Server;

namespace MqlLanguageServer.Parser;

/// <summary>
/// Single include-resolution service for <c>#include</c> directives (issue #25a).
///
/// Before this class, extraction and resolution logic existed in four places with
/// behavioral drift:
/// <list type="bullet">
/// <item><see cref="Mql4SymbolVisitor"/> / <c>Mql5SymbolVisitor</c> (the
/// extractors that populate <c>MqlFile.Includes</c>).</item>
/// <item>didOpen/didChange LSP handlers (re-ran the directive-text regex over
/// already-extracted entries — see the drift note below).</item>
/// <item><see cref="Mql4AntlrParser"/> / <c>Mql5AntlrParser</c>
/// <c>ParseFileWithIncludes</c> (per-parser private copies).</item>
/// <item><c>WorkspaceIndexer.ResolveWorkspaceInclude</c> (scan pass).</item>
/// </list>
///
/// <para><b>Verified drift (issue #25a)</b>: <c>MqlFile.Includes</c> stores
/// <i>extracted</i> entries — a bare relative path for
/// <c>#include "file.mqh"</c> and an angle-bracket-wrapped path for
/// <c>#include &lt;file.mqh&gt;</c> (see <see cref="ExtractFromDirective"/>).
/// The handler and parser copies matched the raw-directive regex
/// <c>#include\s+"..."</c> against those stored entries, which never matches
/// (probe: quoted/angle regex on "foo.mqh" and "&lt;foo.mqh&gt;" both fail).
/// The handlers' include loops and the MQL5 parser's include merge were
/// therefore inert in production. This service treats the stored-entry shape
/// as canonical input, which makes didOpen include indexing and the
/// ParseFileWithIncludes merges actually resolve quoted includes — the
/// behavior the WorkspaceIndexer scan pass (and the MQL4 parser, whose stored
/// entries pass straight through its own copy) already had.</para>
///
/// <para><b>Unified resolution semantics</b> (the didOpen path's behavior wins —
/// it is the most guarded copy, and the only one with both a containment guard
/// and dependency tracking):
/// <list type="number">
/// <item>Quoted includes (<c>#include "file.mqh"</c>) resolve relative to the
/// including file's directory; absolute paths pass through unchanged.</item>
/// <item>Angle-bracket includes (<c>#include &lt;stdlib.mqh&gt;</c>) are
/// SYSTEM-library includes. By default they are NOT resolved (the historical
/// behavior, kept as the fail-open fallback). When system include roots are
/// configured — the terminal's <c>MQL5/Include</c> / <c>MQL4/Include</c>
/// directories, delivered via the <c>systemIncludeRoots</c> initialization
/// option — angle entries resolve against them (separator-normalized,
/// Windows-style case-insensitive existence check, traversal rejected) via
/// <see cref="ResolveWithSystemRoots"/> (named separately from
/// <see cref="Resolve(string, string?)"/> so its doc crefs stay unambiguous)
/// or the process-wide provider consumed by <see cref="TryResolveSystemInclude"/>.
/// </item>
/// <item>Resolution integrates the <see cref="PathSecurity.IsContainedInWorkspace"/>
/// containment guard (symlink-aware, fail-closed per issue #21b) — the guard
/// the didOpen path already applied and the parser copies lacked.</item>
/// </list></para>
/// </summary>
public static class IncludePathResolver
{
    private static readonly Regex QuotedIncludeRegex =
        new(@"#include\s+""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex AngleBracketIncludeRegex =
        new(@"#include\s+<([^>]+)>", RegexOptions.Compiled);

    /// <summary>
    /// Process-wide provider of the configured system include roots, set
    /// during server initialization (Program.OnInitialize wires it to
    /// <see cref="SystemIncludeRoots.Current"/>). Null or a null/empty result
    /// keeps the historical fail-open behavior: angle entries stay unresolved.
    /// Tests set it directly via <see cref="SetSystemIncludeRootsProvider"/>.
    /// </summary>
    private static Func<IReadOnlyList<string>?>? _systemIncludeRootsProvider;

    /// <summary>
    /// Installs the process-wide system-include-roots provider. Passing null
    /// restores the default fail-open behavior (no roots).
    /// </summary>
    public static void SetSystemIncludeRootsProvider(Func<IReadOnlyList<string>?>? provider)
    {
        _systemIncludeRootsProvider = provider;
    }

    private static IReadOnlyList<string> CurrentSystemIncludeRoots
        => _systemIncludeRootsProvider?.Invoke() ?? Array.Empty<string>();

    /// <summary>
    /// Issue #32 (REQ-IA-03): internal read-only accessor for content scans of
    /// existing quoted <c>#include "..."</c> entries. Resolve semantics are
    /// untouched — this only exposes the already-compiled pattern so the
    /// directive service reuses one canonical regex definition.
    /// </summary>
    internal static Regex QuotedIncludeRegexAccessor => QuotedIncludeRegex;

    /// <summary>
    /// Issue #32: internal read-only accessor for the angle-bracket
    /// <c>#include &lt;...&gt;</c> pattern (used by the include-assist insert
    /// position scan and angle-form already-included comparison).
    /// </summary>
    internal static Regex AngleBracketIncludeRegexAccessor => AngleBracketIncludeRegex;

    /// <summary>
    /// Extract the include entry from a raw <c>#include</c> directive token.
    /// This is the grammar-visitor entry point: token text is the full
    /// directive (e.g. <c>#include "file.mqh"</c> or
    /// <c>#include &lt;path/file.mqh&gt;</c>). Returns the canonical stored
    /// entry shape — bare path for quoted includes, angle-bracket-wrapped
    /// path for system includes — or <see langword="null"/> when the token
    /// is not an include directive.
    /// </summary>
    public static string? ExtractFromDirective(string? directiveText)
    {
        if (string.IsNullOrEmpty(directiveText))
        {
            return null;
        }

        var match = QuotedIncludeRegex.Match(directiveText);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = AngleBracketIncludeRegex.Match(directiveText);
        if (match.Success)
        {
            return $"<{match.Groups[1].Value}>";
        }

        return null;
    }

    /// <summary>
    /// Resolve a stored include entry (the canonical <c>MqlFile.Includes</c>
    /// shape produced by <see cref="ExtractFromDirective"/>) to an absolute
    /// file path. Quoted entries resolve relative to
    /// <paramref name="includingFile"/>'s directory; angle-bracket entries are
    /// system includes and return <see langword="null"/>.
    /// </summary>
    /// <returns>The candidate absolute path, or <see langword="null"/> when the
    /// entry is a system include or cannot be normalized.</returns>
    public static string? Resolve(string includingFile, string? includeEntry)
        => ResolveWithSystemRoots(includingFile, includeEntry, null);

    /// <summary>
    /// Resolve a stored include entry with explicit system include roots.
    /// Quoted entries resolve relative to
    /// <paramref name="includingFile"/>'s directory exactly as in
    /// <see cref="Resolve(string, string?)"/>; angle-bracket entries try each
    /// configured root
    /// (separator-normalized, case-insensitive existence check, traversal
    /// rejected) and fall back to the historical "not resolved" result when
    /// no root matches or none are supplied (fail open).
    /// </summary>
    /// <returns>The candidate absolute path, or <see langword="null"/> when the
    /// entry cannot be resolved.</returns>
    public static string? ResolveWithSystemRoots(
        string includingFile, string? includeEntry, IReadOnlyList<string>? systemIncludeRoots)
    {
        if (string.IsNullOrEmpty(includeEntry))
        {
            return null;
        }

        var relative = includeEntry.Trim();
        if (relative.Length == 0)
        {
            return null;
        }

        if (relative.StartsWith("<") || relative.EndsWith(">"))
        {
            // Angle-bracket entry: resolve against the supplied roots, or keep
            // today's behavior (unresolved) when none are supplied.
            return ResolveSystemIncludeEntry(relative, systemIncludeRoots ?? Array.Empty<string>());
        }

        try
        {
            if (Path.IsPathRooted(relative))
            {
                return Path.GetFullPath(relative);
            }

            var includingDir = Path.GetDirectoryName(includingFile);
            var combined = includingDir != null
                ? Path.Combine(includingDir, relative)
                : relative;
            return Path.GetFullPath(combined);
        }
        catch
        {
            // Malformed path (invalid chars, etc.): degrade to "not resolved".
            return null;
        }
    }

    /// <summary>
    /// Resolve an angle-bracket entry (<c>&lt;Trade\Trade.mqh&gt;</c>) against
    /// the process-wide configured system include roots (see
    /// <see cref="SetSystemIncludeRootsProvider"/>). Only angle entries are
    /// handled — quoted entries keep their relative-to-includer semantics and
    /// return false here. Missing roots or unresolvable headers return false
    /// (fail open: callers keep today's behavior).
    /// </summary>
    public static bool TryResolveSystemInclude(string? includeEntry, out string resolvedPath)
    {
        resolvedPath = ResolveSystemIncludeEntry(includeEntry, CurrentSystemIncludeRoots) ?? string.Empty;
        return !string.IsNullOrEmpty(resolvedPath);
    }

    /// <summary>
    /// Root-based resolution for one angle-bracket entry: normalize both
    /// separator forms, reject traversal segments, then try each root with an
    /// exact case-sensitive existence check first and a Windows-style
    /// case-insensitive walk as fallback. Never throws (fail open).
    /// </summary>
    private static string? ResolveSystemIncludeEntry(string? includeEntry, IReadOnlyList<string> systemIncludeRoots)
    {
        if (includeEntry == null || systemIncludeRoots.Count == 0)
        {
            return null;
        }

        var relative = includeEntry.Trim();
        if (relative.StartsWith("<"))
        {
            relative = relative.Substring(1);
        }
        if (relative.EndsWith(">"))
        {
            relative = relative.Substring(0, relative.Length - 1);
        }
        relative = relative.Trim();
        if (relative.Length == 0)
        {
            return null;
        }

        try
        {
            var separator = Path.DirectorySeparatorChar;
            var normalized = relative.Replace('/', separator).Replace('\\', separator);
            var segments = normalized.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                return null;
            }

            // A system entry must never escape its root.
            if (segments.Any(s => s == "." || s == ".."))
            {
                return null;
            }

            foreach (var root in systemIncludeRoots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                var fullRoot = Path.GetFullPath(root);

                // Fast path: exact, case-sensitive hit.
                var candidate = Path.Combine(new[] { fullRoot }.Concat(segments).ToArray());
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }

                // Windows-style fallback: case-insensitive segment walk.
                if (TryResolveCaseInsensitive(fullRoot, segments, out var resolved))
                {
                    return resolved;
                }
            }
        }
        catch
        {
            // Malformed root/entry: degrade to "not resolved" (fail open).
        }

        return null;
    }

    /// <summary>
    /// Walks <paramref name="segments"/> under <paramref name="root"/>
    /// case-insensitively (Windows-style semantics, which also matches how
    /// MQL projects reference the stdlib). Returns the first existing match.
    /// </summary>
    private static bool TryResolveCaseInsensitive(string root, string[] segments, out string resolved)
    {
        resolved = string.Empty;
        var current = root;

        for (var i = 0; i < segments.Length; i++)
        {
            var isLast = i == segments.Length - 1;
            var candidate = Path.Combine(current, segments[i]);

            if (isLast)
            {
                if (File.Exists(candidate))
                {
                    resolved = Path.GetFullPath(candidate);
                    return true;
                }

                var fileMatch = FindCaseInsensitiveMatch(current, segments[i], expectFile: true);
                if (fileMatch == null)
                {
                    return false;
                }

                resolved = fileMatch;
                return true;
            }

            if (Directory.Exists(candidate))
            {
                current = candidate;
                continue;
            }

            var dirMatch = FindCaseInsensitiveMatch(current, segments[i], expectFile: false);
            if (dirMatch == null)
            {
                return false;
            }

            current = dirMatch;
        }

        return false;
    }

    /// <summary>
    /// Case-insensitive single-segment lookup in one directory (null when the
    /// directory is unreadable or no entry matches).
    /// </summary>
    private static string? FindCaseInsensitiveMatch(string directory, string name, bool expectFile)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var fileName = Path.GetFileName(entry);
                if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase)
                    && (expectFile ? File.Exists(entry) : Directory.Exists(entry)))
                {
                    return Path.GetFullPath(entry);
                }
            }
        }
        catch
        {
            // Unreadable directory: no match (fail open).
        }

        return null;
    }

    /// <summary>
    /// Resolve a stored include entry and verify it exists inside the including
    /// file's directory tree (symlink-aware containment, issue #18/#21b).
    /// Combines <see cref="Resolve"/> + <see cref="File.Exists"/> +
    /// <see cref="PathSecurity.IsContainedInWorkspace"/> — the guard the didOpen
    /// path already applied and the parser copies lacked.
    /// </summary>
    public static bool TryResolveContained(
        string includingFile, string? includeEntry, out string resolvedPath)
    {
        resolvedPath = Resolve(includingFile, includeEntry) ?? string.Empty;
        return !string.IsNullOrEmpty(resolvedPath)
            && File.Exists(resolvedPath)
            && PathSecurity.IsContainedInWorkspace(includingFile, resolvedPath);
    }
}