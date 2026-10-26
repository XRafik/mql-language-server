#!/usr/bin/env python3
"""
Documentation-driven enrichment pass for data/builtins/mql4.json, triggered by
the rc.2 real-world corpus measurement (post-#136 follow-up): the golden lists
were seeded from the hand-curated registries, so documented API that no
incident ever reported was still missing (the exact failure mode #136 bans).

Every family below is enumerated from the official MQL4 reference
(docs.mql4.com): Conversion/Math/Common/Datetime functions, Object functions,
Market-info functions and their ENUM_* constants, web colors, MessageBox
constants, chart events, ENUM_BASE_CORNER / ENUM_ALIGN_MODE /
ENUM_ANCHOR_POINT / ENUM_BORDER_TYPE, ENUM_ACCOUNT_*, ENUM_MQL_INFO_*.
Section-level docUrls; per-name URLs are tracked as ongoing data work.

Existing entries are never overwritten. Usage: python3 apply_real_corpus_enrichment.py
"""

import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DATA = REPO / "data/builtins"

URL = {
    "convert": "https://docs.mql4.com/convert",
    "trading": "https://docs.mql4.com/trading",
    "math": "https://docs.mql4.com/math",
    "common": "https://docs.mql4.com/common",
    "datetime": "https://docs.mql4.com/dateandtime",
    "objects": "https://docs.mql4.com/objects",
    "market": "https://docs.mql4.com/marketinformation",
    "series": "https://docs.mql4.com/series",
    "strings": "https://docs.mql4.com/strings",
    "check": "https://docs.mql4.com/check",
    "colors": "https://docs.mql4.com/constants/webcolors",
    "chartevents": "https://docs.mql4.com/constants/chartconstants/chartevents",
    "basecorner": "https://docs.mql4.com/constants/objectconstants/enum_base_corner",
    "align": "https://docs.mql4.com/constants/objectconstants/enum_align_mode",
    "anchor": "https://docs.mql4.com/constants/objectconstants/enum_anchor",
    "border": "https://docs.mql4.com/constants/objectconstants/enum_border_type",
    "account": "https://docs.mql4.com/constants/accountconstants",
    "mqlinfo": "https://docs.mql4.com/constants/environment_state/mqlinfointeger",
    "uninit": "https://docs.mql4.com/constants/uninitialization_reasons",
    "terminal": "https://docs.mql4.com/constants/terminalconstants",
    "objecttypes": "https://docs.mql4.com/constants/objectconstants/enum_object",
}

WEB_COLORS = [
    "clrAliceBlue", "clrAntiqueWhite", "clrAqua", "clrAquamarine", "clrAzure",
    "clrBeige", "clrBisque", "clrBlack", "clrBlanchedAlmond", "clrBlue",
    "clrBlueViolet", "clrBrown", "clrBurlyWood", "clrCadetBlue",
    "clrChartreuse", "clrChocolate", "clrCoral", "clrCornflowerBlue",
    "clrCornsilk", "clrCrimson", "clrCyan", "clrDarkBlue", "clrDarkCyan",
    "clrDarkGoldenrod", "clrDarkGray", "clrDarkGreen", "clrDarkKhaki",
    "clrDarkMagenta", "clrDarkOliveGreen", "clrDarkOrange", "clrDarkOrchid",
    "clrDarkRed", "clrDarkSalmon", "clrDarkSeaGreen", "clrDarkSlateBlue",
    "clrDarkSlateGray", "clrDarkTurquoise", "clrDarkViolet", "clrDeepPink",
    "clrDeepSkyBlue", "clrDimGray", "clrDodgerBlue", "clrFireBrick",
    "clrFloralWhite", "clrForestGreen", "clrFuchsia", "clrGainsboro",
    "clrGhostWhite", "clrGold", "clrGoldenrod", "clrGray", "clrGreen",
    "clrGreenYellow", "clrHoneydew", "clrHotPink", "clrIndianRed",
    "clrIndigo", "clrIvory", "clrKhaki", "clrLavender", "clrLavenderBlush",
    "clrLawnGreen", "clrLemonChiffon", "clrLightBlue", "clrLightCoral",
    "clrLightCyan", "clrLightGoldenrodYellow", "clrLightGray",
    "clrLightGreen", "clrLightPink", "clrLightSalmon", "clrLightSeaGreen",
    "clrLightSkyBlue", "clrLightSlateGray", "clrLightSteelBlue",
    "clrLightYellow", "clrLime", "clrLimeGreen", "clrLinen", "clrMagenta",
    "clrMaroon", "clrMediumAquamarine", "clrMediumBlue", "clrMediumOrchid",
    "clrMediumPurple", "clrMediumSeaGreen", "clrMediumSlateBlue",
    "clrMediumSpringGreen", "clrMediumTurquoise", "clrMediumVioletRed",
    "clrMidnightBlue", "clrMintCream", "clrMistyRose", "clrMoccasin",
    "clrNavajoWhite", "clrNavy", "clrOldLace", "clrOlive", "clrOliveDrab",
    "clrOrange", "clrOrangeRed", "clrOrchid", "clrPaleGoldenrod",
    "clrPaleGreen", "clrPaleTurquoise", "clrPaleVioletRed", "clrPapayaWhip",
    "clrPeachPuff", "clrPeru", "clrPink", "clrPlum", "clrPowderBlue",
    "clrPurple", "clrRed", "clrRosyBrown", "clrRoyalBlue", "clrSaddleBrown",
    "clrSalmon", "clrSandyBrown", "clrSeaGreen", "clrSeaShell", "clrSienna",
    "clrSilver", "clrSkyBlue", "clrSlateBlue", "clrSlateGray", "clrSnow",
    "clrSpringGreen", "clrSteelBlue", "clrTan", "clrTeal", "clrThistle",
    "clrTomato", "clrTurquoise", "clrViolet", "clrWheat", "clrWhite",
    "clrWhiteSmoke", "clrYellow", "clrYellowGreen",
]

