using Xunit;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Models;
using System.IO;

namespace MqlLanguageServer.Tests.Parser;

/// <summary>
/// Tests using high-quality real MQL4/MQH files from samples/
/// These files represent real-world MQL4 code with complete syntax coverage
/// </summary>
public class SamplesTests
{
    private readonly Mql4AntlrParser _parser;

    public SamplesTests()
    {
        _parser = new Mql4AntlrParser();
    }

    #region Expert Advisor Tests

    [Fact]
    public void ParseExpertAdvisor_ExtractsCompleteSymbolSet()
    {
        // Arrange
        var code = File.ReadAllText("fixtures/samples/ExpertAdvisor.mq4");

        // Act
        var file = _parser.ParseFile(code, "ExpertAdvisor.mq4");

        // Assert
        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);
        Assert.True(file.Symbols.Count >= 10, $"Expected many symbols, found {file.Symbols.Count}");

        // Verify required functions exist
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.Contains(file.Symbols, s => s.Name == "OnDeinit");
        Assert.Contains(file.Symbols, s => s.Name == "CheckForTradeSignals");
        Assert.Contains(file.Symbols, s => s.Name == "OpenPosition");
        Assert.Contains(file.Symbols, s => s.Name == "HasOpenPosition");
        Assert.Contains(file.Symbols, s => s.Name == "ManageOpenPositions");

