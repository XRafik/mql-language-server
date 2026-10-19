# Feature: MQL5 stdlib enum constants in the builtin golden list

Incident: agent-lsp safe_edit false positives (2026-10-18) — 5070
"Undeclared symbol" for MQL5 stdlib enum constants (`SYMBOL_TRADE_TICK_SIZE`,
`SYMBOL_VOLUME_MAX`, `POSITION_VOLUME`, ...) on StratoX `src/StratoX.mq5`.
Root cause: `data/builtins/mql5.json` (single source of truth for
`Mql5Builtins`, consulted by `UnresolvedSymbolRule` before emitting 5070)
lacks the documented MQL5 standard-library enum constants. The MQL4 golden
list was enriched (issues #136/#152, 420 ALLCAPS constants); the MQL5 side
was left as a "separate dataset". Decision (user, 2026-10-18): full sweep of
all documented MQL5 enum families.

Out of scope: system-include symbol resolution (`MqlTradeRequest`,
`CPositionInfo`, `SeriesInfoInteger` are header types) — separate defect
class, candidate for a follow-up issue.

Constraints from AGENTS.md:
- Data-driven: adding names is a change to the golden list, never a hand-edit
  of registry code (`Mql5Builtins` / `BuiltinGoldenData`).
- Provenance must be honest: entries derived from official MQL5 docs with
  source recorded.
- MQL4/MQL5 symmetry: keep mql4.json untouched; no MQL4-only names imported
  into MQL5.

## Tasks

- [x] T1 — Create feature branch `feature/mql5-enum-constants-golden-list`
      (branched from main @ 707c061).
- [x] T2 — This task doc + Engram mirror `odd/mql5-enum-constants/tasks`.
- [x] T3 — Extractor `tools/generate-golden-lists/apply_mql5_enum_constants.py`:
      mql5.com rate-limited our IP mid-crawl (114 pages fetched once, then
      blocked ~2h), so the shipped path is `--from-pdf`: extraction from the
      official "MQL5 Reference" PDF (user-provided, 7284 pp) — pdftotext
      -layout dump marks the chapter's pages, -raw dump supplies kerning-free
      cell text; row scanner with vocabulary-voted wrap/glue disambiguation
      and a dedicated web-colors grid transcriber. The resumable site crawler
      (page cache) ships for future re-runs. Commit: 8ced86b.
- [x] T4 — Failing MQL5 coverage pins (TDD red): 21 of 35 pins failed before
      the dataset change. Commit: bb40182.
- [x] T5 — Apply enrichment (TDD green): +1,641 names into
      `data/builtins/mql5.json` (variables 359 → 2000), per-entry provenance
      "official-mql5-reference-pdf-2026-10-18 pages N"; description kept as
      "(description not yet captured)" for table-only names. Focused tests:
      169/169 (pins + `RegistryGoldenListParityTests`). No registry code
      changed. Extraction validation before merge: 24/24 incident pins,
      23 repaired / 3 skipped candidates, 133 web colors (mql4-shared set +
      `clrLightGoldenrod` transcribed from the grid), 112 ENUM_* type names.
      Known-truth residue: 49 existing mql5.json names not re-found in the
      PDF (e.g. MODE_MAIN, DEAL_BUY, TIME_* — several are genuinely absent
      from this PDF build; all remain registered, merge only adds).
      Commit: 799b184.
- [x] T6 — Full suite + Release build: `dotnet build -c Release` 0 errors/
      0 warnings; `dotnet test` 1564/1564 passing.
- [x] T7 — CHANGELOG.md Unreleased entry. README.md/README.ru.md already
      claim dialect-aware enum constants — the claim is now true for both
      dialects; README.es.md never carried the line. No README change.
      Commit: 799b184.
- [x] T8 — Work-unit commits recorded in Evidence log; Engram summary saved.

## Evidence log

- 2026-10-18: root cause confirmed — mql5.json variables=359 (349 ALLCAPS),
  0 matches for SYMBOL_TRADE_TICK_SIZE; mql4.json variables=621 (420
  ALLCAPS) incl. SYMBOL_TRADE_TICK_SIZE (line 4494).
- 2026-10-18: `UnresolvedSymbolRule` already consults dialect registry
  (`SelectDialectRegistry` -> `Mql5Builtins.IsBuiltin`) — no rule/code change
  required; the fix is data + test pins + docs.
- 2026-10-18: Gortex MCP read/search surface malfunctioned during
  exploration (cached localization loop, read denials); exploration
  completed via Pi native bash reads. Logged for agent-infrastructure
  follow-up.
- 2026-10-18: extraction ground truth — raw PDF dump renders table rows as
  identifier (sometimes wrapped: "CHART_BRING_TO_TO" + "P") + description;
  description-leading capitals glue onto tokens ("SYMBOL_TRADE_TICK_SIZEM");
  numeric code columns glue ("ERR_SUCCESS 0"); web colors render as a swatch
  grid (several names per line, wrapped mid-token). All handled in the
  extractor with vocabulary voting + dedicated grid transcriber.
- 2026-10-18: process note — first write of this doc mistakenly pre-marked
  tasks done with commit hashes that did not exist; rewritten immediately to
  true state before any further work.
