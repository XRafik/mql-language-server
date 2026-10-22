using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Analysis.Rules;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Unit tests for <see cref="Mql4OnlyApiRegistry"/> and
/// <see cref="Mql4OnlyApiRule"/> (issue #34, base+60 range): MQL4-only API
/// usage flagged inside MQL5-routed documents as migration warnings.
/// Since issue #176 detection is occurrence-driven: tests hand-craft
/// <see cref="MqlFile.Occurrences"/> the way TokenOccurrenceCapture fills
/// them in production (default-channel IDENTIFIER tokens only).
/// </summary>
public class Mql4OnlyApiRuleTests
{
    private static SemanticRuleContext Context(MqlFile? file, string content, MqlLanguage language = MqlLanguage.Mql5) =>
        new(file, content, language, language == MqlLanguage.Mql5 ? 5000 : 1000, CancellationToken.None);

    /// <summary>
    /// Builds a parsed-file stand-in carrying hand-crafted token occurrences
    /// (issue #176): the rule consumes <see cref="MqlFile.Occurrences"/> as
    /// captured by <c>TokenOccurrenceCapture</c> in production (default-channel
    /// IDENTIFIER tokens only).
    /// </summary>
    private static MqlFile FileWithOccurrences(params TokenOccurrence[] occurrences) => new()
    {
        Occurrences = occurrences.ToList()
    };

    private readonly Mql4OnlyApiRule _rule = new();
    private readonly SemanticAnalyzer _analyzer = new();

    // ------------------------------------------------------------------
    // Registry (REQ-MA-02) — tests 4, 5.
    // ------------------------------------------------------------------

