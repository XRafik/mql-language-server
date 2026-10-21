# Feature: MQL5 golden-list description enrichment (issue #166)

Capture descriptions for every golden-list entry that currently shows a
placeholder or no description at all. Measured baseline on main @ 15458f0:

- `data/builtins/mql5.json` variables: 2,003 entries — 359 real
  descriptions (hand-curated), **1,644 placeholders** (`"(description not
  yet captured)"`): PDF-enriched enum constants, `ENUM_*` type names, and
  133 web colors.
- `data/builtins/mql5.json` functions: 816 entries — **none carry a
  `description` field** (signature + provenance only).

User decisions (2026-10-19): scope is **MQL5 only** (MQL4 functions stay a
follow-up; they need a docs.mql4.com crawler pass, no PDF exists); the 133
web colors get **synthetic descriptions** ("Web color 0xRRGGBB (name)"),
since the PDF swatch grid has no description column.

## Approach

1. New tool `tools/generate-golden-lists/apply_mql5_descriptions.py`
   reusing the proven PDF pipeline (pdftotext `-layout` + `-raw` dumps
   already on disk under `tools/generate-golden-lists/data/pdf/`):
   - Constant tables: pair the identifier column with the description
     column using the -layout geometry lessons from the constants pass.
   - Function pages: prose line(s) under the page heading, before the
     captured signature.
   - `ENUM_*` names: description from the enum's documentation section.
   - Web colors: synthetic, deterministic, no PDF parsing.
2. Update entries **in place, description field only**; provenance gains a
   `descriptions-<date>` marker. Never touch names or signatures (#136
   contract: golden list is the source of truth; registries are
   projections).
3. Red pins first: hover-text parity tests for a representative sample
   (constant families, functions, ENUM_*, web colors). All existing parity
   and registry tests must stay green (description is additive).

## Tasks

- [x] T1 — This doc + Engram mirror `odd/mql5-description-enrichment/tasks`
      + branch feat/mql5-description-enrichment.
- [x] T2 — Red pins (tests fail before dataset change). 30/30 red observed
      by the worker (19 constant/ENUM_*/web-color pins + 10 function pins
      + coverage gate). Commit: d33cade (intentionally red at that commit,
      following the repo's red-pin-first work-unit pattern).
- [x] T3 — Extractor + dataset update delegated to gentle-ai-worker.
      apply_mql5_descriptions.py (8f9546c); dataset ff733d0: placeholders
      1,644 → 66 (96.0%), description-less functions 816 → 13 (98.4%),
      description-only change, provenance markers appended.
- [x] T4 — Full suite + Release build (independent verify):
      gentle-ai-verify 6/6 PASS — 1,646/1,646 tests, Release 0 errors/
      0 warnings, mql5.json keysets + all 816 function signatures
      byte-identical vs HEAD, mql4.json untouched, Generated untouched,
      focused 30/30.
- [x] T5 — CHANGELOG entry committed (4313391). Issue #166 closes with
      the merge (follow-up: MQL4-side descriptions remain open as a
      separate feature decision).
- [x] T6 — Work-unit commits d33cade → 8f9546c → ff733d0 → 4313391
      recorded; Engram updated.

## Deviations from the original plan

- "All 816 functions have pages" assumption was false: 13 description-less
  functions remain — MQL4-legacy names with no page in the MQL5 Reference
  PDF (AccountBalance, OrderClose, FileRead, StringGetChar, …) plus
  TERMINAL_DATA_PATH / TERMINAL_DLLS_ALLOWED (present in both functions
  and variables sections; the variables side got descriptions). Coverage
  gate threshold 15 documents this; unrecoverable evidence in
  tools/generate-golden-lists/data/mql5-descriptions.json.
- 66 variable placeholders remain (≤82 gate): ~50 are truncated-name
  import artifacts of the 2026-10-18 enum pass that never occur verbatim
  in the PDF (ALANCE_DD, OBJ_BUTTO, CHART_SHOW_TRADE, …) — fixing those
  names would violate the never-modify-names rule; 10 ENUM_* names lack
  recoverable prose/heading; a few mid-table enum headings inherit a
  sibling's section title (inheritedFrom in the evidence JSON).
- Narrow-column tables leave occasional wording artifacts in a few
  descriptions (truncated tails at page breaks); all are real reference
  text, full mapping reviewable in the evidence JSON.

## Evidence log

- 2026-10-19: baseline measured (numbers above) with a JSON audit script;
  PDF dumps confirmed present in tools/generate-golden-lists/data/pdf/.
- Scope + web-colors decision recorded by user via structured question.
- 2026-10-19 (T2, gentle-ai-worker): RED captured —
  `Issue136BuiltinRegistryCoverageTests` 30 failed / 0 passed before the
  dataset change (`Mql5Descriptions_AreCaptured` 19 constant pins,
  `Mql5FunctionDescriptions_AreCaptured` 10 function pins, coverage gate).
- 2026-10-19 (T3, gentle-ai-worker): `apply_mql5_descriptions.py` written;
  applied to `data/builtins/mql5.json` IN PLACE (description field only):
  1,644 → 66 variable placeholders (96.0%), 816 → 13 functions without
  description (98.4%). Provenance appended per entry
  (`descriptions-official-mql5-reference-pdf-2026-10-19` /
  `descriptions-synthetic-web-colors`); names and signatures verified
  unchanged against git HEAD (0 diffs); idempotent re-run applies 0.
  Review evidence: tools/generate-golden-lists/data/mql5-descriptions.json
  (per-entry description + kind + pages; unrecoverable list with reasons).
  Unrecoverable residuals: ~50 truncated-name import artifacts from the
  2026-10-18 PDF pass (e.g. `ALANCE_DD`, `OBJ_BUTTO`), 10 ENUM_* type
  names whose headings carry no prose/section title, and 13
  MQL4-legacy functions (AccountBalance, OrderClose, FileRead, ...) with
  no page in the MQL5 Reference PDF. Parity tests untouched and green
  (descriptions are additive; the parity suite compares variables by
  description, so the registry projection follows the golden list
  automatically via `BuiltinGoldenData`).
- 2026-10-19 (T2/T3, gentle-ai-worker): GREEN captured — focused
  `Issue136BuiltinRegistryCoverageTests` 227/227; full suite 1,646/1,646;
  `dotnet build -c Release` 0 errors / 0 warnings. Coverage-gate function
  threshold set to 15 with the MQL4-legacy justification in the test
  comment.
