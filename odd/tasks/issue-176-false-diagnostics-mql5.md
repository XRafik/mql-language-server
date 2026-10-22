# Feature: issue-176-false-diagnostics-mql5

## Objective

Fix GitHub issue #176: four defect classes producing false diagnostics on
clean-compiling MQL5. Function/variable builtin 5070s from the issue are
already fixed on main by unreleased dataset work — the live defects are:
grammar rejection of `input group "…"` and of the `f` real-literal suffix,
dataset gaps for builtin struct types and predefined macros, and 5060 firing
inside string literals and trailing comments.

## Verified diagnosis (main @ f424007, Release build, JSON-RPC repro)

Minimal repro (22 lines, compiles clean in MetaEditor) produces 12 diagnostics
on main: 3× 5100 (`input group`, 2× `f`), 5× 5070 (`group`, 3× `f`,
`__FUNCTION__`, `MqlTradeRequest`, `MqlDateTime`), 2× 5060 (`Close` inside a
string literal + inside a trailing comment).

Root causes:
1. `Mql5Grammar.g4` has no `K_GROUP` keyword and no input-group rule; after
   `K_INPUT` (modifiers rule, line 236) the declaration rules expect `type`,
   `group` lexes as IDENTIFIER → `no viable alternative`.
2. `DOUBLE` lexer rule (line 41) accepts no `f`/`F` suffix; `0.0f` → DOUBLE
   `0.0` + IDENTIFIER `f` → parse errors + 5070 cascade.
3. `data/builtins/mql5.json` contains NO builtin struct type entries
   (`MqlTradeRequest`, `MqlDateTime`, `MqlTick`, `MqlRates`, `MqlBookInfo`: 0
   hits) and NO predefined macros (`__FUNCTION__`/`__FILE__`/`__LINE__`: 0
   hits; mql4.json has `__FUNCTION__` only, via #152). `Mql5Builtins.IsBuiltin`
   = functions ∪ variables; type names ride the variables table (same path as
   the `ENUM_*` entries from #136 — confirmed schema: `description` +
   `provenance`, no signature).
4. `Mql4OnlyApiRule` (src/Analysis/Rules/Mql4OnlyApiRule.cs) is a raw per-line
   `IndexOf` text scan; only whole-line comments (TrimStart `//`) are skipped.
   String literals and trailing comments are flagged — documented phase-1
   limitation inherited from #28's LanguageMisuseRule.

Already fixed on main (do NOT re-fix): function/variable builtin 5070s
(`PrintFormat`, `Symbol`, `Period`, `DBL_MAX`, `INT_MAX`,
`GetMicrosecondCount`, `ZeroMemory`, `TimeToStruct`, `EnumToString`).
Out of scope: stdlib classes via `#include <Trade\Trade.mqh>` (resolved by the
merged `systemIncludeRoots` config + cross-file suppression — configuration,
not a defect); `LanguageMisuseRule`'s identical raw-scan limitation (same
phase-1 class — separate follow-up); mql4.json macro gaps (`__FILE__`,
`__LINE__` missing in MQL4 too — separate follow-up, MQL5-only issue).

## Evidence sources (NO Wine/MetaEditor gate exists in this repo)

- Local MQL5 Reference dump: `tools/generate-golden-lists/data/pdf/mql5-reference.md`
  (plus `-layout.txt` / `-raw.txt` siblings) and the docs page cache under
  `tools/generate-golden-lists/data/page-cache/`.
- Grammar-decision precedent: #141/#143/#145 pattern — implement exactly what
  the reference documents, close the rule otherwise, record the decision in
  test comments and CHANGELOG.
- Data-only contract: #136 (golden list is the source of truth; registries are
  projections; per-entry provenance mandatory).

## TDD mode

ON, observed RED → GREEN per unit. Source: AGENTS.md §9. Runner per unit:
`dotnet test --filter FullyQualifiedName~<UnitTestName>`. Grammar RED = parse
errors on a snippet before the grammar change; dataset RED = gate test
asserting absent entries; 5060 RED = string/comment FP test before the change.

## Route declaration

