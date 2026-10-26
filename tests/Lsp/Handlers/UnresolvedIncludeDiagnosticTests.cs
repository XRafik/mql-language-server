using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.Extensions.Logging.Abstractions;

using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using MqlLanguageServer.Analysis;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;

using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #179 (S4): unresolved angle-bracket <c>#include &lt;...&gt;</c> entries
/// produce exactly one actionable warning each (severity Warning, Source
/// "mql-lsp", numeric code following the shared scheme — 1004 for MQL4, 5004
/// for MQL5, the next free offset after 001/002/003/070/100), with a message
/// naming the include entry and both remedies (the <c>systemIncludeRoots</c>
/// initialization option or a <c>.mql-lsp.json</c> project file).
///
/// <para>Quoted includes are out of scope: only angle entries warn. The
/// undeclared-symbol (1070/5070) diagnostics stay byte-for-byte unchanged —
/// nothing is suppressed (user decision L2: warn, do not suppress).</para>
///
/// <para>Resolution goes through the existing resolver path
/// (<see cref="IncludePathResolver"/>) against the currently effective roots
/// (<see cref="SystemIncludeRoots.Current"/>).</para>
///
/// <para>Runs inside the shared "GlobalSymbolIndex Tests" collection: the class
/// mutates <see cref="SystemIncludeRoots"/> (process-wide static, also mutated
/// by <c>SystemIncludeResolutionTests</c>).</para>
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class UnresolvedIncludeDiagnosticTests : IDisposable
{
    private readonly string _baseDir;
    private readonly string _rootDir;      // fake terminal root WITH a resolvable header
    private readonly string _emptyRootDir; // configured root with no headers
    private readonly string _docPath;

    public UnresolvedIncludeDiagnosticTests()
    {
        _baseDir = Path.Combine(Path.GetTempPath(), "UnresolvedIncludeDiag_" + Guid.NewGuid().ToString("N"));
        _rootDir = Path.Combine(_baseDir, "Include");
        _emptyRootDir = Path.Combine(_baseDir, "EmptyInclude");
        Directory.CreateDirectory(Path.Combine(_rootDir, "Trade"));
        Directory.CreateDirectory(_emptyRootDir);
        File.WriteAllText(Path.Combine(_rootDir, "Trade", "Trade.mqh"),
            "// Trade.mqh (test stdlib header)\nclass CTrade\n{\npublic:\n   bool Buy(double volume);\n};\n");

        _docPath = Path.Combine(_baseDir, "ws", "ea.mq5");
        Directory.CreateDirectory(Path.GetDirectoryName(_docPath)!);
    }

    public void Dispose()
    {
        SystemIncludeRoots.Clear();
        try
        {
            Directory.Delete(_baseDir, recursive: true);
        }
        catch
        {
            // best effort on temp files
        }
    }

    private static List<Diagnostic> GenerateDiagnostics(string content, MqlLanguage language, string documentPath)
    {
        IMqlParser parser = language == MqlLanguage.Mql5 ? new Mql5AntlrParser() : new Mql4AntlrParser();
        var mqlFile = parser.ParseFile(content, documentPath, CancellationToken.None);
        mqlFile.Language = language;

        var analyzer = new SemanticAnalyzer(new IMqlBuiltins[]
        {
            new Mql4BuiltinsAdapter(),
            new Mql5Builtins()
        });

        return DocumentDiagnostics.Generate(
            NullLogger.Instance,
            mqlFile,
            content,
            language,
            CancellationToken.None,
            documentPath,
            analyzer,
            new GlobalSymbolIndex(ctorBypass: true));
    }

    private static List<Diagnostic> IncludeWarnings(List<Diagnostic> diagnostics, string mql5Code = "5004", string mql4Code = "1004")
        => diagnostics.Where(d => d.Code == mql5Code || d.Code == mql4Code).ToList();

    // --- The core S4 contract ---

    [Fact]
    public void UnresolvedAngleInclude_NoRoots_EmitsSingleWarningWithPinnedCodeAndRemedies()
    {
        // The directive is indented so the range pins real columns, not just lines.
        const string directive = "#include <Trade\\Trade.mqh>";
        var content = "   " + directive + "\nvoid OnTick() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        var warnings = IncludeWarnings(diagnostics);

        var warning = Assert.Single(warnings);
        Assert.Equal("5004", warning.Code); // pinned: next free offset (+4) after 001/002/003/070/100
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("mql-lsp", warning.Source);

        // Names the include entry and BOTH remedies.
        Assert.Contains("<Trade\\Trade.mqh>", warning.Message);
        Assert.Contains("systemIncludeRoots", warning.Message);
        Assert.Contains(".mql-lsp.json", warning.Message);

        // Range covers the include directive (not the whole line).
        Assert.Equal(0, warning.Range.Start.Line);
        Assert.Equal(3, warning.Range.Start.Character);
        Assert.Equal(0, warning.Range.End.Line);
        Assert.Equal(3 + directive.Length, warning.Range.End.Character);
    }

    [Fact]
    public void UnresolvedAngleInclude_RootsConfiguredButHeaderMissing_StillWarns()
    {
        SystemIncludeRoots.Set(new[] { _emptyRootDir });

        var content = "#include <Trade\\Trade.mqh>\nvoid OnTick() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        // A declared root that does not contain the header is still a broken
        // include: the actionable warning must fire (and name both remedies).
        Assert.Single(IncludeWarnings(diagnostics));
    }

    [Fact]
    public void ResolvableAngleInclude_RootsConfigured_EmitsNoWarning()
    {
        SystemIncludeRoots.Set(new[] { _rootDir });

        var content = "#include <Trade\\Trade.mqh>\nvoid OnTick() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        Assert.Empty(IncludeWarnings(diagnostics));
    }

    [Fact]
    public void QuotedInclude_Unresolved_NeverWarns()
    {
        // Quoted includes are out of scope (S4): even an unresolvable quoted
        // entry must not produce an include warning.
        var content = "#include \"missing_local_header.mqh\"\nvoid OnTick() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        Assert.Empty(IncludeWarnings(diagnostics));
    }

    [Fact]
    public void UnresolvedAngleInclude_Mql4_UsesPinnedCode1004()
    {
        var content = "#include <Arrays\\ArrayObj.mqh>\nvoid OnStart() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql4, _docPath);

        var warning = Assert.Single(IncludeWarnings(diagnostics, mql5Code: "5004", mql4Code: "1004"));
        Assert.Equal("1004", warning.Code); // MQL4 mirror of the pinned offset
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("mql-lsp", warning.Source);
        Assert.Contains("<Arrays\\ArrayObj.mqh>", warning.Message);
        Assert.Contains(".mql-lsp.json", warning.Message);
    }

    [Fact]
    public void TwoUnresolvedAngleIncludes_EmitExactlyOneWarningPerEntry()
    {
        var content = "#include <A\\One.mqh>\n#include <B\\Two.mqh>\nvoid OnTick() {}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        var warnings = IncludeWarnings(diagnostics);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("<A\\One.mqh>"));
        Assert.Contains(warnings, w => w.Message.Contains("<B\\Two.mqh>"));
    }

    // --- No-suppression guard: 1070/5070 stay byte-for-byte unchanged ---

    [Fact]
    public void UndeclaredSymbolDiagnostic_Unchanged_AlongsideIncludeWarning()
    {
        var content =
            "#include <Missing\\Lib.mqh>\n" +
            "void OnTick()\n" +
            "{\n" +
            "   int bad = TotallyUndeclaredHelper();\n" +
            "}\n";
        var diagnostics = GenerateDiagnostics(content, MqlLanguage.Mql5, _docPath);

        // The pre-existing undeclared-symbol diagnostic is untouched: same
        // code, severity, message and source as before this feature.
        var unresolved = diagnostics.Where(d => d.Code == "5070").ToList();
        var symbolDiagnostic = Assert.Single(unresolved, d => d.Data != null && d.Data.ToString()!.Contains("TotallyUndeclaredHelper"));
        Assert.Equal(DiagnosticSeverity.Error, symbolDiagnostic.Severity);
        Assert.Equal("Undeclared symbol 'TotallyUndeclaredHelper' is not defined in this document.", symbolDiagnostic.Message);
        Assert.Equal("mql-lsp", symbolDiagnostic.Source);

        // And the new include warning coexists — the feature suppresses nothing.
        Assert.Single(IncludeWarnings(diagnostics));
    }
}
