using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using Xunit;

namespace MqlLanguageServer.Tests.Analysis;

/// <summary>
/// Issue #136 regression pins: every name reported as a 1070 false positive
/// on the real ~4.3k-line MQL4 EA corpus (post-#126 residue) plus the 26-name
/// documented-API probe, for both dialects. The corpus stays a regression
/// fixture — never a source of truth; completeness itself is owned by the
/// golden lists (RegistryGoldenListParityTests).
/// </summary>
public class Issue136BuiltinRegistryCoverageTests
{
    // --- Core MQL4/MQL5 functions missing from both registries (residue) ---
    private static readonly string[] CoreFunctionsBothDialects =
    {
        "StringInit", "EventKillTimer", "EventSetMillisecondTimer",
    };

    // --- MQL4-documented functions missing from the MQL4 registry ----------
    private static readonly string[] Mql4Functions =
    {
        "EventSetTimer", "iBars", "GlobalVariableSetOnCondition",
        "IsConnected", "WindowFind", "TimeToStr", "StrToTime",
    };

    // --- Predefined variables (build 600+) missing from the MQL4 registry --
    private static readonly string[] Mql4UnderscoreVariables =
    {
        "_LastError", "_StopFlag", "_UninitReason", "_AppliedTo",
    };

    // --- Stdlib enum constants flagged on the real corpus -------------------
    private static readonly string[] StdlibEnumConstants =
    {
        "REASON_TEMPLATE", "REASON_CHARTCHANGE", "REASON_REMOVE",
        "REASON_PROGRAM", "REASON_ACCOUNT", "REASON_INITFAILED",
        "REASON_CLOSE", "REASON_PARAMETERS",
        "TERMINAL_SCREEN_DPI",
        "FILE_BIN", "FILE_SHARE_READ", "FILE_SHARE_WRITE",
    };