FUNCTIONS = {
    # Conversion
    "NormalizeDouble": ("double NormalizeDouble(double value, int digits)", URL["convert"]),
    "DoubleToStr": ("string DoubleToStr(double value, int digits)", URL["convert"]),
    "DoubleToString": ("string DoubleToString(double value, int digits)", URL["convert"]),
    "StrToDouble": ("double StrToDouble(string value)", URL["convert"]),
    "StringToDouble": ("double StringToDouble(string value)", URL["convert"]),
    # Math (full documented family)
    "MathAbs": ("double MathAbs(double value)", URL["math"]),
    "MathArccos": ("double MathArccos(double value)", URL["math"]),
    "MathArcsin": ("double MathArcsin(double value)", URL["math"]),
    "MathArctan": ("double MathArctan(double value)", URL["math"]),
    "MathArctan2": ("double MathArctan2(double y, double x)", URL["math"]),
    "MathCeil": ("double MathCeil(double value)", URL["math"]),
    "MathRound": ("double MathRound(double value)", URL["math"]),
    "MathCos": ("double MathCos(double value)", URL["math"]),
    "MathExp": ("double MathExp(double value)", URL["math"]),
    "MathFloor": ("double MathFloor(double value)", URL["math"]),
    "MathLog": ("double MathLog(double value)", URL["math"]),
    "MathLog10": ("double MathLog10(double value)", URL["math"]),
    "MathMax": ("double MathMax(double value1, double value2)", URL["math"]),
    "MathMin": ("double MathMin(double value1, double value2)", URL["math"]),
    "MathMod": ("double MathMod(double value1, double value2)", URL["math"]),
    "MathPow": ("double MathPow(double base, double exponent)", URL["math"]),
    "MathRand": ("int MathRand()", URL["math"]),
    "MathSrand": ("void MathSrand(int seed)", URL["math"]),
    "MathSin": ("double MathSin(double value)", URL["math"]),
    "MathSqrt": ("double MathSqrt(double value)", URL["math"]),
    "MathTan": ("double MathTan(double value)", URL["math"]),
    "MathIsValidNumber": ("bool MathIsValidNumber(double number)", URL["math"]),
    # Common
    "GetTickCount": ("uint GetTickCount()", URL["common"]),
    "MessageBox": ("int MessageBox(string text, string caption=NULL, int flags=EMPTY)", URL["common"]),
    "PlaySound": ("bool PlaySound(string filename)", URL["common"]),
    "SendFTP": ("bool SendFTP(string filename, string ftp_path=NULL)", URL["common"]),
    "SendMail": ("bool SendMail(string subject, string body)", URL["common"]),
    "SendNotification": ("bool SendNotification(string text)", URL["common"]),
    "Comment": ("void Comment(...)", URL["common"]),
    "MarketInfo": ("double MarketInfo(string symbol, int type)", URL["common"]),
    # Datetime (pre-600 convenience family, documented)
    "Year": ("int Year()", URL["datetime"]),
    "Month": ("int Month()", URL["datetime"]),
    "Day": ("int Day()", URL["datetime"]),
    "DayOfWeek": ("int DayOfWeek()", URL["datetime"]),
    "DayOfYear": ("int DayOfYear()", URL["datetime"]),
    "Hour": ("int Hour()", URL["datetime"]),
    "Minute": ("int Minute()", URL["datetime"]),
    "Seconds": ("int Seconds()", URL["datetime"]),
    "TimeGMT": ("datetime TimeGMT()", URL["datetime"]),
    "TimeDaylightSavings": ("int TimeDaylightSavings(datetime value)", URL["datetime"]),
    # Objects (full documented family)
    "ObjectCreate": ("bool ObjectCreate(long chart_id, string name, ENUM_OBJECT type, int sub_window, datetime time1, double price1)", URL["objects"]),
    "ObjectDelete": ("bool ObjectDelete(long chart_id, string name)", URL["objects"]),
    "ObjectGet": ("double ObjectGet(string name, int prop_id)", URL["objects"]),
    "ObjectGetDouble": ("double ObjectGetDouble(long chart_id, string name, ENUM_OBJECT_PROPERTY_DOUBLE prop_id, int prop_modifier=0)", URL["objects"]),
    "ObjectGetInteger": ("long ObjectGetInteger(long chart_id, string name, ENUM_OBJECT_PROPERTY_INTEGER prop_id, int prop_modifier=0)", URL["objects"]),
    "ObjectGetString": ("string ObjectGetString(long chart_id, string name, ENUM_OBJECT_PROPERTY_STRING prop_id, int prop_modifier=0)", URL["objects"]),
    "ObjectFind": ("int ObjectFind(long chart_id, string name)", URL["objects"]),
    "ObjectName": ("string ObjectName(long chart_id, int pos, int sub_window=-1, int type=-1)", URL["objects"]),
    "ObjectSet": ("bool ObjectSet(string name, int prop_id, double value)", URL["objects"]),
    "ObjectSetDouble": ("bool ObjectSetDouble(long chart_id, string name, ENUM_OBJECT_PROPERTY_DOUBLE prop_id, double prop_value)", URL["objects"]),
    "ObjectSetInteger": ("bool ObjectSetInteger(long chart_id, string name, ENUM_OBJECT_PROPERTY_INTEGER prop_id, long prop_value)", URL["objects"]),
    "ObjectSetString": ("bool ObjectSetString(long chart_id, string name, ENUM_OBJECT_PROPERTY_STRING prop_id, string prop_value)", URL["objects"]),
    "ObjectSetText": ("bool ObjectSetText(string name, string text, int font_size=10, string font=NULL, color text_color=clrNONE)", URL["objects"]),
    "ObjectsDeleteAll": ("int ObjectsDeleteAll(long chart_id, int sub_window=-1, int type=-1)", URL["objects"]),
    # Market information
    "SymbolInfoTick": ("bool SymbolInfoTick(string symbol, MqlTick &tick)", URL["market"]),
    "SymbolInfoString": ("string SymbolInfoString(string name, int prop_id)", URL["market"]),
    "SymbolInfoSessionQuote": ("bool SymbolInfoSessionQuote(string name, ENUM_DAY_OF_WEEK day_of_week, uint session_index, datetime &from, datetime &to)", URL["market"]),
    "SymbolInfoSessionOrder": ("bool SymbolInfoSessionOrder(string name, ENUM_DAY_OF_WEEK day_of_week, uint session_index, datetime &from, datetime &to)", URL["market"]),
    "SymbolsTotal": ("int SymbolsTotal(bool selected)", URL["market"]),
    "SymbolName": ("string SymbolName(int pos, bool selected)", URL["market"]),
    "SymbolSelect": ("bool SymbolSelect(string name, bool select)", URL["market"]),
    "OrdersHistoryTotal": ("int OrdersHistoryTotal()", URL["series"]),
    "OrderSelect": ("bool OrderSelect(int index, int select, int pool=MODE_TRADES)", URL["trading"]),
    # Strings / check
    "StringCompare": ("int StringCompare(const string &string1, const string &string2, bool case_sensitive=true)", URL["strings"]),
    "IsStopped": ("bool IsStopped()", URL["check"]),
    "TerminalInfoInteger": ("int TerminalInfoInteger(int property_id)", URL["terminal"]),
    "TerminalInfoString": ("string TerminalInfoString(int property_id)", URL["terminal"]),
    "AccountInfoInteger": ("long AccountInfoInteger(int property_id)", URL["account"]),
    "AccountInfoString": ("string AccountInfoString(int property_id)", URL["account"]),
    "AccountInfoDouble": ("double AccountInfoDouble(int property_id)", URL["account"]),
    "MQLInfoInteger": ("int MQLInfoInteger(int property_id)", URL["mqlinfo"]),
    "MQLInfoString": ("string MQLInfoString(int property_id)", URL["mqlinfo"]),
}

