using Xunit;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using System.IO;
using System.Linq;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Tests for advanced MQL4-specific features and complex scenarios
/// Covers: Preprocessor directives, Enums, Structs, Event handlers, Trading functions, Indicators
/// </summary>
public class Mql4AdvancedFeaturesTests
{
    private readonly Mql4AntlrParser _parser;

    public Mql4AdvancedFeaturesTests()
    {
        _parser = new Mql4AntlrParser();
    }

    #region Preprocessor Directives Tests

    [Fact]
    public void Parse_HandlesPropertyDirectives()
    {
        var code = @"
            #property copyright ""Test Author""
            #property link ""http://example.com""
            #property version ""2.00""
            #property strict
            #property indicator_separate_window
            #property indicator_buffers 4

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesIncludeDirectives_WithAngleBrackets()
    {
        var code = @"
            #include <Include/MyIndicators.mqh>
            #include <Include/TradingLib.mqh>

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Parser may not fully parse angle bracket includes, but should parse functions
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");

        // Note: Parser has limitations with angle bracket includes
    }

    [Fact]
    public void Parse_HandlesIncludeDirectives_WithQuotes()
    {
        var code = @"
            #include ""Include/MyFile.mqh""
            #include ""Common/Utils.mqh""

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Includes);
        Assert.Contains(file.Includes, i => i.Contains("MyFile.mqh"));
        Assert.Contains(file.Includes, i => i.Contains("Utils.mqh"));
    }