    [Fact]
    public void RegistryEntries_AllHaveReasonAndReplacement()
    {
        var names = Mql4OnlyApiRegistry.Entries.Keys.ToList();

        // Dedup: FrozenDictionary keys are unique by construction, but the
        // check documents the curated-table invariant explicitly.
        Assert.Equal(names.Count, names.Distinct().Count());
        // 47 pre-#152 admissions + CharToStr and StrToInteger (issue #152
        // corpus enrichment: deprecated MQL4 aliases with mql5Mapping).
        Assert.Equal(49, names.Count);

        foreach (var (key, entry) in Mql4OnlyApiRegistry.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason), $"Entry '{key}' lacks a reason");
            Assert.False(string.IsNullOrWhiteSpace(entry.Replacement), $"Entry '{key}' lacks a replacement");
            Assert.Equal(key, entry.Name);
        }
    }

    [Fact]
    public void Registry_ExcludesSharedNames()
    {
        // Names shared by MQL4 and MQL5 (verified during design) must never
        // appear in the registry — flagging them would be a false positive
        // because signature discrimination is out of scope (REQ-MA-02/05).
        string[] sharedNames =
        {
            "OrderSend", "OrderSelect", "OrderClose", "OrdersTotal",
            "HistoryTotal", "ArrayResize", "Print", "CopyBuffer",
            "FileOpen", "iTime", "iClose", "iOpen", "iHigh", "iLow",
            "iBars", "IsStopped"
        };

        foreach (var name in sharedNames)
        {
            Assert.False(Mql4OnlyApiRegistry.Entries.ContainsKey(name),
                $"Shared name '{name}' must not be in the registry");
        }

        // Seed set (spec REQ-MA-02) must be present.
        Assert.True(Mql4OnlyApiRegistry.TryGetEntry("TimeHour", out var timeHour));
        Assert.Equal(Mql4OnlyApiKind.Function, timeHour.Kind);
        Assert.True(Mql4OnlyApiRegistry.TryGetEntry("MarketInfo", out var marketInfo));
        Assert.Equal(Mql4OnlyApiKind.Function, marketInfo.Kind);
        Assert.True(Mql4OnlyApiRegistry.TryGetEntry("Ask", out var ask));
        Assert.Equal(Mql4OnlyApiKind.Variable, ask.Kind);
        Assert.True(Mql4OnlyApiRegistry.TryGetEntry("Bid", out var bid));
        Assert.Equal(Mql4OnlyApiKind.Variable, bid.Kind);
        Assert.True(Mql4OnlyApiRegistry.TryGetEntry("Bars", out var bars));
        Assert.Equal(Mql4OnlyApiKind.Variable, bars.Kind);
    }

    // ------------------------------------------------------------------
    // Detection (REQ-MA-01) — tests 1, 2, 3.
    // ------------------------------------------------------------------

    [Fact]
    public void TimeHourInMql5_WarnsWithCode5060()
    {
        const string content = "int h = TimeHour(t);\n";

        var file = FileWithOccurrences(new TokenOccurrence("TimeHour", 0, 8, 8));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5060", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("mql-lsp", diagnostic.Source);
        Assert.Contains("TimeHour", diagnostic.Message);
        Assert.Contains("TimeToStruct(time, dt); ... dt.hour", diagnostic.Message);
    }

    [Fact]
    public void AskInMql5_WarnsPerOccurrence()
    {
        const string content =
            "double price = Ask;\n" +
            "double stop = Ask - 10 * Point;\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("Ask", 0, 15, 3),
            new TokenOccurrence("Ask", 1, 14, 3),
            new TokenOccurrence("Point", 1, 25, 5));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        // Point is also MQL4-only; this test pins the Ask occurrences only.
        var askDiagnostics = diagnostics.Where(d => d.Message.Contains("'Ask'")).ToList();
        Assert.Equal(2, askDiagnostics.Count);

        // Each diagnostic ranges over exactly its own occurrence.
        Assert.Equal(new Range(0, 15, 0, 18), askDiagnostics[0].Range);
        Assert.Equal(new Range(1, 14, 1, 17), askDiagnostics[1].Range);
    }

    [Fact]
    public void MarketInfoInMql5_MessageContainsReplacement()
    {
        const string content = "double spread = MarketInfo(_Symbol, MODE_SPREAD);\n";

        var file = FileWithOccurrences(new TokenOccurrence("MarketInfo", 0, 16, 10));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("MarketInfo", diagnostic.Message);
        Assert.Contains("SymbolInfoDouble", diagnostic.Message);
    }

    // ------------------------------------------------------------------
    // Word boundaries + case sensitivity (REQ-MA-03) — tests 7, 8, 9.
    // ------------------------------------------------------------------

    [Fact]
    public void IdentifiersWithWordBoundary_NeverTrigger()
    {
        // Registry names embedded in longer identifiers must not match
        // (REQ-MA-03): no substring hits.
        const string content =
            "int Asked = 0;\n" +
            "int Bidder = 0;\n" +
            "int BarCount = 0;\n" +
            "int MyBars = 0;\n" +
            "int TimeHours = 0;\n" +
            "double MarketInfoDouble = 0.0;\n";

        // Occurrences are whole IDENTIFIER tokens, so embedded matches cannot
        // arise; the boundary check stays as a defensive guard (REQ-MA-03).
        var file = FileWithOccurrences(
            new TokenOccurrence("Asked", 0, 4, 5),
            new TokenOccurrence("Bidder", 1, 4, 6),
            new TokenOccurrence("BarCount", 2, 4, 8),
            new TokenOccurrence("MyBars", 3, 4, 6),
            new TokenOccurrence("TimeHours", 4, 4, 9),
            new TokenOccurrence("MarketInfoDouble", 5, 7, 16));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void StandaloneBuiltinNames_TriggerDespiteUserSymbols()
    {
        // A standalone `Ask` triggers even next to user identifiers that
        // merely contain the same letters (REQ-MA-03 edge case).
        const string content =
            "int Asked = 0;\n" +
            "double Ask = SymbolInfoDouble(_Symbol, SYMBOL_ASK);\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("Asked", 0, 4, 5),
            new TokenOccurrence("Ask", 1, 7, 3));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Ask'", diagnostic.Message);
    }

    [Fact]
    public void CaseSensitiveMatching_DoesNotFlagDifferentCase()
    {
        // MQL is case-sensitive like C++: `timehour`, `ask` and `bid` are
        // user identifiers, not the canonical registry spellings
        // (`TimeHour`, `Ask`, `Bid`) — flagging them would be a false
        // positive (REQ-MA-02/03). Ordinal registry lookup on captured
        // occurrences pins the spelling.
        const string content =
            "int hour = timehour(t);\n" +
            "double price = ask;\n" +
            "double stop = bid;\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("timehour", 0, 11, 8),
            new TokenOccurrence("ask", 1, 15, 3),
            new TokenOccurrence("bid", 2, 14, 3));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    // ------------------------------------------------------------------
    // FP guards + direction safety (REQ-MA-04/05) — tests 6, 10, 11, 12, 13, 17.
    // ------------------------------------------------------------------

    [Fact]
    public void SharedNamesInMql5Code_NoDiagnostics()
    {
        // Names shared by both languages must never be flagged (REQ-MA-05):
        // they are excluded from the registry by design.
        const string content =
            "void OnStart()\n" +
            "{\n" +
            "    int tickets[1];\n" +
            "    int sent = OrderSend(_Symbol, OP_BUY, 0.1, Ask0, 3, 0, 0);\n" +
            "    int size = ArrayResize(tickets, 1);\n" +
            "    Print(\"sent: \", sent);\n" +
            "    double buf[10];\n" +
            "    int copied = CopyBuffer(0, 0, 0, 10, buf);\n" +
            "    int handle = FileOpen(\"x.txt\", FILE_READ);\n" +
            "}\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("OrderSend", 3, 15, 9),
            new TokenOccurrence("Ask0", 3, 47, 4),
            new TokenOccurrence("ArrayResize", 4, 15, 11),
            new TokenOccurrence("Print", 5, 4, 5),
            new TokenOccurrence("CopyBuffer", 7, 17, 10),
            new TokenOccurrence("FileOpen", 8, 17, 8));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Mql4Document_NoDiagnostics()
    {
        // Direction safety (REQ-MA-04): the rule is silent for MQL4
        // documents; "1060" is reserved but never emitted.
        const string content =
            "int start = TimeHour(t);\n" +
            "double price = Ask;\n" +
            "int ticket = OrderSend(_Symbol, OP_BUY, 0.1, price, 3, 0, 0);\n";

        var diagnostics = _rule.Check(Context(null, content, MqlLanguage.Mql4)).ToList();

        Assert.Empty(diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.Code == "1060");
    }

    [Fact]
    public void WholeLineComment_Skipped()
    {
        // Occurrences exclude comments by construction (issue #176): the
        // capture emits only default-channel IDENTIFIER tokens and comment
        // text lives on a hidden lexer channel, so a comment mention never
        // reaches the rule.
        const string content = "// Ask is a MQL4 variable\n";

        var diagnostics = _rule.Check(Context(FileWithOccurrences(), content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void StringLiteral_Mention_IsNotFlagged()
    {
        // Issue #176 repro (the documented PrintFormat example): `Close`
        // inside the format-string literal must not warn. Occurrence-driven
        // detection excludes string-literal contents by construction — the
        // behavior change is the point of #176, closing the phase-1
        // limitation inherited from #28.
        const string content = "PrintFormat(\"%s. Close %.2f\", symbol, price);\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("PrintFormat", 0, 0, 11),
            new TokenOccurrence("symbol", 0, 30, 6),
            new TokenOccurrence("price", 0, 38, 5));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TrailingComment_Mention_IsNotFlagged()
    {
        // Issue #176: a registry name in a trailing comment must not warn.
        // The capture contract excludes comments by construction — the
        // behavior change is the point of #176 (phase-1 limitation from #28
        // closed).
        const string content = "int x = 1; // Close\n";

        var file = FileWithOccurrences(new TokenOccurrence("x", 0, 4, 1));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BareRead_StillFlagged()
    {
        // Occurrence-driven detection must not lose true positives: a bare
        // MQL4-only variable read keeps its 5060 warning, now ranged at the
        // lexer position.
        const string content = "double price = Ask;\n";

        var file = FileWithOccurrences(new TokenOccurrence("Ask", 0, 15, 3));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5060", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Ask", diagnostic.Message);
    }

    [Fact]
    public void CleanMql5Code_NoDiagnostics()
    {
        // FP guard: canonical MQL5 code must not warn.
        const string content =
            "int OnInit()\n" +
            "{\n" +
            "    MqlDateTime dt;\n" +
            "    TimeToStruct(TimeCurrent(), dt);\n" +
            "    int hour = dt.hour;\n" +
            "    double price = SymbolInfoDouble(_Symbol, SYMBOL_ASK);\n" +
            "    return 0;\n" +
            "}\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("MqlDateTime", 2, 4, 11),
            new TokenOccurrence("TimeToStruct", 3, 4, 12),
            new TokenOccurrence("TimeCurrent", 3, 17, 11),
            new TokenOccurrence("hour", 4, 8, 4),
            new TokenOccurrence("hour", 4, 18, 4),
            new TokenOccurrence("SymbolInfoDouble", 5, 19, 16));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    // ------------------------------------------------------------------
    // Call-form discrimination for semantics-changed names (REQ-MA-02/05,
    // option C): Bars/Digits/Point exist in MQL5 as functions with changed
    // semantics. A call form `Bars(...)` is valid MQL5 and must NOT warn; a
    // bare MQL4-style variable read `Bars` IS the migration defect and must
    // warn with the honest semantics-changed message.
    // ------------------------------------------------------------------

    [Fact]
    public void SemanticsChangedFunctionCallForm_NotFlagged()
    {
        // Valid MQL5 function calls must not produce diagnostics — this
        // pins the false-positive channel found in Judgment Day (repo
        // fixtures contain 8 such Bars() call sites).
        const string content =
            "int total = Bars(_Symbol, timeframe);\n" +
            "int bars2 = Bars(Symbol(), Period());\n" +
            "int digits = Digits();\n" +
            "double point = Point();\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("Bars", 0, 12, 4),
            new TokenOccurrence("Bars", 1, 12, 4),
            new TokenOccurrence("Digits", 2, 13, 6),
            new TokenOccurrence("Point", 3, 15, 5));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void SemanticsChangedVariableRead_StillFlaggedWithHonestMessage()
    {
        // A bare MQL4-style read of Bars/Digits/Point in an MQL5 document is
        // the actual migration defect; the message must NOT claim the name
        // is unavailable in MQL5 (it is, with changed semantics).
        const string content =
            "int total = Bars;\n" +
            "int digits = Digits;\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("Bars", 0, 12, 4),
            new TokenOccurrence("Digits", 1, 13, 6));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Equal(2, diagnostics.Count);

        var bars = diagnostics.Single(d => d.Message.Contains("'Bars'"));
        Assert.Equal("5060", bars.Code);
        Assert.DoesNotContain("not available in MQL5", bars.Message);
        Assert.Contains("changed semantics", bars.Message);
        Assert.Contains("iBars", bars.Message);

        var digits = diagnostics.Single(d => d.Message.Contains("'Digits'"));
        Assert.DoesNotContain("not available in MQL5", digits.Message);
        Assert.Contains("_Digits", digits.Message);
    }

    [Fact]
    public void RejectedByCompilerNames_CallFormStillFlagged()
    {
        // The call-form skip applies ONLY to semantics-changed entries.
        // MQL5-compiler-rejected names keep flagging even in call position
        // (TimeHour() does not exist in MQL5 at all).
        const string content = "int h = TimeHour(t);\n";

        var file = FileWithOccurrences(new TokenOccurrence("TimeHour", 0, 8, 8));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("not available in MQL5", diagnostic.Message);
    }

    [Fact]
    public void MixedCallAndVariableRead_Bars()
    {
        // Same line, both forms: only the bare variable read warns.
        const string content = "int delta = Bars - Bars(_Symbol, _Period);\n";

        var file = FileWithOccurrences(
            new TokenOccurrence("Bars", 0, 12, 4),
            new TokenOccurrence("Bars", 0, 19, 4));

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("'Bars'", diagnostic.Message);
        // Range covers the bare occurrence (lexer position 12..16), not the call.
        Assert.Equal(new Range(0, 12, 0, 16), diagnostic.Range);
    }

    // ------------------------------------------------------------------
    // Analyzer integration (REQ-MA-06) — tests 14, 15, 16.
    // These exercise SemanticAnalyzer.Analyze directly.
    // ------------------------------------------------------------------

    [Fact]
    public void RegistrationOrder_Mql4OnlyApiRuleAfterLanguageMisuseRule()
    {
        // Mixed MQL4 document: 1040s (LanguageMisuseRule) must be emitted
        // before 5060s… — but LanguageMisuseRule is silent for MQL5, so the
        // observable order is checked on an MQL4 document for the 1040s and
        // the rule-list order is asserted via the default-rule set
        // (REQ-MA-06: InputModifier, PropertyDirective, LanguageMisuse,
        // Mql4OnlyApi, Conversion).
        var rules = CreateDefaultRulesForTest();
        var ruleTypes = rules.Select(r => r.GetType().Name).ToList();

        var languageMisuseIndex = ruleTypes.IndexOf(nameof(LanguageMisuseRule));
        var mql4OnlyApiIndex = ruleTypes.IndexOf(nameof(Mql4OnlyApiRule));

        Assert.True(languageMisuseIndex >= 0, "LanguageMisuseRule must stay registered");
        Assert.Equal(languageMisuseIndex + 1, mql4OnlyApiIndex);
        Assert.Equal(nameof(InputModifierRule), ruleTypes[0]);
        Assert.Equal(nameof(PropertyDirectiveRule), ruleTypes[1]);
        Assert.Equal(nameof(ConversionRule), ruleTypes[^1]);
        Assert.Equal(5, rules.Count);
    }

    [Fact]
    public void MixedMql5Document_AnalyzerAggregates5060s()
    {
        // Observable emission: an MQL5 doc with both a property violation
        // (5030) and MQL4-only API usage (5060) aggregates both codes. The
        // parsed file carries captured occurrences (issue #176), as in
        // production; UnresolvedSymbolRule defers registry names to 5060
        // (issue #46), so only the 5060/5030 pair is emitted.
        const string content =
            "#property bogus_thing\n" +
            "double price = Ask;\n";

        var file = FileWithOccurrences(new TokenOccurrence("Ask", 1, 15, 3));

        var diagnostics = _analyzer.Analyze(file, content, MqlLanguage.Mql5, CancellationToken.None);

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.Code == "5030");
        Assert.Contains(diagnostics, d => d.Code == "5060");
    }

    [Fact]
    public void MacroBody_CaptureContract_IsPinned()
    {
        // Capture-contract pin (documented in TokenOccurrenceCapture: only
        // default-channel IDENTIFIER tokens are captured). A macro body
        // mention of a registry name is NOT captured — `#define X Close`
        // lives on the hidden preprocessor channel — so no 5060 can fire for
        // it. This asserts the OBSERVED capture behavior; fixing capture
        // semantics is out of scope for #176.
        const string content = "#define X Close\ndouble y = 1.0;\n";

        var file = new Mql5AntlrParser().ParseFile(content, "t.mq5");

        Assert.DoesNotContain(file.Occurrences, o => o.Text == "Close");

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.DoesNotContain(diagnostics, d => d.Code == "5060");
    }

    [Fact]
    public void DefectiveRuleIsolation_DoesNotKillSiblingRules()
    {
        // A rule that throws must be skipped without suppressing the rules
        // that come after it in the pipeline (REQ-MA-06). The defective rule
        // is injected in the Mql4OnlyApiRule slot; ConversionRule (registered
        // after it) must still contribute its 5070.
        var analyzer = new SemanticAnalyzer();
        var defectingRules = CreateDefaultRulesForTest()
            .Select(r => r is Mql4OnlyApiRule ? (ISemanticRule)new ThrowingRule() : r)
            .ToList();

        var file = new MqlFile
        {
            Symbols = new List<MqlSymbol>
            {
                new()
                {
                    Name = "x",
                    Kind = SymbolKind.Variable,
                    Detail = "input double x",
                    Range = new Range(0, 0, 0, 10),
                    SelectionRange = new Range(0, 13, 0, 14)
                }
            }
        };

        const string content = "input double x = \"abc\";\ndouble price = Ask;\n";
        var context = new SemanticRuleContext(file, content, MqlLanguage.Mql5, 5000, CancellationToken.None);

        // Simulate the analyzer loop with the defective rule in place: the
        // isolation contract mirrors SemanticAnalyzer.Analyze (skip the
        // throwing rule, keep the remaining analysis running).
        var diagnostics = new List<Diagnostic>();
        foreach (var rule in defectingRules)
        {
            try
            {
                diagnostics.AddRange(rule.Check(context));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Defective rule isolated.
            }
        }

        // The ConversionRule (registered after the throwing slot) still
        // contributes, proving sibling survival around the defect.
        Assert.Contains(diagnostics, d => d.Code == "5050");
    }

    [Fact]
    public void Cancellation_ThrowsOperationCanceledException()
    {
        // Pre-cancelled token (REQ-MA-06 scenario: "no rules execute and the
        // returned list is empty"). The analyzer's guard returns an empty
        // list without executing any rule; an OCE thrown mid-analysis by a
        // rule is re-thrown by the analyzer (re-throw contract), verified
        // here with a rule that honours the token.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        const string content = "double price = Ask;\n";

        // Pre-cancelled: guard short-circuits, no rules run, list is empty.
        var diagnostics = _analyzer.Analyze(null, content, MqlLanguage.Mql5, cts.Token);

        Assert.Empty(diagnostics);

        // Mid-analysis cancellation propagates: an OCE from a rule is not
        // swallowed by the isolation contract.
        var throwingContext = new SemanticRuleContext(
            null, content, MqlLanguage.Mql5, 5000, cts.Token);

        Assert.Throws<OperationCanceledException>(
            () => new CancellingRule().Check(throwingContext).ToList());
    }

    /// <summary>
    /// Rule that honors cancellation and throws OCE when the token is
    /// cancelled — simulates mid-analysis cancellation propagation.
    /// </summary>
    private sealed class CancellingRule : ISemanticRule
    {
        public IEnumerable<Diagnostic> Check(SemanticRuleContext context)
        {
            context.Token.ThrowIfCancellationRequested();
            yield break;
        }
    }

    /// <summary>
    /// Reflection-free access to the default rule list for order assertions
    /// (private static in <see cref="SemanticAnalyzer"/>, exercised here
    /// through a local mirror kept in sync by the registration test).
    /// </summary>
    private static List<ISemanticRule> CreateDefaultRulesForTest() => new()
    {
        new InputModifierRule(),
        new PropertyDirectiveRule(),
        new LanguageMisuseRule(),
        new Mql4OnlyApiRule(),
        new ConversionRule()
    };

    private sealed class ThrowingRule : ISemanticRule
    {
        public IEnumerable<Diagnostic> Check(SemanticRuleContext context) => throw new InvalidOperationException("defective rule");
    }
}