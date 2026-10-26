using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace MqlLanguageServer.Builtins;

/// <summary>
/// Loads the documentation-driven golden lists (issue #136) embedded as
/// assembly resources from <c>data/builtins/&lt;dialect&gt;.json</c>.
///
/// The golden lists are the single source of truth for three consumers:
/// <list type="bullet">
/// <item><see cref="Mql4.Builtins.Mql4Builtins"/> and
/// <see cref="Mql5.Builtins.Mql5Builtins"/> — builtin lookup that suppresses
/// 1070/5070 unresolved-symbol false positives.</item>
/// <item><see cref="Analysis.Mql4OnlyApiRegistry"/> — migration admissions for
/// the 5060 rule (REQ-MA-02), projected from explicit
/// <c>mql4OnlyApi</c>/<c>mql5Mapping</c> metadata on MQL4 entries.</item>
/// </list>
///
/// Completeness against the official references is enforced by
/// <c>RegistryGoldenListParityTests</c>; adding names is a data change, never
/// a hand-edit of registry code.
/// </summary>
internal static class BuiltinGoldenData
{
    /// <summary>
    /// One REQ-MA-02 admission migrated from the former curated
    /// MQL4OnlyApiRegistry (issue #34), now expressed as metadata on the
    /// MQL4 golden-list entry it refers to.
    /// </summary>
    public sealed record Mql4OnlyAdmission(
        string Name,
        bool IsFunction,
        string Reason,
        string Replacement,
        bool SemanticsChanged);

    /// <summary>Loaded golden list for one dialect.</summary>
    public sealed class GoldenDocument
    {
        /// <summary>Dialect the document belongs to ("mql4" / "mql5").</summary>
        public required string Dialect { get; init; }

        /// <summary>Function signatures keyed by canonical name.</summary>
        public required Dictionary<string, string> Functions { get; init; }

        /// <summary>Variable/constant descriptions keyed by canonical name.</summary>
        public required Dictionary<string, string> Variables { get; init; }

        /// <summary>REQ-MA-02 admissions (MQL4 dialect only; empty for MQL5).</summary>
        public IReadOnlyList<Mql4OnlyAdmission> Admissions { get; init; } =
            Array.Empty<Mql4OnlyAdmission>();
    }

    private static readonly ConcurrentDictionary<string, GoldenDocument> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Load (and cache) the golden list for <paramref name="dialect"/>.
    /// Function and variable dictionaries keep the case-insensitive
    /// (OrdinalIgnoreCase) semantics of the hand-curated registries they
    /// replace.
    /// </summary>
    public static GoldenDocument Load(string dialect)
    {
        return Cache.GetOrAdd(dialect, static d => Parse(d));
    }

    private static GoldenDocument Parse(string dialect)
    {
        var json = ReadEmbedded(dialect);
        using var doc = JsonDocument.Parse(json);

        var root = doc.RootElement;
        var functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var admissions = new List<Mql4OnlyAdmission>();

        if (root.TryGetProperty("functions", out var fSection))
        {
            foreach (var entry in fSection.EnumerateObject())
            {
                var signature = entry.Value.TryGetProperty("signature", out var sig)
                    ? sig.GetString()
                    : null;
                functions[entry.Name] = signature ?? "(signature not yet captured)";
            }
        }

        if (root.TryGetProperty("variables", out var vSection))
        {
            foreach (var entry in vSection.EnumerateObject())
            {
                var description = entry.Value.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null;
                variables[entry.Name] = description ?? "(description not yet captured)";
            }
        }

        // REQ-MA-02 admissions: explicit metadata, never derived from
        // apiTier — shared names with unchanged semantics must NOT be
        // flagged by the 5060 rule (issue #34 contract).
        if (root.TryGetProperty("functions", out var fAdm))
        {
            CollectAdmissions(fAdm, isFunction: true, admissions);
        }
        if (root.TryGetProperty("variables", out var vAdm))
        {
            CollectAdmissions(vAdm, isFunction: false, admissions);
        }

        return new GoldenDocument
        {
            Dialect = dialect,
            Functions = functions,
            Variables = variables,
            Admissions = admissions,
        };
    }

    private static void CollectAdmissions(
        JsonElement section, bool isFunction, List<Mql4OnlyAdmission> admissions)
    {
        foreach (var entry in section.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("mql4OnlyApi", out var flag) ||
                flag.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            string reason = "MQL4-only API";
            string replacement = string.Empty;
            var semanticsChanged = false;
            if (entry.Value.TryGetProperty("mql5Mapping", out var mapping) &&
                mapping.ValueKind == JsonValueKind.Object)
            {
                if (mapping.TryGetProperty("note", out var note) && note.ValueKind == JsonValueKind.String)
                {
                    reason = note.GetString() ?? reason;
                }
                if (mapping.TryGetProperty("replacement", out var rep) && rep.ValueKind == JsonValueKind.String)
                {
                    replacement = rep.GetString() ?? replacement;
                }
                if (mapping.TryGetProperty("semanticsChanged", out var sc))
                {
                    semanticsChanged = sc.ValueKind == JsonValueKind.True;
                }
            }

            admissions.Add(new Mql4OnlyAdmission(entry.Name, isFunction, reason, replacement, semanticsChanged));
        }
    }

    private static string ReadEmbedded(string dialect)
    {
        var assembly = typeof(BuiltinGoldenData).Assembly;
        var suffix = $"builtins.{dialect}.json";
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded golden list '{suffix}' not found. Resources: " +
                string.Join(", ", assembly.GetManifestResourceNames()));
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' has no stream.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
