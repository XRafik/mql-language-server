using System;
using System.Collections.Generic;
using System.Linq;
using Antlr4.Runtime;
using Mql5Grammar;
using Xunit;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// TDD red-first grammar tests for MQL5 constructs.
/// Each test parses a minimal .mq5 snippet directly via the generated
/// Mql5Grammar lexer/parser and asserts no syntax errors.
/// These tests are intentionally RED before T-S2-02 (grammar rules).
/// </summary>
public class Mql5GrammarTests
{
    private static void ParseWithoutErrors(string code)
    {
        var inputStream = new AntlrInputStream(code);
        var lexer = new Mql5GrammarLexer(inputStream);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new Mql5GrammarParser(tokenStream);

        var errors = new List<string>();
        parser.RemoveErrorListeners();
        parser.AddErrorListener(new FailOnSyntaxErrorListener(errors));

        var tree = parser.compilationUnit();
        Assert.NotNull(tree);
        Assert.Empty(errors);
    }

    [Fact]
    public void Parses_NullptrLiteral()
    {
        ParseWithoutErrors("void f() { int* p = nullptr; }");
    }

    [Fact]
    public void Parses_UnionDeclaration()
    {
        ParseWithoutErrors("union U { int i; double d; };");
    }

    [Fact]
    public void Parses_FinalMethodQualifier()
    {
        ParseWithoutErrors("class C { virtual void f() final {} };");
    }

    [Fact]
    public void Parses_PackPragma()
    {
        ParseWithoutErrors("#pragma pack(8) class C { int x; };");
    }

    [Fact]
    public void Parses_ReferenceType()
    {
        ParseWithoutErrors("void swap(int& a, int& b) {}");
    }

    [Fact]
    public void Parses_EnumClass()
    {
        ParseWithoutErrors("enum class Color { Red, Green, Blue };");
    }

    [Fact]
    public void Parses_UsingDirective()
    {
        ParseWithoutErrors("using namespace std; void f() {}");
    }

    [Fact]
    public void Parses_DefaultParameter()
    {
        ParseWithoutErrors("void f(int x = 10) {}");
    }

    [Fact]
    public void Parses_InitializationList()
    {
        ParseWithoutErrors("struct S { int x; S(int v) : x(v) {} };");
    }

    [Fact]
    public void Parses_CopyConstructor()
    {
        ParseWithoutErrors("class C { int x; C(const C& other) : x(other.x) {} };");
    }

    [Fact]
    public void Parses_ResourceDirective()
    {
        ParseWithoutErrors("#resource \"res/icon.bmp\" as bitmap MyIcon;");
    }

    // Issue #176: documented input-parameter grouping. The MQL5 Reference
    // "input variables" chapter (local dump:
    // tools/generate-golden-lists/data/pdf/mql5-reference.md) documents
    // `input group "text"` — no trailing semicolon. `sinput group` is not
    // documented and stays rejected (issue #141 honesty rule).
    [Fact]
    public void InputGroup_String_IsAccepted()
    {
        ParseWithoutErrors("input group \"Trend Filters\"\ninput int InpX = 1;");
    }

    [Fact]
    public void InputGroup_BeforeInputs_ParsesAllFollowingInputs()
    {
        ParseWithoutErrors("input group \"X\"\ninput int InpA = 1;\ninput double InpB = 2.0;");
    }

    // Issue #176: `f`/`F` suffix on real literals. The MQL5 Reference
    // constants chapter documents real constants without naming the suffix,
    // but official reference code samples use f-suffixed float literals
    // (`{1.0f, 2.0f, 3.0f, 4.0f, 5.0f}` in the matrixf examples) and
    // MetaEditor compiles them cleanly. C-style lexing: the suffix is
    // accepted on all DOUBLE branches (decimal, leading-dot, scientific
    // EXP); the dump is silent on the EXP form, accepted for consistency.
    // Integer `123f` stays rejected (branch structure keeps INTEGER +
    // IDENTIFIER).
    [Fact]
    public void RealLiteral_FSuffix_IsAccepted()
    {
        ParseWithoutErrors("double a = 0.0f; double b = 1.5F; double c = 1e3f;");
    }

    [Fact]
    public void Parses_ClassInheritance()
    {
        ParseWithoutErrors("class Base {}; class Derived : public Base {};");
    }

    [Fact]
    public void Parses_Template()
    {
        ParseWithoutErrors("template<typename T> class C { T x; };");
    }

    [Fact]
    public void Parses_NewDeleteHeapObjects()
    {
        ParseWithoutErrors("void f() { int* p = new int; delete p; }");
    }

    [Fact]
    public void Parses_PointerDerefWithParens()
    {
        ParseWithoutErrors("class C { public: void f() {} }; void g() { C* p = new C(); (*p).f(); delete p; }");
    }

    [Fact]
    public void Parses_DestructorWithVoidParameter()
    {
        ParseWithoutErrors("class C { public: ~C(void); }; C::~C(void) {}");
    }

    [Fact]
    public void Parses_ForLoopCommaSeparatedInitAndIncrement()
    {
        ParseWithoutErrors("void f() { int i, j; for(i = 0, j = 0; j < 10; i++, j += 4) { } }");
    }

    [Fact]
    public void Parses_SimpleForLoopRegression()
    {
        ParseWithoutErrors("void f() { for(int i = 0; i < 10; i++) { } }");
    }

    [Fact]
    public void Parses_DestructorWithoutParametersRegression()
    {
        ParseWithoutErrors("class C { public: ~C(); }; C::~C() {}");
    }

    private sealed class FailOnSyntaxErrorListener : IAntlrErrorListener<IToken>
    {
        private readonly List<string> _errors;

        public FailOnSyntaxErrorListener(List<string> errors)
        {
            _errors = errors;
        }

        public void SyntaxError(System.IO.TextWriter output, IRecognizer recognizer, IToken offendingSymbol, int line, int charPositionInLine, string msg, RecognitionException e)
        {
            _errors.Add($"line {line}:{charPositionInLine} {msg}");
        }
    }
}
