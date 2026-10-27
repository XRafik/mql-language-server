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
- **L4** (T1 evidence, reproduced by the parent, not only by the writer): focused filter
  `dotnet test --filter "FullyQualifiedName~IncludePathResolverTests|FullyQualifiedName~SystemIncludeFixtureTests"`
  measured 3 failed / 31 passed / 34 total, and the three failures are exactly the new pins:
  `TryResolveContained_QuotedParentRelativeInsideSystemRoot_Resolves` (`Assert.True() Failure /
  Expected: True / Actual: False`), `Diagnostics_Mql5_CommittedFixture_QuotedParentRelativeInclude_
  SuppressesUndeclaredSymbolsAsync` and its Mql4 twin (collection holds `Undeclared symbol
  'CArrayLongSynth' is not defined in this document.` — the false 5070/1070 of S2). The three
  controls were green already. Committed as `eee72e2`.
- **L5** (self-check on this session's own reasoning): mid-task the parent announced a "correction"
  claiming the containment base is the includer FILE path rather than its directory, which would
  have implied the committed fixture's same-directory quoted hop (`Expert\ExpertLib.mqh`) never
  resolved. That correction was wrong and is retracted: `PathSecurity.IsContainedInWorkspace` takes
  `Path.GetDirectoryName(Path.GetFullPath(includingFile))`, so L2 as written stands, the fixture
  README claim is accurate, and the only rejected shape is a hop into a DIFFERENT directory of the
  same root. Consequence: task #5 (differential check against a scratch copy of the base commit for
  the same-directory shape) lost its premise and is dropped; the differential evidence is now the
  pre-existing pins (`TryResolveContained_ExistingSiblingInclude_Resolves`,
  `..._InsideSubdirectoryOfIncluderDir_Resolves`, `..._QuotedBackslashSeparator_Resolves`,
  `PathSecurityTests`, `CrossFileTests`, `RealWorldParsingTests`) staying green plus the suite-count
  delta being exactly the six new tests (base measures 1,762, so the expected total is 1,768).
- **L6** (T2 implemented and committed as `75f65ff`): new `PathSecurity.IsContainedInCommonSystemRoot(
  includingFile, resolvedPath, systemIncludeRoots)` — one and the same declared root must contain
  the includer's directory and the resolved target, with the #21b fail-closed rule kept for the
  queried path and the trusted-reference lexical fallback for the includer directory and the root;
  `IncludePathResolver.TryResolveContained` now accepts either containment shape
  (`IsContainedInWorkspace || IsContainedInCommonSystemRoot(…, CurrentSystemIncludeRoots)`) and
  still clears nothing on the false path. `IsContainedInWorkspace` itself is untouched, so every
  `PathSecurityTests` pin on it holds unchanged. Focused set green: 61/61 over
  `IncludePathResolverTests|SystemIncludeFixtureTests|PathSecurityTests|SystemIncludeResolutionTests|DiagnosticHandlerCrossFileSuppressionTests`.
- **L7** (consequence worth naming for review): a workspace document that itself sits inside a
  declared system include root now follows its parent-relative quoted hops too. That is the same
  trust unit the root already grants through `WorkspaceRoots.IsReadAllowed`, and it partially covers
  the L3 case — the general workspace-sibling case (no root involved) is still rejected and stays a
  follow-up candidate.
- **L8** (post-mutation graph checks and the direct guard pins they forced): the impact check on
  `TryResolveContained` (62 dependents; the exposed `change` op was unavailable, so `analyze
  kind:impact` served as the pre-mutation check) listed four production consumers of the shared
  service — didOpen lazy indexing, the Tier-1 suppression closure, `WorkspaceIndexer
  .ResolveWorkspaceInclude` and `Mql5AntlrParser.ParseFileWithIncludes` — which is why the fix sits
  in the service instead of in either branch site: all four agree by construction, and the MQL5 +
  MQL4 fixture pins plus the fail-open control observe the agreement through the public surface.
  `change(detect)` on the unstaged tree flagged the new guard as having no direct coverage, so
  `PathSecurityTests` gained five pins of its own (same root admitted; escaped target rejected;
  two different declared roots rejected; no roots declared rejected; null/empty pairs rejected on
  the existing theory pattern). Measured after that: Release build 0 warnings / 0 errors, full suite
  1,776 passing — base 1,762 + the six #186 pins + the eight guard-pin cases, so the delta is
  exactly the new tests and no pre-existing test changed behaviour.
