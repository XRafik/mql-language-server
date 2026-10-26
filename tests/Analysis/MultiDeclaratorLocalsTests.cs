using System.Linq;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Analysis;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Issue #109 (follow-up): local declarations with MULTIPLE declarators
/// ("uchar src[], dst[], key[32];") emitted only the FIRST declarator's
/// symbol — the rest became unresolved-symbol false positives (dst, key) at
/// every later use. Both visitors now emit one symbol per declarator.
/// </summary>
public class MultiDeclaratorLocalsTests
{
    private const string Fixture =
        "class Crypter\n" +
        "{\n" +
        "    string EnCrypt(ENUM_CRYPT_METHOD method, string toKey);\n" +
        "};\n" +
        "string Crypter::EnCrypt(ENUM_CRYPT_METHOD method, string toKey)\n" +
        "{\n" +
        "    uchar src[], dst[], key[32];\n" +
        "    int res = 0;\n" +
        "    return \"x\";\n" +
        "}\n";

    [Fact]
    public void Mql5Visitor_EmitsEveryDeclarator()
    {
        var parser = new Mql5AntlrParser();
        var file = parser.ParseFile(Fixture, "multi.mq5");

        foreach (var name in new[] { "src", "dst", "key", "res" })
        {
            Assert.Contains(file.Symbols, s => s.Name == name);
        }
    }

    [Fact]
    public void Mql4Visitor_CoversMultiDeclarators()
    {
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile(Fixture, "multi.mq4");

        foreach (var name in new[] { "src", "dst", "key", "res" })
        {
            Assert.Contains(file.Symbols, s => s.Name == name);
        }
    }

    [Fact]
    public void Analyzer_DoesNotFlagLaterDeclaratorsAsUnresolved()
    {
        var parser = new Mql5AntlrParser();
        var file = parser.ParseFile(Fixture, "multi.mq5");
        var analyzer = new SemanticAnalyzer(new IMqlBuiltins[] { new Mql5Builtins() });

        var diags = analyzer.Analyze(file, Fixture, MqlLanguage.Mql5, System.Threading.CancellationToken.None).ToList();

        Assert.DoesNotContain(diags, d => d.Message.Contains("'dst'"));
        Assert.DoesNotContain(diags, d => d.Message.Contains("'key'"));
        Assert.DoesNotContain(diags, d => d.Message.Contains("'src'"));
    }
}