VARIABLES = {
    # Web colors (documented table)
    **{c: (f"Web color constant ({c[3:]})", URL["colors"]) for c in WEB_COLORS},
    "clrNONE": ("Special color constant: no color (transparent)", URL["colors"]),
    # ENUM_BASE_CORNER
    "CORNER_LEFT_UPPER": ("ENUM_BASE_CORNER: upper-left corner", URL["basecorner"]),
    "CORNER_LEFT_LOWER": ("ENUM_BASE_CORNER: lower-left corner", URL["basecorner"]),
    "CORNER_RIGHT_UPPER": ("ENUM_BASE_CORNER: upper-right corner", URL["basecorner"]),
    "CORNER_RIGHT_LOWER": ("ENUM_BASE_CORNER: lower-right corner", URL["basecorner"]),
    # ENUM_ALIGN_MODE
    "ALIGN_LEFT": ("ENUM_ALIGN_MODE: left alignment", URL["align"]),
    "ALIGN_CENTER": ("ENUM_ALIGN_MODE: center alignment", URL["align"]),
    "ALIGN_RIGHT": ("ENUM_ALIGN_MODE: right alignment", URL["align"]),
    # ENUM_ANCHOR_POINT
    "ANCHOR_LEFT_UPPER": ("ENUM_ANCHOR_POINT: upper-left anchor", URL["anchor"]),
    "ANCHOR_LEFT": ("ENUM_ANCHOR_POINT: left anchor", URL["anchor"]),
    "ANCHOR_LEFT_LOWER": ("ENUM_ANCHOR_POINT: lower-left anchor", URL["anchor"]),
    "ANCHOR_LOWER": ("ENUM_ANCHOR_POINT: lower anchor", URL["anchor"]),
    "ANCHOR_RIGHT_LOWER": ("ENUM_ANCHOR_POINT: lower-right anchor", URL["anchor"]),
    "ANCHOR_RIGHT": ("ENUM_ANCHOR_POINT: right anchor", URL["anchor"]),
    "ANCHOR_RIGHT_UPPER": ("ENUM_ANCHOR_POINT: upper-right anchor", URL["anchor"]),
    "ANCHOR_UPPER": ("ENUM_ANCHOR_POINT: upper anchor", URL["anchor"]),
    "ANCHOR_CENTER": ("ENUM_ANCHOR_POINT: center anchor", URL["anchor"]),
    # ENUM_BORDER_TYPE
    "BORDER_FLAT": ("ENUM_BORDER_TYPE: flat border", URL["border"]),
    "BORDER_RAISED": ("ENUM_BORDER_TYPE: raised border", URL["border"]),
    "BORDER_SUNKEN": ("ENUM_BORDER_TYPE: sunken border", URL["border"]),
    # Chart events
    "CHARTEVENT_KEYDOWN": ("Chart event: key press", URL["chartevents"]),
    "CHARTEVENT_MOUSE_MOVE": ("Chart event: mouse move", URL["chartevents"]),
    "CHARTEVENT_MOUSE_WHEEL": ("Chart event: mouse wheel", URL["chartevents"]),
    "CHARTEVENT_CLICK": ("Chart event: chart click", URL["chartevents"]),
    "CHARTEVENT_OBJECT_CLICK": ("Chart event: object click", URL["chartevents"]),
    "CHARTEVENT_OBJECT_DRAG": ("Chart event: object drag", URL["chartevents"]),
    "CHARTEVENT_OBJECT_ENDEDIT": ("Chart event: object text-edit end", URL["chartevents"]),
    "CHARTEVENT_OBJECT_CREATE": ("Chart event: object create", URL["chartevents"]),
    "CHARTEVENT_OBJECT_DELETE": ("Chart event: object delete", URL["chartevents"]),
    "CHARTEVENT_CHART_CHANGE": ("Chart event: chart size/property change", URL["chartevents"]),
    "CHARTEVENT_CUSTOM": ("Chart event: custom event base", URL["chartevents"]),
    "CHARTEVENT_CUSTOM_LAST": ("Chart event: last custom event id", URL["chartevents"]),
    # MessageBox / dialog constants
    "MB_OK": ("MessageBox flag: OK button", URL["common"]),
    "MB_OKCANCEL": ("MessageBox flag: OK and Cancel buttons", URL["common"]),
    "MB_YESNO": ("MessageBox flag: Yes and No buttons", URL["common"]),
    "MB_YESNOCANCEL": ("MessageBox flag: Yes, No and Cancel buttons", URL["common"]),
    "MB_ABORTRETRYIGNORE": ("MessageBox flag: Abort, Retry and Ignore buttons", URL["common"]),
    "MB_RETRYCANCEL": ("MessageBox flag: Retry and Cancel buttons", URL["common"]),
    "MB_CANCELTRYCONTINUE": ("MessageBox flag: Cancel, Try Again and Continue buttons", URL["common"]),
    "MB_ICONERROR": ("MessageBox flag: error icon", URL["common"]),
    "MB_ICONQUESTION": ("MessageBox flag: question icon", URL["common"]),
    "MB_ICONWARNING": ("MessageBox flag: warning icon", URL["common"]),
    "MB_ICONINFORMATION": ("MessageBox flag: information icon", URL["common"]),
    "MB_DEFBUTTON1": ("MessageBox flag: default button 1", URL["common"]),
    "MB_DEFBUTTON2": ("MessageBox flag: default button 2", URL["common"]),
    "MB_DEFBUTTON3": ("MessageBox flag: default button 3", URL["common"]),
    "MB_DEFBUTTON4": ("MessageBox flag: default button 4", URL["common"]),
    "IDOK": ("MessageBox result: OK button", URL["common"]),
    "IDCANCEL": ("MessageBox result: Cancel button", URL["common"]),
    "IDYES": ("MessageBox result: Yes button", URL["common"]),
    "IDNO": ("MessageBox result: No button", URL["common"]),
    "IDABORT": ("MessageBox result: Abort button", URL["common"]),
    "IDRETRY": ("MessageBox result: Retry button", URL["common"]),
    "IDIGNORE": ("MessageBox result: Ignore button", URL["common"]),
    "IDCLOSE": ("MessageBox result: Close button", URL["common"]),
    # OrderSelect / order pool / sorting (documented trading constants)
    "SELECT_BY_POS": ("OrderSelect flag: select by position", URL["trading"]),
    "SELECT_BY_TICKET": ("OrderSelect flag: select by ticket", URL["trading"]),
    "MODE_TRADES": ("Order pool: active market orders", URL["trading"]),
    "MODE_HISTORY": ("Order pool: closed/history orders", URL["trading"]),
    "MODE_ASCEND": ("Series/array sorting: ascending", URL["series"]),
    "MODE_DESCEND": ("Series/array sorting: descending", URL["series"]),
    # MarketInfo ENUM_SYMBOL_INFO_* / market constants
    "MODE_ASK": ("MarketInfo: current ask price", URL["market"]),
    "MODE_BID": ("MarketInfo: current bid price", URL["market"]),
    "MODE_POINT": ("MarketInfo: point size", URL["market"]),
    "MODE_DIGITS": ("MarketInfo: digits after the decimal point", URL["market"]),
    "MODE_SPREAD": ("MarketInfo: spread in points", URL["market"]),
    "MODE_STOPLEVEL": ("MarketInfo: minimum stop distance in points", URL["market"]),
    "MODE_FREEZELEVEL": ("MarketInfo: freeze distance in points", URL["market"]),
    "MODE_LOTSIZE": ("MarketInfo: contract lot size", URL["market"]),
    "MODE_TICKVALUE": ("MarketInfo: tick value", URL["market"]),
    "MODE_TICKSIZE": ("MarketInfo: tick size", URL["market"]),
    "MODE_MINLOT": ("MarketInfo: minimum lot size", URL["market"]),
    "MODE_LOTSTEP": ("MarketInfo: lot step", URL["market"]),
    "MODE_MAXLOT": ("MarketInfo: maximum lot size", URL["market"]),
    "MODE_SWAPLONG": ("MarketInfo: long swap value", URL["market"]),
    "MODE_SWAPSHORT": ("MarketInfo: short swap value", URL["market"]),
    "MODE_TRADEALLOWED": ("MarketInfo: trading allowed for the symbol", URL["market"]),
    "MODE_MARGININIT": ("MarketInfo: initial margin requirement", URL["market"]),
    "MODE_MARGINMAINTENANCE": ("MarketInfo: maintenance margin", URL["market"]),
    "MODE_MARGINHEDGED": ("MarketInfo: hedged margin", URL["market"]),
    "MODE_MARGINREQUIRED": ("MarketInfo: margin required for one lot", URL["market"]),
    "MODE_PROFITCALCMODE": ("MarketInfo: profit calculation mode", URL["market"]),
    "MODE_MARGINCALCMODE": ("MarketInfo: margin calculation mode", URL["market"]),
    # ENUM_SYMBOL_INFO_* (MQL4 build 600+ SymbolInfo* constants)
    "SYMBOL_SELECT": ("ENUM_SYMBOL_INFO_INTEGER: symbol is in Market Watch", URL["market"]),
    "SYMBOL_DIGITS": ("ENUM_SYMBOL_INFO_INTEGER: digits after the decimal point", URL["market"]),
    "SYMBOL_SPREAD": ("ENUM_SYMBOL_INFO_INTEGER: spread in points", URL["market"]),
    "SYMBOL_TRADE_CALC_MODE": ("ENUM_SYMBOL_INFO_INTEGER: profit calculation mode", URL["market"]),
    "SYMBOL_TRADE_MODE": ("ENUM_SYMBOL_INFO_INTEGER: trading mode", URL["market"]),
    "SYMBOL_TRADE_MODE_DISABLED": ("ENUM_SYMBOL_TRADE_MODE: trading disabled", URL["market"]),
    "SYMBOL_TRADE_MODE_LONGONLY": ("ENUM_SYMBOL_TRADE_MODE: long only", URL["market"]),
    "SYMBOL_TRADE_MODE_SHORTONLY": ("ENUM_SYMBOL_TRADE_MODE: short only", URL["market"]),
    "SYMBOL_TRADE_MODE_CLOSEONLY": ("ENUM_SYMBOL_TRADE_MODE: close only", URL["market"]),
    "SYMBOL_TRADE_MODE_FULL": ("ENUM_SYMBOL_TRADE_MODE: full trading", URL["market"]),
    "SYMBOL_TRADE_STOPS_LEVEL": ("ENUM_SYMBOL_INFO_INTEGER: minimum stop distance in points", URL["market"]),
    "SYMBOL_TRADE_FREEZE_LEVEL": ("ENUM_SYMBOL_INFO_INTEGER: freeze distance in points", URL["market"]),
    "SYMBOL_TRADE_EXEMODE": ("ENUM_SYMBOL_INFO_INTEGER: trade execution mode", URL["market"]),
    "SYMBOL_SWAP_MODE": ("ENUM_SYMBOL_INFO_INTEGER: swap calculation mode", URL["market"]),
    "SYMBOL_SWAP_ROLLOVER3DAYS": ("ENUM_SYMBOL_INFO_INTEGER: 3-day swap rollover day", URL["market"]),
    "SYMBOL_POINT": ("ENUM_SYMBOL_INFO_DOUBLE: point size", URL["market"]),
    "SYMBOL_TRADE_TICK_VALUE": ("ENUM_SYMBOL_INFO_DOUBLE: tick value", URL["market"]),
    "SYMBOL_TRADE_TICK_SIZE": ("ENUM_SYMBOL_INFO_DOUBLE: tick size", URL["market"]),
    "SYMBOL_TRADE_CONTRACT_SIZE": ("ENUM_SYMBOL_INFO_DOUBLE: contract size", URL["market"]),
    "SYMBOL_VOLUME_MIN": ("ENUM_SYMBOL_INFO_DOUBLE: minimum volume", URL["market"]),
    "SYMBOL_VOLUME_MAX": ("ENUM_SYMBOL_INFO_DOUBLE: maximum volume", URL["market"]),
    "SYMBOL_VOLUME_STEP": ("ENUM_SYMBOL_INFO_DOUBLE: volume step", URL["market"]),
    "SYMBOL_VOLUME_LIMIT": ("ENUM_SYMBOL_INFO_DOUBLE: max aggregate volume", URL["market"]),
    "SYMBOL_SWAP_LONG": ("ENUM_SYMBOL_INFO_DOUBLE: long swap", URL["market"]),
    "SYMBOL_SWAP_SHORT": ("ENUM_SYMBOL_INFO_DOUBLE: short swap", URL["market"]),
    "SYMBOL_MARGIN_INITIAL": ("ENUM_SYMBOL_INFO_DOUBLE: initial margin", URL["market"]),
    "SYMBOL_MARGIN_MAINTENANCE": ("ENUM_SYMBOL_INFO_DOUBLE: maintenance margin", URL["market"]),
    "SYMBOL_ASK": ("ENUM_SYMBOL_INFO_DOUBLE: current ask price", URL["market"]),
    "SYMBOL_BID": ("ENUM_SYMBOL_INFO_DOUBLE: current bid price", URL["market"]),
    "SYMBOL_LAST": ("ENUM_SYMBOL_INFO_DOUBLE: last deal price", URL["market"]),
    "SYMBOL_VOLUME": ("ENUM_SYMBOL_INFO_DOUBLE: last deal volume", URL["market"]),
    "SYMBOL_VOLUMEHIGH": ("ENUM_SYMBOL_INFO_DOUBLE: max volume of the day", URL["market"]),
    "SYMBOL_VOLUMELOW": ("ENUM_SYMBOL_INFO_DOUBLE: min volume of the day", URL["market"]),
    "SYMBOL_TIME": ("ENUM_SYMBOL_INFO_INTEGER: last quote time", URL["market"]),
    "SYMBOL_TIME_MSC": ("ENUM_SYMBOL_INFO_INTEGER: last quote time in ms", URL["market"]),
    "SYMBOL_CURRENCY_BASE": ("ENUM_SYMBOL_INFO_STRING: base currency", URL["market"]),
    "SYMBOL_CURRENCY_PROFIT": ("ENUM_SYMBOL_INFO_STRING: profit currency", URL["market"]),
    "SYMBOL_CURRENCY_MARGIN": ("ENUM_SYMBOL_INFO_STRING: margin currency", URL["market"]),
    # ENUM_ACCOUNT_* / account constants
    "ACCOUNT_LOGIN": ("ENUM_ACCOUNT_INFO_INTEGER: account number", URL["account"]),
    "ACCOUNT_TRADE_MODE": ("ENUM_ACCOUNT_INFO_INTEGER: account trade mode", URL["account"]),
    "ACCOUNT_TRADE_MODE_DEMO": ("ENUM_ACCOUNT_TRADE_MODE: demo account", URL["account"]),
    "ACCOUNT_TRADE_MODE_CONTEST": ("ENUM_ACCOUNT_TRADE_MODE: contest account", URL["account"]),
    "ACCOUNT_TRADE_MODE_REAL": ("ENUM_ACCOUNT_TRADE_MODE: real account", URL["account"]),
    "ACCOUNT_LEVERAGE": ("ENUM_ACCOUNT_INFO_INTEGER: leverage", URL["account"]),
    "ACCOUNT_LIMIT_ORDERS": ("ENUM_ACCOUNT_INFO_INTEGER: max active pending orders", URL["account"]),
    "ACCOUNT_MARGIN_MODE": ("ENUM_ACCOUNT_INFO_INTEGER: margin calculation mode", URL["account"]),
    "ACCOUNT_TRADE_ALLOWED": ("ENUM_ACCOUNT_INFO_INTEGER: trading allowed for the account", URL["account"]),
    "ACCOUNT_TRADE_EXPERT": ("ENUM_ACCOUNT_INFO_INTEGER: EA trading allowed", URL["account"]),
    "ACCOUNT_BALANCE": ("ENUM_ACCOUNT_INFO_DOUBLE: balance", URL["account"]),
    "ACCOUNT_CREDIT": ("ENUM_ACCOUNT_INFO_DOUBLE: credit", URL["account"]),
    "ACCOUNT_PROFIT": ("ENUM_ACCOUNT_INFO_DOUBLE: current profit", URL["account"]),
    "ACCOUNT_EQUITY": ("ENUM_ACCOUNT_INFO_DOUBLE: equity", URL["account"]),
    "ACCOUNT_MARGIN": ("ENUM_ACCOUNT_INFO_DOUBLE: used margin", URL["account"]),
    "ACCOUNT_MARGIN_FREE": ("ENUM_ACCOUNT_INFO_DOUBLE: free margin", URL["account"]),
    "ACCOUNT_MARGIN_LEVEL": ("ENUM_ACCOUNT_INFO_DOUBLE: margin level %", URL["account"]),
    "ACCOUNT_MARGIN_SO_CALL": ("ENUM_ACCOUNT_INFO_DOUBLE: margin call level", URL["account"]),
    "ACCOUNT_MARGIN_SO_SO": ("ENUM_ACCOUNT_INFO_DOUBLE: stop-out level", URL["account"]),
    "ACCOUNT_STOPOUT_MODE_PERCENT": ("ENUM_ACCOUNT_STOPOUT_MODE: stop-out as percentage", URL["account"]),
    "ACCOUNT_STOPOUT_MODE_MONEY": ("ENUM_ACCOUNT_STOPOUT_MODE: stop-out as money", URL["account"]),
    "ACCOUNT_NAME": ("ENUM_ACCOUNT_INFO_STRING: account name", URL["account"]),
    "ACCOUNT_SERVER": ("ENUM_ACCOUNT_INFO_STRING: trade server", URL["account"]),
    "ACCOUNT_CURRENCY": ("ENUM_ACCOUNT_INFO_STRING: account currency", URL["account"]),
    "ACCOUNT_COMPANY": ("ENUM_ACCOUNT_INFO_STRING: broker company", URL["account"]),
    # ENUM_MQL_INFO_*
    "MQL_TESTER": ("ENUM_MQL_INFO_INTEGER: running in the tester", URL["mqlinfo"]),
    "MQL_OPTIMIZATION": ("ENUM_MQL_INFO_INTEGER: running in optimization", URL["mqlinfo"]),
    "MQL_VISUAL_MODE": ("ENUM_MQL_INFO_INTEGER: visual testing mode", URL["mqlinfo"]),
    "MQL_TRADE_ALLOWED": ("ENUM_MQL_INFO_INTEGER: EA trading allowed", URL["mqlinfo"]),
    "MQL_TRADE_EXPERT": ("ENUM_MQL_INFO_INTEGER: an EA is running", URL["mqlinfo"]),
    "MQL_TRADE_MODE": ("ENUM_MQL_INFO_INTEGER: MQLInfo trade mode", URL["mqlinfo"]),
    "MQL_PROGRAM_NAME": ("ENUM_MQL_INFO_STRING: program name", URL["mqlinfo"]),
    "MQL_PROGRAM_PATH": ("ENUM_MQL_INFO_STRING: program path", URL["mqlinfo"]),
    # Uninitialization reason (family sibling)
    "REASON_CHARTCLOSE": ("ENUM_UNINIT_REASON constant: terminal closed the chart", URL["uninit"]),
    # Terminal constants (family of the 5060 replacement strings)
    "TERMINAL_CONNECTED": ("Terminal property: terminal connected to the server", URL["terminal"]),
    "TERMINAL_TRADE_ALLOWED": ("Terminal property: AutoTrading enabled", URL["terminal"]),
    "TERMINAL_DLLS_ALLOWED": ("Terminal property: DLL imports allowed", URL["terminal"]),
    # Object types (family sibling)
    "OBJ_RECTANGLE_LABEL": ("ENUM_OBJECT type: rectangle label object", URL["objecttypes"]),
    # Structure type names (documented)
    "MqlTick": ("Structure: latest quote prices (tick data)", "https://docs.mql4.com/constants/structures/mqltick"),
    "MqlDateTime": ("Structure: date/time components", "https://docs.mql4.com/constants/structures/mqldatetime"),
    # Enum type names used in signatures/annotations
    "ENUM_ACCOUNT_TRADE_MODE": ("Enumeration type: account trade modes", URL["account"]),
    "ENUM_SYMBOL_INFO_INTEGER": ("Enumeration type: symbol integer properties", URL["market"]),
    "ENUM_SYMBOL_INFO_DOUBLE": ("Enumeration type: symbol double properties", URL["market"]),
    "ENUM_SYMBOL_INFO_STRING": ("Enumeration type: symbol string properties", URL["market"]),
    "ENUM_SYMBOL_TRADE_MODE": ("Enumeration type: symbol trading modes", URL["market"]),
    "ENUM_ACCOUNT_INFO_INTEGER": ("Enumeration type: account integer properties", URL["account"]),
    "ENUM_ACCOUNT_INFO_DOUBLE": ("Enumeration type: account double properties", URL["account"]),
    "ENUM_ACCOUNT_INFO_STRING": ("Enumeration type: account string properties", URL["account"]),
    "ENUM_ACCOUNT_STOPOUT_MODE": ("Enumeration type: account stop-out modes", URL["account"]),
    "ENUM_MQL_INFO_INTEGER": ("Enumeration type: MQLInfoInteger properties", URL["mqlinfo"]),
    "ENUM_MQL_INFO_STRING": ("Enumeration type: MQLInfoString properties", URL["mqlinfo"]),
    "ENUM_DAY_OF_WEEK": ("Enumeration type: days of the week", URL["market"]),
}


def main() -> None:
    doc = json.loads((DATA / "mql4.json").read_text(encoding="utf-8"))
    added = {"function": 0, "variable": 0}
    for name, (sig, url) in FUNCTIONS.items():
        if name in doc["functions"]:
            continue
        doc["functions"][name] = {
            "signature": sig, "apiTier": "shared", "docUrl": url,
            "provenance": "mql4-reference-enrichment-rc2",
        }
        added["function"] += 1
    for name, (desc, url) in VARIABLES.items():
        if name in doc["variables"]:
            continue
        doc["variables"][name] = {
            "description": desc, "apiTier": "shared", "docUrl": url,
            "provenance": "mql4-reference-enrichment-rc2",
        }
        added["variable"] += 1
    (DATA / "mql4.json").write_text(
        json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"added {added['function']} functions, {added['variable']} variables/constants")
    print(f"new totals: {len(doc['functions'])} fn, {len(doc['variables'])} var")


if __name__ == "__main__":
    main()
