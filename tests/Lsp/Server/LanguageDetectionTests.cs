using System;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Server;

public class LanguageDetectionTests
{
    [Fact]
    public void Detect_ClientLanguageId_Mql5_Returns_Mql5()
    {
        var uri = new Uri("file:///x.mq5");
        var result = LanguageDetection.Detect(uri, "mql5", "int start() { return 0; }");
        Assert.Equal(MqlLanguage.Mql5, result);
    }

    [Fact]
    public void Detect_ClientLanguageId_Mql4_Returns_Mql4()
    {
        var uri = new Uri("file:///x.mq4");
        var result = LanguageDetection.Detect(uri, "mql4", "int start() { return 0; }");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_UnknownLanguageId_Returns_Mql4()
    {
        var uri = new Uri("file:///x.py");
        var result = LanguageDetection.Detect(uri, "python", "print('hello')");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_MqhWithMql4PredefinedVariables_DefaultsToMql4()
    {
        // `_Point` (like `_Digits`, `_Symbol`, `_Period`) is a predefined variable valid in
        // both MQL4 (build 600+) and MQL5, so it must not be treated as an MQL5-only marker.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "void OnTick() { double p = _Point; }");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_With_Remaining_Mql5Token_Returns_Mql5()
    {
        // Tokens that remain MQL5-exclusive (nullptr, #resource, union, pack(, enum class)
        // must still route .mqh content to MQL5.
        var uri = new Uri("file:///x.mqh");
        Assert.Equal(MqlLanguage.Mql5, LanguageDetection.Detect(uri, null, "void f() { int* p = nullptr; }"));
        Assert.Equal(MqlLanguage.Mql5, LanguageDetection.Detect(uri, null, "union U { int a; };"));
    }

    [Fact]
    public void Detect_Mqh_Only_Mql4_Returns_Mql4()
    {
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "int start() { return(0); }");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_Empty_Returns_Mql4()
    {
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_Mql5Token_After_600_Bytes_Returns_Mql5()
    {
        var uri = new Uri("file:///x.mqh");
        var prefix = new string('x', 600);
        var result = LanguageDetection.Detect(uri, null, $"{prefix}\nvoid f() {{ int* p = nullptr; }}");
        Assert.Equal(MqlLanguage.Mql5, result);
    }

    [Fact]
    public void Detect_Mqh_Mql5Token_In_Line_Comment_Returns_Mql4()
    {
        // Issue #124: a lone MQL5-exclusive token inside a line comment must not
        // flip the header to the MQL5 pipeline.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "// void f(union U u) { pack(1); }\nint start() { return(0); }");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_Mql5Token_In_Block_Comment_Returns_Mql4()
    {
        // Issue #124: same rule for block comments, including doc-comment style.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "/*\n * Legacy signature: void f(union U u)\n *\n/\nint start() { return(0); }");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_Mql5Token_After_Comment_Still_Returns_Mql5()
    {
        // Stripping comments must not hide real code: a token in code after a
        // comment still flips the dialect.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "// unused: union U { int a; };\nvoid f() { int* p = nullptr; }");
        Assert.Equal(MqlLanguage.Mql5, result);
    }

    [Fact]
    public void Detect_Mqh_CommentMarker_In_String_Is_Not_A_Comment()
    {
        // A "//" or "/*" inside a string literal is not a comment; code after it
        // on the same line must still be seen by the sniffer.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "string url = \"http://example.com/*x\"; union U { int a; };");
        Assert.Equal(MqlLanguage.Mql5, result);
    }

    [Fact]
    public void Detect_Mqh_Unterminated_Block_Comment_Degrades_To_Mql4()
    {
        // Unterminated block comment: the remainder is comment (compiler-like),
        // so a token after it cannot be real code and must not flip the dialect.
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "int start() { return(0); }\n/* void f(union U u)");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_Mqh_With_Using_System_Returns_Mql4()
    {
        var uri = new Uri("file:///x.mqh");
        var result = LanguageDetection.Detect(uri, null, "using System;");
        Assert.Equal(MqlLanguage.Mql4, result);
    }

    [Fact]
    public void Detect_WithUnknownLanguageId_FallsBackToExtension()
    {
        // Unknown languageId ("mqh" or anything else) must still trigger extension sniff.
        var mq5Uri = new Uri("file:///x.mq5");
        Assert.Equal(MqlLanguage.Mql5, LanguageDetection.Detect(mq5Uri, "unknown-id", "int start(){}"));

        var mq4Uri = new Uri("file:///x.mq4");
        Assert.Equal(MqlLanguage.Mql4, LanguageDetection.Detect(mq4Uri, "unknown-id", "int start(){}"));
    }

    [Fact]
    public void Detect_WithMqhLanguageId_SniffsContent()
    {
        // languageId "mqh" is unknown to the server; content sniff must still apply.
        var uri = new Uri("file:///x.mqh");
        var mql5Content = "void f() { int* p = nullptr; }";
        Assert.Equal(MqlLanguage.Mql5, LanguageDetection.Detect(uri, "mqh", mql5Content));

        var mql4Content = "int start() { return(0); }";
        Assert.Equal(MqlLanguage.Mql4, LanguageDetection.Detect(uri, "mqh", mql4Content));
    }

    [Theory]
    [InlineData("file:///x.mq5", "mql5")]
    [InlineData("file:///x.MQ5", "mql5")]
    [InlineData("file:///x.mq4", "mql4")]
    [InlineData("file:///x.mqh", "mql4")]
    [InlineData("file:///backup.mq5.bak", "mql4")]
    [InlineData("file:///noext", "mql4")]
    public void GetLanguageIdFromUri_UsesExtensionOnly(string uriString, string expectedLanguageId)
    {
        var uri = new Uri(uriString);
        Assert.Equal(expectedLanguageId, LanguageDetection.GetLanguageIdFromUri(uri));
    }
}