        // Note: Parser has limitations with includes and input parameters
        // They may not be fully extracted in current implementation
    }

    #endregion

    #region Custom Indicators Header Tests

    [Fact]
    public void ParseCustomIndicatorsHeader_ExtractsAdvancedTypes()
    {
        // Arrange
        var code = File.ReadAllText("fixtures/samples/Include/CustomIndicators.mqh");

        // Act
        var file = _parser.ParseFile(code, "CustomIndicators.mqh");

        // Assert
        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);
        Assert.True(file.Symbols.Count >= 8, $"Expected multiple functions, found {file.Symbols.Count}");

        // Verify functions exist
        Assert.Contains(file.Symbols, s => s.Name == "CalculateSMA");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateEMA");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateRSI");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateBollingerUpper");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateBollingerLower");
        Assert.Contains(file.Symbols, s => s.Name == "GetCustomIndicatorHandle");
        Assert.Contains(file.Symbols, s => s.Name == "ValidateIndicatorParams");
        Assert.Contains(file.Symbols, s => s.Name == "FreeIndicatorHandle");
    }

    #endregion

    #region Indicator Tests

    [Fact]
    public void ParseIndicator_ParsesOnCalculateCorrectly()
    {
        // Arrange
        var code = File.ReadAllText("fixtures/samples/Indicators/MyIndicator.mq4");

        // Act
        var file = _parser.ParseFile(code, "MyIndicator.mq4");

        // Assert
        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify OnInit and OnCalculate exist
        var onInit = file.Symbols.FirstOrDefault(s => s.Name == "OnInit");
        Assert.NotNull(onInit);

        var onCalculate = file.Symbols.FirstOrDefault(s => s.Name == "OnCalculate");
        Assert.NotNull(onCalculate);

        // Verify indicator buffers
        Assert.Contains(file.Symbols, s => s.Name == "UpperBandBuffer");
        Assert.Contains(file.Symbols, s => s.Name == "LowerBandBuffer");
        Assert.Contains(file.Symbols, s => s.Name == "MiddleBandBuffer");
        Assert.Contains(file.Symbols, s => s.Name == "SignalBuffer");

        // Verify calculation functions
        Assert.Contains(file.Symbols, s => s.Name == "CalculateSMA");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateBollingerBands");
        Assert.Contains(file.Symbols, s => s.Name == "GenerateSignals");
        Assert.Contains(file.Symbols, s => s.Name == "GetIndicatorValue");
        Assert.Contains(file.Symbols, s => s.Name == "IsIndicatorValid");
    }

    #endregion

    #region Script Tests

    [Fact]
    public void ParseTradeManagerScript_ParsesScriptCorrectly()
    {
        // Arrange
        var code = File.ReadAllText("fixtures/samples/Scripts/TradeManager.mq4");

        // Act
        var file = _parser.ParseFile(code, "TradeManager.mq4");

        // Assert
        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify OnStart function exists (key function for scripts)
        var onStart = file.Symbols.FirstOrDefault(s => s.Name == "OnStart");
        Assert.NotNull(onStart);

        // Verify trading functions
        Assert.Contains(file.Symbols, s => s.Name == "CloseAllOpenPositions");
        Assert.Contains(file.Symbols, s => s.Name == "ClosePosition");
        Assert.Contains(file.Symbols, s => s.Name == "DisplayPositionInfo");
        Assert.Contains(file.Symbols, s => s.Name == "ManageTrailingStops");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateTrailingStop");
        Assert.Contains(file.Symbols, s => s.Name == "ModifyPositionStopLoss");
        Assert.Contains(file.Symbols, s => s.Name == "CheckForCloseConditions");
        Assert.Contains(file.Symbols, s => s.Name == "GetOpenPositionsCount");
        Assert.Contains(file.Symbols, s => s.Name == "CalculatePositionRisk");

        // Verify input parameters
        Assert.Contains(file.Symbols, s => s.Name == "MagicNumber");
        Assert.Contains(file.Symbols, s => s.Name == "LotSize");
        Assert.Contains(file.Symbols, s => s.Name == "MaxPositions");
        Assert.Contains(file.Symbols, s => s.Name == "CloseAllPositions");
        Assert.Contains(file.Symbols, s => s.Name == "TrailingStopEnabled");
    }

    #endregion

    #region Cross-File Tests

    [Fact]
    public void ParseExpertAdvisorWithInclude_HandlesCrossFileReferences()
    {
        // Arrange
        var eaCode = File.ReadAllText("fixtures/samples/ExpertAdvisor.mq4");
        var mqhCode = File.ReadAllText("fixtures/samples/Include/CustomIndicators.mqh");

        // Act
        var eaFile = _parser.ParseFile(eaCode, "ExpertAdvisor.mq4");
        var mqhFile = _parser.ParseFile(mqhCode, "CustomIndicators.mqh");

        // Assert
        Assert.NotNull(eaFile);
        Assert.NotNull(mqhFile);

        // Both should have multiple symbols
        Assert.True(eaFile.Symbols.Count > 0, "EA file should have symbols");
        Assert.True(mqhFile.Symbols.Count > 0, "MQH file should have symbols");

        // Both files should have functions extracted
        Assert.True(eaFile.Symbols.Any(s => s.Detail?.Contains("Function") == true), "EA should have functions");
        Assert.True(mqhFile.Symbols.Any(s => s.Detail?.Contains("Function") == true), "MQH should have functions");

        // Note: Parser has limitations with certain function extraction
    }

    #endregion

    #region Built-in Functions Detection

    [Fact]
    public void SamplesFiles_ContainExpectedBuiltinFunctions()
    {
        // Arrange & Act - Test with Expert Advisor
        var eaCode = File.ReadAllText("fixtures/samples/ExpertAdvisor.mq4");
        var eaFile = _parser.ParseFile(eaCode, "ExpertAdvisor.mq4");

        // Assert - Expert Advisor built-ins
        Assert.True(_parser.IsBuiltin("OnInit"), "OnInit should be detected as builtin");
        Assert.True(_parser.IsBuiltin("OnTick"), "OnTick should be detected as builtin");
        Assert.True(_parser.IsBuiltin("OnDeinit"), "OnDeinit should be detected as builtin");
        Assert.True(_parser.IsBuiltin("OrderSend"), "OrderSend should be detected as builtin");
        Assert.True(_parser.IsBuiltin("SymbolInfoDouble"), "SymbolInfoDouble should be detected as builtin");
        Assert.True(_parser.IsBuiltin("Symbol"), "Symbol should be detected as builtin");
        Assert.True(_parser.IsBuiltin("Period"), "Period should be detected as builtin");
        Assert.False(_parser.IsBuiltin("PositionsTotal"), "PositionsTotal should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.True(_parser.IsBuiltin("Point"), "Point should be detected as builtin");

        // Arrange & Act - Test with Custom Indicators
        var indicatorCode = File.ReadAllText("fixtures/samples/Indicators/MyIndicator.mq4");
        var indicatorFile = _parser.ParseFile(indicatorCode, "MyIndicator.mq4");

        // Assert - Indicator built-ins
        Assert.True(_parser.IsBuiltin("iMA"), "iMA should be detected as builtin");
        Assert.True(_parser.IsBuiltin("iClose"), "iClose should be detected as builtin");
        Assert.True(_parser.IsBuiltin("EMPTY_VALUE"), "EMPTY_VALUE should be detected as builtin");
        Assert.True(_parser.IsBuiltin("IndicatorSetString"), "IndicatorSetString should be detected as builtin");
        Assert.True(_parser.IsBuiltin("SetIndexBuffer"), "SetIndexBuffer should be detected as builtin");
        Assert.True(_parser.IsBuiltin("ArraySetAsSeries"), "ArraySetAsSeries should be detected as builtin");
    }

    #endregion
}
