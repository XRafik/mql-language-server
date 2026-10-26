using System.Reflection;

namespace MqlLanguageServer;

/// <summary>
/// Single source of truth for the server version at runtime (issue #22).
/// The version is derived from this assembly, whose version the MSBuild SDK
/// stamps from <c>&lt;Version&gt;</c> in MqlLanguageServer.Server.csproj —
/// the same value CI's tag↔csproj version gate enforces. The former
/// hardcoded constant (Constants.Server.Version = "1.0.0") drifted from the
/// packaged version, so any code that needs to display the server version
/// must read it here instead of hardcoding it.
/// </summary>
public static class ServerVersion
{
    /// <summary>
    /// Server version as written in <c>&lt;Version&gt;</c>, including
    /// prerelease labels (e.g. "2.5.0-rc.1") — everything the user sees in
    /// <c>--version</c>, the LSP initialize ServerInfo and the startup log
    /// must equal the packaged version, or a release candidate becomes
    /// indistinguishable from the stable line it belongs to.
    ///
    /// <para>The value comes from the <c>AssemblyInformationalVersion</c>:
    /// MSBuild strips the prerelease label from the numeric
    /// <c>AssemblyVersion</c> (2.5.0-rc.1 → 2.5.0.0), so the former
    /// assembly-name-based display reported "2.5.0" for an rc build. The
    /// InformationalVersion carries the full value plus the "+source-hash"
    /// CI metadata suffix, which is stripped for display.</para>
    /// </summary>
    public static string Version
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return FormatVersion(informational, assembly.GetName().Version);
        }
    }

    /// <summary>
    /// Pure formatting core (unit-tested with synthetic inputs): prefer the
    /// informational version without its "+metadata" suffix; fall back to the
    /// numeric assembly version (major.minor.build — the packaging revision is
    /// noise) when no informational version is present.
    /// </summary>
    internal static string FormatVersion(string? informationalVersion, System.Version? assemblyVersion)
    {
        if (!string.IsNullOrEmpty(informationalVersion))
        {
            var plus = informationalVersion.IndexOf('+');
            return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        }

        return assemblyVersion is null ? "unknown" : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
    }
}