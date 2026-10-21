# Feature: issue-172-incoming-calls-mql5

## Objective

Fix GitHub issue #172: `callHierarchy/incomingCalls` returns no callers for any
MQL5 file (reported as a template-function symptom), while
`textDocument/references` resolves the same call sites.

## Problem and root cause (verified)

`GlobalSymbolIndex.GetFileSymbols(string filePath)` (1-arg overload,
`src/Lsp/Server/GlobalSymbolIndex.cs:461`) defaults `language: MqlLanguage.Mql4`.
Production indexing keys symbols by `(file, language)` (Mql5 for `.mq5`;
`.mqh` routed by includer). `CallHierarchyHandler.FindEnclosingFunction`
(`src/Lsp/Handlers/CallHierarchyHandler.cs:427`) and `BuildItemFromIndex`
(`:477`) use the 1-arg overload, look up `(file, Mql4)`, get null, and drop
every caller for non-MQL4 files.

Evidence (Release build, stdio JSON-RPC repro):
- `references(mean)` on `.mqh` template → 2 call sites (works: name-keyed occurrences, language-independent).
- `incomingCalls` on `.mq5` (template AND plain function) → empty.
- Identical scenario as `.mq4` → caller found. Asymmetry proven.
- `GetFileSymbols(path)` → null vs `GetFileSymbols(path, Mql5)` → symbols present after identical `AddFile`.

`TypeHierarchyHandler.ResolveItemSymbol`
(`src/Lsp/Handlers/TypeHierarchyHandler.cs:256`) has the same defect pattern in
its index fallback: it resolves `language` for the parser but calls the 1-arg
`GetFileSymbols(filePath)`.

Why existing tests miss it: `Issue96CallHierarchyTests.Create()` indexes under
`MqlLanguage.Mql4`, matching the broken overload's default.

## Scope

- `src/Lsp/Handlers/CallHierarchyHandler.cs` — resolve per-file language and call the language-keyed `GetFileSymbols` overload in `FindEnclosingFunction` and `BuildItemFromIndex`.
- `src/Lsp/Handlers/TypeHierarchyHandler.cs` — same fix in `ResolveItemSymbol`'s index fallback (reuse the already-resolved `language`).
- New regression tests `tests/Lsp/Handlers/Issue172CallHierarchyMql5Tests.cs` (pattern: `Issue96CallHierarchyTests`).
- `CHANGELOG.md` entry (user-visible fix).

Non-goals: no changes to `GlobalSymbolIndex` semantics (the 1-arg overload's
Mql4 default is legacy API surface; call sites are fixed instead); no grammar
or parser changes; no README/docs sync beyond changelog (no user-facing feature
docs affected).

## Constraints

- MQL4 behavior must not regress (MQL4 positive control exists in Issue96 tests).
- `.mqh` headers keep includer-routed language keys (D2 design): use the
  established `ResolveLanguage` machinery of `LanguageAwareHandlerBase`.
- Occurrence/name-keyed behavior (OCC-05) untouched.
- No hand-edits under `src/Parser/Generated/`.

## TDD mode

- Mode: ON, observed RED → GREEN. Source: AGENTS.md §9 bug-fix workflow
  (reproduce → focused test → fix). Runner: `dotnet test --filter
  FullyQualifiedName~Issue172CallHierarchyMql5`.

## Route declaration

- T1, T2: delegated to `gentle-ai-worker` — Multi-file write rule (3 non-trivial files: 2 handlers + new test file).
- T3: inline — gentle-ai-verify not admitted (no declared high-risk item); full suite + Release build run inline per its routing rule.
- T4: inline — mechanical (changelog + work-unit commit).

Delivery strategy: `ask-on-risk` (default). Forecast authored changed lines:
~150 (fix ~10, tests ~120, changelog ~5). Under 400 — no chaining needed.

## Tasks

- [x] T1: Add failing MQL5 regression tests (RED)
  - New `tests/Lsp/Handlers/Issue172CallHierarchyMql5Tests.cs`:
    - `IncomingCalls_Mql5PlainFunction_FindsCallerAsync`: `.mq5` with `helper` + `OnStart`, expect incoming call from `OnStart`.
    - `IncomingCalls_Mql5TemplateInInclude_FindsCallerAsync`: the issue #172 scenario — `.mqh` template function + `.mq5` caller under Mql5 keys, expect caller.
  - Index/store setup mirrors `Issue96CallHierarchyTests.Create()` but with `MqlLanguage.Mql5`.
  - Run with filter; observe RED (empty incoming calls) BEFORE any handler fix.
