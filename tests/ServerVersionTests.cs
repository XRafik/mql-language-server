using System.Reflection;
using MqlLanguageServer;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace MqlLanguageServer.Tests;

/// <summary>
/// Tests for the assembly-derived server version (issue #22).
/// Guards against version drift: the version reported by --version and the
/// LSP initialize response (ServerInfo) must always equal the version of the
/// running assembly, which MSBuild stamps from &lt;Version&gt; in the csproj.
/// Since 2.5.0-rc.1 the value comes from the AssemblyInformationalVersion
/// (which carries prerelease labels) instead of the numeric AssemblyVersion
/// that MSBuild strips the label from — an rc build printed "2.5.0" for a
/// 2.5.0-rc.1 package, indistinguishable from the stable line.
/// </summary>
public class ServerVersionTests
{
    [Fact]
    public void Version_MatchesAssemblyInformationalVersion_WithoutBuildMetadata()
    {
        // Arrange: MSBuild stamps InformationalVersion as "<Version>+<sha>"
        // for CI builds; the display strips the "+metadata" suffix only.
        var informational = Assembly.GetAssembly(typeof(ServerVersion))!
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        // Act
        var version = ServerVersion.Version;

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(informational.Split('+')[0], version);
        Assert.DoesNotContain("+", version);
    }

    [Fact]
    public void Version_StartsWithTheNumericAssemblyVersionPrefix()
    {
        // The numeric AssemblyVersion (major.minor[.0]) must remain the prefix
        // of the displayed version: 2.5.0-rc.1 displays as "2.5.0-rc.1", not
        // "2.4.2" or "1.0.0".
        var assemblyVersion = Assembly.GetAssembly(typeof(ServerVersion))!.GetName().Version!;
        var prefix = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";

        Assert.StartsWith(prefix, ServerVersion.Version);
    }

    [Fact]
    public void FormatVersion_StripsBuildMetadata_PreservesPrereleaseLabel()
    {
        // CI builds: "<Version>+<source-hash>" → the hash is metadata, not version.
        Assert.Equal("2.4.2", ServerVersion.FormatVersion("2.4.2+bc6ba2eee2a5abbcc6d760f6d20eecdb404ee3e9", null));
        // Prerelease labels are user-facing and MUST survive.
        Assert.Equal("2.5.0-rc.1", ServerVersion.FormatVersion("2.5.0-rc.1+763a741edd1f86e5b8151621ff6859dbb7c02d0a", null));
        Assert.Equal("2.4.0-rc.3", ServerVersion.FormatVersion("2.4.0-rc.3", null));
        // Stable informational versions without metadata pass through.
        Assert.Equal("2.5.0", ServerVersion.FormatVersion("2.5.0", null));
    }

    [Fact]
    public void FormatVersion_FallsBackToNumericAssemblyVersion_WhenInformationalIsMissing()
    {
        // Older/edge assemblies without an informational attribute keep the
        // legacy major.minor.build derivation (the issue #22 behavior).
        Assert.Equal("2.4.2", ServerVersion.FormatVersion(null, new Version(2, 4, 2, 0)));
        Assert.Equal("unknown", ServerVersion.FormatVersion(null, null));
    }

    [Fact]
    public void Version_IsNotStaleHardcodedValue()
    {
        // Regression guard for issue #22: the initialize response used to
        // report a hardcoded "1.0.0" while the package shipped 2.x.
        Assert.NotEqual("1.0.0", ServerVersion.Version);
    }

    [Fact]
    public void ServerInfo_CanBeBuiltFromDerivedVersion()
    {
        // Mirrors the WithServerInfo wiring in Program.cs: the ServerInfo
        // sent in the initialize response carries the assembly-derived version.
        var serverInfo = new ServerInfo
        {
            Name = Constants.Server.Name,
            Version = ServerVersion.Version
        };

        Assert.Equal(Constants.Server.Name, serverInfo.Name);
        Assert.Equal(ServerVersion.Version, serverInfo.Version);
        Assert.NotEqual("1.0.0", serverInfo.Version);
    }
}