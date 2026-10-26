using System;
using System.IO;
using MqlLanguageServer.Lsp.Server;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Server;

/// <summary>
/// Characterization tests for <see cref="SystemIncludeRoots"/>, pinning the
/// normalization/dedup/fail-open contract that the issue #179 union path
/// (<see cref="ProjectConfiguration.ResolveEffectiveRoots"/> feeding Set)
/// relies on. These document existing behavior — they are not new behavior.
/// </summary>
public class SystemIncludeRootsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "mql-lsp-sysroots-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Set_AbsoluteRoots_NormalizesDeduplicatesAndFiltersBlankEntries()
    {
        Directory.CreateDirectory(_root);

        SystemIncludeRoots.Set(new[]
        {
            _root,                    // duplicated after normalization
            Path.Combine(_root, "."), // normalizes to the same path
            "   ",                    // blank: dropped
            string.Empty,             // blank: dropped
            null!                     // null: dropped
        });

        Assert.Single(SystemIncludeRoots.Current, Path.GetFullPath(_root));
        Assert.True(SystemIncludeRoots.HasRoots);
    }

    [Fact]
    public void Set_EmptyOrNull_ClearsToFailOpenMode()
    {
        Directory.CreateDirectory(_root);
        SystemIncludeRoots.Set(new[] { _root });
        Assert.True(SystemIncludeRoots.HasRoots);

        SystemIncludeRoots.Set(Array.Empty<string>());
        Assert.False(SystemIncludeRoots.HasRoots);
        Assert.Empty(SystemIncludeRoots.Current);

        SystemIncludeRoots.Set(null);
        Assert.False(SystemIncludeRoots.HasRoots);
    }

    [Fact]
    public void ContainsPath_InsideDeclaredRoot_True_Outside_False()
    {
        Directory.CreateDirectory(_root);
        var inside = Path.Combine(_root, "Trade", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(_root, "Trade"));
        File.WriteAllText(inside, "// header"); // real file: IsUnderAnyRoot resolves real paths fail-closed
        var outside = Path.Combine(Path.GetTempPath(), "mql-lsp-sysroots-outside.mqh");
        File.WriteAllText(outside, "// outside");

        SystemIncludeRoots.Set(new[] { _root });

        Assert.True(SystemIncludeRoots.ContainsPath(inside));
        Assert.False(SystemIncludeRoots.ContainsPath(outside));

        // No roots at all: fail open containment check (never true).
        SystemIncludeRoots.Clear();
        Assert.False(SystemIncludeRoots.ContainsPath(inside));

        try { File.Delete(outside); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        // Never leak static state into other tests.
        SystemIncludeRoots.Clear();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best effort */ }
    }
}
