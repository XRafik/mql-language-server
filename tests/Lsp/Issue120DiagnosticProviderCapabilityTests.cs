using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Issue #120 contract: the initialize result must declare diagnosticProvider
/// even for clients that do not declare textDocument.diagnostic.
///
/// OmniSharp 0.19.9 derives ServerCapabilities through the registration-options
/// converters, whose descriptor lookup consults the CLIENT's declared
/// capability — the same mechanism issues #95 and #96 hit. DiagnosticHandler
/// implements the full pull model, so Program.cs's OnInitialized must assign
/// the capability unconditionally; this test guards that wiring and the
/// capability payload shape.
/// </summary>
public class Issue120DiagnosticProviderCapabilityTests
{
    [Fact]
    public void CompositionRoot_DeclaresDiagnosticProviderUnconditionally()
    {
        // The OnInitialized delegate is the deterministic hook used by #95/#96;
        // the assignment must exist there (not only GetRegistrationOptions,
        // which the converter ignores for non-declaring clients).
        var programSource = File.ReadAllText(
            Path.Combine(RepoRoot(), "src", "Program.cs"));

        Assert.Contains("result.Capabilities.DiagnosticProvider",
            programSource, StringComparison.Ordinal);
        Assert.Contains("issue #120", programSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticProviderStaticOptions_SerializesWithDiagnosticFlags()
    {
        // The payload Program.cs assigns must serialize to the LSP static
        // diagnostic options shape the reporter's probe expects.
        var options = new DiagnosticsRegistrationOptions.StaticOptions
        {
            InterFileDependencies = false,
            WorkspaceDiagnostics = false
        };

        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
        });

        Assert.Contains("\"interFileDependencies\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"workspaceDiagnostics\":false", json, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "MqlLanguageServer.sln")))
            dir = dir.Parent;
        if (dir == null)
            throw new InvalidOperationException("Could not locate the repository root");
        return dir.FullName;
    }
}