All units delegated to `gentle-ai-worker` (multi-file writes + grammar
regeneration), one unit per launch, in order T1→T4. Parent closes each unit:
full `dotnet test` suite + work-unit commit. Delivery strategy `ask-on-risk`:
forecast ~430 authored lines total (slightly over the ~400 budget) — before
opening the PR, ask the user once: single PR vs stacked chain.

## Tasks

### T1 — Grammar: `input group "…"` (MQL5 only)

Surfaces: `src/Mql5/Grammar/Mql5Grammar.g4`, `tests/Parser/Mql5GrammarTests.cs`, `CHANGELOG.md`, this file.

RED: add to `tests/Parser/Mql5GrammarTests.cs` (mirror its existing
parse-and-assert pattern) `InputGroup_String_IsAccepted` parsing:
```mql5
input group "Trend Filters"
input int InpX = 1;
```
assert zero parse errors (assert the exact error-text collection the file
already uses). Also `InputGroup_BeforeInputs_ParsesAllFollowingInputs` with
two `input` declarations after the group. Observe RED (5100
`no viable alternative at input 'inputgroup"…"'`).

Fix (exact edit):
1. Lexer keywords section (next to the other `K_*` keywords, e.g. near
   `K_INPUT`): `K_GROUP : 'group';`
2. Parser: add alternative to `translationUnit`: `| inputGroupDirective`, and
   the rule:
   ```antlr
   // Issue #176: documented input-parameter grouping (`input group "text"`,
   // MQL5 Reference, inputs). Exact documented form: no trailing semicolon;
   // `sinput group` is not documented and stays rejected (#141 honesty rule).
   inputGroupDirective
       : K_INPUT K_GROUP STRING
       ;
   ```
3. Regenerate via normal build (`dotnet build`). NO visitor changes expected —
   the base visitor no-ops the new context. If any switch/exhaustive match on
   translationUnit contexts breaks the build, stop and report (do not
   hand-patch Generated/).

Pinned decisions: no `SEMICOLON` (documented form; cite the dump section in
the test comment); `sinput group` stays rejected; no symbol recorded, no
documentSymbol entry (minimal fix).

GREEN: both tests pass; run `dotnet test --filter
FullyQualifiedName~Mql5GrammarTests` (whole file green). Commit:
`fix(parser): accept input group directives in MQL5 (#176)`.

### T2 — Grammar: `f`/`F` real-literal suffix

Surfaces: `src/Mql5/Grammar/Mql5Grammar.g4`, `src/Mql4/Grammar/Mql4Grammar.g4` (mirror if it has the same gap), `tests/Parser/Mql5GrammarTests.cs`, `CHANGELOG.md`, this file.

Evidence step (before editing): grep the dump for the real-constants suffix
rule — `grep -n -i "suffix" tools/generate-golden-lists/data/pdf/mql5-reference.md`
— and read the surrounding section. Expected: real constants may carry `f`
(float). Record exactly what the dump says in the test comment.

RED: `RealLiteral_FSuffix_IsAccepted` parsing
`double a = 0.0f; double b = 1.5F; double c = 1e3f;` → zero parse errors and
(no 5070 on `f`: parser-level test only asserts errors here).

Fix (exact edit, line 41):
```antlr
DOUBLE        : [0-9]+ '.' [0-9]* EXP? [fF]?
              | '.' [0-9]+ EXP? [fF]?
              | [0-9]+ EXP [fF]?
              ;
```
Pinned decisions: suffix accepted on ALL three branches (`1e3f` — C-style
lexing; record if the dump is silent on the EXP form); both cases `[fF]`;
integer `123f` stays rejected (branch structure keeps INTEGER + IDENTIFIER) —
matches MetaEditor. Symmetry: check `Mql4Grammar.g4` DOUBLE rule; MQL4
documents the same suffix — mirror the change + a mirror test there if absent;
if already present, report no-op.

GREEN: filter `Mql5GrammarTests` green (+ MQL4 mirror file). Commit:
`fix(parser): accept f/F real-literal suffixes (#176)`.

### T3 — Dataset: MQL5 builtin struct types + predefined macros (data-only)

Surfaces: `data/builtins/mql5.json`, `tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs`, `CHANGELOG.md`, this file.