    [Theory]
    [MemberData(nameof(CoreFunctionNames))]
    public void CoreFunctions_AreRegisteredInBothDialects(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinFunction(name), $"MQL4 registry is missing core function '{name}'");
        Assert.True(new Mql5Builtins().IsBuiltinFunction(name), $"MQL5 registry is missing core function '{name}'");
    }

    [Theory]
    [MemberData(nameof(Mql4FunctionNames))]
    public void Mql4DocumentedFunctions_AreRegistered(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinFunction(name), $"MQL4 registry is missing documented function '{name}'");
    }

    [Theory]
    [MemberData(nameof(UnderscoreVariableNames))]
    public void Mql4UnderscoreVariables_AreRegistered(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinVariable(name), $"MQL4 registry is missing predefined variable '{name}'");
    }

    [Theory]
    [MemberData(nameof(EnumConstantNames))]
    public void StdlibEnumConstants_AreRegisteredInBothDialects(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinVariable(name), $"MQL4 registry is missing enum constant '{name}'");
        Assert.True(new Mql5Builtins().IsBuiltinVariable(name), $"MQL5 registry is missing enum constant '{name}'");
    }

    // --- Post-rc.2 real-corpus enrichment pins (mql4-reference-enrichment-rc2)
    // --- documented MQL4 API no incident had ever reported ----------------
    public static TheoryData<string> ReferenceEnrichmentNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[]
        {
        "NormalizeDouble", "MathRound", "MathFloor", "GetTickCount",
        "ObjectCreate", "ObjectDelete", "ObjectSetInteger", "ObjectSetString",
        "ObjectName", "ObjectsDeleteAll", "Hour", "DoubleToStr",
        "SymbolInfoTick", "SymbolInfoString", "SymbolsTotal", "SymbolName",
        "SymbolSelect", "OrdersHistoryTotal", "StringCompare",
        "clrWhite", "clrBlack", "clrNONE",
        "SYMBOL_ASK", "SYMBOL_BID", "SYMBOL_POINT", "SYMBOL_DIGITS",
        "SYMBOL_TRADE_TICK_SIZE", "SYMBOL_TRADE_CONTRACT_SIZE",
        "MODE_TRADES", "MODE_HISTORY", "MODE_TICKVALUE",
        "SELECT_BY_POS", "SELECT_BY_TICKET",
        "CHARTEVENT_OBJECT_CLICK", "CHARTEVENT_KEYDOWN", "CHARTEVENT_CHART_CHANGE",
        "MB_OK", "MB_YESNO", "IDYES",
        "CORNER_RIGHT_LOWER", "ALIGN_CENTER", "ANCHOR_LEFT_LOWER", "BORDER_FLAT",
        "OBJ_RECTANGLE_LABEL",
        "ACCOUNT_STOPOUT_MODE_PERCENT", "MQL_TRADE_ALLOWED",
        "REASON_CHARTCLOSE", "TERMINAL_CONNECTED", "MqlTick",
        })
        {
            data.Add(name);
        }
        return data;
    }

    // --- Issue #152 real-corpus enrichment: documented MQL4 functions ------
    // --- flagged as unresolved on the Botlidator/Optimator/Ducibus corpus -
    private static readonly string[] Mql4CorpusEnrichmentFunctions =
    {
        // deprecated MQL4 aliases (REQ-MA-02 admissions, mql5Mapping present)
        "CharToStr", "StrToInteger",
        // file operations
        "FileDelete", "FileIsExist",
        // tester + date conversion
        "TesterStatistics", "TimeToStruct",
        // arrays + string conversions
        "ArrayFree", "ShortArrayToString", "StringToShortArray",
        // objects + chart operations + series + common + window + text
        "ObjectMove", "ObjectGetValueByShift",
        "ChartClose", "ChartFirst", "ChartNext", "ChartOpen", "ChartXYToTimePrice",
        "CopyTime", "EventChartCustom", "HideTestIndicators",
        "TextGetSize", "TextSetFont",
        "WindowBarsPerChart", "WindowFirstVisibleBar", "WindowHandle",
        "WindowPriceMax", "WindowPriceMin",
    };

    // --- Issue #152 real-corpus enrichment: documented MQL4 constants and --
    // --- predefined macros flagged as unresolved on the same corpus -------
    // (MQL4-only assertions: several of these are absent from mql5.json by
    // design — the corpus is MQL4 and the MQL5 side is a separate dataset.)
    private static readonly string[] Mql4CorpusEnrichmentConstants =
    {
        // ENUM_STATISTICS (TesterStatistics)
        "STAT_INITIAL_DEPOSIT", "STAT_PROFIT", "STAT_GROSS_PROFIT", "STAT_GROSS_LOSS",
        "STAT_EQUITY_DD", "STAT_EQUITYDD_PERCENT", "STAT_PROFIT_FACTOR",
        "STAT_TRADES", "STAT_PROFIT_TRADES", "STAT_LOSS_TRADES",
        // file open flags + seek origin
        "FILE_CSV", "FILE_READ", "FILE_WRITE", "SEEK_END",
        // TimeToString flags
        "TIME_DATE", "TIME_SECONDS",
        // web colors
        "Aqua", "Blue", "Red", "Yellow", "Lime", "HotPink", "DeepSkyBlue", "White",
        // chart / object properties + named constant
        "CHARTS_MAX", "CHART_COLOR_BACKGROUND", "CHART_SHOW_TRADE_LEVELS", "OBJPROP_RAY_RIGHT",
        // predefined macro substitution
        "__FUNCTION__",
    };

    [Theory]
    [MemberData(nameof(ReferenceEnrichmentNames))]
    public void Mql4ReferenceEnrichedNames_AreRegistered(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltin(name), $"MQL4 registry is missing documented name '{name}'");
    }

    // --- MQL5 stdlib enum constants missing from the MQL5 registry ---------
    // --- (StratoX incident 2026-10-18: 5070 false positives blocked edits) -
    // --- One-to-two representatives per documented family; completeness ---
    // --- itself is owned by data/builtins/mql5.json (parity tests). ------
    private static readonly string[] Mql5EnumConstants =
    {
        // ENUM_SYMBOL_INFO_* (SymbolInfoInteger/Double/String)
        "SYMBOL_BID", "SYMBOL_ASK", "SYMBOL_POINT", "SYMBOL_DIGITS",
        "SYMBOL_TRADE_TICK_SIZE", "SYMBOL_TRADE_TICK_VALUE",
        "SYMBOL_VOLUME_MAX", "SYMBOL_VOLUME_LIMIT", "SYMBOL_VOLUME_STEP",
        // ENUM_POSITION_*
        "POSITION_VOLUME", "POSITION_TYPE_BUY", "POSITION_REASON_EXPERT",
        // ENUM_ORDER_* / ENUM_DEAL_*
        "ORDER_TYPE_BUY", "ORDER_TIME_GTC", "ORDER_REASON_EXPERT",
        "DEAL_ENTRY_IN", "DEAL_REASON_EXPERT",
        // ENUM_TRADE_REQUEST_* / ENUM_ACCOUNT_*
        "TRADE_ACTION_DEAL", "TRADE_ACTION_PENDING",
        "ACCOUNT_MARGIN_MODE_RETAIL_HEDGING", "ACCOUNT_TRADE_EXPERT",
        // ENUM_CHART_* / ENUM_OBJECT_PROPERTY_*
        "CHART_SHOW_GRID", "CHART_FOREGROUND", "OBJPROP_XSIZE", "OBJPROP_TIME",
        // ENUM_FILE_* / ENUM_SERIES_INFO_* / ENUM_TERMINAL_*
        "FILE_ANSI", "FILE_COMMON", "SERIES_BARS_COUNT", "TERMINAL_CONNECTED",
        // ENUM_TIMEFRAMES / ENUM_INDICATOR_* / ENUM_MQL_INFO_* / errors / colors
        "PERIOD_M1", "PERIOD_H1", "INDICATOR_DIGITS", "MQL_TESTER",
        "ERR_SUCCESS", "clrMediumSeaGreen",
    };

    [Theory]
    [MemberData(nameof(CorpusEnrichmentFunctionNames))]
    public void Mql4CorpusEnrichedFunctions_AreRegistered(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinFunction(name), $"MQL4 registry is missing documented function '{name}' (issue #152 corpus)");
    }

    [Theory]
    [MemberData(nameof(CorpusEnrichmentConstantNames))]
    public void Mql4CorpusEnrichedConstants_AreRegistered(string name)
    {
        Assert.True(Mql4Builtins.IsBuiltinVariable(name), $"MQL4 registry is missing documented constant '{name}' (issue #152 corpus)");
    }

    [Theory]
    [MemberData(nameof(Mql5EnumConstantNames))]
    public void Mql5EnumConstants_AreRegistered(string name)
    {
        Assert.True(new Mql5Builtins().IsBuiltinVariable(name), $"MQL5 registry is missing documented enum constant '{name}'");
    }

    // --- MQL5 documented stdlib functions missing from the golden list -----
    // --- (2026-10-18 audit: only 155 of ~760 documented functions were -----
    // --- registered; same 5070 false-positive class as the constants). -----
    private static readonly string[] Mql5DocumentedFunctions =
    {
        // indicators
        "iCustom", "iMA", "iRSI", "iMACD", "iAlligator",
        // chart / objects
        "ChartOpen", "ChartSetInteger", "ChartGetInteger", "ChartScreenShot",
        "ObjectCreate", "ObjectSetInteger",
        // series / data access
        "CopyBuffer", "CopyRates", "CopyTicks", "SeriesInfoInteger",
        // trade
        "OrderCalcMargin", "OrderSendAsync", "OrderCheck", "PositionSelectByTicket",
        "HistorySelect", "HistoryDealsTotal",
        // files / strings / math / common
        "FileReadDouble", "FileLoad", "StringAdd", "MathSrand", "ZeroMemory",
        "GetTickCount", "EventChartCustom",
        // calendar / socket / database / onnx
        "CalendarValueHistory", "SocketCreate", "DatabaseOpen", "OnnxRun",
    };

    [Theory]
    [MemberData(nameof(Mql5DocumentedFunctionNames))]
    public void Mql5DocumentedFunctions_AreRegistered(string name)
    {
        Assert.True(new Mql5Builtins().IsBuiltinFunction(name), $"MQL5 registry is missing documented function '{name}'");
    }

    // --- Issue #166: MQL5 golden-list description enrichment ---------------
    // --- representative description pins: the golden list is the source of --
    // --- truth; these names must carry real (non-placeholder) hover text. --
    private static readonly string[] Mql5DescriptionPins =
    {
        // ENUM_SYMBOL_INFO_* (SymbolInfoInteger/Double/String)
        "SYMBOL_BID", "SYMBOL_TRADE_TICK_SIZE",
        // ENUM_POSITION_*
        "POSITION_VOLUME", "POSITION_REASON_EXPERT",
        // ENUM_ORDER_*
        "ORDER_TIME_GTC", "ORDER_REASON_EXPERT",
        // ENUM_ACCOUNT_*
        "ACCOUNT_MARGIN_MODE_RETAIL_HEDGING", "ACCOUNT_TRADE_EXPERT",
        // ENUM_MQL_INFO_*
        "MQL_TESTER", "MQL_DEBUG",
        // runtime errors
        "ERR_SUCCESS", "ERR_INTERNAL_ERROR",
        // ENUM_TERMINAL_* / ENUM_SERIES_INFO_*
        "TERMINAL_CONNECTED", "SERIES_BARS_COUNT",
        // ENUM_* type names (documented enumeration sections)
        "ENUM_ACCOUNT_MARGIN_MODE", "ENUM_BOOK_TYPE", "ENUM_APPLIED_VOLUME",
        // web colors (synthetic deterministic descriptions)
        "clrRed", "clrBlue",
    };

    // --- Issue #166: MQL5 documented functions must carry a real description --
    // --- (prose under the page heading, before the captured signature) ------
    private static readonly string[] Mql5FunctionDescriptionPins =
    {
        // indicators
        "iCustom",
        // trade
        "OrderCalcMargin", "HistorySelect",
        // chart / objects
        "ChartScreenShot",
        // series / data access
        "CopyTicks",
        // files / strings / math / common
        "FileReadDouble", "MathSrand",
        // calendar / socket / database / onnx
        "SocketCreate", "DatabaseOpen", "OnnxRun",
    };

    [Theory]
    [MemberData(nameof(Mql5DescriptionPinNames))]
    public void Mql5Descriptions_AreCaptured(string name)
    {
        var description = new Mql5Builtins().GetBuiltinVariableDescription(name);
        Assert.False(string.IsNullOrWhiteSpace(description),
            $"MQL5 golden list has no description for '{name}'");
        Assert.NotEqual("(description not yet captured)", description);
    }

    [Theory]
    [MemberData(nameof(Mql5FunctionDescriptionPinNames))]
    public void Mql5FunctionDescriptions_AreCaptured(string name)
    {
        var descriptions = LoadMql5GoldenFunctionDescriptions();
        Assert.True(descriptions.TryGetValue(name, out var description)
                    && !string.IsNullOrWhiteSpace(description),
            $"MQL5 golden list has no description for function '{name}'");
        Assert.NotEqual("(description not yet captured)", description);
    }

    /// <summary>
    /// Issue #166 coverage gate: at least 95% of the 1,644 baseline
    /// placeholder descriptions are replaced with real text, and virtually
    /// every documented function carries a description. Unrecoverable
    /// entries keep their placeholder and are reported by the extraction
    /// evidence instead of being silently dropped.
    /// </summary>
    [Fact]
    public void Mql5GoldenList_DescriptionCoverage_MeetsTarget()
    {
        var (functions, variables) = LoadMql5Golden();
        const int baselinePlaceholders = 1644;
        const int baselineFunctionsWithoutDescription = 816;

        var placeholders = variables.Values
            .Count(v => v == "(description not yet captured)");
        var functionsWithoutDescription = functions.Values
            .Count(v => string.IsNullOrWhiteSpace(v)
                        || v == "(description not yet captured)");

        Assert.True(placeholders <= (int)(baselinePlaceholders * 0.05),
            $"MQL5 golden list still has {placeholders} placeholder descriptions " +
            $"(baseline {baselinePlaceholders}); target is >= 95% captured.");
        Assert.True(functionsWithoutDescription <= 15,
            $"MQL5 golden list has {functionsWithoutDescription} functions without " +
            $"a description (baseline {baselineFunctionsWithoutDescription}).");
        // The residual functions are MQL4-legacy names (AccountBalance,
        // OrderClose, FileRead, ...) that have no page in the MQL5
        // Reference PDF; they are reported with evidence in
        // tools/generate-golden-lists/data/mql5-descriptions.json.
    }

    public static TheoryData<string> CoreFunctionNames() => ToTheoryData(CoreFunctionsBothDialects);
    public static TheoryData<string> Mql4FunctionNames() => ToTheoryData(Mql4Functions);
    public static TheoryData<string> UnderscoreVariableNames() => ToTheoryData(Mql4UnderscoreVariables);
    public static TheoryData<string> EnumConstantNames() => ToTheoryData(StdlibEnumConstants);
    public static TheoryData<string> CorpusEnrichmentFunctionNames() => ToTheoryData(Mql4CorpusEnrichmentFunctions);
    public static TheoryData<string> CorpusEnrichmentConstantNames() => ToTheoryData(Mql4CorpusEnrichmentConstants);
    public static TheoryData<string> Mql5EnumConstantNames() => ToTheoryData(Mql5EnumConstants);
    public static TheoryData<string> Mql5DocumentedFunctionNames() => ToTheoryData(Mql5DocumentedFunctions);
    public static TheoryData<string> Mql5DescriptionPinNames() => ToTheoryData(Mql5DescriptionPins);
    public static TheoryData<string> Mql5FunctionDescriptionPinNames() => ToTheoryData(Mql5FunctionDescriptionPins);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data", "builtins")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static (Dictionary<string, string> Functions, Dictionary<string, string> Variables) LoadMql5Golden()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "builtins", "mql5.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var functions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.GetProperty("functions").EnumerateObject())
        {
            functions[entry.Name] = entry.Value.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? string.Empty
                : string.Empty;
        }
        foreach (var entry in doc.RootElement.GetProperty("variables").EnumerateObject())
        {
            variables[entry.Name] = entry.Value.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? string.Empty
                : "(description not yet captured)";
        }
        return (functions, variables);
    }

    private static Dictionary<string, string> LoadMql5GoldenFunctionDescriptions()
    {
        var path = Path.Combine(FindRepoRoot(), "data", "builtins", "mql5.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in doc.RootElement.GetProperty("functions").EnumerateObject())
        {
            result[entry.Name] = entry.Value.TryGetProperty("description", out var desc)
                ? desc.GetString() ?? string.Empty
                : string.Empty;
        }
        return result;
    }

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
