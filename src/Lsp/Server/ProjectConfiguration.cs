using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Serilog;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Issue #179 (S1): immutable project configuration parsed from a
/// workspace-folder <c>.mql-lsp.json</c> file — the fallback source of
/// system include roots when the client does not declare the
/// <c>systemIncludeRoots</c> initialization option (see
/// <see cref="SystemIncludeRoots"/>).
///
/// <para>Same contract as <see cref="MqlIgnoreSpec"/>'s project file: the
/// file is read once per session during <c>initialize</c>; there is no
/// watcher, so editing the file requires a server restart. Only one key is
/// recognized: <c>systemIncludeRoots</c>, an array of strings. Entries may
/// be absolute or relative; a relative entry resolves against the directory
/// containing the config file so the file stays committable.</para>
///
/// <para>Fail-open robustness (never throws): a missing file is silent and
/// simply yields no configuration; a malformed or unreadable file (invalid
/// JSON, wrong types, I/O error) is ignored entirely with a logged warning;
/// a malformed entry (non-string, blank, unresolvable) inside an otherwise
/// valid file is dropped with a logged warning while the remaining entries
/// are kept.</para>
///
/// <para>Deliberately NOT done (issue #179 S3, non-goal): no implicit
/// discovery — the server never walks ancestors looking for MQL4/MQL5
/// directories and never probes installed terminals. The roots come from
/// exactly two explicit sources: the client option and this file.</para>
/// </summary>
public sealed class ProjectConfiguration
{
    /// <summary>The project configuration file name, at the workspace-folder root.</summary>
    public const string FileName = ".mql-lsp.json";

    /// <summary>The single recognized configuration key.</summary>
    public const string SystemIncludeRootsKey = "systemIncludeRoots";

    private static readonly ProjectConfiguration EmptyConfiguration = new(Array.Empty<string>());

    private readonly IReadOnlyList<string> _systemIncludeRoots;

    private ProjectConfiguration(IReadOnlyList<string> systemIncludeRoots)
    {
        _systemIncludeRoots = systemIncludeRoots;
    }

    /// <summary>
    /// Normalized absolute system include roots declared by the file, in file
    /// order. Empty when the file declares none.
    /// </summary>
    public IReadOnlyList<string> SystemIncludeRoots => _systemIncludeRoots;

