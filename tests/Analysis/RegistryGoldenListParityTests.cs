using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Issue #136: the builtin registries are documentation-driven. These tests
/// are the CI build gate that keeps each registry in exact parity with the
/// golden list embedded in <c>data/builtins/&lt;dialect&gt;.json</c> — the
/// single source of truth derived from the official language references.
///
/// A failure prints the exact missing/extra names so the fix is always a
/// change to the golden list (documented name) or a data correction, never a
/// silent hand-edit of registry code.
/// </summary>
public class RegistryGoldenListParityTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "builtins")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (Dictionary<string, string> Functions, Dictionary<string, string> Variables) LoadGolden(string dialect)
    {
        var path = Path.Combine(FindRepoRoot(), "data", "builtins", $"{dialect}.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.GetProperty("functions").EnumerateObject())
        {
            functions[entry.Name] = entry.Value.TryGetProperty("signature", out var sig)
                ? sig.GetString() ?? string.Empty
                : "(signature not yet captured)";
        }
        foreach (var entry in doc.RootElement.GetProperty("variables").EnumerateObject())
        {
            variables[entry.Name] = entry.Value.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? string.Empty
                : "(description not yet captured)";
        }
        return (functions, variables);
    }

    private static void AssertParity(
        Dictionary<string, string> expected,
        Dictionary<string, string> actual,
        string label)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var missing = expected.Keys.Except(actual.Keys, comparer).OrderBy(n => n, comparer).ToList();
        var extra = actual.Keys.Except(expected.Keys, comparer).OrderBy(n => n, comparer).ToList();

        Assert.True(missing.Count == 0 && extra.Count == 0,
            $"{label} parity violation against the golden list.\n" +
            $"Missing from the registry ({missing.Count}): {string.Join(", ", missing)}\n" +
            $"Not in the golden list ({extra.Count}): {string.Join(", ", extra)}");

        // Values must match too: the registry is a projection, not a fork.
        foreach (var (name, expectedValue) in expected)
        {
            var actualValue = actual.First(kvp => comparer.Equals(kvp.Key, name)).Value;
            Assert.True(comparer.Equals(expectedValue, actualValue),
                $"{label} value mismatch for '{name}':\n" +
                $"  golden list: {expectedValue}\n" +
                $"  registry:    {actualValue}");
        }
    }

    [Fact]
    public void Mql4_Functions_MatchGoldenList()
    {
        var (functions, _) = LoadGolden("mql4");
        AssertParity(functions, Mql4Builtins.BuiltInFunctions, "MQL4 functions");
    }

    [Fact]
    public void Mql4_Variables_MatchGoldenList()
    {
        var (_, variables) = LoadGolden("mql4");
        AssertParity(variables, Mql4Builtins.BuiltInVariables, "MQL4 variables/constants");
    }

    [Fact]
    public void Mql5_Functions_MatchGoldenList()
    {
        var (functions, _) = LoadGolden("mql5");
        AssertParity(functions, new Mql5Builtins().BuiltInFunctions, "MQL5 functions");
    }

    [Fact]
    public void Mql5_Variables_MatchGoldenList()
    {
        var (_, variables) = LoadGolden("mql5");
        AssertParity(variables, new Mql5Builtins().BuiltInVariables, "MQL5 variables/constants");
    }
}
