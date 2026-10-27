# Issue #186 — quoted parent-relative nested include inside a system root

Feature: make `#include "..\Arrays\ArrayLong.mqh"` written inside `<systemRoot>/Trade/Trade.mqh`
contribute its symbols to the include closure in both MQL4 and MQL5, without moving the trust
boundary that issues #18/#21b established for quoted includes outside any declared root.

## Specs

- **S1** (issue #186, Expected, verbatim): "Both forms should feed the include closure when the
  resolved target stays inside a configured system root: `ResolveWithSystemRoots` already resolves
  a quoted parent-relative entry to `<root>/Arrays/ArrayLong.mqh` (`IncludePathResolver`, quoted
  branch: normalize separators, combine with the includer's directory, `Path.GetFullPath`). So the
  gap is downstream of that resolution, in whichever path decides what to merge or index".
- **S2** (issue #186, Observed, verbatim): "Observed: `CTrade` is suppressed correctly (Tier 1
  works), but `CArrayLong` is flagged `5070 Undeclared symbol`." The reviewed document is
  `#include <Trade\Trade.mqh>` + `void OnStart(){ CTrade t; CArrayLong a; }` with roots =
  `/tmp/mql-standin/Include`, and the nested directive inside `Trade/Trade.mqh` is
  `#include "..\Arrays\ArrayLong.mqh"`. After the fix the same tree must yield **zero** `5070`s, the
  way the angle form `#include <Arrays\ArrayLong.mqh>` already does.
- **S3** (issue #186, Affected language, verbatim): "Both". The behaviour must be pinned for MQL4
  and for MQL5.
- **S4** (unchanged contracts, quoted from the pins that already exist on `main`): a quoted entry
  that escapes every declared root stays rejected — "Escape target exists on disk but is OUTSIDE
  the includer's directory tree — the PathSecurity containment guard must reject it"
  (`TryResolveContained_PathTraversalOutsideIncluderDir_ReturnsFalse`); the system branch "must
  never claim" quoted entries (`TryResolveSystemInclude_QuotedEntry_ReturnsFalse`); angle entries
  stay out of the contained branch (`TryResolveContained_AngleBracketSystemInclude_ReturnsFalse`);
  "A system entry must never escape the configured root"
  (`Resolve_AngleBracketEntry_TraversalUnderRoot_Rejected`); same-directory and subdirectory quoted
  resolution keeps working (`TryResolveContained_ExistingSiblingInclude_Resolves`,
  `TryResolveContained_InsideSubdirectoryOfIncluderDir_Resolves`,
`TryResolveContained_QuotedBackslashSeparator_Resolves`).
- **S5** (issue #186, Impact, verbatim): "The risk is a real library header that uses the quoted
  form — then its transitive symbols stay unresolved and produce exactly the false `1070`/`5070`
  diagnostics #179 is about." Nothing is suppressed by this change and the `1070`/`5070` output for
  the shapes that work today stays byte-for-byte unchanged; the `1004`/`5004` unresolved-include
  warnings from #179 are untouched.

## Tasks

- T1 — S2, S3, S4 — inline: RED tests. Containment-level pin (quoted parent-relative entry that
  lands inside a declared system root, resolver level) plus a closure-level pin for the `5070`
  symptom in both dialects, before any production change. Commit: `c1`.
- T2 — S1, S4 — inline: GREEN. Admit a quoted entry whose resolved target stays inside a declared
  system include root that also contains the includer, keeping the includer-directory rule for
  everything outside a declared root. Commit: `c2`.
- T3 — S1, S2 — inline: agreement check across the two closure consumers
  (`DidOpenTextDocumentHandler.IndexIncludes` push/lazy-index path and
  `CrossFileSymbolCorrelator.CollectIncludeClosureDeclaredNames` pull/suppression path) plus the
  committed `tests/fixtures/system-include/` chain extended to the quoted parent-relative form.
  Commit: `c2`.
- T4 — S2, S5 — inline: CHANGELOG + docs, `tools/verify-system-includes.py` probe variant for the
  quoted form, focused tests, full suite, build, memory save. Commit: `c3`.

## Log

- **L1** (user, verbatim): "arranca con #186" — following the prior decision "¿Qué cogemos como
  siguiente trabajo? → #186: includes anidados "..\"".
- **L2** (diagnosis, this session): the defect is the junction of the two branches, not one of the
  two candidates the issue names. `PathSecurity.IsContainedInWorkspace` compares the resolved path
  against the *includer's directory* (`IsDescendant(realIncludingDir, realResolvedFull)`), so a
  sibling directory inside the same system root is rejected; `TryResolveSystemInclude` cannot
  recover it because it receives only the entry, never the includer path, and
  `ResolveSystemIncludeEntry` rejects any `.`/`..` segment. Both closure consumers do
  `TryResolveContained(...) || TryResolveSystemInclude(...)` and then `continue`, which is why the
  symbol disappears instead of failing loudly.
- **L3** (derived finding, scope question): by construction the same rejection applies to workspace
  documents — a quoted `..\common\util.mqh` between sibling folders inside a declared workspace
  folder is readable (`WorkspaceRoots.IsReadAllowed`) yet dropped by the containment guard. That is
  wider than S1 and is not part of this feature unless the user asks for it; candidate for a
  follow-up issue.
