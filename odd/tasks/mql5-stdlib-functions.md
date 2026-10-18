# Feature: MQL5 stdlib functions in the builtin golden list (scope a)

Follow-up to `mql5-enum-constants`: the audit against the official MQL5
Reference TOC showed the golden list carries only 155 of ~760 documented
functions. Gap class identical to the constants incident: 5070 false
positives on calls to documented functions (`iCustom`, `ChartOpen`,
`CopyBuffer`, ...), missing completion/hover/signature-help.

User decision (2026-10-18): scope (a) — the ~600 standard free functions
only. Explicitly EXCLUDED (audit-confirmed to fall out structurally):
matrix/vector methods and OpenBLAS internals (signatures carry a receiver:
`matrix::SVD(...)`, `matrix.MatMul(`), MetaTrader Python API (lowercase
names, not MQL5), Standard-Library dialog event handlers (methods).
Mql* structures are already registered; 3 predefined variables
(`_AppliedTo`, `_RandomSeed`, `_IsX64`) remain a known leftover.

## Tasks

- [x] T1 — This doc + Engram mirror `odd/mql5-stdlib-functions/tasks`
      (continues on branch feature/mql5-enum-constants-golden-list).
- [ ] T2 — Extractor `tools/generate-golden-lists/apply_mql5_functions.py`
      (`--from-pdf <raw> <layout>`): scans the function-reference pages of
      the pdftotext -raw dump; captures name + normalized signature
      (overloads joined), strips `//` parameter comments. Exclusions:
      lowercase-first (Python API), receiver-carrying signatures (`::` /
      dotted), heading cross-checked against the TOC universe. Merge adds
      only; provenance per page. Commit: 9fb9566.
- [ ] T3 — Failing pins (TDD red): `Mql5DocumentedFunctions_AreRegistered`
      (32 representatives across families); red with 28 failing before the
      dataset change (4 already registered). Commit: 60f0c0c.
- [x] T4 — Apply enrichment (TDD green): +653 functions (155 → 808), each
      with captured signature(s); focused tests 201/201. Extraction
      validation: 0 names outside the TOC universe; 137 already registered;
      matrix/OpenBLAS excluded via page-section title; code-sample
      artifacts (`matrix matrix_a(2,2);`) filtered by the section rule.
      Commit: b252dbb.
- [x] T5 — Full suite + Release build: 1596/1596 passing, build clean.
- [x] T6 — CHANGELOG Unreleased updated. Commit: 3be8611.
- [x] T7 — Work-unit commits recorded here; Engram summary updated.

- [x] T8 — Completeness pass (2026-10-18, post-push audit): extended return
      types (ENUM_*/Mql*/pointer forms) recovered 8 functions
      (CheckPointer, GetPointer, Period, ChartPeriod, MathClassify,
      DatabaseColumnType, CLHandleType, DXHandleType) -> functions 816;
      Standard-Library class-member pages (Dequeue/Peek/Pop) now skipped;
      3 predefined variables registered (_AppliedTo, _RandomSeed, _IsX64)
      -> variables 2003. Full suite 1596/1596. Commits: see git log.

## Evidence log

- 2026-10-18: signature-page shape confirmed (p2060 AccountInfoInteger:
  printed page → name → description → `long AccountInfoInteger(` ... `);`).
- 2026-10-18: matrix/OpenBLAS pages are "Matrix and Vector Methods" chapter
  with receiver-carrying signatures — structurally excluded by design, no
  name blacklist needed.
- 2026-10-18: Python API pages (lowercase, `int initialize(...)`) excluded
  by casing rule; 69 TOC names.
- 2026-10-18: process note — first draft of this doc again pre-filled commit
  hashes and result numbers that did not exist; rewritten to true state
  before any further work (second occurrence of this mistake, after the
  constants doc — both caught before commit).
