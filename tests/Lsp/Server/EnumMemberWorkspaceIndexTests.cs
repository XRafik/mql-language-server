using System;
using System.Linq;
using System.Threading;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Tests.Lsp.Server;

/// <summary>
/// Issue #126 follow-up: enum members (and the MQL4 enum type name itself)
/// must reach the workspace name index so Tier-2 correlation, hover, and
/// references see nested declarations — without leaking method locals or
/// class fields into the index.
/// </summary>
public class EnumMemberWorkspaceIndexTests
{
    [Fact]
    public void Mql4_ParsesEnumDeclaration_TypeAndMembersCaptured()
    {
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile("enum Mode { OFF, ON = 1 };\nint f() { return 0; }", "x.mq4", CancellationToken.None);

        var enumSymbol = Assert.Single(file.Symbols, s => s.Name == "Mode");
        Assert.Equal(SymbolKind.Enum, enumSymbol.Kind);

        var members = enumSymbol.Children.Where(c => c.Kind == SymbolKind.EnumMember).ToList();
        Assert.Equal(new[] { "OFF", "ON" }, members.Select(m => m.Name));
    }

    [Fact]
    public void Mql5_ParsesEnumDeclaration_MembersCapturedAsChildren()
    {
        var parser = new Mql5AntlrParser();
        var file = parser.ParseFile("enum Color { RED, GREEN = 2, BLUE };\nint f() { return 0; }", "x.mq5", CancellationToken.None);

        var enumSymbol = Assert.Single(file.Symbols, s => s.Name == "Color");
        var members = enumSymbol.Children.Where(c => c.Kind == SymbolKind.EnumMember).Select(c => c.Name).ToList();
        Assert.Equal(new[] { "RED", "GREEN", "BLUE" }, members);
    }

    [Fact]
    public void AddFile_IndexesEnumMembers_AndEnumTypeNames()
    {
        var index = new GlobalSymbolIndex(ctorBypass: true);
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile("enum Mode { OFF, ON = 1 };\nint f() { return 0; }", "hdr.mqh", CancellationToken.None);
        index.AddFile("hdr.mqh", MqlLanguage.Mql4, file.Symbols,
            SymbolOccurrenceMapper.Map(file, "hdr.mqh", MqlLanguage.Mql4));

        Assert.NotEmpty(index.FindSymbol("Mode", MqlLanguage.Mql4));
        Assert.NotEmpty(index.FindSymbol("OFF", MqlLanguage.Mql4));
        Assert.NotEmpty(index.FindSymbol("ON", MqlLanguage.Mql4));
    }

    [Fact]
    public void AddFile_IndexesNestedEnumMembers_InsideClassScope()
    {
        const string content = """
            enum Color { RED, GREEN };
            class Holder
            {
            public:
                enum Inner { A, B };
                int Method(int arg)
                {
                    int localOnly = arg;
                    return localOnly;
                }
            };
            """;
        var index = new GlobalSymbolIndex(ctorBypass: true);
        var parser = new Mql5AntlrParser();
        var file = parser.ParseFile(content, "cls.mqh", CancellationToken.None);
        index.AddFile("cls.mqh", MqlLanguage.Mql5, file.Symbols,
            SymbolOccurrenceMapper.Map(file, "cls.mqh", MqlLanguage.Mql5));

        // Top-level and nested enum members (plus the enum type names) cross
        // into the workspace name index...
        Assert.NotEmpty(index.FindSymbol("RED", MqlLanguage.Mql5));
        Assert.NotEmpty(index.FindSymbol("A", MqlLanguage.Mql5));
        Assert.NotEmpty(index.FindSymbol("Inner", MqlLanguage.Mql5));
        // ...while method locals remain outside the enum-member boundary. They
        // are top-level symbols via pre-existing REQ-SM-02 behavior, unchanged
        // by this follow-up (indexing them differently is separate work).
        Assert.Single(index.FindSymbol("localOnly", MqlLanguage.Mql5));
    }

    [Fact]
    public void Tier2Correlation_EnumMemberResolvableViaFindSymbol()
    {
        // End-to-end shape of the #126 fixture: once the header is indexed,
        // Tier 2 (FindSymbol) resolves an enum member used by an including EA,
        // independent of the Tier-1b occurrence path.
        var index = new GlobalSymbolIndex(ctorBypass: true);
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile("enum TradeMode { MODE_OFF, MODE_AGGRESSIVE };\nint RiskPoints(int level) { return level; }", "enums_utils.mqh", CancellationToken.None);
        index.AddFile("enums_utils.mqh", MqlLanguage.Mql4, file.Symbols,
            SymbolOccurrenceMapper.Map(file, "enums_utils.mqh", MqlLanguage.Mql4));

        Assert.NotEmpty(index.FindSymbol("MODE_AGGRESSIVE", MqlLanguage.Mql4));
        Assert.NotEmpty(index.FindSymbol("RiskPoints", MqlLanguage.Mql4));
    }
}
