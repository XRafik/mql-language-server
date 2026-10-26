# Feature: issue-116-crossfile-definition-nondeterminism

GitHub issue: https://github.com/davalillo/mql-language-server/issues/116
Bug: cross-file `textDocument/definition` is non-deterministic per server session (~50% of
sessions return null permanently) on v2.5.0-rc.4. Stable within a session; references,
workspace/symbol and pull diagnostics unaffected. No didChange involved.

## Diagnosis summary (exploration, 2026-09-28)

Definition path: `DefinitionHandler.Handle` (src/Lsp/Handlers/DefinitionHandler.cs:56) ->
in-file lookup -> type-usage fallback (#89) -> cross-file fallback #92:
`IncludeSymbolResolver.TryResolve` (src/Lsp/Server/IncludeSymbolResolver.cs:41-76) ->
`GlobalSymbolIndex.FindSymbol` reading `_symbolsByName`.
The permanent null is NOT cached: it is permanent because the index write that would repair
it never happens.

Ranked root-cause candidates:
- C1 (highest): didOpen parse-reuse early return (DidOpenTextDocumentHandler.cs:103-108)
  skips `AddFile` + `IndexIncludesRecursively` when the store already holds the doc
  (e.g. a query touched the .mqh before didOpen wrote it via
  LanguageAwareHandlerBase.cs:130-140 without indexing); WorkspaceIndexer skips open
  documents, so no other writer populates `_symbolsByName`.
- C2: `IndexIncludes` (DidOpenTextDocumentHandler.cs:186-231) has no per-hop try/catch;
  one throw/cancel aborts the chain after the root -> permanently partial include graph.
- C3: GlobalSymbolIndex readers copy `List<T>` buckets without locks and
  `AddDependency` (GlobalSymbolIndex.cs:494-508) writes without `_lock` -> torn reads ->
  exception -> handler catch returns null (DefinitionHandler.cs:223-227).
- C5: rc.3/rc.4 emit every declarator; `AddFile` dedup is last-writer-wins per name
  (GlobalSymbolIndex.cs:176-186) so a local/param can shadow the function declaration.
- U6: ANTLR static shared DFA state used concurrently by singleton parsers (request
  thread + WorkspaceIndexer Task.Run + ParserWarmup Task.Run) could flip per process.

## Tasks

### Phase 1 — discriminant diagnosis (completed)
- [x] P1-1 Build current binary (rc.5 = main 1209403) + rc.4 worktree build.
- [x] P1-2 Probe harnesses: /tmp/probe116*.py (small fixture); Ducibus probe at
      ~/source/Ducibus/tooling-eval/probe_nondet_rc4.py is the authoritative repro.
- [x] P1-3 Baselines: small fixture (3 files, .mq4 includer, .mqh includer, 49-file
      project) — 24/24 OK on both rc.4 and rc.5 builds. Ducibus project with the real
      probe: 2-4 of 6 sessions null, stable per session — reproduced on rc.4, rc.5 and
      the installed binary (which is rc.5, NOT rc.4 as the issue assumed).
- [x] P1-4 Instrumentation (reverted after diagnosis): handler entry/exit, AddFile
      writers, scan skips, OmniSharp Debug-level routing logs, built-in sync-open probe.
- [x] P1-5 Root cause identified (see below).
- [x] P1-6 Phase 1 findings recorded in this document.

### Phase 1 root cause (evidence-backed)

OmniSharp 0.19.9 routes each textDocument/didOpen notification to EXACTLY ONE of two
competing handlers registered for the same method:
  1. the custom DidOpenTextDocumentHandler (real logic: store + parse + AddFile +
     include chain), and
  2. the built-in TextDocumentSync handler registered by Program.cs
     OnTextDocumentSync(..., _ => {}, _ => {}, ...) with EMPTY lambdas.
Which one wins flips per server process (~50%; consistent with per-process hash
randomization inside the router) and is stable for the whole session. Instrumented
evidence (Ducibus probe, 6 sessions, mutual exclusion perfect):
  - 3 OK sessions: custom handler received every didOpen (entry log + AddFile tu/su,
    routing log "Finished in 579ms").
  - 3 null sessions: built-in handler received every didOpen (SYNC-OPEN log fired,
    custom entry log never did, routing "Finished in 5ms" = empty lambdas).
Downstream chain that makes the swallowed didOpen PERMANENT:
  1. didOpen lost -> nothing indexed for the file.
  2. First definition request (+3s) self-parses from disk into OpenDocumentStore via
     LanguageAwareHandlerBase.TryGetDocumentContent — WITHOUT GlobalSymbolIndex.AddFile.
  3. Workspace scan reaches the file later, sees it in the store, skips it
     ("OpenDocumentStore is authoritative for open buffers") -> never indexed.
  4. Every later cross-file definition: IncludeSymbolResolver.TryResolve ->
     index.FindSymbol(identifier) -> 0 candidates -> null. Forever.
Disproved along the way: C1 parse-reuse skip (not triggered by this repro), C3 torn
reads (index healthy in null sessions), language adoption misdetection (language=Mql4
in both session kinds), client languageId influence ("mql" vs "mq4" both flip),
project-size alone (49-file fixture never flips; needs the slow scan + skip-open
interaction to become permanent).

Also affected by the same double registration: didChange and didClose (built-in empty
lambdas vs DidChangeTextDocumentHandler/DidCloseTextDocumentHandler) — same swallow
risk, matches the #86-era symptoms.

### Phase 2 — fixes (completed)
- [x] P2-1 (primary) f31fdba: OnTextDocumentSync lambdas delegate to the DI-resolved
      custom didOpen/didChange/didClose handlers via a synchronous HandleSync entry
      point; both routing branches now run the real logic. Handlers' Handle semantics
      unchanged (HandleCore extraction). 4 forwarding tests + source-scan wiring test.
- [x] P2-2 (defense) 7d8807f: TryGetDocumentContent store-miss path also indexes the
      fresh parse (AddFile with SymbolOccurrenceMapper.Map + defensive symbol-list
      copy). 3 tests incl. rename-first-request aliasing regression.
- [x] P2-3 (defense) 8144782: didOpen parse-reuse path heals the index when the
      (file, language) key is missing (AddFile with copies + include-chain walk on the
      same branch; zero cost when the root is already indexed). didClose analysis:
      RemoveFile has no production call sites, didClose never removes index entries.
      3 healing tests.
- [x] P2-4 Verification: full suite 1304/1304 pass; authoritative Ducibus 30-session
      probe -> 30/30 OK (baseline was 40-50% null, stable per session); small-fixture
      probe 6/6 OK; .mqh-includer shape 6/6 OK (resolves into the header). Working
      tree clean; graph change analysis (detect_changes) run before every commit
      (critical/high risk flags are the notification-entry blast radius, accepted with
      the full-suite + probe evidence).
- [x] P2-5 Re-verify #86 end-to-end on the fixed build — confirmed by the user
      ("Verificado. Ok") after the merge of PR #119 (main @ 1147fa2).

## Evidence log

- Ducibus probe (authoritative): 6-session loop at
  ~/source/Ducibus/tooling-eval/probe_nondet_rc4.py. Flip observed on: installed binary
  (rc.5+1209403), our rc.4 worktree build, our rc.5 build. ~40-50% null sessions,
  stable within session — matches the issue report exactly.
- Small-fixture probes never flip: the 3-file workspace scan repairs the swallowed
  didOpen before the first definition arrives, masking the bug. Ducibus scan takes
  >3s, so the skip-open rule seals the null in permanently.
- Mutual-exclusion instrumented run: built-in SYNC-OPEN log fired 6/6 in null sessions
  and 0/6 in OK sessions; custom handler entry log the exact inverse.

## Commit evidence

- f31fdba fix(server): forward OnTextDocumentSync lambdas to the real
  didOpen/didChange/didClose handlers (issue #116)
- 7d8807f fix(lsp): index request-path parses, not just store them (issue #116 defense)
- 8144782 fix(lsp): heal the index on didOpen's parse-reuse path (issue #116 defense)
- Branch: fix/116-didopen-swallowed-notifications (from main 1209403). Push/PR/merge
  remain the user's decision. 10 new tests: Issue116DidOpenForwardingTests (4),
  Issue116RequestPathIndexingTests (3), Issue116ReusePathIndexHealingTests (3).
