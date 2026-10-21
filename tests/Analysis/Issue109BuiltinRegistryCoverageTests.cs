using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Issue #109: massive false-positive 5070/1070 unresolved-symbol diagnostics
/// over real standard-library names (TerminalInfoInteger, CharToString,
/// StringSplit, the boolean keywords True/False, …) that were absent from
/// both builtin registries. These tests pin the registry coverage for the
/// names surfaced by the rc.1 battery and the issue report.
/// </summary>
public class Issue109BuiltinRegistryCoverageTests
{
    // The exact names from the issue report.
    private static readonly string[] ReportedNames =
    {
        "True", "False", "TerminalInfoInteger", "CharToString", "StringSplit"
    };

    // A broader sweep of common standard-library names that the same grep
    // audit found missing from both dialects. StringToDateTime and
    // CryptMethod are asserted separately: they are phantom names with
    // zero evidence in ANY dialect reference, removed from the MQL4
    // golden list by the MQL4-dataset-hygiene change (see
    // tools/generate-golden-lists/data/mql4-dataset-audit.json) and from
    // the MQL5 golden list by the MQL5-dataset-hygiene change (see
    // tools/generate-golden-lists/data/mql5-dataset-audit.json).
    // StringGetChar/StringSetChar/WindowExpertName are MQL4-only since
    // the MQL5-dataset-hygiene change and are pinned in
    // Mql4OnlySecondSweepNames_AreMql4Only_AfterMql5DatasetHygiene.
    private readonly Mql5Builtins _mql5 = new();

    private static readonly string[] AdditionalBothDialects =
    {
        "TerminalInfoDouble", "TerminalInfoString", "DoubleToString",
        "StringReplace", "StringFormat", "StringToInteger",
        "ArrayInitialize", "ArrayFill", "iOpen", "iHigh", "iLow", "iBarShift",
        "iHighest", "iLowest", "PlaySound", "SendMail", "SendNotification",
        "MQLInfoInteger", "MQLInfoString",
        // crypto family (MQL5 builtin since the compiler bundles Crypt.mqh)
        "CryptEncode", "CryptDecode", "CharArrayToString",
        // second sweep (real-project analyze findings). StringGetChar,
        // StringSetChar and WindowExpertName left this both-dialects list:
        // they are MQL4-only since the MQL5-dataset-hygiene change and
        // are pinned in Mql4OnlySecondSweepNames_AreMql4Only_AfterMql5DatasetHygiene.
        "StringGetCharacter", "StringToCharArray", "GlobalVariableCheck", "MessageBox"
    };

    [Fact]
    public void Mql4Registry_CoversReportedBuiltins()
    {
        foreach (var name in ReportedNames)
        {
            Assert.True(Mql4Builtins.IsBuiltin(name), $"MQL4 registry is missing '{name}' (issue #109)");
        }
    }

    [Fact]
    public void Mql5Registry_CoversReportedBuiltins()
    {
        foreach (var name in ReportedNames)
        {
            Assert.True(_mql5.IsBuiltin(name), $"MQL5 registry is missing '{name}' (issue #109)");
        }
    }

    [Fact]
    public void Mql4Registry_CoversTheAdditionalSweep()
    {
        foreach (var name in AdditionalBothDialects)
        {
            Assert.True(Mql4Builtins.IsBuiltin(name), $"MQL4 registry is missing '{name}' (issue #109)");
        }
    }

    [Fact]
    public void Mql5Registry_CoversTheAdditionalSweep()
    {
        foreach (var name in AdditionalBothDialects)
        {
            Assert.True(_mql5.IsBuiltin(name), $"MQL5 registry is missing '{name}' (issue #109)");
        }
    }

    [Fact]
    public void PhantomSweepNames_AreAbsentFromBothDialects()
    {
        // StringToDateTime and CryptMethod were phantom names with zero
        // evidence in ANY dialect reference: removed from the MQL4 golden
        // list by the MQL4-dataset-hygiene change (see
        // tools/generate-golden-lists/data/mql4-dataset-audit.json) and
        // from the MQL5 golden list by the MQL5-dataset-hygiene change
        // (see tools/generate-golden-lists/data/mql5-dataset-audit.json).
        foreach (var name in new[] { "StringToDateTime", "CryptMethod" })
        {
            Assert.False(_mql5.IsBuiltin(name),
                $"{name} should not be registered as MQL5 built-in (phantom name; see mql5-dataset-audit.json)");
            Assert.False(Mql4Builtins.IsBuiltin(name),
                $"{name} should not be registered as MQL4 built-in (phantom name; see mql4-dataset-audit.json)");
        }
    }