Evidence step (before editing): enumerate from the dump —
`grep -n "struct Mql" tools/generate-golden-lists/data/pdf/mql5-reference.md`
for structs, and the predefined-macro chapter for macros. Admission rule
(#152 precedent): only names with a documented definition/description section
in the dump; NO Windows-interop names. Minimum gate set (must be present after
the change; extend with whatever else the dump documents):
- Structs: `MqlDateTime`, `MqlTradeRequest`, `MqlTradeResult`,
  `MqlTradeTransaction`, `MqlTick`, `MqlRates`, `MqlBookInfo`, `MqlParam`.
- Macros: `__FILE__`, `__LINE__`, `__FUNCTION__`, `__MQLBUILD__` (enumerate
  the full documented set from the dump).

JSON shape (confirmed, same as `ENUM_TIMEFRAMES`): variables-table entries —
`"Name": { "description": "...", "provenance": "mql5-reference-structs-macros-2026-10-21" }`.
Descriptions derived from the dump chapter prose (mirror the mql4
`__FUNCTION__` entry's style). No signatures, no registry code change.

RED: add `Mql5StructTypes_AreRegistered` + `Mql5PredefinedMacros_AreRegistered`
to `Issue136BuiltinRegistryCoverageTests` (mirror its existing pin pattern) →
RED (entries absent). GREEN after data edit. End-to-end pin: a semantic-rule or
parser test asserting the repro snippet's `MqlTradeRequest`/`MqlDateTime`/
`__FUNCTION__` produce no 5070 (mirror `UnresolvedSymbolRuleTests` pattern).

Commit: `fix(data): add MQL5 builtin struct types and predefined macros (#176)`.

### T4 — `Mql4OnlyApiRule`: token-occurrence detection (no strings/comments)

Surfaces: `src/Analysis/Rules/Mql4OnlyApiRule.cs`, `tests/Analysis/Mql4OnlyApiRuleTests.cs`, `CHANGELOG.md`, this file.

RED: new tests (mirror the file's existing rule-test harness): 
`StringLiteral_Mention_IsNotFlagged` (the #176 line: `PrintFormat("%s. Close %.2f", …)` → no 5060), 
`TrailingComment_Mention_IsNotFlagged` (`int x = 1; // Close` → no 5060),
`BareRead_StillFlagged` (existing positive case stays).

Fix (exact shape): replace the per-line text scan with occurrence-driven
detection — iterate `context.File?.Occurrences` (TokenOccurrence: Line,
Column, Length, Text; default-channel IDENTIFIER tokens only, so strings and
comments are excluded by construction):
1. Skip when `Mql4OnlyApiRegistry.Entries` does not contain `occurrence.Text`
   (Ordinal, as today).
2. Split `context.Content` into lines ONCE (file precedent). For each matching
   occurrence: identifier-boundary check via
   `line[occurrence.Column-1]` / `line[occurrence.Column+Length]`
   `IsIdentChar` (guard indices), replicating the current boundary semantics.
3. `SemanticsChanged` entries: keep the call-position disambiguation —
   `NextNonSpaceCharIsOpenParen(line, occurrence.Column+Length)` → skip.
4. DELETE the whole-line-comment skip (occurrences exclude comments; dead
   code). Keep message construction identical (same wording, same
   `context.BaseCode + 60`, Warning severity).
5. Capture-contract pin: add a test with a macro body (`#define X Close`) and
   ASSERT THE OBSERVED behavior of occurrence capture, with a comment naming
   it as the documented capture contract (do not "fix" capture here).

Then run ALL existing `Mql4OnlyApiRuleTests`: if any existing test pins the
phase-1 string/comment flagging, update it with a one-line justification
comment (behavior change is the point of #176); the registry count pin
(49 entries) must stay green.

GREEN + `dotnet build -c Release` clean. Commit:
`fix(lsp): stop flagging MQL4-only names inside strings and trailing comments (#176)`.

### T5 — Verify and close (parent, inline)

Full `dotnet test` suite (baseline 1666 on main; this feature adds tests per
unit) + `dotnet build -c Release` after EACH unit's commit; end-to-end
JSON-RPC repro re-run (expect: 0 diagnostics for classes 1/2 on the
grammar units, no struct/macro 5070s, no 5060 in string/comment). Update
CHANGELOG `[Unreleased] › ### Fixed` per unit (done inside each unit's
commit). At delivery: forecast ~430 > ~400 → ask the user once (single PR vs
stacked chain, `ask-on-risk`).

## Acceptance criteria

- Repro snippet from #176 produces ZERO diagnostics on main + this feature.
- MQL4 grammar/behavior: no regression (mirror tests for the f suffix; 5060
  rule untouched for MQL4 documents — it never fires there).
- Golden-list parity/projection tests stay green (registries remain
  projections of the golden data).
- Full suite green, Release build clean, CHANGELOG updated per unit.

## Progress / evidence

- T1: DONE — RED observed (2 failing: `InputGroup_String_IsAccepted`,
  `InputGroup_BeforeInputs_ParsesAllFollowingInputs`; error
  `line 1:12 no viable alternative at input 'inputgro…'`, 19 pre-existing
  tests green) → GREEN after `K_GROUP` lexer keyword +
  `translationUnit › inputGroupDirective` rule (`K_INPUT K_GROUP STRING`);  
  21/21 `Mql5GrammarTests` + Release build clean (0 warnings). Full suite
  1668/1668. Commit: `5120ef8 fix(parser): accept input group directives in
  MQL5 (#176)` (g4 + tests + CHANGELOG + this doc; `src/Parser/Generated/` is
  gitignored — regenerated via normal build only).
- T2: DONE — RED observed (2 failing: `RealLiteral_FSuffix_IsAccepted`,
  `ParseVariable_RealLiteralFSuffix_ParsesWithoutErrors`; error
  `extraneous input 'f' expecting ';'` ×3 — DOUBLE+IDENTIFIER lexing split)
  → GREEN after `[fF]?` on all three DOUBLE branches in MQL5 + MQL4 mirror
  (dump evidence: constants chapter silent on the suffix, official matrixf
  samples use `1.0f`-style literals; 123f stays rejected). 48/48 filtered
  tests + Release clean. Full suite 1670/1670. Commit: `90f139c fix(parser):
  accept f/F real-literal suffixes (#176)`.
- T3: DONE — RED observed (25 failing: 12 struct pins + 12 macro pins +
  end-to-end `Mql5Document_WithStructTypesAndPredefinedMacros_DoesNotFlagThem`)
  → GREEN after +24 `variables` entries (12 struct types incl.
  `MqlTradeCheckResult`/`MqlCalendar*` beyond the minimum gate set; full
  documented macro set incl. `__MQL5BUILD__` alias), provenance
  `mql5-reference-structs-macros-2026-10-21`, JSON round-trip byte-stable
  (indent=2). 286/286 filtered + Release clean. Full suite 1695/1695. Commit:
  `903cce6 fix(data): add MQL5 builtin struct types and predefined macros
  (#176)`.
- T4: DONE (delegated to gentle-ai-worker at context backstop; writer
  verified line-by-line). RED observed (with HEAD text-scan rule temporarily
  restored: 3 failing — `StringLiteral_Mention_IsNotFlagged`,
  `TrailingComment_Mention_IsNotFlagged`, `MacroBody_CaptureContract_IsPinned`;
  representative: `5060 (0,17)-(0,22) MQL4-only variable 'Close' …` inside the
  format literal) → GREEN: `Check` walks `context.File?.Occurrences`
  (null → yield break), Ordinal registry lookup, defensive boundary checks,
  JD-1 call-position skip retained, whole-line-comment skip deleted as dead
  code, messages/severity/code unchanged, ranges derived from lexer
  positions. Capture contract observed + pinned: `#define X Close` yields NO
  Close occurrence (preprocessor hidden channel 1). Phase-1 pins replaced
  with #176 justification comments. 24/24 filtered + Release clean. Two
  verified column corrections vs the task doc (Digits (1,13,6); Ask
  (1,15,3)). Full suite 1697/1697. Commit: `5a7ccb8 fix(lsp): stop flagging
  MQL4-only names inside strings and trailing comments (#176)`.
- T5: pending

## Next step

Delegate T1 to `gentle-ai-worker` (RED-first protocol embedded in the task
text); parent closes each unit with full suite + work-unit commit.
