using System.Collections.Generic;
using System.Linq;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Analysis.Rules;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Regression tests for issue #163: the MQL5 builtin golden list missed the
/// File* function family and the TIME_*/FILE_*/SEEK_*/CP_* constants, so
/// <see cref="UnresolvedSymbolRule"/> emitted false 5070 diagnostics for
/// standard runtime names in any document doing file I/O or datetime string
/// formatting. Data-completeness gap (same failure class #136 addressed);
/// the fix lives in <c>data/builtins/mql5.json</c> plus the symmetric gap in
/// <c>data/builtins/mql4.json</c>, never in registry code.
/// </summary>
public class Issue163BuiltinRegistryCoverageTests
{
    // --- MQL5 file operations reported on issue #163 ------------------------
    private static readonly string[] Mql5FileFunctions =
    {
        "FileOpen", "FileClose", "FileRead", "FileWrite", "FileWriteString",
        "FileReadString", "FileIsEnding", "FolderCreate", "FolderDelete",
        "FileDelete", "FileCopy", "FileMove", "FileSeek", "FileSize",
        "FileTell", "FileFlush",
    };

    // --- MQL5 constants reported on issue #163 ------------------------------
    // (FILE_BIN/FILE_SHARE_READ/FILE_SHARE_WRITE were already present; the
    // issue overcounted by three. FILE_CSV/FILE_READ/FILE_WRITE/SEEK_END/
    // TIME_DATE/TIME_SECONDS existed in MQL4 since #152 but were missing on
    // the MQL5 side.)
    private static readonly string[] Mql5FileAndTimeConstants =
    {
        "TIME_DATE", "TIME_MINUTES", "TIME_SECONDS",
        "FILE_COMMON", "FILE_WRITE", "FILE_READ", "FILE_TXT", "FILE_ANSI",
        "FILE_CSV", "FILE_SHARE_READ", "FILE_SHARE_WRITE",
        "SEEK_SET", "SEEK_CUR", "SEEK_END",
        "CP_UTF8", "CP_ACP",
    };

    // --- MQL4 symmetry gap (all names documented for MQL4 build 600+) -------
    private static readonly string[] Mql4SymmetryFunctions =
    {
        "FileWriteString", "FileReadString", "FolderCreate", "FolderDelete",
        "FileCopy", "FileMove", "FileTell",
    };

    private static readonly string[] Mql4SymmetryConstants =
    {
        "TIME_MINUTES", "FILE_COMMON", "FILE_TXT", "FILE_ANSI",
        "SEEK_SET", "SEEK_CUR", "CP_UTF8", "CP_ACP",
    };

    [Theory]
    [MemberData(nameof(FileFunctionNames))]
    public void FileFunctions_AreRegisteredInBothDialects(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinFunction(name), $"MQL4 registry is missing file function '{name}'");
        Assert.True(new Mql5Builtins().IsBuiltinFunction(name), $"MQL5 registry is missing file function '{name}' (issue #163)");
    }

    [Theory]
    [MemberData(nameof(FileConstantNames))]
    public void FileAndTimeConstants_AreRegisteredInBothDialects(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinVariable(name), $"MQL4 registry is missing file/time constant '{name}'");
        Assert.True(new Mql5Builtins().IsBuiltinVariable(name), $"MQL5 registry is missing file/time constant '{name}' (issue #163)");
    }

    // --- End-to-end: the issue #163 repro snippet must be diagnostic-free ---

    private static SemanticRuleContext Context(
        MqlFile file,
        string content,
        MqlLanguage language = MqlLanguage.Mql5,
        IMqlBuiltins[]? builtins = null) =>
        new(file, content, language, language == MqlLanguage.Mql5 ? 5000 : 1000, System.Threading.CancellationToken.None, builtins);

    [Fact]
    public void Issue163ReproSnippet_Mql5Document_EmitsNoUnresolvedSymbolDiagnostics()
    {
        // Issue #163 repro: file I/O + datetime string formatting.
        const string content =
            "void OnStart()\n" +
            "  {\n" +
            "   int h = FileOpen(\"log.txt\", FILE_WRITE | FILE_TXT | FILE_ANSI);\n" +
            "   if(h != INVALID_HANDLE)\n" +
            "     {\n" +
            "      FileWriteString(h, \"hello\" + TimeToString(TimeCurrent(), TIME_DATE));\n" +
            "      FileClose(h);\n" +
            "     }\n" +
            "  }\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("FileOpen", 2, 11, 8),
                new("FILE_WRITE", 2, 31, 10),
                new("FILE_TXT", 2, 44, 8),
                new("FILE_ANSI", 2, 55, 9),
                new("INVALID_HANDLE", 3, 11, 14),
                new("FileWriteString", 5, 6, 15),
                new("TimeToString", 5, 35, 12),
                new("TimeCurrent", 5, 48, 11),
                new("TIME_DATE", 5, 63, 9),
                new("FileClose", 6, 6, 9),
            }
        };

        var diagnostics = new UnresolvedSymbolRule().Check(Context(file, content)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void Issue163Repro_Mql4Document_EmitsNoUnresolvedSymbolDiagnostics()
    {
        // MQL4 symmetry: the same file-I/O surface with names that were also
        // missing from mql4.json (FILE_COMMON, SEEK_CUR, TIME_MINUTES, ...).
        const string content =
            "void OnStart()\n" +
            "  {\n" +
            "   int h = FileOpen(FILE_COMMON + \"\\\\MQL4\\\\log.txt\", FILE_WRITE | FILE_ANSI);\n" +
            "   FileSeek(h, 0, SEEK_CUR);\n" +
            "   FileWriteString(h, TimeToString(TimeCurrent(), TIME_MINUTES));\n" +
            "   FileClose(h);\n" +
            "  }\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence>
            {
                new("FileOpen", 2, 11, 8),
                new("FILE_COMMON", 2, 20, 11),
                new("FILE_WRITE", 2, 45, 10),
                new("FILE_ANSI", 2, 58, 9),
                new("FileSeek", 3, 3, 8),
                new("SEEK_CUR", 3, 16, 8),
                new("FileWriteString", 4, 3, 15),
                new("TimeToString", 4, 22, 12),
                new("TimeCurrent", 4, 35, 11),
                new("TIME_MINUTES", 4, 48, 12),
                new("FileClose", 5, 3, 9),
            }
        };

        var diagnostics = new UnresolvedSymbolRule().Check(Context(file, content, MqlLanguage.Mql4)).ToList();

        Assert.Empty(diagnostics);
    }

    [Fact]
    public void TrulyUndeclaredSymbol_StillFlagged_AfterEnrichment()
    {
        // Guard against a false green: the rule must keep flagging genuinely
        // undeclared identifiers next to the now-resolving builtins.
        const string content = "void OnStart()\n{\n   MyHelper();\n}\n";
        var file = new MqlFile
        {
            Occurrences = new List<TokenOccurrence> { new("MyHelper", 2, 3, 8) }
        };

        var diagnostics = new UnresolvedSymbolRule().Check(Context(file, content)).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("5070", diagnostic.Code);
        Assert.Contains("MyHelper", diagnostic.Message);
    }

    public static TheoryData<string> FileFunctionNames() => ToTheoryData(Mql5FileFunctions);
    public static TheoryData<string> FileConstantNames() => ToTheoryData(Mql5FileAndTimeConstants);

    private static TheoryData<string> ToTheoryData(string[] names)
    {
        var data = new TheoryData<string>();
        foreach (var name in names)
        {
            data.Add(name);
        }
        return data;
    }
}
