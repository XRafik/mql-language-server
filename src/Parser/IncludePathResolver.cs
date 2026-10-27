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
    /// Process-wide provider of the declared workspace-folder roots, set during
    /// server initialization (Program.OnInitialize wires it to
    /// <see cref="MqlLanguageServer.Lsp.Server.WorkspaceRoots.Current"/>). Null or
    /// an empty result keeps the historical behavior: the includer-directory rule
    /// and the #186 system-root rule are the only containment gates. Tests set it
    /// directly via <see cref="SetWorkspaceRootsProvider"/>.
    /// </summary>
    private static Func<IReadOnlyList<string>?>? _workspaceRootsProvider;

    /// <summary>
    /// Installs the process-wide workspace-roots provider. Passing null restores
    /// the default behavior (workspace folders do not widen containment).
    /// </summary>
    public static void SetWorkspaceRootsProvider(Func<IReadOnlyList<string>?>? provider)
    {
        _workspaceRootsProvider = provider;
    }

    private static IReadOnlyList<string> CurrentWorkspaceRoots
        => _workspaceRootsProvider?.Invoke() ?? Array.Empty<string>();

    /// <summary>
    /// Issue #200: the union of every declared root the containment gate may
    /// admit a quoted parent-relative hop through — the declared workspace
    /// folders plus the system include roots. De-duplicated and order-stable
    /// (workspace folders first, then system roots, both as configured). Each
    /// candidate still has to hold BOTH sides of the hop on its own (see
    /// <see cref="MqlLanguageServer.Lsp.Server.PathSecurity.IsContainedInCommonSystemRoot"/>)
    /// so merging the two sets never admits a cross-root hop.
    /// </summary>
    private static List<string> AllDeclaredRoots()
    {
        var result = new List<string>();
        foreach (var root in CurrentWorkspaceRoots)
        {
            if (!string.IsNullOrEmpty(root) && result.IndexOf(root) < 0)
            {
                result.Add(root);
            }
        }

        foreach (var root in CurrentSystemIncludeRoots)
        {
            if (!string.IsNullOrEmpty(root) && result.IndexOf(root) < 0)
            {
                result.Add(root);
            }
        }

        return result;
    }

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
            // MQL convention uses the Windows separator in quoted includes
            // too ("Lib\\wfa.mqh"); normalize both separators to the host
            // separator so resolution works on POSIX hosts as well
            // (client-reported false 5070/1070 on Linux).
            var normalized = relative.Replace('\\', Path.DirectorySeparatorChar)
                                     .Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalized))
            {
                return Path.GetFullPath(normalized);
            }

            var includingDir = Path.GetDirectoryName(includingFile);
            var combined = includingDir != null
                ? Path.Combine(includingDir, normalized)
                : normalized;
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
    ///
    /// <para>Issue #186 adds the second containment shape a quoted entry can
    /// satisfy: when the includer and the resolved target both sit inside ONE
    /// declared system include root, that root — not the header's own directory —
    /// is the unit the user declared trustworthy, so the quoted parent-relative
    /// hop the MetaQuotes library headers use
    /// (<c>#include "..\Arrays\ArrayLong.mqh"</c> from <c>Trade\Trade.mqh</c>)
    /// reaches the include closure instead of being dropped silently. Issue #200
    /// generalizes that rule to declared workspace folders as well: the same
    /// parent-relative hop between sibling folders of an ordinary workspace
    /// (<c>#include "..\Common\util.mqh"</c> from <c>Include\Trade.mqh</c>) is
    /// admitted when both sides sit in one workspace folder, restoring the
    /// include edge the rename, call-hierarchy and .mqh language routing key on.
    /// Outside a single declared root the directory rule above stays the only
    /// gate: an escape to an arbitrary path, and a hop into a different declared
    /// tree, keep failing exactly as before.</para>
    /// </summary>
    public static bool TryResolveContained(
        string includingFile, string? includeEntry, out string resolvedPath)
    {
        var candidate = Resolve(includingFile, includeEntry) ?? string.Empty;
        if (string.IsNullOrEmpty(candidate) || !File.Exists(candidate))
        {
            resolvedPath = string.Empty;
            return false;
        }

        if (PathSecurity.IsContainedInWorkspace(includingFile, candidate))
        {
            resolvedPath = candidate;
            return true;
        }

        // Issue #186 (and #200): a quoted parent-relative hop is also admitted
        // when the includer and the resolved target both sit inside ONE declared
        // root. The trusted unit is the root the user declared, not the header's
        // own directory, so library headers that reach sibling directories with a
        // quoted form (e.g. `..\Arrays\ArrayLong.mqh` from `Trade\Trade.mqh`)
        // reach the include closure instead of being dropped. #186 opened this to
        // declared system include roots; #200 generalizes it to declared
        // workspace folders as well — the ordinary-workspace shape where a
        // quoted parent-relative include between sibling folders of the same
        // workspace (e.g. `..\Common\util.mqh` from `Include\Trade.mqh`)
        // resolves and is readable but the includer-directory guard alone
        // rejects it, silently dropping the include edge the rename
        // (#90), call-hierarchy (#96) and .mqh language routing key on. A
        // target that escapes every declared root, or a hop from one declared
        // tree into a different one, still fails exactly as before: IsContainedIn
        // CommonRoot requires one single root to contain BOTH sides.
        if (PathSecurity.IsContainedInCommonSystemRoot(
            includingFile, candidate, AllDeclaredRoots()))
        {
            resolvedPath = candidate;
            return true;
        }

        resolvedPath = string.Empty;
        return false;
    }
}