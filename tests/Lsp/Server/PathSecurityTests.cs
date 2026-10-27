using System;
using System.IO;
using MqlLanguageServer.Lsp.Server;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Server;

/// <summary>
/// Tests for PathSecurity symlink-traversal guard (A-001).
/// </summary>
public class PathSecurityTests
{
    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mql-lsp-pathsec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void IsContainedInWorkspace_AcceptsDescendantPath()
    {
        var workspace = CreateTempDir();
        var including = Path.Combine(workspace, "main.mq4");
        File.WriteAllText(including, "// x");
        var include = Path.Combine(workspace, "sub", "header.mqh");
        Directory.CreateDirectory(Path.Combine(workspace, "sub"));
        File.WriteAllText(include, "// h");

        Assert.True(PathSecurity.IsContainedInWorkspace(including, include));

        Cleanup(workspace);
    }

    [Fact]
    public void IsContainedInWorkspace_RejectsParentTraversal()
    {
        var workspace = CreateTempDir();
        var including = Path.Combine(workspace, "main.mq4");
        File.WriteAllText(including, "// x");
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".mqh");
        File.WriteAllText(outside, "// secret");

        Assert.False(PathSecurity.IsContainedInWorkspace(including, outside));

        Cleanup(workspace);
        File.Delete(outside);
    }

    [Fact]
    public void IsContainedInWorkspace_RejectsSymlinkEscapingWorkspace()
    {
        // Symlink created inside the workspace whose target lives outside the workspace.
        var workspace = CreateTempDir();
        var outsideDir = Path.Combine(Path.GetTempPath(), "mql-lsp-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideDir);
        var outsideTarget = Path.Combine(outsideDir, "secret.mqh");
        File.WriteAllText(outsideTarget, "// secret");

        var including = Path.Combine(workspace, "main.mq4");
        File.WriteAllText(including, "// x");

        var linkPath = Path.Combine(workspace, "link.mqh");
        try
        {
            File.CreateSymbolicLink(linkPath, outsideTarget);
        }
        catch (PlatformNotSupportedException)
        {
            Cleanup(workspace);
            Cleanup(outsideDir);
            throw; // rethrow so the test is skipped on platforms without symlinks
        }

        // The symlink's resolved real path points outside the workspace -> must reject.
        Assert.False(PathSecurity.IsContainedInWorkspace(including, linkPath));

        Cleanup(workspace);
        Cleanup(outsideDir);
    }

    [Fact]
    public void IsContainedInWorkspace_AcceptsSymlinkInsideWorkspace()
    {
        var workspace = CreateTempDir();
        var realInside = Path.Combine(workspace, "real.mqh");
        File.WriteAllText(realInside, "// real");
        var including = Path.Combine(workspace, "main.mq4");
        File.WriteAllText(including, "// x");

        var linkPath = Path.Combine(workspace, "link.mqh");
        try
        {
            File.CreateSymbolicLink(linkPath, realInside);
        }
        catch (PlatformNotSupportedException)
        {
            Cleanup(workspace);
            throw;
        }

        Assert.True(PathSecurity.IsContainedInWorkspace(including, linkPath));

        Cleanup(workspace);
    }

    [Fact]
    public void IsContainedInWorkspace_RejectsSymlinkLoop_FailClosed()
    {
        // Issue #21b: a symlink loop makes ResolveLinkTarget throw
        // ("too many levels of symbolic links"). The old implementation fell
        // back to the unresolved path and ALLOWED the include (fail-open);
        // the fail-closed guard must reject it instead.
        var workspace = CreateTempDir();
        var including = Path.Combine(workspace, "main.mq4");
        File.WriteAllText(including, "// x");

        var linkPath = Path.Combine(workspace, "loop.mqh");
        try
        {
            File.CreateSymbolicLink(linkPath, linkPath);
        }
        catch (PlatformNotSupportedException)
        {
            Cleanup(workspace);
            throw; // rethrow so the test is skipped on platforms without symlinks
        }

        Assert.False(PathSecurity.IsContainedInWorkspace(including, linkPath));

        Cleanup(workspace);
    }

    [Fact]
    public void IsUnderAnyRoot_RejectsSymlinkLoopPath_FailClosed()
    {
        // Issue #21b, read-guard side: a symlink loop anywhere in the queried
        // path makes resolution fail; the path must be rejected rather than
        // falling back to the unresolved value.
        var workspace = CreateTempDir();
        File.WriteAllText(Path.Combine(workspace, "real.mq5"), "int x;");

        var linkPath = Path.Combine(workspace, "loop.mq5");
        try
        {
            File.CreateSymbolicLink(linkPath, linkPath);
        }
        catch (PlatformNotSupportedException)
        {
            Cleanup(workspace);
            throw;
        }

        Assert.False(PathSecurity.IsUnderAnyRoot(linkPath, new[] { workspace }));

        Cleanup(workspace);
    }

    [Fact]
    public void IsUnderAnyRoot_NonLinkPaths_StillAllowed()
    {
        // Fail-closed must not break the ordinary case: real files under a
        // declared root are not symlinks, resolution "fails" only in the sense
        // that no link exists, and containment must keep working.
        var workspace = CreateTempDir();
        var file = Path.Combine(workspace, "plain.mq5");
        File.WriteAllText(file, "int y;");

        Assert.True(PathSecurity.IsUnderAnyRoot(file, new[] { workspace }));

        Cleanup(workspace);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public void IsContainedInWorkspace_RejectsNullOrEmpty(string? a, string? b)
    {
        Assert.False(PathSecurity.IsContainedInWorkspace(a!, b!));
    }

    // --- IsContainedInCommonSystemRoot (issue #186: the root, not the header's
    //     own directory, is the trusted unit inside a declared system root) ---

    [Fact]
    public void IsContainedInCommonSystemRoot_BothSidesInsideOneRoot_True()
    {
        // Trade\Trade.mqh reaching Collections\ with the quoted parent-relative
        // form: includer and target share one declared root.
        var root = CreateTempDir();
        var including = Path.Combine(root, "Trade", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(root, "Trade"));
        File.WriteAllText(including, "// x");
        var target = Path.Combine(root, "Collections", "ArrayLong.mqh");
        Directory.CreateDirectory(Path.Combine(root, "Collections"));
        File.WriteAllText(target, "// h");

        Assert.True(PathSecurity.IsContainedInCommonSystemRoot(including, target, new[] { root }));

        Cleanup(root);
    }

    [Fact]
    public void IsContainedInCommonSystemRoot_TargetOutsideEveryRoot_False()
    {
        // The widening never reaches an arbitrary path: the target escaped the
        // only declared root (#18/#21b rule kept outside a root).
        var root = CreateTempDir();
        var including = Path.Combine(root, "Trade", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(root, "Trade"));
        File.WriteAllText(including, "// x");
        var outside = Path.Combine(Path.GetTempPath(), "escaped-" + Guid.NewGuid().ToString("N") + ".mqh");
        File.WriteAllText(outside, "// secret");

        Assert.False(PathSecurity.IsContainedInCommonSystemRoot(including, outside, new[] { root }));

        Cleanup(root);
        File.Delete(outside);
    }

    [Fact]
    public void IsContainedInCommonSystemRoot_TwoDifferentRoots_False()
    {
        // One and the SAME root must hold both sides: an includer declared under
        // RootA cannot hop into RootB just because RootB is also declared.
        var rootA = CreateTempDir();
        var rootB = CreateTempDir();
        var including = Path.Combine(rootA, "Trade", "Trade.mqh");
        Directory.CreateDirectory(Path.Combine(rootA, "Trade"));
        File.WriteAllText(including, "// x");
        var target = Path.Combine(rootB, "Arrays", "ArrayLong.mqh");
        Directory.CreateDirectory(Path.Combine(rootB, "Arrays"));
        File.WriteAllText(target, "// h");

        Assert.False(PathSecurity.IsContainedInCommonSystemRoot(including, target, new[] { rootA, rootB }));

        Cleanup(rootA);
        Cleanup(rootB);
    }

    [Fact]
    public void IsContainedInCommonSystemRoot_NoRootsDeclared_False()
    {
        // Fail open (issue #179 contract): with no root declared nothing is
        // admitted, so pre-feature behaviour is unchanged.
        var root = CreateTempDir();
        var including = Path.Combine(root, "Trade.mqh");
        File.WriteAllText(including, "// x");
        var target = Path.Combine(root, "ArrayLong.mqh");
        File.WriteAllText(target, "// h");

        Assert.False(PathSecurity.IsContainedInCommonSystemRoot(including, target, Array.Empty<string>()));

        Cleanup(root);
    }

    [Theory]
    [InlineData("", "x")]
    [InlineData("x", "")]
    [InlineData(null, "x")]
    [InlineData("x", null)]
    public void IsContainedInCommonSystemRoot_RejectsNullOrEmpty(string? a, string? b)
    {
        Assert.False(PathSecurity.IsContainedInCommonSystemRoot(a!, b!, new[] { "/tmp" }));
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* best effort */ }
    }
}