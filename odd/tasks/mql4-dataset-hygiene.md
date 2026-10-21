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

- [x] T1: `tools/generate-golden-lists/audit_mql4_dataset.py` + generated
  `data/mql4-dataset-audit.json` (deterministic CHM evidence report; JSON
  stays untracked per repo convention, like the sibling description dumps).
- [x] T2 (RED): gate test `Mql4GoldenList_ExcludesMql5OnlyNames` in
  `tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs` — verified RED
  (32 offenders named) before T3, GREEN after.
- [x] T3 (GREEN): removed the 32 entries from `data/builtins/mql4.json`
  (functions 350→330, variables 621→609; diff = 134 deleted lines, 0 added).
- [x] T4: Stale assertions updated in 6 test files (Mql4AdvancedFeatures,
  ProgramAndBuiltins, SamplesTests, Issue136/Issue109/Issue163 coverage
  tests); description-coverage threshold 44→24 with new narrative.
- [x] T5: Independent verification (gentle-ai-verify): build 0 errors/
  0 warnings; focused 441/441; full suite 1662/1662; diff = exactly the 32
  deletions; no stale MQL4-True assertions for removed names remain.
- [x] T6: CHANGELOG [Unreleased] entry; two work-unit commits on
  `feat/mql4-dataset-hygiene` (below); Engram evidence.

## Commits

- 4742631 feat(tools): CHM-evidence audit for MQL4 golden-list dataset hygiene
  (script + task doc)
- 66fcf16 fix(dataset): remove MQL5-only entries from the MQL4 golden list
  (dataset + 6 test files + CHANGELOG)

## Notes

- The writer found 3 stale assertions outside the original edit surfaces
  (SamplesTests, Issue109, Issue163) — ratified by the parent; each was a
  direct consequence of the T4 rule.
- Reverse leaks (MQL4-only names in mql5.json: OrderClose, OrderModify,
  OrderDelete, WindowExpertName, ...) remain a separate follow-up.
- Fixtures using CopyBuffer (.mq4) now produce 1070 diagnostics by design;
  no fixture-based test asserted their absence.