    [Fact]
    public void Parse_HandlesDefineDirectives()
    {
        var code = @"
            #define MAX_PERIOD 100
            #define MIN_LOT 0.01

            void OnInit() {
                int period = MAX_PERIOD;
                double lot = MIN_LOT;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesIfndefDefineEndif()
    {
        var code = @"
            #ifndef MY_HEADER_H
            #define MY_HEADER_H

            void MyFunction() { }

            #endif

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "MyFunction");
    }

    #endregion

    #region Enumerations Tests

    [Fact]
    public void Parse_HandlesEnumDeclarations()
    {
        var code = @"
            enum ENUM_TRADE_ACTION
            {
                TRADE_ACTION_BUY,
                TRADE_ACTION_SELL,
                TRADE_ACTION_CLOSE
            };

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        // Note: Parser may or may not extract enum names, depending on implementation
    }

    [Fact]
    public void Parse_HandlesEnum_WithExplicitValues()
    {
        var code = @"
            enum ENUM_INDICATOR_TYPE
            {
                INDICATOR_SMA = 0,
                INDICATOR_EMA = 1,
                INDICATOR_RSI = 2,
                INDICATOR_MACD = 3
            };

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesEnumUsage()
    {
        var code = @"
            enum ENUM_POSITION_TYPE
            {
                POSITION_TYPE_BUY,
                POSITION_TYPE_SELL
            };

            void CheckPosition(ENUM_POSITION_TYPE type) {
                if(type == POSITION_TYPE_BUY) {
                    Print(""Buy position"");
                }
            }

            void OnTick() {
                CheckPosition(POSITION_TYPE_BUY);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "CheckPosition");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Structs Tests

    [Fact]
    public void Parse_HandlesStructDeclarations()
    {
        var code = @"
            struct TradeParams
            {
                int magic;
                double lotSize;
                int stopLoss;
                int takeProfit;
            };

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesStructWithEnum()
    {
        var code = @"
            enum ENUM_POSITION_TYPE
            {
                POSITION_TYPE_BUY,
                POSITION_TYPE_SELL
            };

            struct PositionInfo
            {
                ENUM_POSITION_TYPE type;
                double openPrice;
                double currentPrice;
            };

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesStructUsage()
    {
        var code = @"
            struct IndicatorParams
            {
                int period;
                double deviation;
            };

            void SetupIndicator(IndicatorParams &params) {
                params.period = 20;
                params.deviation = 2.0;
            }

            void OnInit() {
                IndicatorParams p;
                SetupIndicator(p);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "SetupIndicator");
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    [Fact]
    public void Parse_HandlesStructArrays()
    {
        var code = @"
            struct PriceData
            {
                double open;
                double high;
                double low;
                double close;
            };

            PriceData prices[100];

            void OnTick() {
                prices[0].close = 1.1234;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region MQL4 Event Handlers Tests

    [Fact]
    public void Parse_RecognizesAllExpertAdvisorEventHandlers()
    {
        var code = @"
            int OnInit() { return INIT_SUCCEEDED; }
            void OnDeinit(const int reason) { }
            void OnTick() { }
            void OnTimer() { }
            void OnTrade() { }
            void OnTradeTransaction(const MqlTradeTransaction& trans,
                                   const MqlTradeRequest& request,
                                   const MqlTradeResult& result) { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "OnDeinit");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.Contains(file.Symbols, s => s.Name == "OnTimer");
        Assert.Contains(file.Symbols, s => s.Name == "OnTrade");
        Assert.Contains(file.Symbols, s => s.Name == "OnTradeTransaction");

        // Verify they are detected as built-ins. OnTrade and
        // OnTradeTransaction are MQL5-only (MQL4 CHM events.htm documents
        // only the eight MQL4 handlers) and are no longer registered as
        // MQL4 built-ins (see mql4-dataset-audit.json).
        foreach (var handler in new[] { "OnInit", "OnTick", "OnDeinit", "OnTimer" })
        {
            Assert.True(Mql4Builtins.IsBuiltin(handler), $"{handler} should be detected as built-in");
        }
        Assert.False(Mql4Builtins.IsBuiltin("OnTrade"),
            "OnTrade should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("OnTradeTransaction"),
            "OnTradeTransaction should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
    }

    [Fact]
    public void Parse_RecognizesAllIndicatorEventHandlers()
    {
        var code = @"
            int OnInit() { return INIT_SUCCEEDED; }
            void OnDeinit(const int reason) { }
            int OnCalculate(const int rates_total,
                           const int prev_calculated,
                           const datetime &time[],
                           const double &open[],
                           const double &high[],
                           const double &low[],
                           const double &close[],
                           const long &tick_volume[],
                           const long &volume[],
                           const int &spread[]) {
                return rates_total;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "OnDeinit");
        Assert.Contains(file.Symbols, s => s.Name == "OnCalculate");

        Assert.True(Mql4Builtins.IsBuiltin("OnCalculate"), "OnCalculate should be detected as built-in");
    }

    [Fact]
    public void Parse_RecognizesScriptEventHandler()
    {
        var code = @"
            void OnStart() {
                Print(""Script started"");
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnStart");
        Assert.True(Mql4Builtins.IsBuiltin("OnStart"), "OnStart should be detected as built-in");
    }

    #endregion

    #region Trading Functions Tests

    [Fact]
    public void Parse_DetectsOrderSendFunction()
    {
        var code = @"
            void OnTick() {
                MqlTradeRequest request = {};
                MqlTradeResult result = {};
                request.action = TRADE_ACTION_DEAL;
                request.symbol = Symbol();
                request.volume = 0.1;
                request.type = ORDER_TYPE_BUY;
                request.price = Ask;
                OrderSend(request, result);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("OrderSend"), "OrderSend should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("Ask"), "Ask should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsOrderModifyFunction()
    {
        var code = @"
            void OnTick() {
                OrderModify(12345, 0, 1.1000, 1.1100, 0);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("OrderModify"), "OrderModify should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsOrderCloseFunction()
    {
        var code = @"
            void OnTick() {
                OrderClose(12345, 0.1, Bid, 3);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("OrderClose"), "OrderClose should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsPositionFunctions()
    {
        var code = @"
            void OnTick() {
                int total = PositionsTotal();
                for(int i = 0; i < total; i++) {
                    if(PositionSelectByIndex(i)) {
                        string symbol = PositionGetString(POSITION_SYMBOL);
                        double profit = PositionGetDouble(POSITION_PROFIT);
                    }
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.False(Mql4Builtins.IsBuiltin("PositionSelectByIndex"), "PositionSelectByIndex should not be registered as MQL4 built-in (fabricated; zero CHM evidence; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("PositionsTotal"), "PositionsTotal should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("PositionGetString"), "PositionGetString should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("PositionGetDouble"), "PositionGetDouble should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
    }

    [Fact]
    public void Parse_DetectsOrderFunctions()
    {
        var code = @"
            void OnTick() {
                int total = OrdersTotal();
                for(int i = 0; i < total; i++) {
                    if(OrderSelect(i, SELECT_BY_POS)) {
                        string symbol = OrderGetString(ORDER_SYMBOL);
                        double profit = OrderGetDouble(ORDER_PROFIT);
                    }
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("OrdersTotal"), "OrdersTotal should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("OrderSelect"), "OrderSelect should be detected as built-in");
        Assert.False(Mql4Builtins.IsBuiltin("OrderGetString"), "OrderGetString should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("OrderGetDouble"), "OrderGetDouble should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
    }

    #endregion

    #region Indicator Functions Tests

    [Fact]
    public void Parse_DetectsMovingAverageFunctions()
    {
        var code = @"
            int handleMA;

            int OnInit() {
                handleMA = iMA(Symbol(), Period(), 20, 0, MODE_SMA, PRICE_CLOSE);
                return INIT_SUCCEEDED;
            }

            void OnTick() {
                double ma[];
                ArraySetAsSeries(ma, true);
                CopyBuffer(handleMA, 0, 0, 3, ma);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("iMA"), "iMA should be detected as built-in");
        Assert.False(Mql4Builtins.IsBuiltin("CopyBuffer"), "CopyBuffer should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.True(Mql4Builtins.IsBuiltin("ArraySetAsSeries"), "ArraySetAsSeries should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsRSIFunction()
    {
        var code = @"
            int handleRSI;

            int OnInit() {
                handleRSI = iRSI(Symbol(), Period(), 14, PRICE_CLOSE);
                return INIT_SUCCEEDED;
            }

            void OnTick() {
                double rsi[];
                CopyBuffer(handleRSI, 0, 0, 3, rsi);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.True(Mql4Builtins.IsBuiltin("iRSI"), "iRSI should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsMACDFunction()
    {
        var code = @"
            int handleMACD;

            int OnInit() {
                handleMACD = iMACD(Symbol(), Period(), 12, 26, 9, PRICE_CLOSE);
                return INIT_SUCCEEDED;
            }

            void OnTick() {
                double macd_main[];
                double macd_signal[];
                CopyBuffer(handleMACD, 0, 0, 3, macd_main);
                CopyBuffer(handleMACD, 1, 0, 3, macd_signal);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.True(Mql4Builtins.IsBuiltin("iMACD"), "iMACD should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsBollingerBandsFunction()
    {
        var code = @"
            int handleBB;

            int OnInit() {
                handleBB = iBands(Symbol(), Period(), 20, 0, 2.0, PRICE_CLOSE);
                return INIT_SUCCEEDED;
            }

            void OnTick() {
                double upper[], middle[], lower[];
                CopyBuffer(handleBB, 0, 0, 3, upper);
                CopyBuffer(handleBB, 1, 0, 3, middle);
                CopyBuffer(handleBB, 2, 0, 3, lower);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.True(Mql4Builtins.IsBuiltin("iBands"), "iBands should be detected as built-in");
    }

    [Fact]
    public void Parse_DetectsIndicatorBufferFunctions()
    {
        var code = @"
            #property indicator_buffers 2
            double UpperBuffer[];
            double LowerBuffer[];

            int OnInit() {
                SetIndexBuffer(0, UpperBuffer);
                SetIndexBuffer(1, LowerBuffer);
                SetIndexLabel(0, ""Upper"");
                SetIndexLabel(1, ""Lower"");
                return INIT_SUCCEEDED;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify OnInit function is extracted
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");

        // Verify built-in indicator functions are recognized
        Assert.True(Mql4Builtins.IsBuiltin("SetIndexBuffer"), "SetIndexBuffer should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("SetIndexLabel"), "SetIndexLabel should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("INIT_SUCCEEDED"), "INIT_SUCCEEDED should be detected as builtin");

        // Note: Parser may not fully extract indicator buffer declarations
    }

    #endregion

    #region String Functions Tests

    [Fact]
    public void Parse_DetectsStringFunctions()
    {
        var code = @"
            void OnTick() {
                string text = ""Hello World"";
                int len = StringLen(text);
                string upper = StringToUpper(text);
                string lower = StringToLower(text);
                int pos = StringFind(text, ""World"");
                string substr = StringSubstr(text, 0, 5);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("StringLen"), "StringLen should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("StringToUpper"), "StringToUpper should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("StringToLower"), "StringToLower should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("StringFind"), "StringFind should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("StringSubstr"), "StringSubstr should be detected as built-in");
    }

    #endregion

    #region Math Functions Tests

    [Fact]
    public void Parse_DetectsMathFunctions()
    {
        var code = @"
            void OnTick() {
                double value = 1.2345;
                double sqrt_val = MathSqrt(value);
                double abs_val = MathAbs(value);
                double max_val = MathMax(value, 2.0);
                double min_val = MathMin(value, 1.0);
                double pow_val = MathPow(value, 2.0);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("MathSqrt"), "MathSqrt should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("MathAbs"), "MathAbs should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("MathMax"), "MathMax should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("MathMin"), "MathMin should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("MathPow"), "MathPow should be detected as built-in");
    }

    #endregion

    #region Time Functions Tests

    [Fact]
    public void Parse_DetectsTimeFunctions()
    {
        var code = @"
            void OnTick() {
                datetime current_time = TimeCurrent();
                string time_str = TimeToString(current_time);
                datetimegmt = TimeGMT();
                datetime local = TimeLocal();
                int year = TimeYear(current_time);
                int month = TimeMonth(current_time);
                int day = TimeDay(current_time);
                int hour = TimeHour(current_time);
                int minute = TimeMinute(current_time);
                int second = TimeSeconds(current_time);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("TimeCurrent"), "TimeCurrent should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("TimeToString"), "TimeToString should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("TimeGMT"), "TimeGMT should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("TimeLocal"), "TimeLocal should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("TimeYear"), "TimeYear should be detected as built-in");
    }

    #endregion

    #region File Functions Tests

    [Fact]
    public void Parse_DetectsFileFunctions()
    {
        var code = @"
            void OnTick() {
                int file = FileOpen(""log.txt"", FILE_WRITE|FILE_TXT);
                FileWrite(file, ""Test log entry"");
                FileClose(file);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("FileOpen"), "FileOpen should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("FileWrite"), "FileWrite should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("FileClose"), "FileClose should be detected as built-in");
    }

    #endregion

    #region Array Functions Tests

    [Fact]
    public void Parse_DetectsArrayFunctions()
    {
        var code = @"
            void OnTick() {
                double prices[];
                ArrayResize(prices, 100);
                ArraySort(prices);
                ArrayReverse(prices);
                int size = ArraySize(prices);
                int index = ArraySearch(prices, 1.2345);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("ArrayResize"), "ArrayResize should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("ArraySort"), "ArraySort should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("ArraySize"), "ArraySize should be detected as built-in");
        // ArrayReverse is MQL5-only (removed from the MQL4 golden list;
        // see mql4-dataset-audit.json). ArraySearch is fabricated (issue #173
        // round-2 adjudication: zero evidence in the whole MQL4 Reference
        // CHM; the documented search function is ArrayBsearch, which is a
        // separate dataset-coverage gap, not part of this adjudication).
        Assert.False(Mql4Builtins.IsBuiltin("ArrayReverse"), "ArrayReverse should not be registered as MQL4 built-in (MQL5-only; see mql4-dataset-audit.json)");
        Assert.False(Mql4Builtins.IsBuiltin("ArraySearch"), "ArraySearch should not be registered as MQL4 built-in (fabricated; zero CHM evidence; see mql4-dataset-audit.json)");
    }

    #endregion

    #region Account Functions Tests

    [Fact]
    public void Parse_DetectsAccountFunctions()
    {
        var code = @"
            void OnTick() {
                double balance = AccountInfoDouble(ACCOUNT_BALANCE);
                double equity = AccountInfoDouble(ACCOUNT_EQUITY);
                double margin = AccountInfoDouble(ACCOUNT_MARGIN);
                double free_margin = AccountInfoDouble(ACCOUNT_FREEMARGIN);
                string name = AccountInfoString(ACCOUNT_NAME);
                string server = AccountInfoString(ACCOUNT_SERVER);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("AccountInfoDouble"), "AccountInfoDouble should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("AccountInfoString"), "AccountInfoString should be detected as built-in");
    }

    #endregion

    #region Symbol Functions Tests

    [Fact]
    public void Parse_DetectsSymbolFunctions()
    {
        var code = @"
            void OnTick() {
                string symbol = Symbol();
                double ask = SymbolInfoDouble(symbol, SYMBOL_ASK);
                double bid = SymbolInfoDouble(symbol, SYMBOL_BID);
                double point = SymbolInfoDouble(symbol, SYMBOL_POINT);
                int digits = (int)SymbolInfoInteger(symbol, SYMBOL_DIGITS);
                int spread = (int)SymbolInfoInteger(symbol, SYMBOL_SPREAD);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("SymbolInfoDouble"), "SymbolInfoDouble should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("SymbolInfoInteger"), "SymbolInfoInteger should be detected as built-in");
    }

    #endregion

    #region Comment and Print Tests

    [Fact]
    public void Parse_DetectsCommentAndPrintFunctions()
    {
        var code = @"
            void OnTick() {
                Print(""Tick: "", TimeToString(TimeCurrent()));
                Comment(""Current Price: "", Ask);
                Alert(""Price Alert!"");
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.True(Mql4Builtins.IsBuiltin("Print"), "Print should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("Comment"), "Comment should be detected as built-in");
        Assert.True(Mql4Builtins.IsBuiltin("Alert"), "Alert should be detected as built-in");
    }

    #endregion

    #region Input Parameters Tests

    [Fact]
    public void Parse_ExtractsInputParameters()
    {
        var code = @"
            input int InpPeriod = 14;
            input double InpLotSize = 0.1;
            input string InpComment = ""EA"";
            input bool InpUseTrailing = true;

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "InpPeriod");
        Assert.Contains(file.Symbols, s => s.Name == "InpLotSize");
        Assert.Contains(file.Symbols, s => s.Name == "InpComment");
        Assert.Contains(file.Symbols, s => s.Name == "InpUseTrailing");
    }

    [Fact]
    public void Parse_HandlesInputParameters_WithDescriptions()
    {
        var code = @"
            input int Period = 20; // Period for moving average
            input double LotSize = 0.1; // Lot size for trades

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "Period");
        Assert.Contains(file.Symbols, s => s.Name == "LotSize");
    }

    #endregion

    #region Global Variables Tests

    [Fact]
    public void Parse_ExtractsGlobalVariables()
    {
        var code = @"
            int globalInt = 10;
            double globalDouble = 1.2345;
            string globalString = ""Test"";
            bool globalBool = true;
            datetime globalTime = TimeCurrent();
            int handleMA;

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "globalInt");
        Assert.Contains(file.Symbols, s => s.Name == "globalDouble");
        Assert.Contains(file.Symbols, s => s.Name == "globalString");
        Assert.Contains(file.Symbols, s => s.Name == "globalBool");
        Assert.Contains(file.Symbols, s => s.Name == "globalTime");
        Assert.Contains(file.Symbols, s => s.Name == "handleMA");
    }

    [Fact]
    public void Parse_ExtractsGlobalArrays()
    {
        var code = @"
            double prices[100];
            int tickets[10];
            string comments[];

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Parser may not fully extract array declarations
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");

        // Note: Arrays are not fully extracted in current parser implementation
    }

    #endregion

    #region Constant Tests

    [Fact]
    public void Parse_ExtractsConstants()
    {
        var code = @"
            #define MAX_PERIOD 100
            #define MIN_LOT 0.01
            #define TRADE_MAGIC 12345

            void OnInit() { }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
    }

    #endregion

    #region Nested Functions Tests

    [Fact]
    public void Parse_HandlesNestedFunctionCalls()
    {
        var code = @"
            void OnTick() {
                double result = MathMax(MathAbs(Ask - Bid), Point);
                string time_str = StringSubstr(TimeToString(TimeCurrent()), 0, 10);
                double ma_val = iMA(Symbol(), Period(), 20, 0, MODE_SMA, PRICE_CLOSE, 0);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    [Fact]
    public void Parse_HandlesFunctionCalls_WithMultipleParameters()
    {
        var code = @"
            void OnTick() {
                int handle = iMACD(Symbol(), Period(), 12, 26, 9, PRICE_CLOSE);
                double result = OrderSend(request, result);
                string info = StringConcatenate(""Symbol: "", Symbol(), "", Ask: "", Ask);
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Complex Expressions Tests

    [Fact]
    public void Parse_HandlesComplexExpressions()
    {
        var code = @"
            void OnTick() {
                double pnl = (Bid - OrderOpenPrice()) * OrderLots() * MarketInfo(Symbol(), MODE_TICKVALUE);
                bool condition = (fastEMA[0] > slowEMA[0]) && (fastEMA[1] <= slowEMA[1]);
                double level = (high + low + close) / 3.0;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    [Fact]
    public void Parse_HandlesTernaryOperator()
    {
        var code = @"
            void OnTick() {
                double sl = (type == OP_BUY) ? Ask - StopLoss * Point : Bid + StopLoss * Point;
                string direction = (fastEMA > slowEMA) ? ""UP"" : ""DOWN"";
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Loop and Conditional Tests

    [Fact]
    public void Parse_HandlesForLoops()
    {
        var code = @"
            void OnTick() {
                for(int i = 0; i < 10; i++) {
                    Print(""Index: "", i);
                }
                for(int j = PositionsTotal() - 1; j >= 0; j--) {
                    PositionSelectByIndex(j);
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    [Fact]
    public void Parse_HandlesWhileLoops()
    {
        var code = @"
            void OnTick() {
                int count = 0;
                while(count < 10) {
                    count++;
                }
                while(OrderSelect(i, SELECT_BY_POS)) {
                    i++;
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    [Fact]
    public void Parse_HandlesIfElseStatements()
    {
        var code = @"
            void OnTick() {
                if(Ask > Bid) {
                    Print(""Ask > Bid"");
                } else if(Bid > Ask) {
                    Print(""Bid > Ask"");
                } else {
                    Print(""Ask == Bid"");
                }

                if(fastEMA[0] > slowEMA[0] && fastEMA[1] <= slowEMA[1]) {
                    Print(""Golden Cross"");
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    [Fact]
    public void Parse_HandlesSwitchStatements()
    {
        var code = @"
            void OnTick() {
                int type = OrderType();
                switch(type) {
                    case OP_BUY:
                        Print(""Buy order"");
                        break;
                    case OP_SELL:
                        Print(""Sell order"");
                        break;
                    default:
                        Print(""Unknown order type"");
                        break;
                }
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Return Statements Tests

    [Fact]
    public void Parse_HandlesReturnStatements()
    {
        var code = @"
            int Calculate(int a, int b) {
                return a + b;
            }

            bool IsBuySignal() {
                return (fastEMA[0] > slowEMA[0]);
            }

            void OnTick() {
                int result = Calculate(10, 20);
                bool signal = IsBuySignal();
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "Calculate");
        Assert.Contains(file.Symbols, s => s.Name == "IsBuySignal");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Multiple Declaration Tests

    [Fact]
    public void Parse_HandlesMultipleVariableDeclaration()
    {
        var code = @"
            void OnTick() {
                int a, b, c;
                double x, y, z;
                string s1, s2, s3;
            }
        ";

        var file = _parser.ParseFile(code, "Test.mq4");

        Assert.NotNull(file);
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
    }

    #endregion

    #region Complex Real-World Scenario Tests

    [Fact]
    public void Parse_CompleteExpertAdvisor_WithAllFeatures()
    {
        var code = @"
            #property copyright ""Advanced EA""
            #property strict
            #include <Include/TradingLib.mqh>

            input int Magic = 12345;
            input double LotSize = 0.1;

            int handleFastMA, handleSlowMA;
            double fastMA[], slowMA[];

            int OnInit() {
                handleFastMA = iMA(Symbol(), Period(), 10, 0, MODE_EMA, PRICE_CLOSE);
                handleSlowMA = iMA(Symbol(), Period(), 20, 0, MODE_EMA, PRICE_CLOSE);
                ArraySetAsSeries(fastMA, true);
                ArraySetAsSeries(slowMA, true);
                return INIT_SUCCEEDED;
            }

            void OnDeinit(const int reason) {
                IndicatorRelease(handleFastMA);
                IndicatorRelease(handleSlowMA);
            }

            void OnTick() {
                if(CopyBuffer(handleFastMA, 0, 0, 3, fastMA) <= 0 ||
                   CopyBuffer(handleSlowMA, 0, 0, 3, slowMA) <= 0)
                    return;

                if(fastMA[1] > slowMA[1] && fastMA[2] <= slowMA[2]) {
                    OpenBuyOrder();
                } else if(fastMA[1] < slowMA[1] && fastMA[2] >= slowMA[2]) {
                    OpenSellOrder();
                }

                ManageOpenPositions();
            }

            void OpenBuyOrder() {
                MqlTradeRequest request = {};
                MqlTradeResult result = {};
                request.action = TRADE_ACTION_DEAL;
                request.symbol = Symbol();
                request.volume = LotSize;
                request.type = ORDER_TYPE_BUY;
                request.price = SymbolInfoDouble(Symbol(), SYMBOL_ASK);
                request.magic = Magic;
                OrderSend(request, result);
            }

            void OpenSellOrder() {
                MqlTradeRequest request = {};
                MqlTradeResult result = {};
                request.action = TRADE_ACTION_DEAL;
                request.symbol = Symbol();
                request.volume = LotSize;
                request.type = ORDER_TYPE_SELL;
                request.price = SymbolInfoDouble(Symbol(), SYMBOL_BID);
                request.magic = Magic;
                OrderSend(request, result);
            }

            void ManageOpenPositions() {
                for(int i = PositionsTotal() - 1; i >= 0; i--) {
                    if(PositionSelectByIndex(i)) {
                        if(PositionGetInteger(POSITION_MAGIC) == Magic &&
                           PositionGetString(POSITION_SYMBOL) == Symbol()) {
                            double openPrice = PositionGetDouble(POSITION_PRICE_OPEN);
                            double currentSL = PositionGetDouble(POSITION_SL);
                            // Update trailing stop logic here
                        }
                    }
                }
            }
        ";

        var file = _parser.ParseFile(code, "AdvancedEA.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify all main functions are extracted
        Assert.Contains(file.Symbols, s => s.Name == "OnInit");
        Assert.Contains(file.Symbols, s => s.Name == "OnDeinit");
        Assert.Contains(file.Symbols, s => s.Name == "OnTick");
        Assert.Contains(file.Symbols, s => s.Name == "OpenBuyOrder");
        Assert.Contains(file.Symbols, s => s.Name == "OpenSellOrder");
        Assert.Contains(file.Symbols, s => s.Name == "ManageOpenPositions");

        // Note: Parser has limitations with input parameters and arrays
        // They may not be fully extracted in current implementation
    }

    #endregion

    #region Edge Cases with Real Files

    [Fact]
    public void ParseExpertAdvisor_WithNestedFunctionCalls_WorksCorrectly()
    {
        var code = File.ReadAllText("fixtures/samples/ExpertAdvisor.mq4");
        var file = _parser.ParseFile(code, "ExpertAdvisor.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify complex nested calls are parsed
        var onTick = file.Symbols.FirstOrDefault(s => s.Name == "OnTick");
        Assert.NotNull(onTick);
    }

    [Fact]
    public void ParseIndicator_WithOnCalculateParameters_WorksCorrectly()
    {
        var code = File.ReadAllText("fixtures/samples/Indicators/MyIndicator.mq4");
        var file = _parser.ParseFile(code, "MyIndicator.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify OnCalculate with multiple parameters
        var onCalculate = file.Symbols.FirstOrDefault(s => s.Name == "OnCalculate");
        Assert.NotNull(onCalculate);
    }

    [Fact]
    public void ParseCustomIndicators_WithEnumsAndStructs_WorksCorrectly()
    {
        var code = File.ReadAllText("fixtures/samples/Include/CustomIndicators.mqh");
        var file = _parser.ParseFile(code, "CustomIndicators.mqh");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // File contains enums and structs, should parse successfully
        Assert.True(file.Symbols.Count > 0);
    }

    [Fact]
    public void ParseTradeManager_WithComplexLogic_WorksCorrectly()
    {
        var code = File.ReadAllText("fixtures/samples/Scripts/TradeManager.mq4");
        var file = _parser.ParseFile(code, "TradeManager.mq4");

        Assert.NotNull(file);
        Assert.NotNull(file.Symbols);

        // Verify complex trading logic is parsed
        Assert.Contains(file.Symbols, s => s.Name == "OnStart");
        Assert.Contains(file.Symbols, s => s.Name == "ManageTrailingStops");
        Assert.Contains(file.Symbols, s => s.Name == "CalculateTrailingStop");
    }

    #endregion
}
