# Issue #179 — system-include configuration without friction

Reduce the configuration friction of `systemIncludeRoots` (issue #179, gap 2) and protect Tier 1
against regression (gap 3). Explicit configuration only: no implicit filesystem discovery.

## Specs

**S1 — Project configuration file.** The server reads `.mql-lsp.json` from each workspace folder
root during `initialize`; the set is captured once per session and editing the file requires a
server restart (same contract as `.mqlignore`, no watcher). Recognized key: `systemIncludeRoots`,
an array of strings. Entries may be absolute or relative; a relative entry resolves against the
directory containing the file, so the file stays committable. A missing file is silent; a
malformed or unreadable file (invalid JSON, wrong types, I/O error) is ignored entirely — the
session degrades to fail-open and a warning is logged. Loading must never throw.

**S2 — Precedence.** A non-empty `systemIncludeRoots` initialization option supplied by the client
is the effective set and the project files are not consulted. Otherwise the union of the project
files across workspace folders (normalized via `Path.GetFullPath`, deduplicated) is the effective
set. With neither source, behavior is exactly today's fail-open mode:
`Resolve_AngleBracketEntry_NoRoots_ReturnsNull_TodaysBehavior` keeps passing unchanged.

**S3 — No implicit discovery (non-goal).** No ancestor walk for a `MQL4`/`MQL5` directory and no
probing of installed terminals (`%APPDATA%\MetaQuotes\...`, Wine prefixes, macOS paths). The roots
come from exactly two explicit sources: the client option and the project file.

**S4 — Actionable diagnostic.** Each angle-bracket `#include <...>` entry that fails to resolve
produces exactly one diagnostic: severity `Warning`, `Source = "mql-lsp"`, code following the
existing numeric scheme (`Mql4DiagnosticBase`/`Mql5DiagnosticBase` + the next free offset, pinned
by a test), range covering the include directive, and a message that names the include entry and
both remedies (configure `systemIncludeRoots`, or declare it in `.mql-lsp.json`). Existing
diagnostics are unchanged: undeclared-symbol (`1070`/`5070`) diagnostics stay exactly as they are;
nothing is suppressed.

**S5 — Synthetic Tier-1 fixture.** A committed fixture under `tests/fixtures/system-include/`
reproduces the *shape* the real Standard Library exercises — a class with inheritance, reached
through a transitive include chain (the `CTrade`/`CArrayLong` pattern) — using project-authored
code only, no MetaQuotes content. A regression test resolves a document that includes them through
angle brackets with roots pointing at the fixture and asserts the undeclared-symbol diagnostics are
suppressed.

**S6 — Opt-in evidence script.** `verify-system-includes.sh` takes the path to a real
`MQL5/Include` tree, drives a document that includes `<Trade\Trade.mqh>`, asserts the symbols
resolved through that header produce zero symbol diagnostics, and emits JSON evidence. It exits
non-zero with an actionable message when the given path lacks the headers. It is not referenced by
any workflow and never runs in CI.

**S7 — Documentation.** `README.md`, `README.es.md` and `README.ru.md` describe the two sources,
the precedence rule, relative-path resolution and the restart requirement. `CHANGELOG.md` gains an
`[Unreleased]` entry.

## Tasks

- T1 — `.mql-lsp.json` loader: parse, relative resolution, fail-open. (S1)
- T2 — Wire into `initialize` with the S2 precedence. (S2)
- T3 — Unresolved system-include warning diagnostic. (S4)
- T4 — Synthetic fixture + Tier-1 regression test. (S5)
- T5 — `verify-system-includes.sh` opt-in script. (S6)
- T6 — Docs in three READMEs + CHANGELOG. (S7)

## Log

L1 (user, 2026-10-23): "En la opción A, volvemos a lo de siempre... No debería ser el cliente el que
añada las rutas de los includes? Como fallback, creo que lo mejor sería un archivo .mql-lsp.json" —
decision on gap 2: the client remains the primary source; `.mql-lsp.json` is the fallback. Options
A2 (derive from layout) and A3 (probe terminal installs) rejected.

L2 (user, 2026-10-23): chose B2, "warning accionable por include": warn, do not suppress. B3
(suppressing `1070`/`5070`) rejected.

L3 (user, 2026-10-23): chose "las dos cosas" for gap 3: opt-in script against the real terminal plus
a synthetic fixture for CI.

L4 (parent, 2026-10-23): relative entries resolve against the config file directory (keeps the file
committable); the client option wins when non-empty; the file is read once per session at
`initialize`. Diagnostic uses the repo's numeric code convention with `Source = "mql-lsp"` rather
than a string code.

L5 (parents, 2026-10-26, from the delivered units):
- Diagnostic codes are `1004` (MQL4) / `5004` (MQL5); offset `003` stays reserved because the
  retired underscore rule's absences are still asserted by existing tests.
- The warning fires even when no roots are configured — that is exactly the case where naming both
  remedies matters most (consistent with L2).
- `#include <...>` text inside a `/* */` block comment would warn: the rule is a line scan that only
  guards `//`. Accepted as a known edge; a block-comment guard is a follow-up if it ever bites.
- Fixture chains must use same-directory quoted includes or root-relative angle includes: the
  containment guard rejects a quoted `..\` hop out of the includer's directory (issue #186,
  mechanism confirmed there).
- Evidence script validated against a synthetic stand-in only; no real MetaTrader installation was
  available on this machine, so the first real-terminal run is still owed.

L6 (parent, 2026-10-26, delivery slices): measured against `origin/main`, four behavior slices plus
one docs slice —

| Commit | Slice | Files | Lines |
|---|---|---|---|
| `e0907bb` | config file + precedence (S1, S2) | 2 src + 2 test | 749 |
| `5f3f7b8` | unresolved-include warning (S4) | 1 src + 1 test | 342 |
| `b407d3a` | Tier-1 fixture + regression suite (S5) | 4 fixture + 1 test + README | 350 |
| `92b0041` | opt-in evidence script (S6) | 2 tools | 536 |
| docs | three READMEs + CHANGELOG (S7) | 4 | ~100 |

Production code is 401 lines; everything else is tests, fixture headers, the script and docs (feature
total ~2,077). That is past the ~400-authored-line slice guidance for a single review, so delivery is
one PR (slices are thematically separable) or two chained PRs (S1+S2+S4 behavior, then S5+S6
verification assets with the docs). The decision belongs to the user.

L7 (parent, 2026-10-26, end-to-end verification of the wiring no unit covered): a scripted stdio
session against the Release build proved — (a) `.mql-lsp.json` with a **relative** root supplies the
roots with no client option (0 diagnostics total); (b) with no configuration anywhere, the document
gets exactly one `5004` warning plus the one `5070` for `CTrade`; (c) a valid project root plus an
empty client root produces the same one warning, proving the client option wins and the project file
is not consulted. Full suite 1,742 passing on the integrated tree.
