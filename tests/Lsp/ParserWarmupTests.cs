using System;
using MqlLanguageServer;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Issue #93: the parser warmup is fire-and-forget on the server's initialize
/// path, so a silent failure would quietly reduce the startup fix to a no-op.
/// These tests pin that the warmup completes, parses both pipelines cleanly
/// (zero syntax errors), and that its returned status reflects that.
/// </summary>
public class ParserWarmupTests
{
    [Fact]
    public void WarmUp_CompletesWithoutThrowing_AndReportsSuccess()
    {
        // Smoke: the fire-and-forget call on the initialize path must never
        // throw, and must report that both pipelines are warm.
        var ex = Record.Exception(() => Assert.True(ParserWarmup.WarmUp()));
        Assert.Null(ex);
    }

    [Fact]
    public void WarmupSnippets_ParseCleanly_WithBothParsers()
    {
        // Pin the warmup content itself: if a grammar change makes the
        // snippets produce syntax errors, the warmup runs but warms the
        // error paths instead of the happy path (silent quality loss).
        var mql4 = new Mql4AntlrParser();
        var file4 = mql4.ParseFile(
            "int OnInit()\n{\n    Print(\"warmup\");\n    return(INIT_SUCCEEDED);\n}\n",
            "warmup.mq4");
        Assert.Empty(file4.SyntaxErrors ?? new System.Collections.Generic.List<SyntaxError>());
        Assert.NotEmpty(file4.Symbols);

        var mql5 = new Mql5AntlrParser();
        var file5 = mql5.ParseFile(
            "int OnInit()\n{\n    Print(\"warmup\");\n    return(INIT_SUCCEEDED);\n}\n",
            "warmup.mq5");
        Assert.Empty(file5.SyntaxErrors ?? new System.Collections.Generic.List<SyntaxError>());
        Assert.NotEmpty(file5.Symbols);
    }
}