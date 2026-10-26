using System;
using System.IO;
using System.Linq;
using System.Threading;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Parser;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Issue #126: opened-EA document regression guard.
///
/// Synthetic fixture (header declaring enums + one function; EA calling core
/// builtins, stdlib enum constants, and the header's symbols). Reproduces the
/// two false-positive classes reported in #126 and pins both fixes:
///
/// 1. Core MQL4 builtins / underscore predefined variables / stdlib enum
///    constants resolve through the dialect registry (Gap A).
/// 2. Identifiers declared in the include closure — enum members included —
///    are suppressed by the workspace-correlated tier (Gap B), even though
///    the symbol tree does not capture enum members as symbols.
///
/// A genuinely-undeclared control symbol must STILL error (no over-suppression).
/// Entirely synthetic: every name here is either invented for this fixture or
/// standard published MQL API.
/// </summary>
public class Issue126EaDocumentFalsePositivesTests
{
    private const string HeaderContent = """
        // Synthetic include header (issue #126 fixture).
        enum TradeMode
        {
            MODE_OFF = 0,
            MODE_CONSERVATIVE = 1,
            MODE_AGGRESSIVE = 2
        };

        enum TimeframeFlag
        {
            TF_CURRENT = 0,
            TF_H1 = 16385
        };

        int RiskPoints(int level)
        {
            return level * 10;
        }
        """;

    private const string EaContent = """
        #include "enums_utils.mqh"

        int OnInit()
        {
            PrintFormat("symbol=%s", _Symbol);
            Sleep(100);
            long id = ChartID();
            long v = ChartGetInteger(id, CHART_CANDLES);
            ChartSetInteger(id, OBJPROP_BMPFILE, v);
            datetime t = iTime(_Symbol, PERIOD_CURRENT, 0);
            GlobalVariableSet("g", 1.0);
            double g = GlobalVariableGet("g");
            datetime st = StringToTime("2024.01.01");
            FileWriteInteger(1, 7);
            int r = FileReadInteger(1);
            FileFlush(1);
            ZeroMemory(g);
            TerminalClose(0);
            ExpertRemove();
            int total = ObjectsTotal();
            ResetLastError();
            int n = rand();
            double x = round(1.4);
            TradeMode mode = MODE_AGGRESSIVE;
            TimeframeFlag tf = TF_CURRENT;
            int points = RiskPoints(2);
            // Control: genuinely undeclared anywhere — must keep erroring.
            int bad = TotallyUndeclaredHelper(points);
            return 0;
        }
        """;

    private static (string eaPath, string headerPath) WriteFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "issue126", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var headerPath = Path.Combine(root, "enums_utils.mqh");
        var eaPath = Path.Combine(root, "monolith_sim.mq4");
        File.WriteAllText(headerPath, HeaderContent);
        File.WriteAllText(eaPath, EaContent);
        return (eaPath, headerPath);
    }

    private static List<Diagnostic> GenerateForEa(string eaPath, string headerPath)
    {
        var index = new GlobalSymbolIndex(ctorBypass: true);
        var parser = new Mql4AntlrParser();

        // Mirror the workspace scan's header indexing (pass B): symbols AND
        // token occurrences land in the index.
        var parsedHeader = parser.ParseFile(HeaderContent, headerPath, CancellationToken.None);
        parsedHeader.Language = MqlLanguage.Mql4;
        index.AddFile(headerPath, MqlLanguage.Mql4, parsedHeader.Symbols,
            SymbolOccurrenceMapper.Map(parsedHeader, headerPath, MqlLanguage.Mql4));

        var parsedEa = parser.ParseFile(EaContent, eaPath, CancellationToken.None);
        parsedEa.Language = MqlLanguage.Mql4;

        var analyzer = new SemanticAnalyzer(new IMqlBuiltins[]
        {
            new Mql4BuiltinsAdapter(),
            new Mql5Builtins()
        });

        return DocumentDiagnostics.Generate(
            NullLogger.Instance,
            parsedEa,
            EaContent,
            MqlLanguage.Mql4,
            CancellationToken.None,
            eaPath,
            analyzer,
            index);
    }

    [Fact]
    public void EaDocument_Fp1070_IsZero()
    {
        var (eaPath, headerPath) = WriteFixture();
        var diagnostics = GenerateForEa(eaPath, headerPath);

        var unresolved = diagnostics
            .Where(d => d.Code == "1070")
            .Select(d => d.Data?.ToString() ?? string.Empty)
            .ToList();

        // Every flagged name except the control symbol must be resolved
        // (registry or include-closure suppression).
        Assert.True(unresolved.Count == 1 &&
                    unresolved[0].Contains("TotallyUndeclaredHelper"),
            $"Expected only the control symbol to remain, got {unresolved.Count}: {string.Join("; ", unresolved)}");
    }

    [Fact]
    public void EaDocument_GenuinelyUndeclaredControl_StillErrors()
    {
        var (eaPath, headerPath) = WriteFixture();
        var diagnostics = GenerateForEa(eaPath, headerPath);

        var control = diagnostics
            .Where(d => d.Code == "1070")
            .Where(d => (d.Data?.ToString() ?? string.Empty).Contains("TotallyUndeclaredHelper"))
            .ToList();

        Assert.Single(control);
        Assert.Equal(DiagnosticSeverity.Error, control[0].Severity);
    }
}
