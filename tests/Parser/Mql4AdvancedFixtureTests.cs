using System.IO;
using System.Linq;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// TDD-driven integration tests for advanced MQL4 grammar constructs.
/// Each .mq4 fixture under tests/fixtures/mq4/advanced must parse through
/// Mql4AntlrParser without throwing. Per-fixture Fact tests assert specific
/// symbols are extracted (functions and variables only — the Mql4SymbolVisitor
/// does NOT extract classes, structs, or enums).
/// </summary>
public class Mql4AdvancedFixtureTests
{
    private static readonly string FixturesDirectory = Path.Combine(
        Path.GetDirectoryName(typeof(Mql4AdvancedFixtureTests).Assembly.Location)!,
        "..", "..", "..", "..", "tests", "fixtures", "mq4", "advanced");

    private static string GetFixturePath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(FixturesDirectory, fileName));
    }

    private static Mql4File ParseFixture(string fileName)
    {
        var path = GetFixturePath(fileName);
        Assert.True(File.Exists(path), $"Fixture not found: {path}");

        var parser = new Mql4AntlrParser();
        // ParseFileFromPath calls ParseFile internally; ParseFile catches exceptions
        // and returns a non-null Mql4File (possibly with empty symbols on parse error).
        var file = parser.ParseFileFromPath(path);

        Assert.NotNull(file);
        // A clean fixture must not produce any ANTLR syntax errors.
        Assert.Empty(file.SyntaxErrors);
        return file;
    }

    /// <summary>
    /// Asserts that a symbol with the given name exists in the parsed file.
    /// Optionally checks its LSP SymbolKind.
    /// </summary>
    private static void AssertHasSymbol(Mql4File file, string name, SymbolKind? expectedKind = null)
    {
        var symbol = file.Symbols.FirstOrDefault(s => s.Name == name);
        Assert.NotNull(symbol);
        if (expectedKind.HasValue)
        {
            Assert.True(symbol!.Kind == expectedKind.Value,
                $"Symbol '{name}' expected Kind={expectedKind.Value}, got {symbol.Kind}");
        }
    }

    [Theory]
    [InlineData("advanced_templates.mq4")]
    [InlineData("advanced_class_inheritance.mq4")]
    [InlineData("advanced_constructor_destructor.mq4")]
    [InlineData("advanced_global_ctor_dtor.mq4")]
    [InlineData("advanced_enum_values.mq4")]
    [InlineData("advanced_operator_overload.mq4")]
    [InlineData("advanced_import.mq4")]
    [InlineData("advanced_preprocessor.mq4")]
    [InlineData("advanced_small_types.mq4")]
    [InlineData("advanced_multidim_arrays.mq4")]
    [InlineData("advanced_references.mq4")]
    [InlineData("advanced_new_delete_sizeof.mq4")]
    [InlineData("advanced_special_literals.mq4")]
    [InlineData("advanced_modifiers.mq4")]
    [InlineData("advanced_control_flow.mq4")]
    [InlineData("advanced_array_initializer.mq4")]
    [InlineData("advanced_pure_virtual.mq4")]
    public void Fixture_Parses_WithoutErrors(string fileName)
    {
        ParseFixture(fileName);
    }

    [Fact]
    public void Templates_Extracts_MaxValue_OnTick()
    {
        var file = ParseFixture("advanced_templates.mq4");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from advanced_templates");
        AssertHasSymbol(file, "MaxValue", SymbolKind.Function);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void ClassInheritance_Extracts_OnTick()
    {
        var file = ParseFixture("advanced_class_inheritance.mq4");

        // The visitor does NOT extract class symbols, so we only assert functions.
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    /// <summary>
    /// Issue #143: pure virtual specifiers (`= 0` and the documented `= NULL`
    /// spelling) must parse without spurious syntax errors in MQL4 too
    /// (docs.mql4.com compile error 381 confirms the construct exists there).
    /// The visitor does not extract class symbols, so only OnStart is asserted.
    /// </summary>
    [Fact]
    public void PureVirtual_Extracts_OnStart()
    {
        var file = ParseFixture("advanced_pure_virtual.mq4");

        AssertHasSymbol(file, "OnStart", SymbolKind.Function);
    }

    [Fact]
    public void ConstructorDestructor_Extracts_OnTick()
    {
        var file = ParseFixture("advanced_constructor_destructor.mq4");

        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void GlobalCtorDtor_ParsesWithoutErrors()
    {
        var file = ParseFixture("advanced_global_ctor_dtor.mq4");

        // globalConstructorDeclaration and globalDestructorDeclaration are parsed
        // by the grammar, but the Mql4SymbolVisitor has NO visitor overrides for them
        // (only VisitFunctionDeclaration and VisitVariableDeclaration extract symbols).
        // So the file parses cleanly but may have zero symbols extracted.
        // We assert the file is non-null (parsing did not throw).
        Assert.NotNull(file);
    }

    [Fact]
    public void EnumValues_Extracts_OnTick()
    {
        var file = ParseFixture("advanced_enum_values.mq4");

        // The visitor does NOT extract enum symbols, so we only assert functions.
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void OperatorOverload_Extracts_AddOperator_OnTick()
    {
        var file = ParseFixture("advanced_operator_overload.mq4");

        // MQL4 grammar does NOT support operator overloading (no rule consumes K_OPERATOR).
        // The fixture uses a plain function AddOperator instead.
        AssertHasSymbol(file, "AddOperator", SymbolKind.Function);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void Import_Extracts_MessageBoxA_OnTick()
    {
        var file = ParseFixture("advanced_import.mq4");

        // #import is recognized by VisitDirective but NOT added to Includes.
        // Only #include populates the Includes list.
        AssertHasSymbol(file, "MessageBoxA", SymbolKind.Function);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
        // Includes should be empty (only #import is present, not #include)
        Assert.Empty(file.Includes);
    }

    [Fact]
    public void Preprocessor_Extracts_Macros_AndSymbols()
    {
        var file = ParseFixture("advanced_preprocessor.mq4");

        // Macros are extracted via token scanning (PRE_DEFINE on Channel 1).
        Assert.NotEmpty(file.Macros);
        Assert.Contains(file.Macros, m => m == "DEBUG_VERSION");
        Assert.Contains(file.Macros, m => m == "MAX_BARS");

        // Variables and functions should also be extracted
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void SmallTypes_Extracts_Variables_AndFunction()
    {
        var file = ParseFixture("advanced_small_types.mq4");

        AssertHasSymbol(file, "c", SymbolKind.Variable);
        AssertHasSymbol(file, "uc", SymbolKind.Variable);
        AssertHasSymbol(file, "s", SymbolKind.Variable);
        AssertHasSymbol(file, "us", SymbolKind.Variable);
        AssertHasSymbol(file, "ui", SymbolKind.Variable);
        AssertHasSymbol(file, "l", SymbolKind.Variable);
        AssertHasSymbol(file, "ul", SymbolKind.Variable);
        AssertHasSymbol(file, "f", SymbolKind.Variable);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void MultidimArrays_Extracts_Variables_AndFunctions()
    {
        var file = ParseFixture("advanced_multidim_arrays.mq4");

        AssertHasSymbol(file, "matrix", SymbolKind.Variable);
        AssertHasSymbol(file, "cube", SymbolKind.Variable);
        AssertHasSymbol(file, "simple", SymbolKind.Variable);
        AssertHasSymbol(file, "FillMatrix", SymbolKind.Function);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void References_Extracts_Functions()
    {
        var file = ParseFixture("advanced_references.mq4");

        AssertHasSymbol(file, "IncrementBy", SymbolKind.Function);
        AssertHasSymbol(file, "Swap", SymbolKind.Function);
        AssertHasSymbol(file, "GetOut", SymbolKind.Function);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void NewDeleteSizeof_Extracts_OnTick()
    {
        var file = ParseFixture("advanced_new_delete_sizeof.mq4");

        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void SpecialLiterals_Extracts_Variables_AndFunction()
    {
        var file = ParseFixture("advanced_special_literals.mq4");

        AssertHasSymbol(file, "startDate", SymbolKind.Variable);
        AssertHasSymbol(file, "red", SymbolKind.Variable);
        AssertHasSymbol(file, "green", SymbolKind.Variable);
        AssertHasSymbol(file, "hexValue", SymbolKind.Variable);
        AssertHasSymbol(file, "letter", SymbolKind.Variable);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    // REQ-CP-01: C-literals captured at their exact ranges by the full parser
    // pipeline (ColorOccurrenceCapture wired into Mql4AntlrParser).

    [Fact]
    public void SpecialLiterals_Captures_Three_CLiterals_At_Exact_Ranges()
    {
        var file = ParseFixture("advanced_special_literals.mq4");

        var cLiterals = file.ColorOccurrences.Where(o => o.Kind == ColorKind.CLiteral).ToList();
        Assert.Equal(3, cLiterals.Count);

        // Fixture: `color red = C'255,0,0';` — 1-based line 5, 0-based col 12.
        var red = cLiterals.Single(o => o.R == 255);
        Assert.Equal(4, red.Line);
        Assert.Equal(12, red.Column);
        Assert.Equal(10, red.Length); // C'255,0,0' is 10 chars (green/blue are 9)
        Assert.Equal((byte)0xFF, red.Alpha);

        // `color green = C'0,255,0';` — 1-based line 6, 0-based col 14.
        var green = cLiterals.Single(o => o.G == 255 && o.R == 0 && o.B == 0);
        Assert.Equal(5, green.Line);
        Assert.Equal(14, green.Column);

        // `color blue = C'0,0,255';` — 1-based line 14 (inside OnTick).
        var blue = cLiterals.Single(o => o.B == 255 && o.R == 0 && o.G == 0);
        Assert.Equal(13, blue.Line);
        Assert.Equal(17, blue.Column);
    }

    // D5 / REQ-CP-08: the grammar also lexes bitmasks (0xFF, 0xABCD, 0xFFFF,
    // all present in this fixture) — they must NOT be reported as colors.

    [Fact]
    public void SpecialLiterals_Bitmasks_Produce_No_Colors()
    {
        var file = ParseFixture("advanced_special_literals.mq4");

        Assert.DoesNotContain(file.ColorOccurrences, o => o.Length == 4 && o.Kind == ColorKind.Hex); // 0xFF / 0xFFFF
        Assert.DoesNotContain(file.ColorOccurrences, o => o.Length == 6 && o.Kind == ColorKind.Hex); // 0xABCD
        // The only hex color in the fixture would be none — all hex tokens are
        // masks. Nothing hex at all:
        Assert.DoesNotContain(file.ColorOccurrences, o => o.Kind == ColorKind.Hex);
    }

    [Fact]
    public void Modifiers_Extracts_InputAndStaticVariables()
    {
        var file = ParseFixture("advanced_modifiers.mq4");

        AssertHasSymbol(file, "InpPeriod", SymbolKind.Variable);
        AssertHasSymbol(file, "InpLotSize", SymbolKind.Variable);
        AssertHasSymbol(file, "InpHidden", SymbolKind.Variable);
        AssertHasSymbol(file, "ExtFlag", SymbolKind.Variable);
        AssertHasSymbol(file, "StaticCounter", SymbolKind.Variable);
        AssertHasSymbol(file, "Pi", SymbolKind.Variable);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void ControlFlow_Extracts_OnTick()
    {
        var file = ParseFixture("advanced_control_flow.mq4");

        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }

    [Fact]
    public void ArrayInitializer_Extracts_Variables_AndFunction()
    {
        var file = ParseFixture("advanced_array_initializer.mq4");

        AssertHasSymbol(file, "values", SymbolKind.Variable);
        AssertHasSymbol(file, "sparse", SymbolKind.Variable);
        AssertHasSymbol(file, "nested", SymbolKind.Variable);
        AssertHasSymbol(file, "OnTick", SymbolKind.Function);
    }
}