- **L9** (delivery shape and review routing): three work-unit commits on
  `fix/186-quoted-parent-relative-closure` — `eee72e2` (RED pins + committed fixture header,
  266 lines), `75f65ff` (the guard + service change, the five direct guard pins, CHANGELOG and the
  three READMEs, 250 lines), `002da87` (this tracking document). The branch total (533 authored
  lines) passed the ~400-line review slice, so the boundary is recorded here and the delivery unit
  for review is the pair `eee72e2..75f65ff`: the tests-to-behaviour slice is `c1`+`c2`, and `c3` is
  bookkeeping that can be dropped from a review slice without changing the diff's meaning.
  `gentle_review assess` reports `nativeRisk: unassessable` (reason code
  `package-local-binary-missing`: the native assessment is unavailable in this environment, so no
  native tier was computed), and the agent escalation of Task Size item 2 (guard / trust boundary)
  was applied, yielding risk `high` with the plan "writer self-verifies + a separate independent
  verifier runs". The independent verification is delegated to a read-only `gentle-ai-verify` task
  over this document (T1-T4, S1-S5) rather than asserted here.
- **L10** (T4 scope adjustment): the `tools/verify-system-includes.py` probe variant for the quoted
  form is NOT added, deliberately. That script runs against a real terminal installation and asserts
  over the user's own `MQL5/Include` tree; exercising a quoted parent-relative hop there would mean
  writing into the user's terminal directory, which the tool's contract forbids (it only reads, and
  the real Standard Library's cross-directory convention is the angle form — which is exactly why
  #186 stayed invisible in that evidence). The committed synthetic chain
  `tests/fixtures/system-include/Include/Expert/ExpertQuotedEntry.mqh` is the durable evidence for
  S2 instead, and it is what the MQL4/MQL5 pins run against.
- **L11** (independent verifier verdict and the two things it asked for): the read-only
  `gentle-ai-verify` pass returned **no blockers** — S1, S2, S3, S4, S5, docs-vs-code and test
  quality all `pass`, measuring focused 69/69, Release build 0 warnings / 0 errors and full suite
  1,776, and confirming line by line that the new guard reuses the #21b fail-closed statement on the
  queried path, that its admission set is a subset of what `WorkspaceRoots.IsReadAllowed` already
  allows (`SystemIncludeRoots.ContainsPath` delegates to the same `PathSecurity.IsUnderAnyRoot`),
  that the two closure consumers evaluate the identical predicate per hop so they cannot disagree,
  and that every control pin would fail against an over-permissive implementation. It named two
  gaps, both closed by the follow-up commit on this branch:
  1. **missing pin** — `IsContainedInCommonSystemRoot` had no symlink case, so an implementation
     that dropped the fail-closed `TryResolveRealPath` on the queried path would still have passed
     all eight guard cases. Three pins added, mirroring the pre-existing `IsContainedInWorkspace`
     trio: `..._SymlinkInsideRoot_True` (containment judged on the real path),
     `..._SymlinkEscapingRoot_False` (a link under the root cannot be turned into an arbitrary
     read), `..._SymlinkLoop_FailClosed` (unresolvable queried path is rejected, never allowed).
  2. **hygiene nit** — the new guard used `Path.GetDirectoryName(...)!` where
     `IsContainedInWorkspace` keeps a null check, so a degenerate includer path (a filesystem root)
     could null-deref inside `IsDescendant`. The check is added with the same reasoning.
  Two limits it recorded, kept here because they are honest limits of the evidence: the RED
  measurement and the 1,762 base were not re-run, because reproducing either requires checking out
  another commit and the task was read-only; the structural corroboration is that `eee72e2` carries
  zero production changes and every test hunk on this branch is a pure addition (0 deletions).
- **L12** (delivery): pushed as `fix/186-quoted-parent-relative-closure`; PR
  davalillo/mql-language-server#199 with `type:bug` and `Closes #186`, and #186 labelled
  `status:approved` on the user's explicit choice ("Aprueba #186 y ciérralo", single-PR slice).
  First CI cycle green: Build and Test, Analyze (csharp), VS Code Extension, CodeQL, and
  `mergeable: MERGEABLE`. Merge remains the user's decision.
