using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// System include roots for angle-bracket <c>#include &lt;...&gt;</c> directives
/// (MQL4/MQL5 Standard Library support).
///
/// <para>The terminal's <c>MQL5/Include</c> / <c>MQL4/Include</c> directories are
/// NOT workspace folders (the agent-lsp remediation attempt proved adding them
/// as workspace folders changes nothing) — they are a server-side concept,
/// delivered via the <c>systemIncludeRoots</c> initialization option and
/// captured here at initialize time. IncludePathResolver's
/// TryResolveSystemInclude resolves angle entries against these roots, and
/// <see cref="WorkspaceRoots.IsReadAllowed"/> lets the central read guard
/// admit reads under a declared system root.</para>
///
/// <para>A static singleton mirrors the established
/// <c>GlobalSymbolIndex.Instance</c> / <see cref="WorkspaceRoots"/> convention
/// of this codebase, keeping the feature testable (tests call
/// <see cref="Set"/>/<see cref="Clear"/>) without threading a dependency
/// through every handler constructor.</para>
///
/// <para>Edge cases (deliberate decisions):</para>
/// <list type="bullet">
/// <item>No roots configured: resolution is FAIL-OPEN — angle entries stay
/// unresolved, which is exactly the pre-feature behavior.</item>
/// <item>Paths are normalized with Path.GetFullPath and stored deduplicated;
/// malformed entries from the client are dropped rather than poisoning the set.</item>
/// <item>The set captured at initialize is final for the session (the server
/// does not handle workspace/didChangeWorkspaceFolders).</item>
/// </list>
/// </summary>
public static class SystemIncludeRoots
{
    private static volatile string[] _roots = Array.Empty<string>();

    /// <summary>
    /// Normalized absolute system include roots currently in effect. Empty
    /// when none were declared (fail-open mode).
    /// </summary>
    public static IReadOnlyList<string> Current => _roots;

    /// <summary>
    /// True once at least one system include root has been declared.
    /// </summary>
    public static bool HasRoots => _roots.Length > 0;

    /// <summary>
    /// Replaces the configured root set. Called from OnInitialize with the
    /// client-declared <c>systemIncludeRoots</c> initialization option and
    /// from tests. Null/empty input clears the set (fail-open mode).
    /// </summary>
    public static void Set(IEnumerable<string>? systemIncludeRoots)
    {
        _roots = (systemIncludeRoots ?? Enumerable.Empty<string>())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Normalize)
            .Where(root => root != null)
            .Distinct(StringComparer.Ordinal)
            .ToArray()!;
    }

    /// <summary>
    /// Clears all configured roots, restoring the pre-feature behavior.
    /// </summary>
    public static void Clear() => Set(null);

    /// <summary>
    /// True when <paramref name="filePath"/> sits inside any declared system
    /// include root. Used by <see cref="WorkspaceRoots.IsReadAllowed"/> so the
    /// central read guard admits standard-library headers, which live outside
    /// every workspace folder by design. False when no roots are configured.
    /// </summary>
    public static bool ContainsPath(string filePath)
    {
        if (!HasRoots || string.IsNullOrEmpty(filePath) || !Path.IsPathRooted(filePath))
        {
            return false;
        }

        return PathSecurity.IsUnderAnyRoot(filePath, _roots);
    }

    private static string? Normalize(string root)
    {
        try
        {
            return Path.GetFullPath(root);
        }
        catch
        {
            // Malformed root from the client: drop it rather than poisoning
            // the root set.
            return null;
        }
    }
}
