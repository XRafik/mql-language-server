using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Analysis.Rules;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Unit tests for <see cref="UnresolvedSymbolRule"/> (issue #32, base+70 range,
/// REQ-IA-01/REQ-IA-02). Pure document-local analysis: no index, no I/O.
/// </summary>
public class UnresolvedSymbolRuleTests
{
    private static SemanticRuleContext Context(
        MqlFile? file,
        string content,
        MqlLanguage language = MqlLanguage.Mql5,
        IMqlBuiltins[]? builtins = null) =>
        new(file, content, language, language == MqlLanguage.Mql5 ? 5000 : 1000, CancellationToken.None, builtins);

    private readonly UnresolvedSymbolRule _rule = new();

    [Fact]
    public void UndeclaredSymbol_Mql4Document_EmitsCode1070WithSymbolData()
    {
        // REQ-IA-01: undeclared workspace symbol reported with structured Data.
        const string content = "void OnInit()\n{\n    MyHelper();\n}\n";
        var file = new MqlFile { Occurrences = new List<TokenOccurrence> { new("MyHelper", 2, 4, 8) } };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql4)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("1070", diagnostic.Code);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("mql-lsp", diagnostic.Source);
        Assert.Contains("MyHelper", diagnostic.Message);
        Assert.NotNull(diagnostic.Data);
        Assert.Equal("MyHelper", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void UndeclaredSymbol_Mql5Document_EmitsCode5070WithSymbolData()
    {
        const string content = "void OnInit()\n{\n    MyHelper();\n}\n";
        var file = new MqlFile { Occurrences = new List<TokenOccurrence> { new("MyHelper", 2, 4, 8) } };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql5)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5070", diagnostic.Code);
        Assert.Equal("MyHelper", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void OneDiagnostic_PerOccurrence_WithRangeOnOccurrence()
    {
        // REQ-IA-01: one diagnostic per unresolved occurrence, Range on the identifier.
        const string content = "MyHelper();\nMyHelper();\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("MyHelper", 0, 0, 8),
                new("MyHelper", 1, 0, 8)
            }
        };

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal("5070", d.Code));
        Assert.All(diagnostics, d => Assert.Equal("MyHelper", ReadSymbol(d.Data)));
        var first = diagnostics[0];
        Assert.Equal(0, first.Range.Start.Line);
        Assert.Equal(0, first.Range.Start.Character);
        Assert.Equal(0, first.Range.End.Line);
        Assert.Equal(8, first.Range.End.Character);
    }

    [Fact]
    public void DeclaredSymbols_SymbolsAndMacrosFlattenedWithChildren_AreNotFlagged()
    {
        // REQ-IA-01: declared set = File.Symbols flattened (incl. Children) + File.Macros.
        // Also covers the 5.2 subtype/Children verification: nested symbols via
        // base-type traversal are recognized as declared.
        const string content = "MyHelper();\nMY_MACRO;\nMyEnum value = MyEnumValue;\n";
        var helper = new MqlSymbol
        {
            Name = "MyHelper",
            Kind = SymbolKind.Function,
            Range = new Range(0, 0, 0, 10),
            SelectionRange = new Range(0, 0, 0, 8)
        };
        var function = new MqlSymbol
        {
            Name = "DoWork",
            Kind = SymbolKind.Function,
            Range = new Range(0, 0, 0, 10),
            SelectionRange = new Range(0, 0, 0, 6),
            Children = new List<MqlSymbol> { helper }
        };
        var file = new MqlFile
        {
            Symbols = new List<MqlSymbol> { function },
            Macros = new List<string> { "MY_MACRO", "MyEnumValue" },
            Occurrences = new List<TokenOccurrence>
            {
                new("MyHelper", 0, 0, 8),
                new("MY_MACRO", 1, 0, 8),
                new("MyEnumValue", 1, 9, 11)
            }
        };

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void BuiltinIdentifiers_AreNotFlagged()
    {
        // REQ-IA-01: builtins (Period, Bars) not flagged when the language
        // registry recognizes them.
        const string content = "int p = Period;\nint b = Bars;\n";
        var builtins = new TestBuiltins();
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("Period", 0, 8, 6),
                new("Bars", 1, 8, 4)
            }
        };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql5, new IMqlBuiltins[] { builtins })).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MemberAccessReceiver_IsNotFlagged()
    {
        // REQ-IA-01: the receiver part of a member access (obj.Method) is not
        // an unresolved free identifier.
        const string content = "obj.Method();\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("obj", 0, 0, 3),
                new("Method", 0, 4, 6)
            }
        };

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        // "obj" is undeclared too; the phase-1 rule flags only free
        // identifiers not followed by '.' — obj.Method() has neither flagged
        // (receiver precedes '.', Method follows '.').
        Assert.Empty(diagnostics);
    }

    [Fact]
    public void MultiLineDocument_WithEarlyDotBearingTokens_FreeIdentifier_StillFlagged()
    {
        // Issue #52: IsMemberAccessPosition rebuilt the absolute offset by
        // accumulating Split('\n') part lengths without +1 per newline, so the
        // sampled position drifted left by exactly occurrence.Line characters.
        // With these three dot-bearing lines the drifted sample lands right
        // after the '.' of "Point * 0.1", silently classifying the free
        // identifier 'x' as a member-access receiver — a false negative.
        const string content =
            "double tickSize = 0.00001;\n" +
            "double profit = Bid - 0.007;\n" +
            "double point = Point * 0.1;\n" +
            "x();\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("x", 3, 0, 1)
            }
        };

        var diagnostics = _rule.Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5070", diagnostic.Code);
        Assert.NotNull(diagnostic.Data);
        Assert.Equal("x", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void NullFile_NoDiagnostics()
    {
        var diagnostics = _rule.Check(Context(null, "MyHelper();\n"));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void NoBuiltinsProvided_StillRunsDocumentLocalAnalysis()
    {
        // REQ-IA-02: the rule is a pure function of the document. Missing
        // builtins (null) degrades to no builtin filter, never an exception.
        const string content = "MyHelper();\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("MyHelper", 0, 0, 8) }
        };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql5, null)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5070", diagnostic.Code);
    }

    [Fact]
    public void AnalyzerWithBuiltins_EmitsUnresolvedDiagnostic_ThroughDefaultRules()
    {
        // Wiring check: SemanticAnalyzer.CreateDefaultRules registers the rule
        // and passes builtins through the extended SemanticRuleContext.
        const string content = "void OnInit()\n{\n    WorkspaceHelper();\n}\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("WorkspaceHelper", 2, 4, 15) }
        };
        var analyzer = new SemanticAnalyzer(new IMqlBuiltins[] { new Mql5Builtins() });

        var diagnostics = analyzer.Analyze(file, content, MqlLanguage.Mql5, CancellationToken.None);

        var unresolved = diagnostics.Where(d => d.Code == "5070").ToList();
        var diagnostic = Assert.Single(unresolved);
        Assert.Equal("WorkspaceHelper", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void Analyzer_FlagsUndeclaredSymbol_Mql4Document()
    {
        const string content = "void OnInit()\n{\n    WorkspaceHelper();\n}\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("WorkspaceHelper", 2, 4, 15) }
        };
        var analyzer = new SemanticAnalyzer(new IMqlBuiltins[] { new Mql4BuiltinsAdapter() });

        var diagnostics = analyzer.Analyze(file, content, MqlLanguage.Mql4, CancellationToken.None);

        Assert.Contains(diagnostics, d => d.Code == "1070");
    }

    // --- Issue #46: dialect-tagged builtin registries + Mql4OnlyApiRegistry guard ---

    [Fact]
    public void Mql5Document_WithStdlibEnumConstants_DoesNotFlagThem()
    {
        // Issue #46 acceptance (a): MQL5 stdlib enum constants resolve against
        // the dialect registry, so no unresolved-symbol diagnostics fire.
        const string content = "int t = PERIOD_H1;\ndouble p = PRICE_CLOSE;\nint m = MODE_SMA;\nObjectSetInteger(0, \"o\", OBJPROP_TIME, 0);\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("PERIOD_H1", 0, 8, 10),
                new("PRICE_CLOSE", 1, 12, 11),
                new("MODE_SMA", 2, 9, 8),
                new("OBJPROP_TIME", 3, 25, 11)
            }
        };

        var diagnostics = _rule.Check(Context(
            file,
            content,
            MqlLanguage.Mql5,
            new IMqlBuiltins[] { new Mql5Builtins(), new Mql4BuiltinsAdapter() })).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Mql5Document_WithStructTypesAndPredefinedMacros_DoesNotFlagThem()
    {
        // Issue #176 acceptance: documented builtin struct types and predefined
        // macro substitutions resolve against the MQL5 registry — no false 5070
        // on the issue repro snippet.
        const string content = "MqlTradeRequest request;\nMqlDateTime now;\nPrint(__FUNCTION__);\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("MqlTradeRequest", 0, 0, 14),
                new("MqlDateTime", 1, 0, 11),
                new("__FUNCTION__", 2, 6, 12)
            }
        };

        var diagnostics = _rule.Check(Context(
            file,
            content,
            MqlLanguage.Mql5,
            new IMqlBuiltins[] { new Mql5Builtins() })).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Mql4OnlyConstant_InMql5Document_StillFlagged_ByDialectFilter()
    {
        // Issue #46 acceptance (b): OP_BUY is MQL4-only and NOT present in
        // Mql4OnlyApiRegistry, so the dialect filter must keep flagging it in
        // an MQL5 document even when the MQL4 registry is also provided.
        const string content = "int cmd = OP_BUY;\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("OP_BUY", 0, 10, 6) }
        };
        var builtins = new IMqlBuiltins[] { new Mql5Builtins(), new Mql4BuiltinsAdapter() };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql5, builtins)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5070", diagnostic.Code);
        Assert.Equal("OP_BUY", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void Mql4OnlyConstant_InMql5Document_WithMql4RegistryOnly_StillFlagged()
    {
        // Dialect honesty: providing only the MQL4 registry must not silence
        // MQL4-only names in an MQL5 document (the pre-#46 union behavior did).
        const string content = "int cmd = OP_BUY;\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("OP_BUY", 0, 10, 6) }
        };

        var diagnostics = _rule.Check(Context(
            file,
            content,
            MqlLanguage.Mql5,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("OP_BUY", ReadSymbol(diagnostic.Data));
    }

    [Fact]
    public void Mql4Document_WithMql4OnlyConstant_DoesNotFlagIt()
    {
        // Issue #46 acceptance (c): OP_BUY resolves in an MQL4 document.
        const string content = "int cmd = OP_BUY;\nint main = MODE_MAIN;\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("OP_BUY", 0, 10, 6),
                new("MODE_MAIN", 1, 11, 9)
            }
        };

        var diagnostics = _rule.Check(Context(
            file,
            content,
            MqlLanguage.Mql4,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Mql4OnlyApiRegistryNames_AreNeverDuplicatedAsUnresolved()
    {
        // Issue #46 decision 3: names curated in Mql4OnlyApiRegistry (Ask,
        // TimeHour) are owned by Mql4OnlyApiRule (code 5060) in MQL5
        // documents; the unresolved-symbol rule must stay silent on them.
        const string content = "double price = Ask;\nint hour = TimeHour(TimeCurrent());\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("Ask", 0, 14, 3),
                new("TimeHour", 1, 10, 8)
            }
        };

        var diagnostics = _rule.Check(Context(
            file,
            content,
            MqlLanguage.Mql5,
            new IMqlBuiltins[] { new Mql5Builtins() })).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Mql5Document_WithNullBuiltins_UsesDefaultMql5Registry()
    {
        // Issue #46: with no registries provided, the default dialect registry
        // still resolves stdlib constants while undeclared names stay flagged.
        const string content = "int t = PERIOD_H1;\nMyHelper();\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("PERIOD_H1", 0, 8, 10),
                new("MyHelper", 1, 0, 8)
            }
        };

        var diagnostics = _rule.Check(Context(file, content, MqlLanguage.Mql5, null)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("MyHelper", ReadSymbol(diagnostic.Data));
    }

    private static string? ReadSymbol(object? data)
    {
        if (data is Newtonsoft.Json.Linq.JToken token)
        {
            return token.Value<string>("symbol");
        }

        var json = System.Text.Json.JsonSerializer.SerializeToElement(data);
        return json.TryGetProperty("symbol", out var symbol) ? symbol.GetString() : null;
    }

    private sealed class TestBuiltins : IMqlBuiltins
    {
        public Dictionary<string, string> BuiltInFunctions { get; } = new();
        public Dictionary<string, string> BuiltInVariables { get; } = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Period", "Chart timeframe" },
            { "Bars", "Bars count" }
        };

        public bool IsBuiltinFunction(string name) => false;
        public bool IsBuiltinVariable(string name) => BuiltInVariables.ContainsKey(name);
        public bool IsBuiltin(string name) => IsBuiltinFunction(name) || IsBuiltinVariable(name);
        public string? GetBuiltinFunctionSignature(string name) => null;
        public string? GetBuiltinVariableDescription(string name) =>
            BuiltInVariables.TryGetValue(name, out var description) ? description : null;
    }
}