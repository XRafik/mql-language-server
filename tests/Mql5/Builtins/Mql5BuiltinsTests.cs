using System;
using System.Collections.Generic;
using Xunit;

namespace MqlLanguageServer.Mql5.Builtins.Tests;

/// <summary>
/// Tests for MQL5 built-in registry.
/// </summary>
public class Mql5BuiltinsTests
{
    [Fact]
    public void Mql5Builtins_Contains_PositionGetSymbol_And_Point()
    {
        // RED: Mql5Builtins does not exist yet.
        var builtins = new Mql5Builtins();

        Assert.True(builtins.IsBuiltin("PositionGetSymbol"));
        Assert.True(builtins.IsBuiltin("_Point"));
        Assert.NotNull(builtins.GetBuiltinFunctionSignature("PositionGetSymbol"));
        Assert.NotNull(builtins.GetBuiltinVariableDescription("_Point"));
    }

    [Theory]
    [InlineData("OrderSend", true, "bool OrderSend(MqlTradeRequest& request, MqlTradeResult& result)")]
    [InlineData("CopyOpen", true, "int CopyOpen(string symbol, ENUM_TIMEFRAMES timeframe, int start_pos, int count, double& open[])")]
    [InlineData("OnTick", true, "void OnTick()")]
    [InlineData("TRADE_ACTION_DEAL", true, "Place a market order")]
    [InlineData("DefinitelyNotAnMql5Builtin", false, null)]
    public void Mql5Builtins_Resolves_Functions_Variables_And_NonBuiltins(string name, bool expectedBuiltin, string? expectedDetail)
    {
        var builtins = new Mql5Builtins();

        Assert.Equal(expectedBuiltin, builtins.IsBuiltin(name));

        if (expectedDetail == null)
        {
            Assert.Null(builtins.GetBuiltinFunctionSignature(name));
            Assert.Null(builtins.GetBuiltinVariableDescription(name));
        }
        else if (builtins.IsBuiltinFunction(name))
        {
            Assert.Equal(expectedDetail, builtins.GetBuiltinFunctionSignature(name));
        }
        else
        {
            Assert.Equal(expectedDetail, builtins.GetBuiltinVariableDescription(name));
        }
    }

    [Theory]
    [InlineData("PERIOD_M1")]
    [InlineData("PERIOD_H1")]
    [InlineData("PERIOD_H12")]
    [InlineData("PERIOD_D1")]
    [InlineData("PERIOD_W1")]
    [InlineData("PERIOD_MN1")]
    [InlineData("PRICE_CLOSE")]
    [InlineData("PRICE_WEIGHTED")]
    [InlineData("MODE_SMA")]
    [InlineData("MODE_LWMA")]
    [InlineData("STYLE_SOLID")]
    [InlineData("STYLE_DASHDOTDOT")]
    [InlineData("OBJ_VLINE")]
    [InlineData("OBJ_FIBO")]
    [InlineData("OBJPROP_COLOR")]
    [InlineData("OBJPROP_TIME")]
    [InlineData("CHART_SCALE")]
    [InlineData("CHART_WINDOWS_TOTAL")]
    [InlineData("INDICATOR_SHORTNAME")]
    [InlineData("INDICATOR_DATA")]
    [InlineData("DRAW_LINE")]
    [InlineData("DRAW_COLOR_CANDLES")]
    [InlineData("MODE_PLUSDI")]
    [InlineData("ORDER_TYPE_BUY_STOP_LIMIT")]
    [InlineData("ORDER_TYPE_SELL_STOP_LIMIT")]
    [InlineData("DEAL_ENTRY_IN")]
    [InlineData("DEAL_REASON_SL")]
    [InlineData("ENUM_TIMEFRAMES")]
    [InlineData("ENUM_ORDER_TYPE")]
    [InlineData("ENUM_TRADE_REQUEST_ACTIONS")]
    [InlineData("ENUM_OBJECT")]
    public void Mql5Builtins_Resolves_StdlibEnumConstants(string constant)
    {
        var builtins = new Mql5Builtins();

        Assert.True(builtins.IsBuiltinVariable(constant),
            $"{constant} should be registered as an MQL5 stdlib enum constant (issue #46)");
        Assert.False(string.IsNullOrWhiteSpace(builtins.GetBuiltinVariableDescription(constant)));
    }

    [Fact]
    public void Mql4OnlyIndicatorConstants_AreNotRegistered()
    {
        // MODE_MAIN/MODE_SIGNAL/MODE_UPPER/MODE_LOWER are MQL4 indicator
        // line constants (MQL5 documents MAIN_LINE/SIGNAL_LINE/UPPER_BAND/
        // LOWER_BAND instead); they were removed from the MQL5 golden list
        // by the MQL5-dataset-hygiene change (see
        // tools/generate-golden-lists/data/mql5-dataset-audit.json).
        var builtins = new Mql5Builtins();
        foreach (var name in new[] { "MODE_MAIN", "MODE_SIGNAL", "MODE_UPPER", "MODE_LOWER" })
        {
            Assert.False(builtins.IsBuiltinVariable(name),
                $"{name} should not be registered as MQL5 built-in (MQL4-only; see mql5-dataset-audit.json)");
        }
    }

    [Fact]
    public void Mql5Builtins_BuiltinVariables_WithStdlibEnumConstants_ShouldExceedThreshold()
    {
        // Issue #46 added ~285 enum constants to the MQL5 table; a threshold
        // (never an exact pin) guards against accidental table truncation.
        var builtins = new Mql5Builtins();

        Assert.True(builtins.BuiltInVariables.Count > 250,
            $"Expected > 250 built-in variables after issue #46 enrichment, found {builtins.BuiltInVariables.Count}");
    }
}
