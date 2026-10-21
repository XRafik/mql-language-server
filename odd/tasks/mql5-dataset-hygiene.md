# mql5-dataset-hygiene

Reverse direction of mql4-dataset-hygiene: remove MQL4-only names that
leaked into `data/builtins/mql5.json`.

## Evidence (session 2026-10-21)

Authority chain: the official MQL5 Reference. Evidence sources, in order:

1. **Function pages**: the pdftotext dumps
   (`tools/generate-golden-lists/data/pdf/mql5-reference-*.txt`). Every
   documented MQL5 function has a reference page; a `\bName\b` count of zero
   across the dumps is strong evidence of absence — EXCEPT the dumps have
   known holes (FileRead is MQL5-documented with docUrl from #163 yet has
   zero occurrences; enum member tables like DEAL_BUY did not survive the
   extraction). Therefore: zero-count FUNCTIONS were individually
   adjudicated; the dump is NOT used as evidence for constants.
2. **Live mql5.com docs** (web verification this session):
   - MQL5 "Indicators Lines" documents MAIN_LINE/SIGNAL_LINE/... — MODE_MAIN
     is not there; MQL5 forum threads confirm `'MODE_MAIN' - undeclared
     identifier` compiler errors. MODE_MAIN/MODE_SIGNAL/MODE_UPPER/
     MODE_LOWER are MQL4-only.
   - StringGetChar/StringSetChar are documented in the MQL4 Reference
     (docs.mql4.com/strings/stringgetchar); MQL5 documents
     StringGetCharacter/StringSetCharacter instead.
   - OrderClose/OrderCloseBy/OrderModify/OrderDelete appear in the MQL5
     reference only as CTrade class methods (Standard Library resolves via
     includes per the #150 scope convention); as free functions they are
     MQL4-only.

## Removal set (14 functions + 4 variables)

Functions (all `hand-curated-registry-v2.5.1-rc.1` provenance except
StringToDateTime/CryptMethod — phantom names with zero evidence in ANY
dialect reference): OrderClose, OrderCloseBy, OrderModify, OrderDelete,
WindowExpertName, AccountBalance, AccountEquity, AccountFreeMargin,
AccountMargin, StringGetChar, StringSetChar, CryptMethod, DateTimeToString,
StringToDateTime.

Variables: MODE_MAIN, MODE_SIGNAL, MODE_UPPER, MODE_LOWER (MQL4 indicator
line constants; MQL5 uses MAIN_LINE/SIGNAL_LINE/UPPER_BAND/LOWER_BAND).

Deliberately KEPT (verified MQL5-legit): OrderSend, OrderSelect,
OrdersTotal (free MQL5 trade API, high PDF counts), FileRead (MQL5-documented,
PDF-dump hole), TERMINAL_DATA_PATH/TERMINAL_DLLS_ALLOWED (misplaced under
functions — placement defect, follow-up, not a leak).

## Out of scope / follow-ups

- mql5.json constants cannot be systematically audited with the current PDF
  dump (enum member tables missing: DEAL_BUY et al. are documented MQL5 but
  zero-count). A better MQL5 constants dump or live-crawl audit is needed
  before touching mql5.json variables beyond the 4 adjudicated MODE_*.
- mql4.json placement defects: legacy Account* functions registered under
  `variables` (AccountBalance/AccountEquity/...); constants registered under
  `functions` in both dialects. Cosmetic-asymmetric; separate follow-up.
- mql4.json "unknown bucket" fabrication suspects (ArraySearch,
  PositionSelectByIndex, HistoryTotal, IsTradeAllowedWebRequest,
  SymbolInfoSessionOrder) — unchanged from the previous feature.

## Tasks

- [ ] T1: `tools/generate-golden-lists/audit_mql5_dataset.py` (mirror of the
  MQL4 audit; function evidence = PDF-dump occurrence with documented-hole
  caveat; constants = judgment-adjudicated only) + generated
  `data/mql5-dataset-audit.json` (untracked evidence per convention).
- [ ] T2 (RED): gate test `Mql5GoldenList_ExcludesMql4OnlyNames` in
  `tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs` (or sibling file).
- [ ] T3 (GREEN): remove the 18 entries from `data/builtins/mql5.json`
  (verify exact before/after counts; deletions-only diff).
- [ ] T4: flip stale assertions asserting these names as MQL5 builtins
  (search tests for each name in IsBuiltin/registry contexts; expected hits:
  MODE_MAIN/MODE_SIGNAL in Mql5EnumConstants-style pins, OrderClose/
  WindowExpertName/StringGetChar elsewhere; do NOT touch fixtures; fixtures
  using CTrade methods are member calls and unaffected).
- [ ] T5: focused + full suite + Release build (independent verify).
- [ ] T6: CHANGELOG [Unreleased] paragraph + work-unit commits on
  `feat/mql5-dataset-hygiene`.

## Commits

(recorded as work units land)
