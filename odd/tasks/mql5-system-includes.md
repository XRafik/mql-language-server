# Feature: system-include resolution for the MQL5 Standard Library

Final gap of the 2026-10-18 audit (see mql5-enum-constants and
mql5-stdlib-functions docs): Standard-Library classes (`CTrade`,
`CArrayObj`, `CSigmoidalMembershipFunction`, ~116 classes) resolve via
`#include <Trade\Trade.mqh>` angle-bracket includes from the terminal's
`MQL5/Include` directory. Today the server does not know that directory:
`<...>` includes do not resolve against any root, the headers are never
indexed, so 5070 fires for every Standard-Library name and
definition/hover miss them (this is the exact remediation failure
recorded in the agent-lsp incident report).

Known code facts (explored this session):
- `src/Parser/IncludePathResolver.cs` already models both include forms
  (`AngleBracketIncludeRegex`, `ExtractFromDirective`, `Resolve`) — the
  resolution root for `<...>` is the missing piece.
- `CrossFileSymbolCorrelator.CollectIncludeClosureDeclaredNames` +
  `ExtractIncludesFromRawText` follow includes via
  `IncludePathResolver.ExtractFromDirective` and consult
  `GlobalSymbolIndex` (Tier 1/1b suppression).
- `GlobalSymbolIndex.AddFile` is the indexing API; headers not in any
  workspace folder are never indexed.

Design (to implement):
- Server configuration: initialization option with a list of system
  include roots (terminal `MQL5/Include` / `MQL4/Include` dirs).
- `IncludePathResolver.Resolve`: for angle-bracket includes, try each
  configured root (separator/case normalization).
- Resolved system headers are parsed with the dialect parser and indexed
  into `GlobalSymbolIndex` (lazy, deduped, cancellation-safe), so the
  existing Tier 1/1b suppression, definition, references and hover work
  unchanged.
- Fail open: missing roots or unresolvable headers keep today's behavior.
- AGENTS.md rules: no hand edits under src/Parser/Generated; MQL4/MQL5
  symmetry; regression tests for suppression + definition.

## Tasks

- [x] T1 — This doc + Engram mirror `odd/mql5-system-includes/tasks`.
- [x] T2 — Implementation delegated to gentle-ai-worker (config plumbing,
      resolver roots, lazy header indexing, closure wiring). `systemIncludeRoots`
      init option (Program.OnInitialize, fail-open); SystemIncludeRoots provider;
      IncludePathResolver.SetSystemIncludeRootsProvider/TryResolveSystemInclude/
      ResolveWithSystemRoots; lazy deduped indexing in DidOpenTextDocumentHandler;
      correlator closure follows angle-bracket headers; WorkspaceRoots read guard
      admits system roots. Commit: 9308cda.
- [x] T3 — Regression tests (suppression + definition over a temp include root,
      MQL5 + MQL4 symmetry). +10 IncludePathResolverTests pins +
      SystemIncludeResolutionTests (12 tests: indexing, dedup, no-roots
      preservation, Tier-1 suppression both dialects, definition + hover).
      Commit: 9308cda (same work unit).
- [x] T4 — Full suite + Release build. Evidence: independent gentle-ai-verify
      run: dotnet test 1616/1616 (baseline 1600), dotnet build -c Release
      0 errors / 0 warnings, src/Parser/Generated untouched, focused 32/32.
- [x] T5 — CHANGELOG (Added entry) + README.md/README.es.md/README.ru.md
      (`systemIncludeRoots` section). Commits: 9308cda (CHANGELOG) + 5b806b9
      (READMEs).
- [x] T6 — Work-unit commits recorded; Engram summary updated.

## Deviations from spec

- Roots-accepting method named `ResolveWithSystemRoots` instead of a 3-arg
  `Resolve` overload: the overload caused CS0419 ambiguous-cref errors (with
  TreatWarningsAsErrors) in doc comments of IncludeDirectiveService.cs and
  MacroTableBuilder.cs, which are outside the edit surfaces. Behavior identical.
- Suppression/definition/hover reachability assumes didOpen ran before
  diagnostics (production ordering); pull-diagnostics for a never-opened
  document keep today's behavior.

## Evidence log

- 2026-10-18: incident remediation `add_workspace_folder` with the
  terminal Include dir did not change the 70-diagnostic baseline — the
  roots must be a server-side concept, not workspace folders.
- 2026-10-18: Gortex MCP read surface fully looped during exploration;
  further exploration delegated to the worker session.
