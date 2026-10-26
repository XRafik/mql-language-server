# Scope-aware symbol resolution (#184, with the #183 pin repair)

A name lookup is scope-blind: `file.Symbols` is a flat list and `FindSymbolsByName` returns the first
match. After #182 made declarations win over the builtin registry, a local declared in one function
shadows a same-named builtin everywhere in the document. Fix the ordering, not the registry.

## Specs

**S1 — Enclosing declaration wins.** For an identifier at a position, a candidate whose `Range`
contains that position takes precedence over every candidate that does not. Innermost wins when two
ranges are nested.

**S2 — Document-level before foreign-nested.** With no enclosing candidate, a candidate declared at
document scope (its `Range` is not contained in any function/method declaration's `Range`) beats a
candidate declared inside another function body.

**S3 — Registry last.** The builtin registry stays the final fallback, exactly as #182 left it.

**S4 — Included symbols are document-level.** Symbols merged from other files (a `FilePath` other
than the current document) compete in the S2 document-level class; a nested local of the current
document never beats them.

**S5 — Deterministic tie-break.** Within one precedence class, file order decides, so repeated
lookups return the same symbol (the existing behavior).

**S6 — No performance regression.** The decision must not turn every lookup into a full symbol scan:
reuse or extend the existing per-file symbol index/cache, and keep `PerformanceTests` green.

**S7 — The #183 pin is repaired.** `FindSymbolDefinition_LocalVariableInsideFunction_ReturnsNull`
(MQL5) currently passes vacuously: its cursor lands on whitespace (`IndexOf("int x") + 5` → index 16,
the space after `x`), so `ExtractIdentifierAtPosition` returns null before any lookup, and its comment
claims the visitor only extracts top-level declarations — false, both visitors index locals. Position
the cursor on the identifier and assert the resolved local, or restate the pin around the real
contract. Do not add cases: this is a repair.

**S8 — Both dialects, both surfaces.** Every rule is pinned at the parser level for MQL4 and MQL5,
and the user-visible consequence is pinned through `HoverHandlerTests`: hovering a builtin-named use
in a function that does not declare it returns the builtin, while the enclosing declaration still
wins inside its own function. The #182 pins stay green untouched.

## Tasks

- T1 — RED pins for S1, S2, S4 in both parser suites.
- T2 — Implement S1/S2/S4/S5/S6 in `FindSymbolDefinition` for both dialects.
- T3 — Reproduce and repair the S7 pin (RED first: show it does not test what its name says).
- T4 — Handler-level pins (S8) and full-suite verification.

## Log

L1 (user, 2026-10-26): after reconciling the orphan branches, continue with "#184 + #183: resolución de
símbolos con scopes" — the ordering already agreed when the backlog was triaged.

L2 (parent, 2026-10-26): precedence is decided per lookup position, not per name: a nested declaration
only wins where its range encloses the use. That keeps #182's fix intact (there the use *was* inside
the declaring function) while removing the cross-function shadowing #184 reports.
