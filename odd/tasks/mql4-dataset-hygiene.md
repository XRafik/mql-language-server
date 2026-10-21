# mql4-dataset-hygiene

Remove MQL5-only names that leaked into `data/builtins/mql4.json`, with
CHM-evidence-based classification and a regression gate so they cannot return.

## Evidence (session 2026-10-21)

Authority chain: official MQL4 Reference CHM
(`tools/generate-golden-lists/data/raw/mql4-chm/`, 834 pages) is the MQL4
documentation authority per the #136 contract ("documentation-driven golden
lists"). Classification rule:

- **Remove** = name documented in the MQL5 reference (present in
  `mql5.json` with real description/docUrl) AND absent from all non-`mql5_*`
  MQL4 CHM pages (no dedicated `name.htm` page, no section-table row, not in
  `events.htm` MQL4 handler list, no legacy-alias page).
- **Keep ambiguous** = zero CHM evidence but not provably MQL5-only
  (e.g. `_AppliedTo`: absent from `predefined.htm` but admitted by real
  corpus in #136; not provably absent from the MQL4 compiler).
- **Keep legit** = documented MQL4 (dedicated page, section table, events.htm,
  or alias page such as `mathrand.htm` for `rand`).

MQL4 event handlers per `events.htm`: OnCalculate, OnChartEvent, OnDeinit,
OnInit, OnStart, OnTester, OnTick, OnTimer. Notably OnTrade /
OnTradeTransaction are MQL5-only (migration section only).

## Removal set (20 functions + 12 variables)

Functions: OrderGetDouble, OrderGetInteger, OrderGetString, PositionsTotal,
PositionSelect, PositionGetDouble, PositionGetString, PositionGetInteger,
HistorySelect, HistoryOrderSelect, HistoryDealSelect, StringToDateTime,
CryptMethod, FileRead, ArrayReverse, MathArctan2, CopyBuffer,
IndicatorRelease, OnTrade, OnTradeTransaction.

Variables: POSITION_TYPE_BUY, POSITION_TYPE_SELL, TRADE_ACTION_DEAL,
TRADE_ACTION_PENDING, TRADE_ACTION_SLTP, OBJ_ELLIOTWAVE5, OBJ_ELLIOTWAVE3,
OBJPROP_DEGREE, OBJPROP_COMMENT, CHART_SHOW_TICKER, SYMBOL_TIME_MSC,
ACCOUNT_MARGIN_MODE.

Weak-evidence notes recorded in the audit JSON: CopyBuffer only appears in a
stray code sample inside `chartsavetemplate.htm`; IndicatorRelease only in the
`indicators.htm` overview prose while the page's own normative function table
and `function_indices.htm` exclude it and `mql4changes.htm` never mentions it.

## Unknown bucket (kept, report-only follow-up)

ACCOUNT_MARGIN_SO_STOP, ACCOUNT_TRADE_STOPOUT_LEVEL, TERMINAL_EMAIL,
TERMINAL_EXPERTS_ALLOWED, CRYPT_AES192, CRYPT_HASH_SHA512,
CRYPT_HASH_SHA3_256, CRYPT_HASH_SHA3_512, CRYPT_ARCH_GZIP, CRYPT_ARCH_ZLIB,
CRYPT_ARCH_RLE (constants newer than this CHM build), ArraySearch,
PositionSelectByIndex, HistoryTotal, IsTradeAllowedWebRequest,
SymbolInfoSessionOrder (fabrication suspects), False, LastError,
BarsIsTradeAllowed, AccountSymbol, HotPink, DeepSkyBlue, clrSeaShell,
clrLightGoldenrodYellow, IDCLOSE, MQL_TRADE_EXPERT, MQL_TRADE_MODE,
_AppliedTo (corpus-admitted, ambiguous).

Reverse direction (mql5.json containing MQL4-only names such as
OrderClose/OrderModify/OrderDelete/WindowExpertName) is OUT OF SCOPE here;
separate follow-up.

## Tasks

- [ ] T1: Add `tools/generate-golden-lists/audit_mql4_dataset.py` + generated
  `data/mql4-dataset-audit.json` (deterministic CHM evidence report).
- [ ] T2 (RED): gate test `Mql4GoldenList_ExcludesMql5OnlyNames` in
  `tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs` — the 32 names must
  be absent from mql4.json. Must fail before T3.
- [ ] T3 (GREEN): remove the 32 entries from `data/builtins/mql4.json`
  (functions 350→330, variables 621→609).
- [ ] T4: Update stale assertions:
  `Mql4AdvancedFeaturesTests` (PositionsTotal/PositionGetString/
  PositionGetDouble/OrderGetString/OrderGetDouble/CopyBuffer IsBuiltin
  True→False; PositionSelectByIndex stays True),
  `ProgramAndBuiltinsTests` CopyBuffer InlineData,
  `Issue136BuiltinRegistryCoverageTests.Mql4GoldenList_DescriptionCoverage_MeetsTarget`
  threshold 44→24 + narrative.
- [ ] T5: Focused suite + full suite + Release build; review fixture-based
  diagnostic tests (ExpertAdvisor.mq4 uses CopyBuffer) — expected behavior
  change only.
- [ ] T6: CHANGELOG [Unreleased] entry; work-unit commit(s) on feature branch
  `feat/mql4-dataset-hygiene`; Engram evidence.

## Commits

(recorded as work units land)
