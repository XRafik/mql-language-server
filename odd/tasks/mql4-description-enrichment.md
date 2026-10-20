# Feature: MQL4 golden-list description enrichment (issue #166, MQL4 side)

Capture descriptions for `data/builtins/mql4.json` entries from the
official MQL4 Reference CHM (`~/Descargas/mql4.chm`, decompiles with
`7z x` to 690 HTML files under /tmp/mql4-chm, cp1252 encoding).

Baseline on main @ 15458f0:

- `mql4.json` functions: 350 entries, **none carry a description field**.
- `mql4.json` variables: 621 entries, 4 placeholders
  (`Open`, `High`, `Low`, `Close` — predefined variables).

## Source mapping (measured 2026-10-19)

- **246/350 functions** have their own page `<name>.htm` (lowercase) with
  a uniform structure: breadcrumb `MQL4 Reference / <section>` → function
  name → one-sentence description → signature with per-parameter
  comments → `Parameters` → `Returned value` → `Example`.
- **Constant families** live in grouped pages:
  - `MODE_*` (MarketInfo) — MarketInfo constants page/table.
  - `TERMINAL_*` — terminal information constants table.
  - `CRYPT_*`, `ENUM_CRYPT_METHOD`, `MB_ICON*` — crypto/messagebox
    constants tables.
  - `ACCOUNT_*` (28, hand-curated pseudo-entries misplaced in functions
    with `"signature": "Account property: X"`) — account constants page.
  - `Open/High/Low/Close` — `predefined.htm` Variable/Value table.
- **No page exists** for MQL5-only names that leaked into the mql4
  dataset (`OrderGetDouble`, `CopyBuffer`, `OnTick`, `rand`, `round`,
  `MathArctan2`, `ArrayReverse`, `ArraySearch`, `IsTradeAllowedWebRequest`,
  `SymbolInfoSessionOrder`, …). These stay description-less and are
  REPORTED with the page-audit evidence — never fabricate text.

## Approach

1. New tool `tools/generate-golden-lists/apply_mql4_descriptions.py`:
   decompile the CHM to a cache dir (7z), map dataset entries to pages,
   extract descriptions from the uniform HTML (strip tags, keep the
   description sentence(s) between the name heading and the signature;
   for grouped constants use the identifier→description table rows).
2. Update entries IN PLACE, `description` field only; provenance gains
   `; descriptions-official-mql4-reference-chm-<date>`; never touch names
   or signatures (#136 contract).
3. Idempotent/resumable; dump a per-entry evidence mapping under
   `tools/generate-golden-lists/data/` (gitignored intermediates).
4. Red pins first: ~10 function pins across families + 4 predefined
   variable pins + a small constant sample; coverage gate consistent
   with the measured page-audit numbers.

## Tasks

- [x] T1 — This doc + Engram mirror `odd/mql4-description-enrichment/tasks`
      + branch feat/mql4-description-enrichment.
- [x] T2 — Extractor + dataset update delegated to gentle-ai-worker
      (red pins first, then dataset). DONE 2026-10-20, see evidence log.
- [x] T3 — Full suite + Release build (independent verify):
      gentle-ai-verify 7/7 PASS — 1,631/1,631 tests, Release 0 errors/
      0 warnings, keysets + signatures + existing descriptions intact,
      mql5.json byte-identical, CHM cache gitignored, focused 15/15.
- [x] T4 — CHANGELOG (Unreleased entry) — commit 6713b9c. With PR #170
      (MQL5 side) merged, issue #166 closes.
- [x] T5 — Work-unit commits 7c6cede → 50a6e2f → 9e59929 → 6713b9c
      recorded; Engram updated.

## Evidence log

- 2026-10-19: CHM feasibility audit (numbers above); page-shape sample:
  orderclose.htm extracts cleanly ("OrderClose — Closes opened order.").
- 2026-10-19: scope decision — CHM is the source (user provided it after
  the MQL5 PDF pass, PR #170 covers the MQL5 side).
- 2026-10-20 (T2, worker): page audit confirmed — 246/350 functions have
  their own page; grouped tables cover 60 more (15 MODE_*, 10 TERMINAL_*,
  26 ACCOUNT_*, 7 CRYPT_* + ENUM_CRYPT_METHOD, 1 MB_ICONINFORMATION);
  44 names are absent from this CHM build (MQL5-only leaks, undocumented
  legacy aliases such as rand/round/FileRead, event-handler names, and
  constants newer than the CHM: CRYPT_AES192/SHA3/ARCH_*,
  ACCOUNT_MARGIN_SO_STOP, ACCOUNT_TRADE_STOPOUT_LEVEL,
  TERMINAL_EXPERTS_ALLOWED, TERMINAL_EMAIL). Result: 306 functions +
  4 predefined variables enriched (variables' placeholder is
  "(documented MQL4 API; signature not yet captured)" in this dataset,
  not "(description not yet captured)"). Tool:
  tools/generate-golden-lists/apply_mql4_descriptions.py (description-only
  in-place, provenance marker, idempotent, --dry-run, evidence dump
  data/mql4-descriptions.json). CHM cache relocated under data/raw/
  (gitignored); .gitignore itself was outside the worker's edit surface.
  TDD: 15 red pins failed pre-change; 15/15 green after; full suite
  1631 passing; Release build clean. Dataset diff verified: description
  additions + provenance markers only (names/signatures untouched).
