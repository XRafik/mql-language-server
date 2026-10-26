using System.IO;
using System.Linq;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// TDD-driven integration tests: each .mq5 fixture under tests/fixtures/Mql5
/// must parse through Mql5AntlrParser without unexpected (syntax) errors.
/// RED before fixtures/grammar are in place; GREEN once they parse clean.
/// S-005: extended to assert symbol extraction per fixture (not just Language == Mql5).
/// </summary>
public class Mql5FixtureTests
{
    private static readonly string FixturesDirectory = Path.Combine(
        Path.GetDirectoryName(typeof(Mql5FixtureTests).Assembly.Location)!,
        "..", "..", "..", "..", "tests", "fixtures", "Mql5");

    private static string GetFixturePath(string fileName)
    {
        return Path.GetFullPath(Path.Combine(FixturesDirectory, fileName));
    }

    private static MqlFile ParseFixture(string fileName)
    {
        var path = GetFixturePath(fileName);
        Assert.True(File.Exists(path), $"Fixture not found: {path}");

        var parser = new Mql5AntlrParser();
        var file = parser.ParseFileFromPath(path);

        Assert.NotNull(file);
        Assert.Equal(MqlLanguage.Mql5, file.Language);
        // A clean fixture must not produce any ANTLR syntax errors.
        Assert.Empty(file.SyntaxErrors);
        return file;
    }

    /// <summary>
    /// Asserts that a symbol with the given name and type exists in the parsed file.
    /// </summary>
    private static void AssertHasSymbol(MqlFile file, string name, SymbolType? expectedType = null)
    {
        var symbol = file.Symbols.FirstOrDefault(s => s.Name == name);
        Assert.NotNull(symbol);
        if (expectedType.HasValue)
        {
            Assert.True(symbol!.SymbolType == expectedType.Value,
                $"Symbol '{name}' expected SymbolType={expectedType.Value}, got {symbol.SymbolType}");
        }
    }

    [Theory]
    [InlineData("ClassInheritance.mq5")]
    [InlineData("StructAndInterface.mq5")]
    [InlineData("TemplateAndReferences.mq5")]
    [InlineData("ResourceAndPragma.mq5")]
    [InlineData("NullptrUnionEnumClass.mq5")]
    [InlineData("NewDelete.mq5")]
    [InlineData("AbstractFinalModifiers.mq5")]
    [InlineData("PureVirtualMembers.mq5")]
    [InlineData("DeleteMethodSpecifier.mq5")]
    public void Fixture_Parses_WithoutErrors(string fileName)
    {
        ParseFixture(fileName);
    }

    [Fact]
    public void ClassInheritance_Extracts_Base_Derived_Compute_OnTick()
    {
        var file = ParseFixture("ClassInheritance.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from ClassInheritance");
        AssertHasSymbol(file, "Base", SymbolType.Class);
        AssertHasSymbol(file, "Derived", SymbolType.Class);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    [Fact]
    public void StructAndInterface_Extracts_Settings_IStrategy_OnTick()
    {
        var file = ParseFixture("StructAndInterface.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from StructAndInterface");
        AssertHasSymbol(file, "Settings", SymbolType.Struct);
        AssertHasSymbol(file, "IStrategy", SymbolType.Interface);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    [Fact]
    public void TemplateAndReferences_Extracts_Container_Apply_OnTick()
    {
        var file = ParseFixture("TemplateAndReferences.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from TemplateAndReferences");
        AssertHasSymbol(file, "Container");
        AssertHasSymbol(file, "Apply", SymbolType.Function);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    [Fact]
    public void ResourceAndPragma_Extracts_PackedData_OnTick()
    {
        var file = ParseFixture("ResourceAndPragma.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from ResourceAndPragma");
        AssertHasSymbol(file, "PackedData", SymbolType.Class);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    [Fact]
    public void NullptrUnionEnumClass_Extracts_OrderSide_BufferValue_OnTick()
    {
        var file = ParseFixture("NullptrUnionEnumClass.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from NullptrUnionEnumClass");
        AssertHasSymbol(file, "OrderSide", SymbolType.Enum);
        // Union is mapped to SymbolType.Struct by the visitor (no dedicated Union type)
        AssertHasSymbol(file, "BufferValue", SymbolType.Struct);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    [Fact]
    public void NewDelete_Extracts_PriceSeries_OnTick()
    {
        var file = ParseFixture("NewDelete.mq5");

        Assert.True(file.Symbols.Count > 0, "Should extract symbols from NewDelete");
        AssertHasSymbol(file, "PriceSeries", SymbolType.Class);
        AssertHasSymbol(file, "OnTick", SymbolType.Function);
    }

    /// <summary>
    /// Issue #141: `abstract`/`final` class modifiers must parse without
    /// spurious syntax errors, as one declaration — and the base clause must
    /// still be captured into MqlSymbol.BaseClass (issue #123) regardless of
    /// the modifiers.
    /// </summary>
    [Fact]
    public void AbstractFinalModifiers_Extracts_Types_And_BaseClass()
    {
        var file = ParseFixture("AbstractFinalModifiers.mq5");

        AssertHasSymbol(file, "CAnimal", SymbolType.Class);
        AssertHasSymbol(file, "CFoo", SymbolType.Class);
        AssertHasSymbol(file, "CBar", SymbolType.Class);
        AssertHasSymbol(file, "Settings", SymbolType.Struct);
        AssertHasSymbol(file, "CCat", SymbolType.Class);

        var cBar = file.Symbols.First(s => s.Name == "CBar");
        Assert.Equal("CFoo", cBar.BaseClass);

        var cCat = file.Symbols.First(s => s.Name == "CCat");
        Assert.Equal("CAnimal", cCat.BaseClass);

        var cShape = file.Symbols.First(s => s.Name == "CShape");
        Assert.Equal("CObject", cShape.BaseClass);
    }

    /// <summary>
    /// Issue #143: pure virtual specifiers (`= 0` and the documented `= NULL`
    /// spelling) must parse without spurious syntax errors, and normal
    /// declared/defined virtual members must keep parsing (regression guard
    /// for the relaxed member rule). Also pins `override` as a member
    /// modifier (documented MQL5 access specifier).
    /// </summary>
    [Fact]
    public void PureVirtualMembers_Extracts_Types_Without_Errors()
    {
        var file = ParseFixture("PureVirtualMembers.mq5");

        AssertHasSymbol(file, "CAnimal", SymbolType.Class);
        AssertHasSymbol(file, "CBird", SymbolType.Class);
        AssertHasSymbol(file, "CCat", SymbolType.Class);
        AssertHasSymbol(file, "CDog", SymbolType.Class);
        AssertHasSymbol(file, "OnStart", SymbolType.Function);
    }

    /// <summary>
    /// Issue #145: the documented `= delete` method specifier
    /// (MQL5Book, classes_final_delete) must parse without spurious syntax
    /// errors, and the declared/defined/pure-virtual regression guards in
    /// the same fixture must keep parsing (the #143 pureSpecifier rule is
    /// deliberately separate — compile error 381 closes it to "=0"/"=NULL").
    /// </summary>
    [Fact]
    public void DeleteMethodSpecifier_Extracts_Types_Without_Errors()
    {
        var file = ParseFixture("DeleteMethodSpecifier.mq5");

        AssertHasSymbol(file, "Base", SymbolType.Class);
        AssertHasSymbol(file, "Derived", SymbolType.Class);
        AssertHasSymbol(file, "CAnimal", SymbolType.Class);
        AssertHasSymbol(file, "CCat", SymbolType.Class);
        AssertHasSymbol(file, "OnStart", SymbolType.Function);
    }
}