    /// <summary>
    /// Load the <c>.mql-lsp.json</c> file at <paramref name="workspaceFolderRoot"/>'s
    /// root. Returns false (configuration = null, caller behaves as if the file
    /// were absent) when the file is missing, unreadable, or malformed — the
    /// failure is silent for a missing file and warning-logged otherwise.
    /// Never throws.
    /// </summary>
    public static bool TryLoad(string? workspaceFolderRoot, out ProjectConfiguration? configuration)
    {
        configuration = null;
        if (string.IsNullOrWhiteSpace(workspaceFolderRoot))
        {
            return false;
        }

        string path;
        try
        {
            path = Path.Combine(workspaceFolderRoot, FileName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Invalid workspace folder path for {File}; ignoring it for this session (issue #179)", FileName);
            return false;
        }

        if (!File.Exists(path))
        {
            // Missing file is the normal case: silent, no roots.
            return false;
        }

        string content;
        try
        {
            content = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Warning(ex, "Could not read {Path}; ignoring it for this session — system includes fail open (issue #179)", path);
            return false;
        }

        configuration = Parse(content, workspaceFolderRoot);
        return configuration is not null;
    }

    /// <summary>
    /// Parse <c>.mql-lsp.json</c> content. Returns null (file ignored) when
    /// the content is not a JSON object or declares <c>systemIncludeRoots</c>
    /// with a type other than an array of strings — always with a logged
    /// warning. Malformed entries inside an otherwise valid array are dropped
    /// with a warning and the rest of the file survives. Never throws.
    /// </summary>
    public static ProjectConfiguration? Parse(string? json, string? configDirectory)
    {
        JToken root;
        try
        {
            root = JToken.Parse(json ?? string.Empty);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Malformed {File} content; ignoring the file for this session — system includes fail open (issue #179)", FileName);
            return null;
        }

        if (root is not JObject obj)
        {
            Log.Warning("{File} root is not a JSON object; ignoring the file for this session (issue #179)", FileName);
            return null;
        }

        var property = obj.Property(SystemIncludeRootsKey);
        if (property is null || property.Value.Type == JTokenType.Null)
        {
            // Well-formed file that declares nothing.
            return EmptyConfiguration;
        }

        if (property.Value is not JArray array)
        {
            Log.Warning(
                "{File} declares {Key} with type {Type} instead of an array of strings; ignoring the file for this session (issue #179)",
                FileName, SystemIncludeRootsKey, property.Value.Type);
            return null;
        }

        var roots = new List<string>();
        foreach (var item in array)
        {
            if (item is not JValue { Value: string entry })
            {
                Log.Warning(
                    "{File} has a non-string {Key} entry ({Entry}); dropping the entry (issue #179)",
                    FileName, SystemIncludeRootsKey, item?.ToString(Formatting.None) ?? "null");
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry))
            {
                Log.Warning("{File} has a blank {Key} entry; dropping the entry (issue #179)", FileName, SystemIncludeRootsKey);
                continue;
            }

            var resolved = ResolveEntry(entry, configDirectory);
            if (resolved is not null)
            {
                roots.Add(resolved);
            }
        }

        return new ProjectConfiguration(roots);
    }

    /// <summary>
    /// Issue #179 (S2): the effective system include roots across the two
    /// explicit sources, in precedence order.
    ///
    /// <para>A non-empty client <c>systemIncludeRoots</c> initialization
    /// option wins outright and the project files are not consulted.
    /// Otherwise the union of the <c>.mql-lsp.json</c> files across the
    /// workspace folders is the effective set, in folder order, deduplicated
    /// (Ordinal, on the normalized absolute paths). With neither source the
    /// result is empty — exactly the pre-feature fail-open behavior once
    /// handed to <see cref="SystemIncludeRoots"/>.Set.</para>
    /// </summary>
    public static IReadOnlyList<string> ResolveEffectiveRoots(
        IEnumerable<string>? clientSystemIncludeRoots,
        IEnumerable<string>? workspaceFolders)
    {
        var clientRoots = (clientSystemIncludeRoots ?? Array.Empty<string>())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToList();
        if (clientRoots.Count > 0)
        {
            // The client option is authoritative; project files are not read.
            return clientRoots;
        }

        var effective = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var folder in workspaceFolders ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(folder) ||
                !TryLoad(folder, out var configuration) ||
                configuration is null)
            {
                continue;
            }

            foreach (var root in configuration.SystemIncludeRoots)
            {
                if (seen.Add(root))
                {
                    effective.Add(root);
                }
            }
        }

        return effective;
    }

    /// <summary>
    /// Normalizes one entry: absolute entries go through
    /// <see cref="Path.GetFullPath(string)"/> as-is; relative entries resolve against
    /// <paramref name="configDirectory"/> (the directory containing the config
    /// file). Same normalization conventions as
    /// <see cref="SystemIncludeRoots"/>: a malformed path is dropped (null)
    /// rather than poisoning the set.
    /// </summary>
    private static string? ResolveEntry(string entry, string? configDirectory)
    {
        try
        {
            return Path.IsPathRooted(entry)
                ? Path.GetFullPath(entry)
                : Path.GetFullPath(Path.Combine(
                    string.IsNullOrWhiteSpace(configDirectory)
                        ? throw new ArgumentException("Relative entry without a config directory", nameof(configDirectory))
                        : configDirectory,
                    entry));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{File} entry {Entry} is malformed; dropping the entry (issue #179)", FileName, entry);
            return null;
        }
    }
}
