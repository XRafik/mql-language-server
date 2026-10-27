using System;
using System.IO;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// Unit tests for the single include-resolution service (issue #25a).
/// The service replaced four drifted copies; these tests pin the unified
/// semantics: quoted → relative resolution, angle-bracket → system include
/// (not resolved), containment guard integrated.
/// </summary>
public class IncludePathResolverTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _includerPath;

    public IncludePathResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IncludePathResolverTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempDir);
        _includerPath = Path.Combine(_tempDir, "Main.mq4");
        File.WriteAllText(_includerPath, "int x = 1;\n");
    }

    public void Dispose()
    {
        Directory.Delete(_tempDir, true);
    }

    // --- ExtractFromDirective (raw directive token input) ---

    [Fact]
    public void ExtractFromDirective_Quoted_ReturnsBarePath()
    {
        var entry = IncludePathResolver.ExtractFromDirective("#include \"Trade/Trade.mqh\"");
        Assert.Equal("Trade/Trade.mqh", entry);
    }

    [Fact]
    public void ExtractFromDirective_AngleBracket_ReturnsWrappedPath()
    {
        var entry = IncludePathResolver.ExtractFromDirective("#include <stdlib.mqh>");
        Assert.Equal("<stdlib.mqh>", entry);
    }

    [Fact]
    public void ExtractFromDirective_NonInclude_ReturnsNull()
    {
        Assert.Null(IncludePathResolver.ExtractFromDirective("#property copyright \"x\""));
        Assert.Null(IncludePathResolver.ExtractFromDirective(null));
        Assert.Null(IncludePathResolver.ExtractFromDirective(string.Empty));
    }

    // --- Resolve (stored-entry shape) ---

    [Fact]
    public void Resolve_QuotedEntry_ResolvesRelativeToIncluder()
    {
        var resolved = IncludePathResolver.Resolve(_includerPath, "Sub/Helper.mqh");
        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "Sub", "Helper.mqh")), resolved);
    }

    [Fact]
    public void Resolve_QuotedEntry_BackslashSeparator_ResolvesRelativeToIncluder()
    {
        // MQL convention uses the Windows separator in quoted includes too
        // ("Lib\wfa.mqh"); the resolver must be separator-agnostic on POSIX
        // hosts. Client report: quoted backslash includes failed to resolve
        // on Linux, leaving user symbols falsely flagged 5070/1070.
        Directory.CreateDirectory(Path.Combine(_tempDir, "Lib"));
        File.WriteAllText(Path.Combine(_tempDir, "Lib", "wfa.mqh"), "int w = 5;\n");

        var resolved = IncludePathResolver.Resolve(_includerPath, "Lib\\wfa.mqh");

        Assert.Equal(Path.GetFullPath(Path.Combine(_tempDir, "Lib", "wfa.mqh")), resolved);
    }

    [Fact]
    public void Resolve_AbsoluteEntry_PassesThroughNormalized()
    {
        var absolute = Path.Combine(_tempDir, "abs.mqh");
        var resolved = IncludePathResolver.Resolve(_includerPath, absolute);
        Assert.Equal(absolute, resolved);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_ReturnsNull()
    {
        Assert.Null(IncludePathResolver.Resolve(_includerPath, "<WinUser32.mqh>"));
        Assert.Null(IncludePathResolver.Resolve(_includerPath, "<Arrays\\ArrayLong.mqh>"));
    }

    [Fact]
    public void Resolve_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(IncludePathResolver.Resolve(_includerPath, null));
        Assert.Null(IncludePathResolver.Resolve(_includerPath, string.Empty));
        Assert.Null(IncludePathResolver.Resolve(_includerPath, "  "));
    }

    // --- TryResolveContained (resolve + exists + PathSecurity containment) ---

    [Fact]
    public void TryResolveContained_ExistingSiblingInclude_Resolves()
    {
        File.WriteAllText(Path.Combine(_tempDir, "Included.mqh"), "int y = 2;\n");
        var ok = IncludePathResolver.TryResolveContained(_includerPath, "Included.mqh", out var resolved);
        Assert.True(ok);
        Assert.Equal(Path.Combine(_tempDir, "Included.mqh"), resolved);
    }

    [Fact]
    public void TryResolveContained_MissingFile_ReturnsFalse()
    {
        var ok = IncludePathResolver.TryResolveContained(_includerPath, "NotThere.mqh", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryResolveContained_PathTraversalOutsideIncluderDir_ReturnsFalse()
    {
        // Escape target exists on disk but is OUTSIDE the includer's
        // directory tree — the PathSecurity containment guard must reject it.
        var escapeDir = Path.Combine(Path.GetTempPath(), "IncludePathResolverEscape_" + Guid.NewGuid());
        Directory.CreateDirectory(escapeDir);
        try
        {
            var escapeFile = Path.Combine(escapeDir, "outside.mqh");
            File.WriteAllText(escapeFile, "int z = 3;\n");
            var ok = IncludePathResolver.TryResolveContained(_includerPath, "../../outside.mqh", out _);
            Assert.False(ok);
        }
        finally
        {
            Directory.Delete(escapeDir, true);
        }
    }

    [Fact]
    public void TryResolveContained_AngleBracketSystemInclude_ReturnsFalse()
    {
        var ok = IncludePathResolver.TryResolveContained(_includerPath, "<stdlib.mqh>", out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryResolveContained_InsideSubdirectoryOfIncluderDir_Resolves()
    {
        var sub = Path.Combine(_tempDir, "Sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Nested.mqh"), "int n = 4;\n");
        var ok = IncludePathResolver.TryResolveContained(_includerPath, "Sub/Nested.mqh", out var resolved);
        Assert.True(ok);
        Assert.Equal(Path.Combine(sub, "Nested.mqh"), resolved);
    }

    [Fact]
    public void TryResolveContained_QuotedBackslashSeparator_Resolves()
    {
        // Same separator-agnostic contract as Resolve, through the containment
        // path the include-closure walker uses.
        var sub = Path.Combine(_tempDir, "Lib");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "wfa.mqh"), "int w = 5;\n");

        var ok = IncludePathResolver.TryResolveContained(_includerPath, "Lib\\wfa.mqh", out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.Combine(sub, "wfa.mqh"), resolved);
    }

    [Fact]
    public void TryResolveContained_QuotedParentRelativeInsideSystemRoot_Resolves()
    {
        // Issue #186: a quoted parent-relative entry written inside a declared
        // system include root must resolve when the target stays inside that
        // same root. Inside a declared root the trusted unit is the ROOT, not
        // the including header's own directory: the root is what the user
        // declared trustworthy, and real library headers reach sibling
        // directories of the Include tree with the quoted form. The
        // includer-directory rule keeps protecting everything outside one.
        var root = Path.Combine(_tempDir, "SystemRoot");
        var includer = Path.Combine(root, "Trade", "Trade.mqh");
        var target = Path.Combine(root, "Arrays", "ArrayLong.mqh");
        Directory.CreateDirectory(Path.Combine(root, "Trade"));
        Directory.CreateDirectory(Path.Combine(root, "Arrays"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(target, "int arrayLong = 2;\n");

        IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { root });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(includer, @"..\Arrays\ArrayLong.mqh", out var resolved);

            Assert.True(ok);
            Assert.Equal(target, resolved);
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveContained_QuotedParentRelativeEscapingSystemRoot_ReturnsFalse()
    {
        // Control pin (issue #186): the widening admits only targets inside a
        // declared root. A quoted hop that escapes the root entirely — even
        // with roots configured — must stay rejected.
        var root = Path.Combine(_tempDir, "SystemRoot");
        var includer = Path.Combine(root, "Trade", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(root, "Trade"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(Path.Combine(_tempDir, "outside.mqh"), "int outside = 3;\n");

        IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { root });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(includer, @"..\..\outside.mqh", out _);

            Assert.False(ok);
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveContained_QuotedParentRelativeAcrossTwoRoots_ReturnsFalse()
    {
        // Common-root rule: a hop from one declared root into a DIFFERENT
        // declared root stays rejected — the includer and the target must
        // share one single declared root, not merely two root hits.
        var rootA = Path.Combine(_tempDir, "RootA");
        var rootB = Path.Combine(_tempDir, "RootB");
        var includer = Path.Combine(rootA, "Trade", "Trade.mqh");
        var target = Path.Combine(rootB, "Arrays", "ArrayLong.mqh");
        Directory.CreateDirectory(Path.Combine(rootA, "Trade"));
        Directory.CreateDirectory(Path.Combine(rootB, "Arrays"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(target, "int arrayLong = 2;\n");

        IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { rootA, rootB });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(
                includer, @"..\..\RootB\Arrays\ArrayLong.mqh", out _);

            Assert.False(ok);
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
        }
    }

    // --- Issue #200: quoted parent-relative include between sibling folders of
    //     an ordinary workspace folder is admitted by the containment gate. ---

    [Fact]
    public void TryResolveContained_QuotedParentRelativeInsideWorkspaceFolder_Resolves()
    {
        // Issue #200: the #186 rule admitted a quoted parent-relative hop only
        // when BOTH sides sat inside one declared SYSTEM include root. The
        // ordinary-workspace shape is different: the folders are declared
        // workspace folders, not system roots. A quoted parent-relative include
        // between sibling folders of the SAME workspace folder
        // (e.g. `..\Common\util.mqh` from `Include\Trade.mqh`) resolves and is
        // readable but the includer-directory guard alone rejects it, silently
        // dropping the include edge. It must now be admitted.
        var workspace = Path.Combine(_tempDir, "Workspace");
        var includer = Path.Combine(workspace, "Include", "Trade.mqh");
        var target = Path.Combine(workspace, "Common", "util.mqh");
        Directory.CreateDirectory(Path.Combine(workspace, "Include"));
        Directory.CreateDirectory(Path.Combine(workspace, "Common"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(target, "int util = 2;\n");

        IncludePathResolver.SetWorkspaceRootsProvider(() => new[] { workspace });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(includer, @"..\Common\util.mqh", out var resolved);

            Assert.True(ok);
            Assert.Equal(target, resolved);
        }
        finally
        {
            IncludePathResolver.SetWorkspaceRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveContained_QuotedParentRelativeEscapingWorkspaceFolder_ReturnsFalse()
    {
        // Control pin (issue #200): the widening admits only targets inside a
        // declared workspace folder. A quoted hop that escapes the folder
        // entirely — even with the folder declared — must stay rejected.
        var workspace = Path.Combine(_tempDir, "Workspace");
        var includer = Path.Combine(workspace, "Include", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(workspace, "Include"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(Path.Combine(_tempDir, "outside.mqh"), "int outside = 3;\n");

        IncludePathResolver.SetWorkspaceRootsProvider(() => new[] { workspace });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(includer, @"..\..\outside.mqh", out _);

            Assert.False(ok);
        }
        finally
        {
            IncludePathResolver.SetWorkspaceRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveContained_QuotedParentRelativeAcrossTwoWorkspaceFolders_ReturnsFalse()
    {
        // Common-root rule (issue #200): a hop from one declared workspace
        // folder into a DIFFERENT one stays rejected — the includer and the
        // target must share one single declared root, not merely two hits.
        var workspaceA = Path.Combine(_tempDir, "WorkspaceA");
        var workspaceB = Path.Combine(_tempDir, "WorkspaceB");
        var includer = Path.Combine(workspaceA, "Include", "Trade.mqh");
        var target = Path.Combine(workspaceB, "Common", "util.mqh");
        Directory.CreateDirectory(Path.Combine(workspaceA, "Include"));
        Directory.CreateDirectory(Path.Combine(workspaceB, "Common"));
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(target, "int util = 2;\n");

        IncludePathResolver.SetWorkspaceRootsProvider(() => new[] { workspaceA, workspaceB });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(
                includer, @"..\..\WorkspaceB\Common\util.mqh", out _);

            Assert.False(ok);
        }
        finally
        {
            IncludePathResolver.SetWorkspaceRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveContained_SystemRootStillAdmittedWhenWorkspaceFoldersAlsoDeclared()
    {
        // Issue #200 regression guard: the #186 system-root admission must keep
        // working once workspace folders are also declared (the gate now unions
        // the two root sets, so a hop inside a system root is still admitted).
        var systemRoot = Path.Combine(_tempDir, "SystemRoot");
        var workspace = Path.Combine(_tempDir, "Workspace");
        var includer = Path.Combine(systemRoot, "Trade", "Trade.mqh");
        var target = Path.Combine(systemRoot, "Arrays", "ArrayLong.mqh");
        Directory.CreateDirectory(Path.Combine(systemRoot, "Trade"));
        Directory.CreateDirectory(Path.Combine(systemRoot, "Arrays"));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(includer, "int trade = 1;\n");
        File.WriteAllText(target, "int arrayLong = 2;\n");

        IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { systemRoot });
        IncludePathResolver.SetWorkspaceRootsProvider(() => new[] { workspace });
        try
        {
            var ok = IncludePathResolver.TryResolveContained(includer, @"..\Arrays\ArrayLong.mqh", out var resolved);

            Assert.True(ok);
            Assert.Equal(target, resolved);
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
            IncludePathResolver.SetWorkspaceRootsProvider(null);
        }
    }

    // --- System include roots (T3: angle-bracket resolution against configured roots) ---

    /// <summary>
    /// Creates a fake terminal include root under the instance temp dir
    /// (cleaned up by Dispose) with a StdLib/ subdirectory and returns the
    /// full path the &lt;StdLib/Trade.mqh&gt; entry must resolve to.
    /// </summary>
    private string CreateSystemRoot(out string headerPath)
    {
        var root = Path.Combine(_tempDir, "IncludeRoot");
        Directory.CreateDirectory(Path.Combine(root, "StdLib"));
        headerPath = Path.Combine(root, "StdLib", "Trade.mqh");
        return root;
    }

    [Fact]
    public void Resolve_AngleBracketEntry_WithRoot_ForwardSlashEntry_FindsHeader()
    {
        var root = CreateSystemRoot(out var headerPath);
        File.WriteAllText(headerPath, "// stdlib\n");

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib/Trade.mqh>", new[] { root });

        Assert.Equal(headerPath, resolved);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_WithRoot_BackslashEntry_FindsHeader()
    {
        // MQL stdlib directives use the Windows separator: <Trade\Trade.mqh>.
        var root = CreateSystemRoot(out var headerPath);
        File.WriteAllText(headerPath, "// stdlib\n");

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib\\Trade.mqh>", new[] { root });

        Assert.Equal(headerPath, resolved);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_WithRoot_DeeplyNestedSubpath_FindsHeader()
    {
        var root = CreateSystemRoot(out _);
        var deep = Path.Combine(root, "A", "B");
        Directory.CreateDirectory(deep);
        var deepHeader = Path.Combine(deep, "Baz.mqh");
        File.WriteAllText(deepHeader, "// stdlib\n");

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<A\\B\\Baz.mqh>", new[] { root });

        Assert.Equal(deepHeader, resolved);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_WithRoot_CaseInsensitiveFallback_FindsHeader()
    {
        // Windows-style case-insensitive semantics: the on-disk casing
        // (trade.mqh) differs from the entry (<StdLib/Trade.mqh>).
        var root = CreateSystemRoot(out _);
        File.WriteAllText(Path.Combine(root, "StdLib", "trade.mqh"), "// stdlib\n");

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib/Trade.mqh>", new[] { root });

        Assert.NotNull(resolved);
        Assert.Equal(
            Path.Combine(root, "StdLib", "trade.mqh"),
            resolved,
            ignoreCase: true,
            ignoreLineEndingDifferences: false,
            ignoreWhiteSpaceDifferences: false,
            ignoreAllWhiteSpace: false);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_MissingUnderRoot_ReturnsNull()
    {
        var root = CreateSystemRoot(out _);

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib/Missing.mqh>", new[] { root });

        Assert.Null(resolved);
    }

    [Fact]
    public void Resolve_AngleBracketEntry_NoRoots_ReturnsNull_TodaysBehavior()
    {
        // Fail open: no configured roots keep today's behavior — angle-bracket
        // entries stay unresolved.
        Assert.Null(IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib/Trade.mqh>", null));
        Assert.Null(IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<StdLib/Trade.mqh>", Array.Empty<string>()));
    }

    [Fact]
    public void Resolve_AngleBracketEntry_TraversalUnderRoot_Rejected()
    {
        // A system entry must never escape the configured root.
        var root = CreateSystemRoot(out var headerPath);
        File.WriteAllText(headerPath, "// stdlib\n");

        var resolved = IncludePathResolver.ResolveWithSystemRoots(_includerPath, "<..\\..\\StdLib\\Trade.mqh>", new[] { root });

        Assert.Null(resolved);
    }

    [Fact]
    public void TryResolveSystemInclude_ProviderConfigured_Resolves()
    {
        try
        {
            var root = CreateSystemRoot(out var headerPath);
            File.WriteAllText(headerPath, "// stdlib\n");
            IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { root });

            var ok = IncludePathResolver.TryResolveSystemInclude("<StdLib\\Trade.mqh>", out var resolved);

            Assert.True(ok);
            Assert.Equal(headerPath, resolved);
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
        }
    }

    [Fact]
    public void TryResolveSystemInclude_NoProvider_ReturnsFalse()
    {
        var root = CreateSystemRoot(out var headerPath);
        File.WriteAllText(headerPath, "// stdlib\n");

        Assert.False(IncludePathResolver.TryResolveSystemInclude("<StdLib\\Trade.mqh>", out var resolved));
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveSystemInclude_QuotedEntry_ReturnsFalse()
    {
        var root = CreateSystemRoot(out _);
        File.WriteAllText(Path.Combine(root, "StdLib", "Trade.mqh"), "// stdlib\n");
        IncludePathResolver.SetSystemIncludeRootsProvider(() => new[] { root });
        try
        {
            // Quoted entries keep their relative-to-includer semantics; the
            // system-include path must never claim them.
            Assert.False(IncludePathResolver.TryResolveSystemInclude("\"Trade.mqh\"", out _));
        }
        finally
        {
            IncludePathResolver.SetSystemIncludeRootsProvider(null);
        }
    }
}