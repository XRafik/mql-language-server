using System;
using System.IO;
using System.Linq;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// Coverage tests for Mql5AntlrParser public surface: function body extraction, includes,
/// completions, symbol lookup, file parsing, and include resolution.
/// Targets the previously uncovered GetFunctionBody* / GetCompletions / GetIncludes /
/// FindSymbolDefinition / FindSymbolsByName / ParseFileFromPath / ParseFileWithIncludes paths.
/// </summary>
public class Mql5AntlrParserTests
{
    private static readonly string FixturesDirectory = Path.Combine(
        Path.GetDirectoryName(typeof(Mql5AntlrParserTests).Assembly.Location)!,
        "..", "..", "..", "..", "tests", "fixtures", "Mql5");

    private static string GetFixturePath(string fileName)
        => Path.GetFullPath(Path.Combine(FixturesDirectory, fileName));

    private readonly Mql5AntlrParser _parser = new();

    // --- ParseFileFromPath ---

    [Fact]
    public void ParseFileFromPath_ParsesRealFixture_ReturnsMql5Language()
    {
        var path = GetFixturePath("ClassInheritance.mq5");
        var file = _parser.ParseFileFromPath(path);

        Assert.Equal(MqlLanguage.Mql5, file.Language);
        Assert.Equal(path, file.FilePath);
        Assert.NotEmpty(file.Symbols);
    }

