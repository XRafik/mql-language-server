# Cross-file typeDefinition/definition degrades permanently after didChange (issue #86)

Branch: `fix/86-didchange-include-invalidation` (base: `origin/main`) · Issue: #86 · Related: #62, #64 (PR #68), #76 (PR #80), #78

## Problem

On v2.4.2, any `textDocument/didChange` on the queried file degrades its
cross-file `typeDefinition`/`definition` resolution for the rest of the
session; `didClose` + `didOpen` with full text does not recover (only a server
restart does). Also: when the include file was never opened / not indexed at
the moment the queried file's context is built (report matrix scenarios 3-5),
resolution falls back to the variable declaration.

Reporter evidence: deterministic 5-scenario matrix on the #62 fixture
(`main.mq5` includes `person.mqh`, `Person person("Alice", 30);`), raw stdio
client, identical bytes across arms, v2.4.2.

## Root cause (confirmed by raw-stdio reproduction against the real binary, 2026-09-23)

The reporter's mechanism hypothesis (per-file resolution context not rebuilt on
didChange) was WRONG; the triage rule held. Actual defect — **buffer vs disk
divergence**:

- `TypeDefinitionHandler` / `DefinitionHandler` read the document text from
  disk (`SourceFileReader.ReadAllText(filePath)`) while the parse model comes
  from `OpenDocumentStore` (didOpen/didChange editor buffer).
- `FindSymbolDefinition(mqlFile, content, line, column)` extracts the cursor
  identifier from raw `content` (Mql4AntlrParser.ExtractIdentifierAtPosition)
  and looks it up in the model. With an unsaved buffer edit (didChange content
  ≠ disk), identifier extraction runs on stale text → mismatch → null → `[]`
  permanently; reopen keeps the mismatch; only a server restart re-derives
  both from the same source. Reproduced via /tmp/issue86_driver.py scenario B2
  (rename of the variable on the probe line, disk stale): `[]` at +2s/+7s and
  after reopen.
- Fix: handlers must answer from the store's content (same source as the
  model) when the document is open; disk is only for never-delivered docs.

Secondary observations:

- `DidOpenTextDocumentHandler`'s include loop (lines 134-156) reads includes
  from DISK and registers them; `DidChangeTextDocumentHandler.UpdateIncludes`
  is inert by design comment ("delegated to DidOpen / workspace scan") and the
  workspace scan indexes everything at initialize, so the include graph stays
  populated in practice. Did NOT reproduce as a failure in any matrix arm.
- Reporter's scenarios 3-5 (delivery-channel/open-order fallback) did NOT
  reproduce either in-process or against the real binary with the #62/#78
  fixture (all arms resolve to person.mqh). Their exact fixture/harness delta
  is unexplained; ask the reporter for it (bucket E for that half) before
  claiming closure of those arms.

Earlier static analysis (superseded by the above):

- `DidChangeTextDocumentHandler.HandleForLanguage`
  (src/Lsp/Handlers/DidChangeTextDocumentHandler.cs:112-116) re-indexes the
  file with `SymbolIndex.Index.AddFile(...)` then calls `UpdateIncludes()`,
  which is inert by design ("delegated to DidOpen / workspace scan").
- Include registration (resolve path, parse from disk, index under includer's
  language key) exists only in `DidOpenTextDocumentHandler` (lines 134-156).
- `GlobalSymbolIndex.AddFile` replaces the file's symbols/occurrences
  wholesale; dependency-edge (_fileDependencies) behavior on re-AddFile TBD
  in diagnosis.
- Reporter's mechanism is a hypothesis; symptom only is evidence (triage
  rule). Reproduce before implementing; test written against the reported
  mechanism must FAIL on unmodified main in the reported way.

## Plan

1. [ ] ODD tracking doc + Engram mirror
2. [ ] Diagnosis: why didChange degrades; why reopen does not recover;
       scenario-5 mode (include never opened) — separate or same root
3. [ ] Reproduction tests (matrix A + degradation B + reopen-recovery)
       failing on main as reported
4. [ ] Root fix: shared include-registration routine reused by didChange
       (reuse/relaxation, no new machinery)
5. [ ] Full suite green; work-unit commit(s); detect_changes before commit
6. [ ] Push + PR; comment on #86 naming both failure modes and closing tests
## Verification (independent, 2026-09-24)

gentle-ai-verify verdict: **verified**, no blockers. Full suite 1243 green.

- (a) Store-first semantics: model+content only ever written atomically via
  AddOrUpdate (didOpen/didChange) — the desync is structurally impossible on
  the store path. (Cosmetic: TryGetDocumentContent's bool is always true.)
- (b) Fallback symmetry: byte-identical in both handlers, only reachable when
  FindSymbolDefinition returns null (no case-insensitive in-file match).
- (c) Cross-file defContent read stays on disk — correct; found-symbol loop
  unchanged.
- Test pinning: the 2 disk-stale tests pass WITHOUT the fallback (store fix
  alone), the 2 probe-on-Person tests pass ONLY with the fallback — both
  defects pinned independently. (Pre-fix RED was observed by the writer at
  TDD time.)

### Follow-up (low severity, not fixed here)

Unguarded false-positive path: an identifier with no in-file declaration
whose exact text equals a Class/Struct/Interface/Enum name in another
workspace-indexed file now resolves there instead of null (e.g. free
function `Format(string)` + foreign `class Format`). Navigation hint only;
extends the accepted #64 exact-case heuristic. No test covers it; tracked as #89.