- [x] T2: Fix language-keyed lookups (GREEN)
  - `CallHierarchyHandler.FindEnclosingFunction` + `BuildItemFromIndex`: resolve language per file via `ResolveLanguage` and call `GetFileSymbols(filePath, language)`.
  - `TypeHierarchyHandler.ResolveItemSymbol`: use the resolved `language` for its `GetFileSymbols` fallback.
  - T1 tests pass (GREEN); existing Issue96 tests still pass.
- [x] T3: Broader verification
  - Full `dotnet test` suite + `dotnet build -c Release`.
  - (Shared infrastructure `GlobalSymbolIndex` touched → full suite required by AGENTS.md §3.)
- [x] T4: Changelog + work-unit commit
  - `CHANGELOG.md` entry under Fixed.
  - One work-unit commit (tests + code + changelog together, Conventional Commit: `fix(lsp): ...`).

## Acceptance criteria

- MQL5 incoming calls return callers (plain + template-in-include scenarios).
- MQL4 incoming calls unchanged (Issue96 suite green).
- Full test suite green; Release build clean.
- Changelog documents the fix.

## Progress / evidence

- T1: DONE (RED observed). New `tests/Lsp/Handlers/Issue172CallHierarchyMql5Tests.cs` with both `[Fact]`s
  (`IncomingCalls_Mql5PlainFunction_FindsCallerAsync`, `IncomingCalls_Mql5TemplateInInclude_FindsCallerAsync`),
  mirroring `Issue96CallHierarchyTests.Create()` but with `MqlLanguage.Mql5` keys and `Mql5AntlrParser`;
  the `.mqh` is indexed under its includer's Mql5 key. Command:
  `dotnet test --filter FullyQualifiedName~Issue172CallHierarchyMql5` → exit 1,
  `Con error: 2, Superado: 0, Omitido: 0, Total: 2`. Both failures are the expected empty incoming calls:
  `Assert.Single() Failure: The collection was empty` at the incoming-calls assertion in each test
  (PlainFunction line 162, TemplateInInclude line 189) — prepare itself succeeds (item name asserts pass),
  so the RED is precisely the dropped-caller defect, not a broken prepare. No handler code touched (T2 pending).
- T2: DONE (GREEN observed). `CallHierarchyHandler`: new private helper
  `GetFileSymbolsForQuery(string filePath)` resolves the language via the base
  class `ResolveLanguage(new Uri("file://" + filePath))` (open-store → .mqh
  includer routing → disk sniffing, the same machinery used at indexing time)
  and calls the language-keyed `SymbolIndex.Index.GetFileSymbols(filePath, language)`;
  both `FindEnclosingFunction` and `BuildItemFromIndex` now go through it
  (no public API change). `TypeHierarchyHandler.ResolveItemSymbol`: the index
  fallback now calls `GetFileSymbols(filePath, language)` using the
  already-resolved `language` variable. Evidence:
  - `dotnet test --filter FullyQualifiedName~Issue172CallHierarchyMql5` → exit 0,
    `Con error: 0, Superado: 2, Omitido: 0, Total: 2` (both T1 tests GREEN).
  - `dotnet test --filter FullyQualifiedName~Issue96CallHierarchy` → exit 0,
    `Con error: 0, Superado: 6, Omitido: 0, Total: 6` (MQL4 positive control intact).
  - `dotnet build -c Release` → exit 0, 3 projects, 0 errors, 0 warnings.
  Full suite deferred to T3 (shared `GlobalSymbolIndex` lookup path touched).
- T3: done — full suite 1666/1666 passed (baseline 1664 + 2 new), 0 failed; dotnet build -c Release clean (0 warnings, 0 errors)
- T4: done — CHANGELOG.md `### Fixed` entry added under [Unreleased]; work-unit commit on fix/issue-172-incoming-calls-mql5 (this change)

## Next step

Delegate T1+T2 to `gentle-ai-worker` with allowed edit surfaces; then verify (T3) and commit (T4). Native review candidate: the work-unit commit (RDD on).