    [Fact]
    public void Mql4OnlySecondSweepNames_AreMql4Only_AfterMql5DatasetHygiene()
    {
        // StringGetChar/StringSetChar are documented in the MQL4 Reference
        // only (MQL5 documents StringGetCharacter/StringSetCharacter
        // instead) and WindowExpertName has no MQL5 reference page; they
        // were removed from the MQL5 golden list by the
        // MQL5-dataset-hygiene change (see
        // tools/generate-golden-lists/data/mql5-dataset-audit.json).
        foreach (var name in new[] { "StringGetChar", "StringSetChar", "WindowExpertName" })
        {
            Assert.True(Mql4Builtins.IsBuiltin(name), $"MQL4 registry is missing '{name}' (issue #109)");
            Assert.False(_mql5.IsBuiltin(name),
                $"{name} should not be registered as MQL5 built-in (MQL4-only; see mql5-dataset-audit.json)");
        }
    }

    [Fact]
    public void Mql5Registry_CoversCryptConstants()
    {
        foreach (var name in new[] { "CRYPT_DES", "CRYPT_AES256", "CRYPT_HASH_SHA256", "CRYPT_ARCH_ZIP" })
        {
            Assert.True(_mql5.IsBuiltin(name), $"MQL5 registry is missing '{name}' (issue #109)");
        }
    }

    [Fact]
    public void BooleanKeywords_ResolveCaseInsensitively()
    {
        // The MQL compiler accepts True/true/TRUE — the registries compare
        // OrdinalIgnoreCase, so one canonical entry per dialect covers all.
        foreach (var variant in new[] { "true", "TRUE", "True", "false", "FALSE", "False" })
        {
            Assert.True(Mql4Builtins.IsBuiltin(variant), $"MQL4 registry is missing boolean literal '{variant}'");
            Assert.True(_mql5.IsBuiltin(variant), $"MQL5 registry is missing boolean literal '{variant}'");
        }
    }

    // Fourth sweep: the exact names the published rc.4 binary still flagged on
    // Botlidator_ver_2_90.mqh (22 diagnostics: 19x1070 + 3x1003).
    private static readonly string[] FourthSweepMql4 =
    {
        "CRYPT_BASE64", "CRYPT_HASH_SHA512", "CRYPT_HASH_SHA3_256", "CRYPT_HASH_SHA3_512",
        "CRYPT_ARCH_GZIP", "CRYPT_ARCH_ZLIB", "CRYPT_ARCH_RLE",
        "ACCOUNT_SERVER", "ACCOUNT_CURRENCY", "ACCOUNT_BALANCE", "ACCOUNT_EQUITY",
        "ACCOUNT_PROFIT", "ACCOUNT_LEVERAGE", "ACCOUNT_MARGIN", "ACCOUNT_MARGIN_FREE",
        "ACCOUNT_MARGIN_LEVEL", "ACCOUNT_MARGIN_SO_SO", "ACCOUNT_MARGIN_SO_CALL",
        "ACCOUNT_MARGIN_SO_STOP", "ACCOUNT_MARGIN_SO_MODE", "ACCOUNT_CREDIT",
        "ACCOUNT_TRADE_ALLOWED", "ACCOUNT_TRADE_EXPERT", "ACCOUNT_TRADE_MODE",
        "ACCOUNT_TRADE_MODE_DEMO", "ACCOUNT_TRADE_MODE_CONTEST", "ACCOUNT_TRADE_MODE_REAL",
        "ACCOUNT_LIMIT_ORDERS", "ACCOUNT_TRADE_STOPOUT_LEVEL", "ACCOUNT_ASSETS",
        "ACCOUNT_LIABILITIES", "ACCOUNT_COMMISSION_BLOCKED",
        "TERMINAL_DLLS_ALLOWED", "TERMINAL_DATA_PATH", "TERMINAL_CONNECTED",
        "TERMINAL_TRADE_ALLOWED", "TERMINAL_EXPERTS_ALLOWED", "TERMINAL_COMMONDATA_PATH",
        "TERMINAL_LANGUAGE", "TERMINAL_BUILD", "TERMINAL_NAME", "TERMINAL_PATH",
        "TERMINAL_EMAIL", "TERMINAL_FTP_ENABLED",
        "IsExpertEnabled", "MathRand", "MathSrand",
        "MODE_MINLOT", "MODE_MAXLOT", "MODE_LOTSIZE", "MODE_LOTSTEP", "MODE_POINT",
        "MODE_DIGITS", "MODE_SPREAD", "MODE_STOPLEVEL", "MODE_FREEZELEVEL",
        "MODE_BID", "MODE_ASK", "MODE_HIGH", "MODE_LOW", "MODE_TIME", "MODE_TRADEALLOWED"
    };

    [Fact]
    public void Mql4Registry_CoversFourthSweep()
    {
        foreach (var name in FourthSweepMql4)
        {
            Assert.True(Mql4Builtins.IsBuiltin(name), $"MQL4 registry is missing '{name}' (issue #109 fourth sweep)");
        }
    }
}