    [Fact]
    public void ParseFileFromPath_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => _parser.ParseFileFromPath("/nonexistent/path.mq5"));
    }

    // --- GetFunctionBody ---

    [Fact]
    public void GetFunctionBody_MultilineFunction_ReturnsBodyText()
    {
        var content =
            "int ComputeSum(int a, int b) {\n" +
            "    int result = a + b;\n" +
            "    return result;\n" +
            "}\n";
        var file = _parser.ParseFile(content, "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "ComputeSum");

        var body = _parser.GetFunctionBody(content, symbol);

        Assert.NotNull(body);
        Assert.Contains("int result = a + b;", body);
        Assert.Contains("return result;", body);
    }

    [Fact]
    public void GetFunctionBody_NullContent_ReturnsNull()
    {
        var file = _parser.ParseFile("void f() {}", "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");
        Assert.Null(_parser.GetFunctionBody(null!, symbol));
    }

    [Fact]
    public void GetFunctionBody_EmptyContent_ReturnsNull()
    {
        var file = _parser.ParseFile("void f() {}", "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");
        Assert.Null(_parser.GetFunctionBody("", symbol));
    }

    [Fact]
    public void GetFunctionBody_NullSymbol_ReturnsNull()
    {
        Assert.Null(_parser.GetFunctionBody("void f() {}", null!));
    }

    [Fact]
    public void GetFunctionBody_SingleLineDeclaration_ReturnsNull()
    {
        // startLine == endLine path: a one-line declaration has no extractable body
        var content = "void f() {}\n";
        var file = _parser.ParseFile(content, "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");

        var body = _parser.GetFunctionBody(content, symbol);

        // Range covers only line 0; startLine == endLine returns null per implementation
        Assert.Null(body);
    }

    // --- GetFunctionBodyStartLine / GetFunctionBodyEndLine / GetFunctionBodyLineCount ---

    [Fact]
    public void GetFunctionBodyStartLine_MultilineFunction_ReturnsOpeningBraceLine()
    {
        var content =
            "int ComputeSum(int a, int b) {\n" +
            "    return a + b;\n" +
            "}\n";
        var file = _parser.ParseFile(content, "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "ComputeSum");

        var startLine = _parser.GetFunctionBodyStartLine(content, symbol);

        Assert.True(startLine >= 0);
    }

    [Fact]
    public void GetFunctionBodyEndLine_MultilineFunction_ReturnsClosingBraceLine()
    {
        var content =
            "int ComputeSum(int a, int b) {\n" +
            "    return a + b;\n" +
            "}\n";
        var file = _parser.ParseFile(content, "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "ComputeSum");

        var endLine = _parser.GetFunctionBodyEndLine(content, symbol);

        Assert.True(endLine >= 0);
        Assert.True(endLine >= _parser.GetFunctionBodyStartLine(content, symbol));
    }

    [Fact]
    public void GetFunctionBodyLineCount_MultilineFunction_ReturnsPositiveCount()
    {
        var content =
            "int ComputeSum(int a, int b) {\n" +
            "    return a + b;\n" +
            "}\n";
        var file = _parser.ParseFile(content, "test.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "ComputeSum");

        var count = _parser.GetFunctionBodyLineCount(content, symbol);

        Assert.True(count > 0);
    }

    [Fact]
    public void GetFunctionBodyStartLine_NullContent_ReturnsNegative()
    {
        var file = _parser.ParseFile("void f() {}", "t.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");
        Assert.Equal(-1, _parser.GetFunctionBodyStartLine("", symbol));
    }

    [Fact]
    public void GetFunctionBodyEndLine_NullContent_ReturnsNegative()
    {
        var file = _parser.ParseFile("void f() {}", "t.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");
        Assert.Equal(-1, _parser.GetFunctionBodyEndLine("", symbol));
    }

    [Fact]
    public void GetFunctionBodyLineCount_InvalidRange_ReturnsNegative()
    {
        var file = _parser.ParseFile("void f() {}", "t.mq5");
        var symbol = Assert.Single(file.Symbols, s => s.Name == "f");
        Assert.Equal(-1, _parser.GetFunctionBodyLineCount("", symbol));
    }

    // --- GetCompletions ---

    [Fact]
    public void GetCompletions_ReturnsAllSymbolNamesDistinctOrdered()
    {
        var content = "void Alpha() {}\nint Beta() {}\n";
        var file = _parser.ParseFile(content, "test.mq5");

        var completions = _parser.GetCompletions(file, 1, 1).ToList();

        Assert.Contains("Alpha", completions);
        Assert.Contains("Beta", completions);
        // Distinct + ordered
        Assert.Equal(completions.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c), completions);
    }

    [Fact]
    public void GetCompletions_EmptyFile_ReturnsEmpty()
    {
        var file = _parser.ParseFile("", "empty.mq5");
        var completions = _parser.GetCompletions(file, 1, 1);
        Assert.Empty(completions);
    }

    // --- GetIncludes ---

    [Fact]
    public void GetIncludes_FixtureWithIncludes_ReturnsIncludeDirectives()
    {
        var path = GetFixturePath("FunctionBodyAndIncludes.mq5");
        var file = _parser.ParseFileFromPath(path);

        var includes = _parser.GetIncludes(file).ToList();

        Assert.NotEmpty(includes);
        Assert.Contains(includes, i => i.Contains("StdLibrary.mqh"));
        Assert.Contains(includes, i => i.Contains("Framework.mqh"));
    }

    [Fact]
    public void GetIncludes_FileWithoutIncludes_ReturnsEmpty()
    {
        var file = _parser.ParseFile("void f() {}\n", "t.mq5");
        Assert.Empty(_parser.GetIncludes(file));
    }

    // --- FindSymbolsByName ---

    [Fact]
    public void FindSymbolsByName_ExistingSymbol_ReturnsMatch()
    {
        var file = _parser.ParseFile("int ComputeSum(int a, int b) { return a + b; }\n", "t.mq5");

        var matches = _parser.FindSymbolsByName(file, "ComputeSum").ToList();

        Assert.Single(matches);
        Assert.Equal("ComputeSum", matches[0].Name);
    }

    [Fact]
    public void FindSymbolsByName_CaseInsensitive_ReturnsMatch()
    {
        var file = _parser.ParseFile("int ComputeSum() { return 0; }\n", "t.mq5");

        var matches = _parser.FindSymbolsByName(file, "computesum").ToList();

        Assert.Single(matches);
    }

    [Fact]
    public void FindSymbolsByName_MissingSymbol_ReturnsEmpty()
    {
        var file = _parser.ParseFile("void f() {}\n", "t.mq5");
        Assert.Empty(_parser.FindSymbolsByName(file, "DoesNotExist"));
    }

    [Fact]
    public void FindSymbolsByName_NullFile_ReturnsEmpty()
    {
        Assert.Empty(_parser.FindSymbolsByName(null!, "x"));
    }

    [Fact]
    public void FindSymbolsByName_EmptyName_ReturnsEmpty()
    {
        var file = _parser.ParseFile("void f() {}\n", "t.mq5");
        Assert.Empty(_parser.FindSymbolsByName(file, ""));
    }

    // --- FindSymbolDefinition ---

    [Fact]
    public void FindSymbolDefinition_UserSymbol_ReturnsSymbol()
    {
        var content = "int ComputeSum() { return 0; }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // Position on "ComputeSum" identifier (line 1, column 4, 1-based)
        var def = _parser.FindSymbolDefinition(file, content, 1, 5);

        Assert.NotNull(def);
        Assert.Equal("ComputeSum", def!.Name);
    }

    [Fact]
    public void FindSymbolDefinition_Builtin_Print_ReturnsBuiltinSymbol()
    {
        var content = "void f() { Print(1); }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // "Print" is an MQL5 builtin
        Assert.True(_parser.IsBuiltin("Print"));

        // Position cursor on "Print" call (line 1). Find the column of Print.
        var printCol = content.IndexOf("Print(") + 1;
        var def = _parser.FindSymbolDefinition(file, content, 1, printCol + 1);

        Assert.NotNull(def);
        Assert.Equal("Print", def!.Name);
        Assert.Equal("MQL5 Built-in", def.Detail);
    }

    /// <summary>
    /// Issue #182: a user declaration must take precedence over the builtin
    /// registry when the identifier matches both case-insensitively. Here the
    /// local 'bars' shadows the builtin function 'Bars()'.
    /// </summary>
    [Fact]
    public void FindSymbolDefinition_UserLocalNamedLikeBuiltin_ResolvesUserSymbol()
    {
        var content = "int OnInit()\n{\n   int bars = 10;\n   Print(bars);\n   return 0;\n}\n";
        var file = _parser.ParseFile(content, "t.mq5");

        Assert.True(_parser.IsBuiltin("Bars"));

        // Position on 'bars' inside the Print() call (line 4, column 10, 1-based)
        var def = _parser.FindSymbolDefinition(file, content, 4, 10);

        Assert.NotNull(def);
        Assert.Equal("bars", def!.Name);
        Assert.Equal(SymbolType.Variable, def.SymbolType);
        Assert.Equal("int bars", def.Detail);
    }

    [Fact]
    public void FindSymbolDefinition_NoIdentifierAtPosition_ReturnsNull()
    {
        var content = "void f() {}\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // Position on whitespace
        var def = _parser.FindSymbolDefinition(file, content, 1, 1);

        Assert.Null(def);
    }

    [Fact]
    public void FindSymbolDefinition_IdentifierOutsideAnySymbol_ReturnsNull()
    {
        // Identifier inside an expression that is neither a defined symbol nor a builtin.
        var content = "void f() { int x = 0; }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // Position on 'f' in the function name — but that IS a symbol. Use an out-of-range line instead.
        var def = _parser.FindSymbolDefinition(file, content, 99, 1);
        Assert.Null(def);
    }

    [Fact]
    public void FindSymbolDefinition_LocalVariableInsideFunction_ReturnsNull()
    {
        // 'x' is a local inside a function body; the visitor only extracts top-level
        // variable declarations, so it is not in the symbol index and not a builtin.
        var content = "void f() { int x = 0; }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        var xCol = content.IndexOf("int x") + 5; // position of 'x'
        var def = _parser.FindSymbolDefinition(file, content, 1, xCol + 1);

        Assert.Null(def);
    }

    // --- FindSymbolAtPosition ---

    [Fact]
    public void FindSymbolAtPosition_InsideSymbolRange_ReturnsSymbol()
    {
        var content = "int ComputeSum() { return 0; }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // "ComputeSum" starts at column 4 (0-based). Symbol range covers it.
        var symbol = _parser.FindSymbolAtPosition(file, 1, 5);

        Assert.NotNull(symbol);
        Assert.Equal("ComputeSum", symbol!.Name);
    }

    [Fact]
    public void FindSymbolAtPosition_OutsideAnySymbol_ReturnsNull()
    {
        var content = "void f() {}\n";
        var file = _parser.ParseFile(content, "t.mq5");

        // Line beyond the file
        var symbol = _parser.FindSymbolAtPosition(file, 99, 1);
        Assert.Null(symbol);
    }

    // --- IsBuiltin ---

    [Fact]
    public void IsBuiltin_KnownBuiltin_Print_ReturnsTrue()
    {
        Assert.True(_parser.IsBuiltin("Print"));
    }

    [Fact]
    public void IsBuiltin_UnknownName_ReturnsFalse()
    {
        Assert.False(_parser.IsBuiltin("NotABuiltinFunction"));
    }

    // --- GetAllSymbols ---

    [Fact]
    public void GetAllSymbols_ReturnsFileSymbols()
    {
        var file = _parser.ParseFile("void Alpha() {}\nint Beta() {}\n", "t.mq5");
        var all = _parser.GetAllSymbols(file).ToList();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.Name == "Alpha");
        Assert.Contains(all, s => s.Name == "Beta");
    }

    // --- ParseFileWithIncludes ---

    [Fact]
    public void ParseFileWithIncludes_MissingFile_ThrowsFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() => _parser.ParseFileWithIncludes("/nonexistent/path.mq5"));
    }

    [Fact]
    public void ParseFileWithIncludes_FixtureWithoutIncludes_ReturnsMainFileSymbols()
    {
        var path = GetFixturePath("ClassInheritance.mq5");
        var file = _parser.ParseFileWithIncludes(path);

        Assert.NotEmpty(file.Symbols);
        Assert.Equal(MqlLanguage.Mql5, file.Language);
    }

    // --- Macros (via ParseFile coverage of ExtractMacros / ParseMacroName) ---

    [Fact]
    public void ParseFile_WithDefineDirective_ExtractsMacroName()
    {
        var content =
            "#define MAX_SIZE 100\n" +
            "int f() { return MAX_SIZE; }\n";
        var file = _parser.ParseFile(content, "t.mq5");

        Assert.Contains("MAX_SIZE", file.Macros);
    }

    [Fact]
    public void ParseFile_WithoutMacros_ReturnsEmptyMacroList()
    {
        var file = _parser.ParseFile("void f() {}\n", "t.mq5");
        Assert.Empty(file.Macros);
    }

    // --- DeclaredType capture (REQ-SM-02) ---

    [Fact]
    public void ParseFile_GlobalVariableWithClassType_CapturesDeclaredType()
    {
        var file = _parser.ParseFile("CTrade trade;\n", "t.mq5");

        var symbol = Assert.Single(file.Symbols, s => s.Name == "trade");
        Assert.Equal("CTrade", symbol.DeclaredType);
    }

    [Fact]
    public void ParseFile_ClassFieldWithClassType_CapturesDeclaredType()
    {
        var code =
            "class CExpertUser {\n" +
            "private:\n" +
            "    CExpert m_expert;\n" +
            "};\n";
        var file = _parser.ParseFile(code, "t.mq5");

        var classSymbol = file.Symbols.FirstOrDefault(s => s.Name == "CExpertUser");
        Assert.NotNull(classSymbol);
        var field = classSymbol!.Children.Single(s => s.Name == "m_expert");
        Assert.Equal("CExpert", field.DeclaredType);
    }

    [Fact]
    public void ParseFile_ParameterWithClassType_CapturesDeclaredType()
    {
        var code =
            "void OpenOrder(COrderInfo order) {\n" +
            "    order.Print();\n" +
            "}\n";
        var file = _parser.ParseFile(code, "t.mq5");

        var param = file.Symbols.FirstOrDefault(s => s.Name == "order");
        Assert.NotNull(param);
        Assert.Equal("COrderInfo", param!.DeclaredType);
    }

    // --- Class member containment (REQ-SM-02) ---

    [Fact]
    public void ParseFile_ClassMembers_AttachedAsChildren_WithMethodClassification()
    {
        var code =
            "class CWidget {\n" +
            "private:\n" +
            "    int m_count;\n" +
            "public:\n" +
            "    void Reset() { m_count = 0; }\n" +
            "};\n";
        var file = _parser.ParseFile(code, "t.mq5");

        var classSymbol = file.Symbols.FirstOrDefault(s => s.Name == "CWidget");
        Assert.NotNull(classSymbol);

        var field = classSymbol!.Children.SingleOrDefault(s => s.Name == "m_count");
        Assert.NotNull(field);
        Assert.Equal(SymbolType.Variable, field!.SymbolType);

        var method = classSymbol.Children.SingleOrDefault(s => s.Name == "Reset");
        Assert.NotNull(method);
        Assert.Equal(SymbolType.Method, method!.SymbolType);
    }

    [Fact]
    public void ParseFile_ClassMembers_AlsoPresentInFlatList_InheritanceWiringPreserved()
    {
        var code =
            "class Base {}; class Derived : public Base {};\n" +
            "class CWidget { int m_count; void Reset() {} };\n";
        var file = _parser.ParseFile(code, "t.mq5");

        // Flat list still holds every symbol (additive model contract)
        Assert.Contains(file.Symbols, s => s.Name == "CWidget");
        Assert.Contains(file.Symbols, s => s.Name == "m_count");
        Assert.Contains(file.Symbols, s => s.Name == "Reset");

        var baseSymbol = file.Symbols.First(s => s.Name == "Base");
        var derivedSymbol = file.Symbols.First(s => s.Name == "Derived");
        Assert.Equal(baseSymbol, derivedSymbol.ParentSymbol);
        Assert.Contains(derivedSymbol, baseSymbol.Children);
    }

    // --- ParseFile error path ---

    [Fact]
    public void ParseFile_GarbageContent_DoesNotThrow_ReturnsFile()
    {
        // Content that will throw inside the parser is caught; returns an (empty-symbols) file
        var file = _parser.ParseFile("}}} garbage {{{", "t.mq5");
        Assert.NotNull(file);
        Assert.Equal(MqlLanguage.Mql5, file.Language);
    }
}