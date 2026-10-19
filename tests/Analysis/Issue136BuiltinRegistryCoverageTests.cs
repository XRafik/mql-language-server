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

    public static TheoryData<string> CoreFunctionNames() => ToTheoryData(CoreFunctionsBothDialects);
    public static TheoryData<string> Mql4FunctionNames() => ToTheoryData(Mql4Functions);
    public static TheoryData<string> UnderscoreVariableNames() => ToTheoryData(Mql4UnderscoreVariables);
    public static TheoryData<string> EnumConstantNames() => ToTheoryData(StdlibEnumConstants);
    public static TheoryData<string> CorpusEnrichmentFunctionNames() => ToTheoryData(Mql4CorpusEnrichmentFunctions);
    public static TheoryData<string> CorpusEnrichmentConstantNames() => ToTheoryData(Mql4CorpusEnrichmentConstants);
    public static TheoryData<string> Mql5EnumConstantNames() => ToTheoryData(Mql5EnumConstants);

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
