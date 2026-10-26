using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using MqlLanguageServer.Lsp.Server;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Server;

/// <summary>
/// Issue #179 (S1): the <c>.mql-lsp.json</c> project configuration loader.
/// Recognized key: <c>systemIncludeRoots</c> (array of strings). Absolute
/// entries stay as-is; relative entries resolve against the directory
/// containing the config file. Missing file = silent, no roots; malformed
/// JSON / wrong types / unreadable file / malformed entries = ignore the
/// offending file or entry with a warning — loading never throws.
/// </summary>
public class ProjectConfigurationTests
{
    private readonly string _dir = CreateTempDir();

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mql-lsp-projcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void WriteConfig(string json)
    {
        File.WriteAllText(Path.Combine(_dir, ProjectConfiguration.FileName), json);
    }

    // JSON string literal (with quotes) for a filesystem path, portable across
    // separators: backslashes must be escaped inside JSON on Windows.
    private static string JsonString(string path) => JsonConvert.ToString(path);

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best effort */ }
    }

    // ------------------------------------------------------------------
    // S1: missing file — silent, no roots
    // ------------------------------------------------------------------

    [Fact]
    public void TryLoad_MissingFile_ReturnsFalseWithoutConfiguration()
    {
        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.False(ok);
        Assert.Null(configuration);
    }

    [Fact]
    public void TryLoad_NullOrWhitespaceFolder_ReturnsFalseWithoutConfiguration()
    {
        Assert.False(ProjectConfiguration.TryLoad(null, out var nullConfig));
        Assert.False(ProjectConfiguration.TryLoad("", out var emptyConfig));
        Assert.False(ProjectConfiguration.TryLoad("   ", out var blankConfig));

        Assert.Null(nullConfig);
        Assert.Null(emptyConfig);
        Assert.Null(blankConfig);
    }

    // ------------------------------------------------------------------
    // S1: recognized key + entry resolution
    // ------------------------------------------------------------------

    [Fact]
    public void TryLoad_AbsoluteEntries_ReturnsRootsAsIs()
    {
        var first = Path.Combine(_dir, "abs-one");
        var second = Path.Combine(_dir, "abs-two");
        WriteConfig($"{{\"systemIncludeRoots\": [{JsonString(first)}, {JsonString(second)}]}}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Equal(
            new[] { Path.GetFullPath(first), Path.GetFullPath(second) },
            configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_RelativeEntries_ResolveAgainstConfigFileDirectory()
    {
        WriteConfig("{\"systemIncludeRoots\": [\"Include\", \"sub/inc\"]}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Equal(
            new[]
            {
                Path.GetFullPath(Path.Combine(_dir, "Include")),
                Path.GetFullPath(Path.Combine(_dir, "sub", "inc"))
            },
            configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_MixedAbsoluteAndRelative_ResolvesEachEntryAccordingly()
    {
        var absolute = Path.Combine(_dir, "absolute-root");
        WriteConfig($"{{\"systemIncludeRoots\": [{JsonString(absolute)}, \"relative-root\"]}}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Equal(
            new[]
            {
                Path.GetFullPath(absolute),
                Path.GetFullPath(Path.Combine(_dir, "relative-root"))
            },
            configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_EmptyArray_ReturnsTrueWithNoRoots()
    {
        WriteConfig("{\"systemIncludeRoots\": []}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Empty(configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_UnknownKeysOnly_ReturnsTrueWithNoRoots()
    {
        // Only one key is recognized; anything else is forward-compat noise,
        // not a malformed file.
        WriteConfig("{\"someOtherKey\": true, \"another\": [1, 2]}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Empty(configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_MissingKey_ReturnsTrueWithNoRoots()
    {
        WriteConfig("{}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Empty(configuration!.SystemIncludeRoots);
    }

    // ------------------------------------------------------------------
    // S1: malformed file — ignore the file, log a warning, never throw
    // ------------------------------------------------------------------

    [Fact]
    public void TryLoad_MalformedJson_ReturnsFalse_NeverThrows()
    {
        WriteConfig("{ this is not json ");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.False(ok);
        Assert.Null(configuration);
    }

    [Fact]
    public void TryLoad_NonObjectRoot_ReturnsFalse()
    {
        WriteConfig("[\"systemIncludeRoots\", \"C:/x\"]");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.False(ok);
        Assert.Null(configuration);
    }

    [Fact]
    public void TryLoad_SystemIncludeRootsWrongType_ReturnsFalse()
    {
        WriteConfig("{\"systemIncludeRoots\": \"C:/not-an-array\"}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.False(ok);
        Assert.Null(configuration);
    }

    [Fact]
    public void TryLoad_SystemIncludeRootsNullValue_ReturnsTrueWithNoRoots()
    {
        // Explicit JSON null on the recognized key: the file is well-formed,
        // it just declares nothing.
        WriteConfig("{\"systemIncludeRoots\": null}");

        var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

        Assert.True(ok);
        Assert.NotNull(configuration);
        Assert.Empty(configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void TryLoad_UnreadableFile_ReturnsFalse_NeverThrows()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // chmod-based unreadable simulation is POSIX-only
        }

        var path = Path.Combine(_dir, ProjectConfiguration.FileName);
        File.WriteAllText(path, "{\"systemIncludeRoots\": []}");
        File.SetUnixFileMode(path, UnixFileMode.None);

        try
        {
            var ok = ProjectConfiguration.TryLoad(_dir, out var configuration);

            Assert.False(ok);
            Assert.Null(configuration);
        }
        finally
        {
            // Restore so Cleanup can delete the tree.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    // ------------------------------------------------------------------
    // S1: malformed entries — ignore the entry, keep the valid ones
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_NonStringEntries_Dropped_ValidEntriesKept()
    {
        var kept = Path.Combine(_dir, "kept");
        var configuration = ProjectConfiguration.Parse(
            $"{{\"systemIncludeRoots\": [{JsonString(kept)}, 42, null, {{\"nested\": true}}]}}",
            _dir);

        Assert.NotNull(configuration);
        Assert.Equal(new[] { Path.GetFullPath(kept) }, configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void Parse_BlankEntries_Dropped()
    {
        var kept = Path.Combine(_dir, "kept");
        var configuration = ProjectConfiguration.Parse(
            $"{{\"systemIncludeRoots\": [\"\", \"   \", {JsonString(kept)}]}}",
            _dir);

        Assert.NotNull(configuration);
        Assert.Equal(new[] { Path.GetFullPath(kept) }, configuration!.SystemIncludeRoots);
    }

    [Fact]
    public void Parse_RelativeEntryWithNoConfigDirectory_Dropped_NeverThrows()
    {
        var configuration = ProjectConfiguration.Parse(
            "{\"systemIncludeRoots\": [\"relative/path\"]}",
            configDirectory: null);

        Assert.NotNull(configuration);
        Assert.Empty(configuration!.SystemIncludeRoots);
    }

    // ------------------------------------------------------------------
    // S2: precedence — non-empty client option wins; otherwise the union
    // of the project files across workspace folders (deduplicated)
    // ------------------------------------------------------------------

    [Fact]
    public void ResolveEffectiveRoots_NonEmptyClientOption_Wins_ProjectFilesNotConsulted()
    {
        var clientRoot = Path.Combine(_dir, "from-client");
        var fileRoot = Path.Combine(_dir, "from-file");
        WriteConfig($"{{\"systemIncludeRoots\": [{JsonString(fileRoot)}]}}");

        var effective = ProjectConfiguration.ResolveEffectiveRoots(
            new[] { clientRoot },
            new[] { _dir });

        // The file's root must not leak in: only the client's option counts.
        Assert.Equal(new[] { clientRoot }, effective);
    }

    [Fact]
    public void ResolveEffectiveRoots_ClientOptionEmptyArray_FallsBackToProjectFiles()
    {
        var fileRoot = Path.Combine(_dir, "from-file");
        WriteConfig($"{{\"systemIncludeRoots\": [{JsonString(fileRoot)}]}}");

        var effective = ProjectConfiguration.ResolveEffectiveRoots(
            Array.Empty<string>(),
            new[] { _dir });

        Assert.Equal(new[] { Path.GetFullPath(fileRoot) }, effective);
    }

    [Fact]
    public void ResolveEffectiveRoots_WhitespaceOnlyClientEntries_FallBackToProjectFiles()
    {
        var fileRoot = Path.Combine(_dir, "from-file");
        WriteConfig($"{{\"systemIncludeRoots\": [{JsonString(fileRoot)}]}}");

        var effective = ProjectConfiguration.ResolveEffectiveRoots(
            new[] { "   ", "" },
            new[] { _dir });

        Assert.Equal(new[] { Path.GetFullPath(fileRoot) }, effective);
    }

    [Fact]
    public void ResolveEffectiveRoots_NoClientRoots_UnionsFolders_AndDeduplicates()
    {
        var folderOne = CreateTempDir();
        var folderTwo = CreateTempDir();
        try
        {
            var sharedRoot = Path.Combine(folderOne, "Shared");
            var onlyOne = Path.Combine(folderOne, "Only1");
            var onlyTwo = Path.Combine(folderTwo, "Only2");
            // folderTwo's file reaches the same shared root relatively, so the
            // normalized absolute entries collide and must deduplicate.
            File.WriteAllText(
                Path.Combine(folderOne, ProjectConfiguration.FileName),
                $"{{\"systemIncludeRoots\": [{JsonString(sharedRoot)}, {JsonString(onlyOne)}]}}");
            File.WriteAllText(
                Path.Combine(folderTwo, ProjectConfiguration.FileName),
                $"{{\"systemIncludeRoots\": [{JsonString(Path.Combine("..", Path.GetFileName(folderOne), "Shared"))}, {JsonString(onlyTwo)}]}}");

            var effective = ProjectConfiguration.ResolveEffectiveRoots(
                null,
                new[] { folderOne, folderTwo });

            Assert.Equal(
                new[]
                {
                    Path.GetFullPath(sharedRoot),
                    Path.GetFullPath(onlyOne),
                    Path.GetFullPath(onlyTwo)
                },
                effective);
        }
        finally
        {
            Cleanup(folderOne);
            Cleanup(folderTwo);
        }
    }

    [Fact]
    public void ResolveEffectiveRoots_MalformedFileInOneFolder_SkipsIt_KeepsValidFolders()
    {
        var validFolder = CreateTempDir();
        var malformedFolder = CreateTempDir();
        try
        {
            var validRoot = Path.Combine(validFolder, "valid-root");
            File.WriteAllText(
                Path.Combine(validFolder, ProjectConfiguration.FileName),
                $"{{\"systemIncludeRoots\": [{JsonString(validRoot)}]}}");
            File.WriteAllText(
                Path.Combine(malformedFolder, ProjectConfiguration.FileName),
                "{ not json ");

            var effective = ProjectConfiguration.ResolveEffectiveRoots(
                null,
                new[] { malformedFolder, validFolder });

            Assert.Equal(new[] { Path.GetFullPath(validRoot) }, effective);
        }
        finally
        {
            Cleanup(validFolder);
            Cleanup(malformedFolder);
        }
    }

    [Fact]
    public void ResolveEffectiveRoots_NeitherSource_ReturnsEmpty_TodaysFailOpenBehavior()
    {
        var effective = ProjectConfiguration.ResolveEffectiveRoots(
            null,
            new[] { _dir }); // folder with no .mql-lsp.json

        Assert.Empty(effective);
    }
}
