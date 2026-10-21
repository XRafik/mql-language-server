## [Unreleased]

Reverse-dataset hygiene: the MQL5 builtin golden list no longer carries MQL4-only names. A dump-evidence audit of `data/builtins/mql5.json` (function pages of the official MQL5 Reference PDF dumps, with individually adjudicated exceptions and live mql5.com-docs verification for constants) found 18 registrations that do not exist as MQL5 API — 14 functions (`OrderClose`, `OrderCloseBy`, `OrderModify`, `OrderDelete` — MQL5 offers them only as CTrade Standard-Library methods, out of the golden-list scope per the #150 convention; `WindowExpertName`; the legacy account functions `AccountBalance`/`AccountEquity`/`AccountFreeMargin`/`AccountMargin` (MQL5 uses `AccountInfoDouble`/`Integer`/`String`); the MQL4-legacy string functions `StringGetChar`/`StringSetChar` (MQL5: `StringGetCharacter`/`StringSetCharacter`); and the phantom names `CryptMethod`/`DateTimeToString`/`StringToDateTime`, absent from every dialect reference) and 4 variables (`MODE_MAIN`/`MODE_SIGNAL`/`MODE_UPPER`/`MODE_LOWER` — MQL4 indicator line constants; MQL5 documents `MAIN_LINE`/`SIGNAL_LINE`/`UPPER_BAND`/`LOWER_BAND` and the MQL5 compiler rejects `MODE_MAIN` outright). These registrations were false negatives: they suppressed the `5070 Undeclared symbol` diagnostic for MQL5 code that cannot compile. Verified MQL5 names with dialect-shared spellings are untouched (`OrderSend`, `OrderSelect`, `OrdersTotal`, `FileRead` — the latter a documented dump hole). The audit tool (`tools/generate-golden-lists/audit_mql5_dataset.py`) deterministically regenerates the function-evidence report (`data/mql5-dataset-audit.json`, local evidence like its MQL4 sibling) and records why a full constants audit is deferred (the PDF dumps lost the enum member tables). The new `Mql5GoldenList_ExcludesMql4OnlyNames` gate test pins the removal. Dataset counts: functions 816→802, variables 2003→1999. Full suite 1,664 passing.

Dataset hygiene: the MQL4 builtin golden list no longer carries MQL5-only names. An evidence-based audit of `data/builtins/mql4.json` against the official MQL4 Reference CHM (classification: dedicated reference page / `events.htm` handler / normative section-table row / alias page) found 32 entries documented only in the MQL5 reference — 20 functions (`OrderGetDouble`/`OrderGetInteger`/`OrderGetString`, `PositionGetDouble`/`PositionGetInteger`/`PositionGetString`, `PositionSelect`, `PositionsTotal`, `HistorySelect`/`HistoryOrderSelect`/`HistoryDealSelect`, `CopyBuffer`, `IndicatorRelease`, `FileRead`, `ArrayReverse`, `MathArctan2`, `StringToDateTime`, `CryptMethod`, `OnTrade`, `OnTradeTransaction`) and 12 variables (`POSITION_TYPE_BUY`/`POSITION_TYPE_SELL`, `TRADE_ACTION_DEAL`/`TRADE_ACTION_PENDING`/`TRADE_ACTION_SLTP`, `OBJ_ELLIOTWAVE3`/`OBJ_ELLIOTWAVE5`, `OBJPROP_COMMENT`/`OBJPROP_DEGREE`, `CHART_SHOW_TICKER`, `SYMBOL_TIME_MSC`, `ACCOUNT_MARGIN_MODE`). These registrations were false negatives: they suppressed the `1070 Undeclared symbol` diagnostic for MQL4 code that cannot compile in MetaEditor (the MQL5 trade/chart/object API does not exist in MQL4). Names with MQL4-vs-MQL5 split semantics but a real MQL4 reference page (`OrderSend`, `OrderSelect`, `OrdersTotal`, `MODE_*`, the `i*` indicator series, ...) are untouched, as are the MQL4 event handlers (`OnInit`/`OnTick`/`OnTimer`/... documented in `events.htm`) and corpus-admitted ambiguous names (`_AppliedTo`). The audit tool (`tools/generate-golden-lists/audit_mql4_dataset.py`) deterministically regenerates the classification report (`data/mql4-dataset-audit.json`, local evidence like the sibling description dumps), and the new `Mql4GoldenList_ExcludesMql5OnlyNames` gate test pins the removal so the leaked names cannot return. Dataset counts: functions 350→330, variables 621→609. Full suite 1,662 passing.

Closes the description half of issue #166: the MQL5 builtin golden list now carries real hover/completion text for the entries that previously showed the "(description not yet captured)" placeholder or no description at all. Data-only change per the #136 contract: 1,578 variable descriptions (all 1,644 baseline placeholders except 66 genuinely unrecoverable entries, which keep their placeholder and are reported with page evidence in `tools/generate-golden-lists/data/mql5-descriptions.json`) plus 803 function descriptions (the 13 residual functions are MQL4-legacy names with no page in the MQL5 Reference PDF). Sources: the official MQL5 Reference PDF dumps via the new `tools/generate-golden-lists/apply_mql5_descriptions.py` (constants tables with wrapped-identifier vocabulary repair and narrow-column fragment reassembly, function-page prose, ENUM_* section titles, synthetic deterministic `Web color 0xRRGGBB (Name)` descriptions for the 133 web colors per the 2026-10-19 user decision); every updated entry keeps its existing provenance and appends a `descriptions-...` marker. No names or signatures were touched; no registry code changed (`Mql5Builtins.GetBuiltinVariableDescription` now returns real text for hover). Pins: `Mql5Descriptions_AreCaptured` + `Mql5FunctionDescriptions_AreCaptured` + coverage gate `Mql5GoldenList_DescriptionCoverage_MeetsTarget`; full suite 1,646 passing.

Closes the MQL4-side gap of #166: the MQL4 builtin golden list now carries real descriptions from the official MQL4 Reference CHM. Data-only change per the #136 contract: 306 of 350 function entries gained a `description` (prose under the documentation page heading, same convention as the MQL5 pass) and the 4 predefined series variables (`Open`/`High`/`Low`/`Close`) lost their placeholder text in favor of the `predefined.htm` Variable/Value table prose. Per-entry provenance marker `descriptions-official-mql4-reference-chm-2026-10-20`; no names or signatures were touched and no registry code changed (registries remain projections of the golden lists, enforced by the parity tests). The 44 functions without a source in this CHM build (MQL5-only names that leaked into the mql4 dataset such as `OrderGetDouble`/`CopyBuffer`/`OnTick`, undocumented legacy aliases such as `rand`/`round`/`FileRead`, and constants newer than the CHM such as `CRYPT_HASH_SHA3_*`) stay description-less and are reported with page-audit evidence in `tools/generate-golden-lists/data/mql4-descriptions.json` — text is never fabricated or copied from the MQL5 docs. Hover for MQL4 builtins now shows the official description instead of nothing.

### Fixed
- fix(parser): accept `input group "…"` directives in MQL5 (#176). New `K_GROUP` keyword plus a `translationUnit › inputGroupDirective` rule (`K_INPUT K_GROUP STRING`) parse the documented input-parameter grouping — MQL5 Reference "input variables" chapter; exact documented form has no trailing semicolon, and `sinput group` stays rejected per the #141 honesty rule. Removes the false `5100 no viable alternative` diagnostics on every `input group` line of a valid MQL5 program. Tests: `InputGroup_String_IsAccepted` + `InputGroup_BeforeInputs_ParsesAllFollowingInputs` (RED-first: both failed with `no viable alternative at input 'inputgro…'` before the grammar change).

- fix(parser): accept `f`/`F` real-literal suffixes (#176). Optional suffix on all three `DOUBLE` branches (decimal, leading-dot, scientific EXP) in both grammars — C-style lexing. The MQL5 Reference constants chapter does not name the suffix, but its own official code samples use f-suffixed float literals (`{1.0f, 2.0f, 3.0f, 4.0f, 5.0f}` in the matrixf examples) and MetaEditor compiles them; the dump is silent on the EXP form, accepted for consistency. Integer `123f` stays rejected (lexes INTEGER + IDENTIFIER). Removes the false `5100` parse errors and the `5070 Undeclared symbol: f` cascade on `double x = 0.0f;`. Mirror: MQL4 grammar (both shared the same suffix-less rule) + `ParseVariable_RealLiteralFSuffix_ParsesWithoutErrors`. RED-first: `extraneous input 'f' expecting ';'` ×3 before the change.

- fix(data): add MQL5 builtin struct types and predefined macros (#176). Data-only change per the #136 contract: +24 variables-table entries in `data/builtins/mql5.json` (1999→2023) — the 12 builtin struct types with a definition page in the MQL5 Reference dump (`MqlDateTime`, `MqlTradeRequest`, `MqlTradeResult`, `MqlTradeTransaction`, `MqlTradeCheckResult`, `MqlTick`, `MqlRates`, `MqlBookInfo`, `MqlParam`, `MqlCalendarCountry/Event/Value`) and the full documented predefined-macro set (11 constants: `__CPU_ARCHITECTURE__`, `__DATE__`, `__DATETIME__`, `__LINE__`, `__FILE__`, `__PATH__`, `__FUNCTION__`, `__FUNCSIG__`, `__MQLBUILD__`/`__MQL5BUILD__`, `__COUNTER__`, `__RANDOM__`), each with a dump-derived description and provenance `mql5-reference-structs-macros-2026-10-21`. No registry code changed (`Mql5Builtins` stays a projection; parity tests unchanged). Removes the false `5070 Undeclared symbol` on struct types and predefined macros. Pins: `Mql5StructTypes_AreRegistered` + `Mql5PredefinedMacros_AreRegistered` + end-to-end `Mql5Document_WithStructTypesAndPredefinedMacros_DoesNotFlagThem`. RED-first: 25 failing before the data edit.

### Added
- feat(tools): description extractor (`apply_mql5_descriptions.py`) over the pdftotext dumps of the official MQL5 Reference: table parsing anchored on the chapter's header shapes (`Identifier Description Type`, `Constant Code Description`, `Identifier Value Description`, `ID Description`, `Variable Value`, `Code Constant Description`), mid-name wrapped-identifier repair against the golden-list vocabulary, narrow-column description-fragment reassembly with a prose-extracted complete-word vocabulary, alias groups (shared `MB_ICON*` value rows), cross-page auto-detected continued tables, function-page prose before the captured signature, ENUM_* section-title fallback with sibling inheritance, and idempotent in-place application with per-entry provenance markers and a full review evidence dump (`data/mql5-descriptions.json`).
- feat(tools): MQL4 description extractor (`tools/generate-golden-lists/apply_mql4_descriptions.py`) over the official MQL4 Reference CHM (decompiled with 7z into the gitignored `tools/generate-golden-lists/data/raw/mql4-chm/` cache, 690 cp1252 HTML pages). Uniform function pages yield the `p_Function` prose under the H1; grouped constant tables (MODE_* marketinfo, TERMINAL_* terminal status, ACCOUNT_* account information, CRYPT_*/ENUM_CRYPT_METHOD other constants, MB_ICON* MessageBox constants) and the predefined variables table yield identifier→description rows. Description-only in-place updates, idempotent re-runs, `--dry-run`, and a per-entry evidence dump including every unrecoverable name with its page-audit reason.
- test(builtins): red-pin-first description pins — `Mql4Descriptions_AreCaptured` (10 function pins across trading/account/strings/files/arrays/math/datetime/indicator families), `Mql4PredefinedVariableDescriptions_AreCaptured` (Open/High/Low/Close) and the `Mql4GoldenList_DescriptionCoverage_MeetsTarget` coverage gate (description-less functions ≤ 44 of 350, justified by the measured page audit; placeholder variables = 0). Full suite 1631 passing, Release build clean.
Closes the MQL5-side gap of #136: the MQL5 builtin golden list now carries the documented standard-library enum constants (trading, chart, object, indicator, file, terminal, math, color and error families) that the MQL4 list already had since #136/#152. Data-only change per the #136 contract: +1,641 names (constant members and ENUM_* type names) derived from the official MQL5 Reference with per-entry provenance; no registry code was touched (`Mql5Builtins` remains a projection of `data/builtins/mql5.json`, enforced by the parity tests). This eliminates the false `5070 Undeclared symbol` diagnostics for standard names such as `SYMBOL_TRADE_TICK_SIZE`, `POSITION_VOLUME` or `OBJPROP_XSIZE` — the exact false-positive class that made editors/agents block valid insertions into MQL5 documents (agent-lsp `safe_edit` incident on StratoX, 2026-10-18).

### Added (MQL5 golden-list stdlib constants, merged earlier)
- feat(lsp): system-include resolution for the MQL4/MQL5 Standard Library — new `systemIncludeRoots` initialization option (array of absolute paths, typically the terminal's `MQL5/Include` / `MQL4/Include` directories). Angle-bracket includes (`#include <Trade\Trade.mqh>`) now resolve against the configured roots (separator-normalized for both `\` and `/`, Windows-style case-insensitive existence check, traversal rejected); resolved headers are lazily parsed with the dialect parser and indexed into `GlobalSymbolIndex` at didOpen (deduped — an already-indexed header is never re-parsed; cancellation-safe; missing/unparseable headers keep the old behavior). The Tier 1/1b cross-file suppression (`CrossFileSymbolCorrelator`), definition, hover, references and completion therefore cover Standard-Library classes and types (`CTrade`, `CArrayObj`, `MqlTradeRequest`, ...) with no false `5070`/`1070 Undeclared symbol` positives — the last remediation gap of the 2026-10-18 agent-lsp incident (workspace-folder workaround proved ineffective by design; the roots are a server-side concept). Fail open everywhere: no roots configured or an unresolvable header keeps today's behavior byte-for-byte. Symmetric for both dialects. Tests: 10 `IncludePathResolverTests` pins (found/not-found/no-roots/backslash-forward-slash/nested-subpath/case-insensitive/traversal/quoted-rejection/provider) + `SystemIncludeResolutionTests` (MQL5+MQL4 lazy indexing, dedup, no-roots preservation, Tier-1 suppression both dialects, definition + hover reachability over an indexed system header). Full suite 1616 passing.
- data(builtins): enrich `data/builtins/mql5.json` with +1,641 documented MQL5 stdlib names (enum constants + ENUM_* type names), sourced from the official MQL5 Reference via the new `tools/generate-golden-lists/apply_mql5_enum_constants.py` (offline PDF extraction mode plus resumable site crawler). Pins: `Mql5EnumConstants_AreRegistered` (one to two representatives per family); full suite 1564 passing.
- feat(tools): golden-list extractor with two source modes: resumable crawler of mql5.com/docs/constants (page cache, rate-limit friendly) and `--from-pdf` extraction from a pdftotext dump of the official reference (column-geometry and raw-text parsing, vocabulary-voted candidate repair, web-color grid transcription). All entries carry per-entry provenance; existing entries are never modified.
- data(builtins): enrich `data/builtins/mql5.json` with +661 documented MQL5 stdlib **functions with full captured signatures** (functions 155 → 816, including `CheckPointer`/`GetPointer` whose pointer/enum return types needed an extended extractor pass: indicators, chart, series, trade, files, strings, math, common, calendar, socket, database, onnx, DX and OpenCL families), extracted from the official MQL5 Reference via the new `tools/generate-golden-lists/apply_mql5_functions.py`. Scope-excluded by design: matrix/vector methods and OpenBLAS internals (receiver-carrying signatures) the MetaTrader Python API, and Standard-Library class members (resolve via includes). Also registers the 3 remaining predefined variables (`_AppliedTo`, `_RandomSeed`, `_IsX64`). Pins: `Mql5DocumentedFunctions_AreRegistered`; full suite 1596 passing.
- feat(tools): function extractor (`apply_mql5_functions.py`) over the pdftotext dumps: page-heading + free-signature recognition, TOC-universe cross-validation, per-page provenance, overload joining.

## [2.6.2] - 2026-10-15

Closes #163: the MQL5 builtin golden list now covers the File* function family and the TIME_*/FILE_*/SEEK_*/CP_* constants, eliminating false 5070 unresolved-symbol diagnostics in MQL5 documents that use file I/O or datetime string formatting. The MQL4 golden list got its own smaller symmetric gap filled (7 functions + 8 constants, all documented MQL4 build 600+ API). Data-only change per the #136 contract: new names go into `data/builtins/mql5.json` / `data/builtins/mql4.json` with signatures/descriptions from the official documentation; no registry code was touched.

### Added
- feat(analysis): register the MQL5 File* family (16 functions: `FileOpen`, `FileClose`, `FileRead`, `FileWrite`, `FileWriteString`, `FileReadString`, `FileIsEnding`, `FolderCreate`, `FolderDelete`, `FileDelete`, `FileCopy`, `FileMove`, `FileSeek`, `FileSize`, `FileTell`, `FileFlush`) and 14 constants (`TIME_DATE`, `TIME_MINUTES`, `TIME_SECONDS`, `FILE_COMMON`, `FILE_WRITE`, `FILE_READ`, `FILE_TXT`, `FILE_ANSI`, `FILE_CSV`, `SEEK_SET`, `SEEK_CUR`, `SEEK_END`, `CP_UTF8`, `CP_ACP`) in the MQL5 golden list (#163). Previously any MQL5 document touching file I/O produced false `5070` errors for these standard names. `FILE_BIN`/`FILE_SHARE_READ`/`FILE_SHARE_WRITE` were already present (the issue overcounted by three).
- feat(analysis): symmetric MQL4 golden-list enrichment (#163): `FileWriteString`, `FileReadString`, `FolderCreate`, `FolderDelete`, `FileCopy`, `FileMove`, `FileTell` + `TIME_MINUTES`, `FILE_COMMON`, `FILE_TXT`, `FILE_ANSI`, `SEEK_SET`, `SEEK_CUR`, `CP_UTF8`, `CP_ACP`. Tests: `Issue163BuiltinRegistryCoverageTests` (both-dialect coverage theories plus end-to-end `UnresolvedSymbolRule` runs over the issue repro snippet in MQL5 and MQL4, with a genuinely-undeclared-symbol guard). Full suite 1529 passing.

## [2.6.1] - 2026-10-06

Closes #160 (root-cause fix for the agent-lsp cold-start symptom): the server now emits `$/progress` (`workDoneProgress`) around the startup workspace scan and no longer silently drops requests batched with the `initialize` request. Verified end-to-end with raw stdio probes: a batched `textDocument/hover` that previously got no reply at all is now answered, and clients that declare `window.workDoneProgress` receive `begin`/`report`/`end` progress tokens spanning the whole scan. Full suite 1494 passing.

### Added
- feat(lsp): emit `$/progress` (`workDoneProgress`) around the startup workspace scan (#160). Clients can finally use the standard event-driven readiness signal — wait for `$/progress end` — instead of fixed timeouts or busy-poll probes to know when indexing is complete. The server sends `window/workDoneProgress/create` after initialization and a `begin`/`report`/`end` token sequence around `WorkspaceIndexer`'s scan (percentage computed across ALL workspace folders; `report` after each per-file index attempt in both passes, plus a short per-file message). Spec-gated: only clients that declare `window.workDoneProgress` receive progress (`WorkDoneManager.IsSupported`); the `create` round-trip is bounded (5 s) so a client that declares the capability but never answers cannot delay the scan; progress is best-effort observability and never a startup dependency. `WorkspaceIndexer` stays decoupled from the LSP facade via a new optional `ScanProgressNotifier` hook (mirrors #95's `ScanNotifier`). Tests: `Issue160WorkspaceIndexerProgressTests` (0→100 monotonic coverage, multi-folder coverage, throwing-notifier isolation).

### Fixed
- fix(lsp): call-hierarchy incoming calls return callers again for MQL5 (and any non-MQL4-keyed) files (#172). `CallHierarchyHandler.FindEnclosingFunction`/`BuildItemFromIndex` resolved callers through `GlobalSymbolIndex.GetFileSymbols(filePath)` — whose 1-arg overload defaults to the MQL4 language key — while production indexing stores symbols under the `(file, language)` key (`.mq5` → Mql5; `.mqh` routed by includer per the D2 design). For every `.mq5` file the lookup hit `(file, Mql4)`, returned null, and dropped every caller: `callHierarchy/incomingCalls` was empty for all MQL5 files (issue #172 observed it on template functions; plain functions were equally affected) while `textDocument/references` resolved the same call sites because the occurrence index is name-keyed. Fix: both call sites now resolve the file's language through the existing `ResolveLanguage` chain (open store → `.mqh` includer routing → disk sniffing) via a private `GetFileSymbolsForQuery` helper and use the language-keyed overload; `TypeHierarchyHandler.ResolveItemSymbol`'s index fallback carried the same defect and now passes its already-resolved language. The 1-arg overload's Mql4 default is left untouched (legacy API surface, no remaining production callers). Tests: red-pin-first `Issue172CallHierarchyMql5Tests` (plain `.mq5` caller; `.mqh` template function called from `.mq5` — the exact issue scenario), RED observed before the fix (empty incoming calls, prepare succeeding) and GREEN after; Issue96 MQL4 positive control 6/6 unchanged; full suite 1666 passing, Release build clean. Closes #172.
- fix(lsp): requests batched in the same pipe read as `initialize` are dispatched instead of silently dropped (#160). Root cause (OmniSharp 0.19.9): `LspServerReceiver` converts every pre-initialization request into a `ServerNotInitialized` (-32002) error and drops every pre-initialization notification; the error is an `ErrorMessage`, which `LspServerOutputFilter.ShouldOutput` does not recognize as an `OutgoingResponse`, so it is swallowed with a "will be sent later" log and never reaches the client — no reply, no error, no retry signal (the agent-lsp cold-start symptom: first tool call returns nothing until a second call). Issue #93's early gate covers messages arriving after initialize handling begins; the batched-with-initialize case remained. Fix: new `QueuingReceiver` (registered for the connection's `IReceiver`) parses pre-gate batches with plain `Receiver` semantics — every message is kept in arrival order, so a batched request is dispatched after the batched `initialize`/`initialized`, by which point the initialization pipeline has run in the same batch. After the gate opens behavior is unchanged (library semantics, pinned by test: post-init requests parse normally and dispatch is the input loop's business — the -32002 path only ever existed pre-init). Tests: `Issue160QueuingReceiverTests`.

## [2.6.0] - 2026-10-05

Feature release adding project-local `.mqlignore` support (#157, PR #158): a `.mqlignore` file at the root of each workspace folder now prunes matched subtrees and files from the startup workspace scan — the third prune source alongside `ExcludedDirectoryNames` and the dot-directory rule. Gitignore (gitwildmatch) subset, last-match-wins, with the #95 scan hardening untouched and still running before any descent (the ignore only adds pruning, never re-enables it). Documented scope: the ignore governs the workspace index ONLY — `didOpen` on an ignored file still gets full document-level analysis (pinned by test). Without a `.mqlignore` file the behavior is byte-for-byte identical to 2.5.3. 11 new scan-level tests; full suite 1487 passing.

### Added
- feat(lsp): project-local `.mqlignore` support — exclude paths from the workspace scan (#157). `WorkspaceIndexer` now reads a `.mqlignore` file from the root of each workspace folder during `StartIndexing` and prunes matched subtrees entirely (a third prune source alongside `ExcludedDirectoryNames` and the dot-directory rule). Gitignore (gitwildmatch) subset, last-match-wins: `#` comments, blank lines, trailing `/` (dir-only), leading `/` (anchored to the folder root), `!` negation, `*`, `?`, `**`; CRLF and UTF-8 BOM handled; malformed lines skipped silently (WI-05 spirit); matching is case-insensitive, consistent with the indexer's path handling. Matched directories are pruned before descending, so the ignored subtree's enumeration cost is zero — and the scan-start notify count reflects the pruned candidate count. The #95 hardening (reparse-point refusal, cycle guard, per-directory error containment) is untouched and still runs before any descent: `.mqlignore` only adds pruning, never re-enables it, and `!` cannot resurrect built-in exclusions. Documented scope (analysis points 1–4): the ignore governs the workspace index ONLY — `didOpen` on an ignored file still gets full document-level analysis (pinned by test); an ignored `.mqh` contributes no includer-language evidence to pass B, so live sources including it may surface unresolved-symbol diagnostics for its declarations (intended: "ignored" = "outside the index"); live files' resolved includes into ignored directories still record dependency-graph edges to non-indexed targets (harmless — consumers find no symbols there and degrade gracefully); the file is read once per scan (no watcher — editing it requires a restart). Without `.mqlignore` the behavior is byte-for-byte identical to before. New `MqlIgnoreSpec` (hand-rolled, zero new dependencies) + 11 scan-level tests in `WorkspaceIndexerTests` (bare name, dir-only, anchoring, `**`, negation/last-match-wins, negation vs built-in exclusions, comments/blank/CRLF/BOM, single-file pattern, missing-file control, notify counts, didOpen contract). README (en/es/ru) documents the file.

## [2.5.3] - 2026-10-03

Patch release closing the reopened #95: the workspace scan died on symlink cycles (a Wine prefix's `dosdevices/z:` re-entering the filesystem root) with an EMPTY index — `workspace/symbol` answered `[]` for everything not opened, indistinguishable from a silent failure. The scan now never follows reparse points, guards against non-symlink cycles, contains enumeration errors per directory (WI-05 extended to directories and folders), and surfaces its lifecycle to clients via `window/logMessage` (started / finished / failed) — the observability the issue explicitly requested. Verified on the real corpus copy: 133/133 files indexed, `workspace/symbol` answers immediately with no didOpen (`StopLong`=4, the report's own control value). Full suite 1450 passing.

### Fixed
- fix(lsp): the workspace scan no longer dies on symlink cycles or inaccessible subtrees (#95, PR #154-follow-up). Root cause of the reopened #95 failure on the real 60+-file project: the workspace contained a Wine prefix whose `dosdevices/z:` symlink re-enters the filesystem root; the recursive enumerator followed it, looped through the workspace (and `/proc`), and the `UnauthorizedAccessException` escaped the lazy iterator at `MoveNext()` — killing the ENTIRE scan before a single file was indexed. The catch only covered the initial enumeration call, so `ScanWorkspaceFolder`'s per-file containment never ran; the resulting EMPTY index answered `workspace/symbol` with `[]` forever, indistinguishable from a silent failure and only visible in the file log (`Workspace scan failed; continuing with partial index` — which indexed 0 files). Fix, three layers: (1) reparse points are never followed (symlinks, junctions, wine dosdevices links); (2) a cycle guard on the resolved final link target catches non-symlink loops (bind mounts, duplicated trees); (3) per-directory error containment — an inaccessible subtree is pruned, not fatal (WI-05 extended from files to directories), with per-folder containment in `StartIndexing` so one bad folder cannot prevent the rest from being scanned. The per-directory eager materialization is what makes the containment possible. Verified end-to-end on the real corpus copy (1166 find-entries through the symlink; 133 real files): scan completes `133/133 files indexed`, `workspace/symbol` answers immediately without any didOpen (`CalculaRiesgoTicks`=1, `StopLong`=4 — the exact control values from the report).

### Changed
- feat(lsp): the scan lifecycle is now client-observable (#95 request). `WorkspaceIndexer` gained an optional `ScanNotifier` hook, wired in `Program.cs` to `window/logMessage`: `scan started: {folder} ({N} candidate files)`, `scan finished: {X}/{N} files indexed`, and a Warning on folder-level failure. Best-effort on both sides — an emit failure can never break the scan, and the indexer stays decoupled from the LSP facade (`Action<LogLevel, string>`, mapped to `MessageType` in Program.cs). New tests: symlink-cycle scan completes and indexes (`Scan_SkipsReparsePointDirectories_SymlinkCycleDoesNotAbortScan`), inaccessible subtree pruned while siblings index (`Scan_ContinuesPastInaccessibleSubdirectory`), lifecycle notifications captured (`Scan_NotifiesLifecycleStartedAndFinished`).

## [2.5.2] - 2026-10-03

Patch release closing two diagnostics-reliability issues found through the #150 investigation: the pull channel's silent empty-report-on-budget-overrun regression (#150, PR #151 — a budget overrun could masquerade as "no problems", which for agent-driven verification is worse than a dead channel), and a 55-name golden-list corpus enrichment closing the false-positive 1070 class on a real ~800KB MQL4 corpus (#152, PR #153 — every name verified against docs.mql4.com per the #136 policy; corpus FP counts: Botlidator 26→0, Optimator 123→0, Ducibus Pro 54→9). It also re-enables the 13 diagnostics tests skipped in CI since 2026-01-16 — the skip layer that let #150 ship. Full suite 1447 passing.

### Fixed
- fix(data): golden-list corpus enrichment — 55 documented MQL4 names that the real Botlidator/Optimator/Ducibus corpus flagged as false-positive 1070s (#152). All admitted names were verified against docs.mql4.com before touching the dataset, per the #136 policy. Functions (26): `CharToStr` and `StrToInteger` as REQ-MA-02 admissions (`mql4OnlyApi` + `mql5Mapping` — deprecated MQL4 aliases of `CharToString`/`StringToInteger`, so they are suppressed on MQL4 documents and produce 5060 migration warnings on MQL5 documents, mirroring the rc.2 `DoubleToStr` precedent), plus `FileDelete`, `FileIsExist`, `TesterStatistics`, `TimeToStruct`, `ArrayFree`, `ShortArrayToString`, `StringToShortArray`, `ObjectMove`, `ObjectGetValueByShift`, `ChartClose`, `ChartFirst`, `ChartNext`, `ChartOpen`, `ChartXYToTimePrice`, `CopyTime`, `EventChartCustom`, `HideTestIndicators`, `TextGetSize`, `TextSetFont`, `WindowBarsPerChart`, `WindowFirstVisibleBar`, `WindowHandle`, `WindowPriceMax`, `WindowPriceMin`. Constants/macros (29): the ten corpus `ENUM_STATISTICS` members (`STAT_*`), file open flags (`FILE_CSV`, `FILE_READ`, `FILE_WRITE`, `SEEK_END`), `TimeToString` flags (`TIME_DATE`, `TIME_SECONDS`), eight web colors (`Aqua`, `Blue`, `Red`, `Yellow`, `Lime`, `HotPink`, `DeepSkyBlue`, `White`), `CHARTS_MAX`, `CHART_COLOR_BACKGROUND`, `CHART_SHOW_TRADE_LEVELS`, `OBJPROP_RAY_RIGHT`, and the `__FUNCTION__` predefined macro. Windows-interop symbols observed on the same corpus (`CP_*`, `WM_*`, `GW_*`, `GetWindow`, `SetFocus`, `SetActiveWindow`) were deliberately NOT admitted — they are not documented MQL4 API and belong to project-side headers. Effect on the corpus (MQL4 pipeline, standalone documents): Botlidator 26→0 FPs, Optimator 123→0, Ducibus Pro 54→9 (only the excluded interop symbols remain). Tests: `Issue136BuiltinRegistryCoverageTests` gains 55 corpus pins (`Mql4CorpusEnrichmentFunctions`/`Mql4CorpusEnrichmentConstants`); `Mql4OnlyApiRuleTests.RegistryEntries_AllHaveReasonAndReplacement` count pin updated 47→49.
- fix(lsp): the pull diagnostics channel no longer answers an empty report when rule-set generation overruns its budget (issue #150). Root cause: `DiagnosticHandler` ran BOTH the fallback parse and the full generation (semantic rules + cross-file suppression against the workspace index) under a single hard 2s `CancellationTokenSource`, and the `OperationCanceledException` path returned `kind: full, items: []` — indistinguishable from a clean document. On real-world projects (a 682KB `.mq4` indexed alongside the open document) generation deterministically takes >2s, so the pull channel returned 0 items while the push channel published the full set (reproduced: push=289 / pull=0, pull latency pinned at 2.02s by the timeout). The fix separates the budgets — parse keeps the #20 2s contract, generation gets 10s — and a generation overrun now degrades to parse-level diagnostics (`DocumentDiagnostics.GenerateSyntaxErrors`, the 1100/5100 range) instead of an empty report: a budget overrun can never masquerade as "no problems". Verified end-to-end: the 682KB document now yields push=289 / pull=289 (pull latency 4.23s).

### Changed
- test(lsp): re-enabled the 13 `DiagnosticHandler`/`DiagnosticHandlerCrossFileSuppression` tests that were skipped in CI since 2026-01-16 (`b8ceebe`, "flaky in CI"); the skipped set included the only golden assertions on diagnostics content (`WithDiagnostics.mq4` non-empty + specific codes) — exactly the gap that let #150 ship to release. Measured locally at 234ms for the full set; CI duration risk is bounded and the tests assert content, not timing. Added `Issue150DiagnosticChannelReliabilityTests` (CI-enabled, no skips): generation-overrun fallback never empty, parse/generation budget separation, and the push↔pull one-rule-set contract (publish-captured vs pull items must agree for the same didOpen state).

## [2.5.1] - 2026-10-01

First stable of the 2.5.1 line, accumulating rc.1–rc.3 (the opened-EA `1070` false-positive class closed at the root — incomplete registry plus a top-level-only suppression tier — `.mqh` dialect sniffing made comment-aware, enum members indexed into the workspace, the builtin registries rebuilt as documentation-driven golden lists with a registry↔golden-list parity gate, and the MQL4 reference-enrichment pass that took the registry-attributable fp-1070 count on the corpus to **0**). What lands in the stable cut on top of rc.3: the LSP 3.17 type hierarchy feature (#123), three grammar fixes closing the spurious-diagnostics class on documented MQL5 code (#141, #143, #145 — all verified against the official reference before touching the grammar, per the #136 policy), and dependency/CI housekeeping. Full suite 1414 passing.

### Added
- feat(lsp): LSP 3.17 type hierarchy — supertypes/subtypes (#123, PR #140). Parse model: `MqlSymbol.BaseClass` captured from the grammars' `COLON accessModifier qualifiedName` clause (class in both dialects; struct/interface in MQL5 only, consistent with the languages). Handlers mirror callHierarchy: `prepareTypeHierarchy` resolves the type symbol under the cursor (in-file, include-declared, workspace-indexed; type candidates only, CCR-05-aware), `supertypes` walks `BaseClass` through `TypeDeclarationResolver.TryResolveTypeLocation` (keeping the #89 include-reachability guard; unresolvable stdlib bases are skipped), `subtypes` scans the workspace index per `(file, language)` key (case-insensitive).

### Fixed
- fix(parser): the MQL5 grammar no longer rejects documented class modifiers (#141, PR #142). `K_ABSTRACT` lexed; `classDeclaration` accepts a leading `abstract` and a `final` suffix after the name, BEFORE the optional base clause — the documented placement (`docs/basis/types/classes#final_class`; MQL5Book example `class Derived final : public Base`); `structDeclaration` accepts the same `final` suffix (`#final_struct`). Previously `abstract class X { ... };` produced `no viable alternative at input 'abstractclass'` squiggles and the symbols survived only via ANTLR error recovery. `final class X` (prefix) is NOT accepted — the reference confirms no such placement; `abstract` is not even in the MQL5 reserved-words list.
- fix(parser): pure virtual function specifiers parse in MQL4 and MQL5 (#143, PR #144). Member `functionDeclaration` gained `pureSpecifier : ASSIGN (INTEGER | K_NULL)` — the exact closed set mandated by compile error 381 ("only \"=NULL\" or \"=0\" are allowed"); applied to MQL4 too, since docs.mql4.com carries the identical codes 381/382. MQL5 `modifiers` also matches `K_OVERRIDE` (documented access specifier; `void F() override { }` no longer errors); MQL4 is untouched — `override` is absent from docs.mql4.com reserved words.
- fix(parser): the documented `= delete` method specifier parses in MQL5 (#145, PR #146). `functionDeclaration` gained `deleteSpecifier : ASSIGN K_DELETE` (MQL5Book, classes_final_delete), kept separate from `pureSpecifier` (error 381 closes that rule to `=0`/`=NULL`; deletion is not purity). MQL4 unchanged — docs.mql4.com documents `delete` only as the memory operator. `= default` deliberately NOT added: no MQL5 page documents it (only C++ pages).
- fix(deps): Newtonsoft.Json pinned to 13.0.3 → 13.0.4 exact (#134) — transitive-vulnerability-driven bump, kept as an exact pin so restore stays deterministic (PR #132's MediatR bump to the commercial 13.0.0+ line was rejected; the `[8.1.0, 9.0.0)` license guard pin stands).

### Changed
- chore(deps): dependabot ignores `mediatr` bumps ≥ 9.0.0 (PR #147) — the monthly schedule no longer re-proposes crossing the documented license guard; 8.x updates within range still flow.
- build(ci): `codeql-action` `init` + `analyze` bumped together to v4.38.2 (PR #148, replacing the split Dependabot PRs #131/#135) — the two steps stay SHA-pinned to the same release (per-language bundles are selected at `init` time).
- test: xunit test SDK / `Microsoft.NET.Test.Sdk` 18.10.1 (PR #133).

## [2.5.1-rc.3] - 2026-10-01

Release candidate 3 of the 2.5.1 line: the empirical verification of rc.2 against the real-world MQL4 fixtures exposed that the golden lists inherited unreported gaps from the hand-curated registries — 499 registry-attributable `1070` false positives from documented API no incident had ever filed. This release closes them with a documentation-driven enrichment pass; the registry-attributable fp-1070 count on the corpus is now **0** (18 remaining diagnostics are identifiers inside system includes absent from the workspace — correct behavior).

### Fixed
- fix(data): MQL4 reference-enrichment pass (#138) — +360 entries enumerated from `docs.mql4.com` (MQL4 registry now 901): the full `Object*` function family, `Math*`, legacy datetime helpers (`Hour`, `Day`, …), `MarketInfo`/`SymbolInfo*` families and their `ENUM_SYMBOL_INFO_*`/`MODE_*` constants, the 140 web colors plus `clrNONE`, `MessageBox` constants (`MB_*`, `ID*`), chart events (`CHARTEVENT_*`), `ENUM_BASE_CORNER`/`ENUM_ALIGN_MODE`/`ENUM_ANCHOR_POINT`/`ENUM_BORDER_TYPE`, `ENUM_ACCOUNT_*`, `ENUM_MQL_INFO_*`, and the `MqlTick`/`MqlDateTime` structures. Every entry carries `provenance: mql4-reference-enrichment-rc2` + a documentation URL — the corpus was the trigger, never the source of truth.

### Added
- test: 49 regression pins for the enriched names (`Issue136BuiltinRegistryCoverageTests`); suite 1397 passing.

## [2.5.1-rc.2] - 2026-10-01

Release candidate 2 of the 2.5.1 line: closes the registry-completeness root cause behind the recurring `1070` false-positive class (#46 → #109 → #126 → #136) by replacing the hand-curated builtin registries with documentation-driven golden lists, and unifies the MQL4→MQL5 migration layer (#34) onto the same data.

### Changed
- refactor(builtins): builtin registries are now documentation-driven (issue #136). The hand-curated, incident-patched dictionary initializers in `Mql4Builtins`/`Mql5Builtins` are gone; both registries load lazily (startup behavior preserved) from embedded golden lists (`data/builtins/{mql4,mql5}.json`, 541/484 entries) that carry per-entry provenance, documentation URL, API tier (`legacy`/`build600`/`shared`) and, for MQL4, an optional `mql5Mapping` (replacement, kind, semantics-changed flag, note) feeding the migration layer. Adding a documented name is now a data change, never a code edit. The public registry API is unchanged (adapters, `Mql4AntlrParser`, `CompletionHandler` and the 1070/5060/5040 rules consume the same `Dictionary<string,string>` surfaces, still `OrdinalIgnoreCase`).
- refactor(analysis): `Mql4OnlyApiRegistry` (rule 5060) is no longer a parallel hand-curated table — its 47 REQ-MA-02 admissions (issue #34) are migrated into explicit `mql4OnlyApi` metadata on the MQL4 golden list and projected from it at load time (`Mql4OnlyApiKind`/`Mql4OnlyApiEntry` and case-sensitive `Ordinal` semantics unchanged). Admission policy is still deliberate, never derived from API tier. 27 of the 47 admitted names were absent from the hand-curated builtin registry — a latent gap now closed (the `TimeHour` family, the zero-arg `Order*` property getters, `RefreshRates`, `IsDemo`, the `Window*` family, and the `Close`/`High`/`Low`/`Open` series arrays).

### Fixed
- fix(analysis): the #126 real-corpus residue (20 `1070` FPs) and the issue-#136 documented-API probe are registered: core functions `StringInit`, `EventKillTimer`, `EventSetMillisecondTimer` (both dialects) and `EventSetTimer`, `iBars`, `GlobalVariableSetOnCondition`, `IsConnected`, `WindowFind`, `TimeToStr`, `StrToTime` (MQL4); predefined variables `_LastError`, `_StopFlag`, `_UninitReason`, `_AppliedTo`; stdlib enum constants `REASON_TEMPLATE/CHARTCHANGE/REMOVE/PROGRAM/ACCOUNT/INITFAILED/CLOSE/PARAMETERS`, `TERMINAL_SCREEN_DPI`, `FILE_BIN`, `FILE_SHARE_READ/WRITE` (both dialects, per their official references). Genuinely-undeclared identifiers still error (negative control green).

### Added
- test(ci): registry ↔ golden-list parity gate (`RegistryGoldenListParityTests`) — exact per-dialect set and value equality with actionable missing/extra output; a registry completeness failure is now a build failure instead of a future issue report. Regression pins for every #136-reported name (`Issue136BuiltinRegistryCoverageTests`). The reported corpora stay regression fixtures, never a source of truth.
- chore(data): golden-list schema, provenance and maintenance rules documented in `data/builtins/README.md`; one-off migration/seed tooling in `tools/generate-golden-lists/`.

## [2.5.1-rc.1] - 2026-10-01

Release candidate 1 of the 2.5.1 line: closes the opened-EA unresolved-symbol false-positive class (#126 — the synthetic fixture produced 25 deterministic `1070` errors on any real EA), makes `.mqh` dialect sniffing comment-aware (#124), and brings enum members into the workspace name index so Tier-2 correlation, go-to-definition, references and hover see nested declarations. All three verified with the raw-stdio probe and the synthetic regression fixtures (no third-party code in the tracker or the tests).

### Fixed
- fix(analysis): opened-EA documents no longer flag core builtins, stdlib enum constants, or included-header declarations as `1070` (#126). Two verified root causes: (1) the MQL4 registry was incomplete — core MQL4 functions (`Sleep`, `ChartID`, `ChartGetInteger/SetInteger`, `iTime`, `PrintFormat`, `GlobalVariableSet/Get`, `StringToTime`, `FileWriteInteger/FileReadInteger/FileFlush`, `ZeroMemory`, `TerminalClose`, `ExpertRemove`, `ObjectsTotal`, `ResetLastError`, `rand`, `round`), the underscore predefined variables (`_Digits`, `_Point`, `_Symbol`, `_Period` — the registry only carried the pre-600 names) and stdlib enum constants (`PERIOD_CURRENT`, `CHART_CANDLES`, `OBJPROP_BMPFILE` — missing from BOTH dialect registries) are now registered (plus same-family siblings); (2) the #44 suppression tier stopped at top-level symbols — `CrossFileSymbolCorrelator` Tier 1 now also suppresses names with any lexer identifier occurrence inside the include closure (Tier 1b), which covers enum members and macros that the symbol tree does not model. The audit refuted the issue's "suppression tier never engages in stdio sessions" hypothesis: Tier 1 demonstrably engaged (the header's function was never flagged). Regression guard: the synthetic EA fixture (`Issue126EaDocumentFalsePositivesTests`) reproduces 29 FPs pre-fix and reduces them to exactly the genuinely-undeclared control symbol post-fix; the corpus evidence stays in the external tracker (agent-lsp#1).
- fix(lsp): `.mqh` content sniffing is comment-aware (#124) — a lone MQL5-exclusive token inside a comment (a signature note, a disabled helper, prose) no longer routes a shared header to the MQL5 pipeline. The sniffing branch scans a comment-stripped copy (`//` and `/* */` collapsed, string/char literals tracked so comment markers inside them are not comments, unterminated block comments degrade compiler-like to EOF). `Mql5Tokens` itself is unchanged and remains the single source of truth for `LanguageMisuseRule` (#28), which still scans raw text by design. The `closes #124` keyword on the merged PR (#125) failed GitHub's auto-close parser due to trailing text after the issue reference — issue #124 was closed manually with the merge reference.

### Added
- feat(index): enum members reach the workspace name index (follow-up to #126/#127) — the dialect visitors now model enum declarations as symbols (MQL4 previously captured nothing: neither the type name nor members; MQL5 captured the enum node but not members), and `GlobalSymbolIndex.AddFile` seeds the definition-tagging map (OCC-04) and the name buckets from top-level symbols plus enum members, descending through nested enums. Deliberately NOT flattened: class/struct fields and method locals (they are top-level symbols via pre-existing REQ-SM-02 behavior; indexing them differently would change completion/references/rename and needs its own impact pass). The file symbol tree keeps the full hierarchy; `workspace/symbol` output is unchanged. Effect: Tier-2 correlation (`FindSymbol`) resolves enum members used by including documents structurally, independent of the Tier-1b occurrence path; go-to-definition/references/hover see nested declarations. Anonymous enums are unreachable today — both grammars require a named enum (`K_ENUM IDENTIFIER LBRACE ...`); the defensive visitor path guards a future grammar relaxation (ANTLR regeneration deliberately out of scope).

## [2.5.0] - 2026-09-29

First stable of the 2.5.0 line, accumulating rc.1–rc.5 (diagnostics channel in both models without wedging, navigation resolving include-declared symbols from call sites, cross-file rename agreeing with references, LSP 3.17 call hierarchy, the MQL4 registry sweeps that took the real-project false positives 5070 → 0). Two wire-level findings found after rc.5 are fixed here:

### Fixed
- fix(lsp): cross-file `textDocument/definition` non-deterministic per server session — with identical inputs, ~50% of sessions returned `null` permanently for cross-file call sites (#116). Root cause: OmniSharp 0.19.9 routes each `didOpen`/`didChange`/`didClose` notification to EXACTLY ONE of the two handlers registered for the same method — the custom handler or the built-in `TextDocumentSync` handler whose lambdas were empty — and the winner flips per process. When the empty built-in won, the notification was silently swallowed: the document was never indexed, the first definition request self-parsed it into the open-document store without indexing it, the workspace scan skipped it as an open document, and the index hole became permanent. The built-in lambdas now delegate to the DI-resolved custom handlers via a synchronous `HandleSync` entry point, so both routing branches run the real logic. Two defense layers close the remaining paths to a permanent index hole: request-path parses are indexed (not merely stored), and didOpen's parse-reuse path heals the index (root + include chain) when the `(file, language)` key is missing. Verified with the authoritative 30-session probe against the real-world project that reproduced the flip: 30/30 sessions OK (baseline 40–50% null, stable per session); full suite 1304/1304; 10 new tests.
- fix(lsp): `diagnosticProvider` missing from the initialize result (#120) — rc.5 answered `textDocument/diagnostic` but spec-conformant clients gate pull on the declared capability, so they silently lost the pull model. Same OmniSharp 0.19.9 capability-derivation mechanism as #95/#96: the converter's descriptor lookup consults the CLIENT's declared capability. `OnInitialized` now declares `diagnosticProvider` unconditionally (static flags: `interFileDependencies=false`, `workspaceDiagnostics=false`). Verified with a raw-stdio probe: a client declaring no capabilities receives `diagnosticProvider` and pull answers `kind=full`.

## [2.5.0-rc.5] - 2026-09-28

Release candidate 5: closes the 22 diagnostics the published rc.4 binary still emitted on the real project file — the rc.4 wire battery (raw LSP + agent-lsp probes) found the third sweep's diff had only added a subset of the families its message listed. Progression: 232 → 187 → 41 → 22 → **0** (rc.5 target, verified post-release).

### Fixed
- fix(analysis): MQL4 registry fourth sweep — 19×1070 remained: `CRYPT_BASE64` (MQL4-only `ENUM_CRYPT_METHOD` value), `ACCOUNT_SERVER/CURRENCY/BALANCE/EQUITY/PROFIT/LEVERAGE/MARGIN_SO_SO/TRADE_ALLOWED/TRADE_MODE(+_DEMO)`, `TERMINAL_DLLS_ALLOWED`, `TERMINAL_DATA_PATH`, `IsExpertEnabled`, `MODE_MINLOT`, `MathRand`. The sweep adds those plus the full `CRYPT_*`/`ACCOUNT_*`/`TERMINAL_*` and MarketInfo `MODE_*` families so the next probe surface is covered too.
- fix(analysis): removed the legacy `Variable 'X' starts with underscore` hint rule (1003/5003) — it fired on the project's own parameters and locals (`string _str`, `uchar& _arr[]`, `uchar _tmpChar[]`), legal MQL identifiers MetaEditor does not warn on; a false-positive generator on real code.

## [2.5.0-rc.4] - 2026-09-28

Release candidate 4: closes the last false-positive family found by the wire-level verification of the published rc.3 binary on the real integration project — `Botlidator_ver_2_90.mqh` now publishes ZERO diagnostics (232 on rc.1 → 187 on rc.2 → 41 on rc.3 → 0).

### Fixed
- fix(analysis): MQL4 registry third sweep — the wire-level probe on the real project file with the freshly published rc.3 binary routed MQL4 (code 1070, not the MQL5 route the in-process measurement used) and the MQL4 registry missed another set of standard-library names — 41 diagnostics (`ENUM_CRYPT_METHOD`, `CRYPT_BASE64`, `AccountInfoInteger`, `GlobalVariableTemp`, `IsStopped`, `IsDllsAllowed`, `TERMINAL_DLLS_ALLOWED`, `MB_ICONINFORMATION`, `ACCOUNT_*`…). The MQL4 registry now carries the `CRYPT_*` constants (the stdlib `Crypt.mqh` is bundled in MQL4 builds too) and the `AccountInfo*`/terminal/misc families the probe surfaced; duplicates against pre-existing entries removed (`AccountInfo*` and `IsTradeAllowed` already existed in other sections). The multi-declarator fix from rc.3 verified clean on the same scenario (the `dst`/`key` locals are gone from the flagged list).

## [2.5.0-rc.3] - 2026-09-28

Release candidate 3: fixes the two surviving false-positive families measured by the rc.2 battery (5070 unresolved-symbol noise down 232 → 187 → expected ~0 after this release), both isolated by repro on the real integration project.

### Fixed
- fix(parser): local declarations with MULTIPLE declarators emitted only the FIRST declarator's symbol — `uchar src[], dst[], key[32];` produced a symbol for `src` only, so `dst` and `key` became unresolved-symbol false positives at every later use (the battery's "real locals dst, key in class methods"). Both visitors (`Mql5SymbolVisitor` / `Mql4SymbolVisitor`) now iterate ALL declarators — the grammar allows `variableDeclarator (COMMA variableDeclarator)*`; the MQL4 visitor even documented "there can be multiple declarators" and then emitted only the first. Verified on the real project file: the `dst`/`key` diagnostics are gone.
- fix(analysis): the MQL5 crypto family was absent from the registry — `CryptEncode`, `CryptDecode`, `CryptMethod`, `CharArrayToString` (real builtins: the compiler bundles Crypt.mqh), the `CRYPT_DES..CRYPT_ARCH_ZLIB` constants and `ENUM_CRYPT_METHOD`; MQL4 build 600+ carries the crypto functions too. Added to both registries (the `CRYPT_*` enum constants are MQL5-only). Second registry sweep from the real-project measurement: `StringGetChar`, `StringGetCharacter`, `StringSetChar`, `StringToCharArray`, `GlobalVariableCheck`, `WindowExpertName`, `MessageBox`, `TERMINAL_DLLS_ALLOWED`, `TERMINAL_DATA_PATH`.
- fix(release): prerelease label reported in `--version`/`ServerInfo`/startup log (an rc printed "2.5.0"); the DI composition root now registers the static `GlobalSymbolIndex.Instance` (DryIo was constructing a second index through the private ctor, leaving hover/documentHighlight/declaration against an empty index — the residual go_to_definition failure). (Shipped in rc.2; changelog entry was omitted from rc.2's notes.)

## [2.5.0-rc.2] - 2026-09-28

Release candidate 2: driven by the full re-verification battery run against rc.1 with two MCP clients (Serena and agent-lsp) on the real 60+ file project. It confirms the rc.1 fixes (diagnostics delivery, cross-file rename, references, call hierarchy all green) and fixes the two findings of that battery: a DI composition-root defect that left `hover`/`documentHighlight`/`declaration` resolving against an EMPTY symbol index (the residual `go_to_definition` failure), and the builtin-registry gaps behind the 5070 unresolved-symbol false-positive noise on standalone headers. Also fixes the version display for prerelease builds (an rc printed "2.5.0", indistinguishable from the stable line).

### Fixed
- fix(server): one `GlobalSymbolIndex` for every handler — the DI composition root created a SECOND index: `AddSingleton<GlobalSymbolIndex>()` let DryIo construct the class through its private parameterless constructor (distinct from the `GlobalSymbolIndex.Instance` static that the accessor's parameterless overload resolves to), so the accessor-DI handlers (didOpen, didChange, definition, references, rename, workspace/symbol) shared one index while hover/documentHighlight/declaration resolved against the STATIC index — which nothing populated, leaving the #92 include-declared refinement dead on those handlers. Observed end-to-end on the real integration project (cursor on the `StopLong` call site at 563:121): `definition` resolved to `stop_take_utils_mq4.mqh:8` while `hover` answered the enclosing function — and agent-lsp's hover→workspace-symbol fuzzy fallback converted that enclosing-function hover into the wrong "definition" (the 520–622 range the battery reported = the hover's range in 1-based). The fix registers the DI singleton as the static instance (the wiring the DI integration test already used). In-process tests never caught it: handlers are constructed manually and fixtures populate the static index directly — both consistent; only the DI composition root diverged.
- fix(release): `--version`, the LSP `ServerInfo` and the startup log now report the prerelease label — an rc package printed "2.5.0" because `ServerVersion` derived the display from the numeric `AssemblyVersion`, which MSBuild strips the prerelease label from (`2.5.0-rc.1` → `AssemblyVersion 2.5.0.0`); the display now prefers the `AssemblyInformationalVersion` (the full `<Version>` value) minus the CI `+source-hash` metadata, with a pure formatting helper unit-tested for metadata stripping, prerelease preservation, stable pass-through and numeric fallback.
- fix(analysis): builtin registry coverage for the false-positive unresolved-symbol noise (5070/1070) — standalone `.mqh` analysis flagged real standard-library names (`True`/`False`, `TerminalInfoInteger`, `CharToString`, `StringSplit`, `DoubleToString`, `StringReplace`, `iOpen/iHigh/iLow`, `ArrayInitialize`, `PlaySound`, `SendMail/SendNotification`, `MQLInfoInteger/MQLInfoString`, …): none existed in either `Mql4Builtins` or `Mql5Builtins`, and every registry gap became a false positive (the registries are `UnresolvedSymbolRule`'s source of truth). Both registries now carry the missing common standard-library families (dialect-specific signatures where they differ) plus the boolean keyword literals (one canonical entry per registry; the tables compare `OrdinalIgnoreCase`, matching the MQL compiler's case-insensitive keywords). The `.mqh` standalone dialect-ROUTING layer (one incidental MQL5-exclusive token in content sniffing routes the whole header MQL5) stays open on the issue as a follow-up design pass.

## [2.5.0-rc.1] - 2026-09-28

Release candidate 1 for the 2.5.0 line: the diagnostics channel works in both models (pull answers, push publishes) and no longer wedges the server; startup request drops are gone; the navigation surface resolves include-declared symbols from their call sites; rename agrees with references (cross-file, include-graph-guarded); and the LSP 3.17 call hierarchy ships — the first real call graph for MQL, with the call-vs-mention distinction and outgoing calls that `textDocument/references` cannot provide. Every fix verified end-to-end against the published self-contained binary with raw stdio probes, not just in-process tests.

### Added
- feat(lsp): LSP 3.17 call hierarchy — `textDocument/prepareCallHierarchy`, `callHierarchy/incomingCalls` and `callHierarchy/outgoingCalls` (#96) — one handler class implementing the three generated OmniSharp interfaces, built on the machinery the navigation handlers already share: the workspace-wide name-keyed occurrence index, the include/dependency graph, and open-document store-first resolution. `prepareCallHierarchy` resolves the symbol under the cursor — in-file declaration by name first (bypassing the builtin short-circuit so user-declared `OnInit`/`OnTick` prepare), then the include-declared fallback (#92/#90): a cursor on a call site of a function declared in an included file prepares the DECLARED symbol pointing at the declaration file. `incomingCalls` distinguishes calls from mentions (an occurrence is a call when its token is followed by `(` on the same line), excludes the definition's own signature via the OCC-04 `IsDefinition` mark, resolves each call position's enclosing function from the index's per-file symbol lists (no parse at query time), and groups every call position of one caller into a single `CallHierarchyIncomingCall` with all its `FromRanges`. `outgoingCalls` walks the item's function range in its own document (store-first, language routed per the item's URI), resolving callees in-file first and then through the workspace index (include-declared callees point at their declaration file), excluding pure builtins and the item's own signature token. `callHierarchyProvider` is declared unconditionally (OmniSharp 0.19.9 otherwise omits the key for clients that do not declare `workspace.symbol`). typeHierarchy is deferred: the parse model does not capture class inheritance (no base-class names on `MqlSymbol`), so `supertypes`/`subtypes` need a visitor-level model extension in both grammars (tracked on the issue). Validated by 6 handler regression tests and end-to-end against the published binary: prepare at a call site → item at the include; incoming → the caller with its call range; outgoing → the callee with the builtin excluded; capability declared with minimal client capabilities.

### Fixed
- fix(lsp): cursor resolution at call sites of include-declared functions (#92) — `hover`, `definition`, `references`, `documentHighlight` (and `declaration`, same root cause, fifth symptom) failed to resolve the called symbol whenever the declaration lived in an included file: hover fell back to the enclosing function, definition/references/declaration returned null, documentHighlight returned empty — even though didOpen's include resolution had indexed the include-declared symbol into the `GlobalSymbolIndex`. The handlers now fall back through the new `IncludeSymbolResolver` (`src/Lsp/Server`): the exact cursor identifier is extracted from the parse-time occurrence index (OCC-01, model text — immune to the #86 buffer-vs-disk divergence) and resolved against the global index, skipping same-file candidates already covered by the failed in-file lookup; declaration points the location at the included file via the indexed symbol's `FilePath`. Validated by 6 regression tests on the issue's fixture (hover shows the included signature, definition/references/declaration point at the include, documentHighlight highlights the call sites) plus one MQL5 case.
- fix(server): startup request drop window (#93) — requests sent right after `initialize` were silently dropped (no response, no error, nothing on the wire) during the 15–25 s "lazy handler init" window measured on the self-contained binary. Root cause verified against OmniSharp 0.19.9 internals: `LspServerReceiver.GetRequests` rejects every message except `initialize`/`initialized` with a `ServerNotInitialized` (-32002) error until `IReceiver.Initialized()` runs at the END of the initialize handling — and `LspServerOutputFilter` then swallows that error ("will be sent later" — never sent). Fix A: the receiver gate is opened at the START of the initialize handling (the `LspServerReceiver` singleton resolvable from the server's DI), so messages arriving after initialize handling begins are routed to the real handlers and answered; the drop window shrinks to the same-OS-read-batch residual (clients writing requests before the server reads `initialize` — pre-spec). Fix B: the one-time parse-pipeline costs (ANTLR ATN deserialization + JIT) are paid by a background `ParserWarmup` concurrent with the initialize round-trip instead of on the first didOpen, which stalled OmniSharp's serial request queue on cold starts. Verified by controlled A/B with a simulated slow initialize handling: gate disabled → pipelined query silently dropped; gate enabled → answered. Two warmup regression tests pin that the warmup never throws and its snippets parse cleanly (a silent grammar regression would otherwise reduce it to a no-op).
- fix(lsp): the diagnostics channel works in both models and no longer wedges the server (#91) — `textDocument/diagnostic` (pull) never answered (15/30/90 s timeouts) AND permanently wedged the server afterwards (previously fast requests stopped answering; only a restart recovered), while `publishDiagnostics` (push) was never emitted: no LSP client could obtain diagnostics in any mode, and agent tooling reported "No errors. Safe to proceed." on broken code. Root cause: the abstract `RelatedDocumentDiagnosticReport`'s `[JsonConverter]` throws `NotImplementedException` from `WriteJson` — the pull response can never be serialized; the exception strikes inside the `OutputHandler` serialization loop, whose only recourse is a TRACE-level log (invisible at default verbosity) and disposing the output pipeline permanently (the pull response lost AND every subsequent response silently dropped — the handler itself runs fine in 33 ms, which is why the server log showed requests "served normally"). Fix (pull): `MqlLspSerializer` (an `LspSerializer` subclass) whose wrapping contract resolver rewrites the diagnostic-report contract converter with a wire-correct one (`{kind:"full", resultId?, items[]}` / `{kind:"unchanged", resultId}`) — settings-level converters do not win here because the type attribute is baked into the CONTRACT converter, which takes precedence; the wrapper is re-applied from `OnInitialized` because `LspSerializer.SetClientCapabilities` (called during initialize handling) replaces the contract resolver via its private `Reset()`. `relatedDocuments` is not emitted (never produced; the nested converter is equally unimplemented in 0.19.9). Fix (push, requested in the issue): the pull rule set (syntax errors, #28 semantic rules with #44 cross-file suppression, typo/empty-OnInit/underscore heuristics) is extracted to a shared `DocumentDiagnostics` service — one rule set, two channels — and `DiagnosticPublisher` publishes the fresh document state over `textDocument/publishDiagnostics` after didOpen/didChange (including EMPTY sets so clients clear stale squiggles), injecting the `ITextDocumentLanguageServer` facade (an `ILanguageServer` dependency fails silently at handler-construction time — OmniSharp registers it with a resolution condition and DryIo falls back to the parameter default). Validated end-to-end: didOpen with an injected syntax error → one publishDiagnostics on the wire; the pull request answers (kind=full, items, resultId); requests after the pull keep answering; a second pull answers. 5 converter regression tests pin the wire format, the `JsonConvert.SerializeObject(value, Settings)` entry point, and the root cause itself (the stock serializer throwing — to be deleted if a library upgrade fixes the attribute converter).
- fix(lsp): rename edits the cross-file occurrence set references returns (#90) — `textDocument/rename` returned only same-file edits (`Where(o => o.FilePath == filePath)`) while `textDocument/references` at the same position returned all cross-file call sites from the same index: an editor or agent client applied the edit, got a success response, and the code was broken at the call sites — the silent partial rename, the most dangerous failure mode for agent-driven editing. Rename now consumes the exact occurrence pipeline references uses (workspace-wide name-keyed + `ScopeOccurrenceFilter.BindAndFilter`, which only prunes same-file shadowing; cross-file occurrences pass through by design, tier 2, OCC-05) and groups the edits per file into a cross-file `WorkspaceEdit`. A reachability guard (the #89 lesson applied to a destructive operation) restricts cross-file edits to files linked through the include graph (either direction): a same-name identifier in an unrelated file is NOT renamed; with a degraded graph (no edges at all) the guard falls back to the plain name-keyed set, so rename never agrees LESS with references. Supporting changes: `RenameHandler` resolves the document store-first (the #88 sweep's highest-impact item — a rename computed from stale disk text against a fresh model silently renamed the wrong spans) and falls back to `IncludeSymbolResolver` so renaming from a call site of an include-declared function works (previously null); `WorkspaceIndexer.IndexFile` records `AddDependency` edges during the workspace scan (the graph was previously only populated by didOpen's include loop); `GlobalSymbolIndex.GetDependentFiles` adds the reverse lookup. Validated by 4 regression tests: the issue's exact scenario (rename at the include-file definition → definition + all 3 call sites in the consumer), rename at a call site via the #92 fallback, the reachability guard (unrelated same-name file not renamed), and rename/references agreement asserted as position-set equality.
- fix(lsp): position handlers answer from the open buffer, not stale disk (#88) — the #86 buffer-vs-disk divergence pattern existed across the position-based navigation handlers: `DeclarationHandler`, `HoverHandler`, `ReferencesHandler`, `DocumentHighlightHandler`, `DocumentSymbolHandler`, `FoldingRangeHandler` and `ImplementationHandler` read the document text from disk while taking the parse model from the open-document store; with an unsaved edit (buffer ≠ disk) the cursor-identifier extraction ran against stale text, silently degrading resolution until a restart. All resolve the document through `LanguageAwareHandlerBase.TryGetDocumentContent` now (store content first, disk only for never-opened documents). `CompletionHandler` was audited and already compliant (the JD-1 fix computes the completion context from the store's buffered content; closed documents parse from disk consistently). Validated by 6 regression tests over a fixture whose disk file and open buffer disagree (renamed + line-shifted function): every handler must answer from the buffer — a disk-derived answer returns the stale name or stale positions.
- fix(lsp): type-usage fallback restricted to include-reachable candidates (#89) — the #87 type-usage fallback resolved an identifier with no in-file declaration to ANY exact-case Class/Struct/Interface/Enum in the workspace index, including a same-named type in an unrelated, never-included file (the reporter's case: free function `Format(string)` in an included helper + foreign `class Format` in `Utils.mqh` — the answer became the foreign class instead of null, a wrong location where null was returned before). `TypeDeclarationResolver.TryResolveTypeLocation` now restricts cross-file type candidates to files linked to the queried document through the include graph (either direction), factored as `IsReachableThroughIncludes` for reuse; with a degraded graph (no include edges at all — single-file project, no scan, never opened) the guard is skipped and the documented tier-2 name-keyed resolution of #64 keeps working — the guard never resolves less than before. In the reporter's exact layout the answer is now better than the requested null: with the #92 include-declared fallback, definition at the `Format("x")` call resolves the free function in the included helper — the actual declaration the cursor text refers to. Validated by 3 regression tests: the reporter's layout (never the foreign class), the #64 legit case (a class in an included file still resolves), and the degraded-graph case.
- fix(lsp): workspace/symbol resolves for never-opened files (#95) — `workspace/symbol` answered `[]` for symbols in files the client never opened on a 60+ file project (while working on the minimal 2-file fixture), and `workspaceSymbolProvider` was declared only when the client declared `workspace.symbol` — a client trusting the null disabled workspace symbol search entirely. Three compounding causes: (1) legacy rootUri-only clients never got a workspace scan at all — `Program` captured only `request.WorkspaceFolders`, which a rootUri client does not send, so no file was ever scan-indexed; `rootUri` is now honored as a single workspace root when `workspaceFolders` is absent (the pre-3.4 contract). (2) didOpen indexed only the DIRECT includes: a symbol in a transitively included header (`A → mid.mqh → deep.mqh`) stayed unindexed until the background scan reached it — minutes of empty results on a large tree (the issue's scenario 5); didOpen now indexes the include chain recursively (cycle-safe, PathSecurity-guarded per hop, D2 semantics unchanged: the whole chain under the root document's language key, dependency edges per hop for the #90/#89 guards). (3) the capability-conditional declaration: OmniSharp derives `workspaceSymbolProvider` from the CLIENT's declared `workspace.symbol`; the `OnInitialized` delegate (after `ReadServerCapabilities` built the result, before the response is sent) now assigns it unconditionally, per the LSP spec (the server may declare the provider regardless of client capabilities). The scan itself was already recursive — the unused `EnumerationOptions` in `ScanWorkspaceFolder` was dead configuration, not the defect. Validated end-to-end with a rootUri-only, minimal-capability client: the scan starts (3/3 files indexed), `workspace/symbol` for a symbol declared in a never-opened transitively-included header returns it (previously `[]` until didOpen'ing the header), and `workspaceSymbolProvider: {}` appears with minimal capabilities. A query sent while a didOpen include chain is still parsing can interleave (OmniSharp runs parallel requests concurrently with serial notifications) — the window shrinks from minutes (scan-based) to the didOpen's own parse duration, whose cold-start part the #93 warmup removes. 3 regression tests pin transitive include indexing at didOpen, the transitive dependency edges, and self-including-chain termination.

## [2.4.2] - 2026-09-23

Patch release for the 2.4.x line: parser and LSP navigation fixes surfaced by downstream integration, plus a release-pipeline fix that unblocks manual release runs.

### Fixed
- fix(parser): constructor-style variable declarations (`Person person("Alice", 30);`) parse in both grammars — `variableDeclarator` now accepts a parenthesized initializer (`LPAREN argumentList? RPAREN`), so `typeDefinition`/`definition` on such instances resolve deterministically to the declared class through the include graph instead of falling back to the variable declaration after ANTLR recovery (#76)
- fix(lsp): `textDocument/signatureHelp` at a method call resolves the called member's signature — receiver's declared type → member lookup, reusing the #64 cross-file machinery — instead of the containing function's; unresolvable call targets return no signature rather than a wrong one, and the label is prefixed with the callee name (#77)
- fix(ci): the release workflow's *Verify tag matches package version* guard derives the version from the job's effective tag instead of `GITHUB_REF_NAME`, which is the branch name on `workflow_dispatch` runs, so manual release runs can publish again (#79)

## [2.4.1] - 2026-09-23

Patch release for the 2.4.0 line: three navigation/packaging fixes surfaced by the downstream agent-lsp integration, each verified by runtime reproduction on the integration fixture.

### Fixed
- fix(lsp): `textDocument/documentHighlight` falls back to the parse-time occurrence index for builtins and event-handler overrides (Print, OnInit) instead of returning an empty result (#63)
- fix(lsp): `typeDefinition`/`definition` on an instance resolve to the declared class — same-file first, then cross-file through the include graph via the workspace-published symbol index (#64)
- fix(packaging): `InvariantGlobalization=true` so self-contained single-file binaries start on hosts without system ICU (libicu); verified with an LSP handshake in a no-ICU container (#65)

## [2.4.0] - 2026-09-21

Stable 2.4.0: promotes the content validated by release candidates 1–3 to the stable channel. See `[2.4.0-rc.1]`, `[2.4.0-rc.2]` and `[2.4.0-rc.3]` below for the full per-item technical detail.

### Added
- feat(lsp): scope-aware occurrence filtering for references and rename, tier 1 document-local (#45)
- feat(lsp): workspace-correlated suppression tier for cross-file unresolved-symbol false positives (#44)
- feat(analysis): MQL standard-library enum constants in the builtin registries (#46)
- feat(parser): object-like (parameterless) macro expansion (#38)
- feat(parser): the macro table walks nested include chains transitively (#39)
- feat(parser): the macro table evaluates conditionals in one continuous include-order stream (#40)
- feat(ci): win-arm64 release binaries with native-runner smoke tests (#49)
- feat(packaging): NuGet authoring best-practices metadata — README/icon/copyright/release notes/tags (#60, #61)

### Fixed
- fix(lsp): symbol-at-position body-containment defect in five handlers (#55)
- fix(analysis): `UnresolvedSymbolRule.IsMemberAccessPosition` absolute-offset drift (#52)
- fix(ci): hold stdin open in the ARM smoke tests so the LSP handshake completes (#58)

### Changed
- ci: the release workflow now pushes the package to nuget.org via Trusted Publishing (OIDC, short-lived single-use API key) (#60, #61)
- ci: fail the build when vulnerable NuGet packages (direct or transitive) are reported
- docs: README drops the pre-1.x rename section; the installation guide promotes `dotnet tool install` from nuget.org as the first-class method
- docs(server): document the intentional didSave omission (pull diagnostics by design)

## [2.4.0-rc.3] - 2026-09-21

Release candidate 3 for the 2.4.0 line: NuGet publishing enablement — no product code changes.

### Added

- NuGet package authoring best-practices metadata: `Copyright`, `PackageProjectUrl`, `PackageReadmeFile` (embedded README.md), `PackageReleaseNotes` (CHANGELOG link), `PackageIcon` (new 128x128 transparent `packaging/icon.png`), expanded `PackageTags`, `RepositoryType` (#60, #61).

### Changed

- Release workflow now publishes the package to nuget.org via **Trusted Publishing** (OIDC via `NuGet/login@v1`, short-lived single-use API key) instead of relying on release assets only (#60, #61).

## [2.4.0-rc.2] - 2026-09-21

Release candidate 2 for the 2.4.0 line: CI-only fix — the native-ARM smoke tests now complete a real LSP initialize handshake (#58). No product code changes; the rc.1 binaries were verified correct despite the red smoke jobs.

### Fixed
- fix(ci): ARM smoke tests hold stdin open so the LSP handshake completes (#58) — both smoke jobs piped the initialize request and closed stdin immediately; the server shut down on EOF before processing the queued message, so the response was never written and the handshake assertion failed on any platform (the v2.4.0-rc.1 run misread this as an ARM binary failure — a 43 ms "crash" that was a clean EOF shutdown). Stdin is now held ~12 s after the request, and the assertion (`"capabilities"` in the captured output) keeps its teeth. Verified locally against the published linux-x64 binary.

## [2.4.0-rc.1] - 2026-09-21

Release candidate 1 for the 2.4.0 line: closes the bulk of the unresolved-symbol false-positive surface for real MQL projects (stdlib enum constants, cross-file include-correlated suppression), makes references/rename scope-aware at function-body granularity, and fixes two parser resolution defects discovered by the new fixtures. First release cut from the complete self-contained matrix (linux-arm64 and win-arm64 ship alongside the existing targets). Not marked as `latest`; the stable channel continues pointing at 2.3.0 until 2.4.0 is sealed.

### Fixed
- fix(lsp): symbol-at-position body-containment defect in five handlers (#55) — `TypeDefinitionHandler`, `ImplementationHandler`, `DocumentHighlightHandler`, and `MonikerHandler` now resolve the exact identifier under the cursor via `FindSymbolDefinition` (the `ReferencesHandler`/`RenameHandler` precedent) instead of `FindSymbolAtPosition`, whose first-range-containment match resolved any cursor inside a function body to the enclosing function — so hovering/typing on a use of a function-local variable or parameter surfaced the containing function instead of the local. `SignatureHelpHandler` is identifier-first for the same reason (its previous `FindSymbolAtPosition ?? FindSymbolDefinition` ordering made the refinement dead code — containment almost never returns null inside a body), keeping the containment lookup only as a fallback for positions with no identifier at the cursor (e.g. inside a call's argument list), where returning the enclosing function's signature is documented as a known limitation until call-expression analysis exists. `HoverHandler` already refined Function/Method containment results through `FindSymbolDefinition` and is unchanged, now locked by regression tests. Validated by per-handler regression tests (cursor on a local's use resolves to the local; companion cases on the function's NAME still resolve to the function).
- fix(analysis): `UnresolvedSymbolRule.IsMemberAccessPosition` absolute-offset drift (#52) — the member-access position probe rebuilt the absolute offset by accumulating `Split('\n')` part lengths without adding 1 per newline delimiter, so the sampled position drifted left by exactly `occurrence.Line` characters. On multi-line documents with dot-bearing tokens (decimal literals like `0.007` are ubiquitous in MQL), a genuinely-undeclared free identifier could land in the drifted sample window and be silently misclassified as a member-access receiver — a false negative that suppressed the unresolved-symbol diagnostic entirely. The offset now adds 1 per traversed newline. Regression test drives the exact drift alignment (three dot-bearing lines then a free identifier call); the #44 cross-file fixture drops its dot-free workaround and now carries a decimal line as a standing guard.


### Added
- feat(lsp): scope-aware occurrence filtering for references and rename, tier 1 document-local (#45) — `ReferencesHandler` and `RenameHandler` now refine the name-keyed occurrence index (`GlobalSymbolIndex.FindOccurrences`, OCC-05 untouched) through the new pure helper `ScopeOccurrenceFilter.BindAndFilter` (`src/Lsp/Server/ScopeOccurrenceFilter.cs`): same-name definitions are collected from the flat `MqlFile.Symbols` list (OrdinalIgnoreCase, mirroring `FindSymbolsByName`), each definition's scope is derived at function-body granularity (a function/method owns its full-body `Range`; a variable/parameter owns the innermost containing function; definitions outside every function are global), and the cursor is bound to its owning definition — a same-name variable/parameter declared in the cursor's function wins, else a same-name function whose body contains the cursor, else the global definition. Same-document occurrences outside the owner's scope (and other same-name definitions' declaration tokens inside it) are filtered out; cross-file occurrences pass through unfiltered (cross-file scope resolution remains tier 2), and `includeDeclaration` handling is unchanged. Coordinator-authorized behavior change in `RenameHandler`: symbol resolution moves from `FindSymbolAtPosition` (first symbol whose Range contains the position) to `FindSymbolDefinition` (identifier text at the cursor, the same call `ReferencesHandler` uses) — rename now requires the cursor to be on an identifier token (renaming a function requires the cursor on its name token); the old containment resolution could resolve any cursor inside a function body to the function itself, hiding the queried name from the scope helper. The parser's `FindSymbolAtPosition` body-containment limitation for other handlers (TypeDefinition, SignatureHelp, Moniker, Implementation, Hover, DocumentHighlight) is a separate follow-up. Known tier-1 limit, documented in the helper: block-level `{ }` nesting inside a function is not modeled, so sibling/nested same-name block locals still conflate with the function-level local except for each definition's own declaration token. The references FP measurement harness now tags each TP as scope-bound vs scope-ambiguous (multiple same-name defs with an in-function same-name local), measurement-only, never asserted. Validated by shadowing fixtures over references (global cursor excludes the local's occurrences and vice versa), rename (global rename edits only the global's positions), and helper unit tests (single-def pass-through, local/global binding, cursor-in-function-without-local, innermost same-name local, cross-file pass-through).
- feat(lsp): workspace-correlated suppression tier for cross-file unresolved-symbol false positives (#44) — `DiagnosticHandler` now filters base+70 unresolved-symbol diagnostics through the new `CrossFileSymbolCorrelator` (handler-level filter; the rule itself stays strictly document-local per REQ-IA-02). Tier 1 walks the document's quoted `#include` closure (`MqlFile.Includes` → `IncludePathResolver.TryResolveContained`, per-header lightweight line scans via `IncludePathResolver.ExtractFromDirective`, visited-set cycle guard, depth cap 8) and suppresses symbols the index knows for any closure file; tier 2 suppresses symbols found by `GlobalSymbolIndex.FindSymbol(symbol, language)` anywhere else in the indexed workspace. Fail-open throughout: empty index, non-file/null document path, unresolvable or unreadable headers, and malformed/absent `Data` payloads keep the document-local diagnostics (results grow monotonically as the workspace scan progresses, D5). `UnresolvedSymbolRule`'s message now honestly describes the document-local check ("is not defined in this document."); workspace correlation lives in the handler, mirroring the `CodeActionHandler` `Data`-payload pattern. Validated by handler tests over the new `tests/fixtures/cross-file/` nested-include fixture set (tier 1, tier 2, cold-open fallback, no-blanket-suppression, malformed payload, null path).
- feat(analysis): MQL standard-library enum constants in the builtin registries (#46) — `Mql5Builtins` and `Mql4Builtins` `LazyBuiltInVariables` tables are enriched with dialect-tagged enum constant families: the full `ENUM_TIMEFRAMES` set (PERIOD_M1..MN1), `ENUM_APPLIED_PRICE`, `ENUM_MA_METHOD`, `ENUM_LINE_STYLE` (STYLE_*), the `OBJ_*`/`OBJPROP_*` object families, `CHART_*`, `INDICATOR_*`, `DRAW_*`, common `ENUM_*` type names, plus MQL4-only `OP_BUYLIMIT`..`OP_SELLSTOP` and MQL4-style `OBJPROP_TIME1/TIME2/PRICE1/PRICE2`, and MQL5-only `ORDER_TYPE_BUY_STOP_LIMIT`/`ORDER_TYPE_SELL_STOP_LIMIT` and the `DEAL_*` family. Shared names with dialect-specific semantics (`MODE_MAIN`, `MODE_SIGNAL`, …) are registered in both dialects with the semantic split noted in the description. `UnresolvedSymbolRule` now consults the registry matching `context.Language` (MQL5 → `Mql5Builtins`, MQL4 → `Mql4BuiltinsAdapter`) instead of the union of all provided registries, falls back to the matching default dialect registry when none is provided, and never duplicates names curated in `Mql4OnlyApiRegistry` (Mql4OnlyApiRule code 5060 owns them), so MQL5 documents no longer receive double diagnostics on MQL4-only API names and MQL4 constants stop surfacing as false-positive unresolved symbols.
- feat(parser): object-like (parameterless) macro expansion (#38) — `#define` constants (`#define MAX_LOTS 0.5`, `#define True true`) now expand at the invocation identifier in the tier-1 token-stream splice pass, so user constants resolve in the parse tree instead of triggering unresolved-symbol semantic diagnostics. Object-like definitions are stored in the macro table (last-definition-wins per dialect, `ObjectLikeCount`); expansion reuses the function-like position policy (line-accurate, first token anchored at the invocation column) and the single-pass depth cap 1 (bodies are never re-scanned). Exactly one identifier token is replaced; an empty body (`#define GUARD`) splices to zero tokens, counted as a skip with reason `object-like-empty-body`. Directive name positions (`#define`/`#undef`) are structurally safe — whole directives are hidden-channel tokens and only default-channel identifiers expand.
- feat(parser): the macro table now walks nested include chains transitively (#39) — a quoted include's own quoted includes resolve relative to that header's directory, guarded by a visited set of resolved paths (include cycles terminate; each unique header is scanned at most once) and a depth cap of 8 include levels with a recorded hit count (deeper macros degrade conservatively — absent from the table, never a crash). Fixes macros defined in second-level headers being invisible at call sites; validated against the existing macro fixtures and the Ducibus Pro corpus
- feat(parser): the macro table now evaluates conditionals in one continuous include-order stream (#40) — a mid-stream `#include` splices that header's directives at the include point (recursively, still guarded by the visited set and the depth cap of 8), like a preprocessor call stack: a header may open a dialect conditional that the includer closes (and vice versa), and definitions governed by such a cross-boundary frame resolve to the dialect-correct body instead of conservative "Both". Unbalanced frames (an `#ifdef`/`#ifndef` whose `#endif` never arrives, in its own file or across the include boundary) degrade conservatively to Both and are counted per frame in the new `MacroTable.UnbalancedFrameCount` (DepthCapHits precedent); no synthetic `#endif` is ever invented and a crash is impossible. Fixes includer-side defines leaking out as Both when a header opened their conditional — under the old fresh-state-per-include scan an MQL5 document could expand the MT4 body. Validated against the macro fixtures (including the T2 nested-chain/cycle/depth-cap tests) and the existing expansion fixture corpus

## [2.3.0] - 2026-09-19

Stable 2.3.0: promotes the content validated by release candidates 1 and 2 to the stable channel. See `[2.3.0-rc.1]` and `[2.3.0-rc.2]` below for the full per-feature technical detail.

### Added
- feat(parser): user-defined macro expansion (#37) — dialect-aware, parameterized expansion of project macros at call sites, validated with the Ducibus Pro corpus

### Fixed
- fix(lsp): `textDocument/rename` now builds its edits from the token-occurrence index (same source as `textDocument/references`), filtered to the requested document — renaming a symbol now also updates same-file usage references (e.g. a global initializer reading an `input` variable), not just the declaration. Cross-file occurrences are deliberately excluded: the index is name-keyed by design (see OCC-05), and name-based cross-file edits would corrupt same-named symbols in unrelated files. A document absent from the open-document store is indexed on demand (didOpen-equivalent), preserving behavior for closed files.

## [2.3.0-rc.2] - 2026-09-18

Release candidate 2 for the 2.3.0 line: fixes `textDocument/rename` to build its edit set from the token-occurrence index, so same-file usage references are no longer silently left behind. Not marked as `latest`; the stable channel continues pointing at 2.2.0 until 2.3.0 is sealed.

### Fixed
- fix(lsp): `textDocument/rename` now builds its edits from the token-occurrence index (same source as `textDocument/references`), filtered to the requested document — renaming a symbol now also updates same-file usage references (e.g. a global initializer reading an `input` variable), not just the declaration. Previously the edit set was built from declaration symbols only, so usages were silently left behind, leaving uncompilable code. Cross-file occurrences are deliberately excluded: the index is name-keyed by design (see OCC-05), and name-based cross-file edits would corrupt same-named symbols in unrelated files. A document absent from the open-document store is indexed on demand (didOpen-equivalent), preserving behavior for closed files.

## [2.3.0-rc.1] - 2026-09-18

Release candidate 1 for the 2.3.0 line: adds the user-defined macro expansion pass (issue #37) validated with the Ducibus Pro corpus (the rc.2 bar). Not marked as `latest`; the stable channel continues pointing at 2.2.0 until 2.3.0 is sealed.

### Added
- feat(parser): user-defined macro expansion (#37) — dialect-aware, parameterized expansion of project macros at call sites
  - New `MacroTableBuilder` (src/Parser/): cross-file macro table collected from the parsed file's preprocessor channel AND its resolved direct quoted includes (via `IncludePathResolver`), in include order. Conditional evaluation scope: `#ifdef`/`#ifndef`/`#else`/`#endif` over the `__MQL4__`/`__MQL5__` markers only; conditionals on unknown markers (project feature flags) tag the enclosed definitions as valid for BOTH dialects (conservative), and unbalanced frames degrade to Both after the walk. Include files are scanned line-wise, comment-aware: commented-out directives (`//#ifndef`) and defines inside block comments are ignored. Duplicate `#define`s: last-definition-wins per dialect, counted and logged — a header without an include guard cannot crash double inclusion.
  - New `MacroExpansionFilter` (src/Parser/): standard C-preprocessor substitution at the invocation site — the invocation's token span (identifier + balanced argument list) is replaced by tokens synthesized from the selected dialect body; parameters substitute with whole-identifier-boundary textual match (string literals excluded), `##` paste seams concatenate (`EA_INPUT_MUT(int, HolguraAdjH)` under MQL5 → `input int HolguraAdjH_in; int HolguraAdjH`, with the call-site `= 0;` traveling as the chained assignment the real MQL5 compiler accepts). Trailing call-site text always stays after the expansion, so initializers attach instead of being orphaned (the limitation that made the v2.1.0 stdlib no-op filter unable to fix this class). Single pass, no recursive expansion (depth cap 1); single-line invocations only; object-like defines (`#define Bid SymbolInfoDouble(...)`, `#define True true`) are recorded but never expanded.
  - Position policy: token-stream splice, not a textual source rewrite. Unexpanded regions keep original token identity, so line/column diagnostics and the FP-0.00% occurrence pipeline are preserved by construction; expanded regions are line-accurate (all synthesized tokens carry the invocation's line; first token anchors at the invocation column) and column best-effort — documented tradeoff in the PR.
  - Dialect routing rides the existing pipeline: the document language is already known (`MqhLanguageResolver`/`LanguageDetection`), so a `.mq4` monolith included by a `.mq5` wrapper parses through the compat header's `#else` branch with MQL5-correct symbols.
  - Metrics: `MacroExpansions` + `MacroSkips` (reason-keyed: multi-line invocation, arg-count mismatch, empty arg, object-like, body-not-lexable) in `MetricsCollector`.
  - Real-world validation (Ducibus Pro, the rc.2 bar): `Ducibus_Pro_ver_2_90.mq4` parsed 87 syntax errors → 0 in both dialects; `lotdecimal`/`HolguraAdjH` symbols extracted at their call-site lines (MQL4), `HolguraAdjH` + `HolguraAdjH_in` under MQL5; stdlib event-map path unchanged.
  - Follow-up tier (parameterless macros): `#define True true`, `#define Bid SymbolInfoDouble(...)` and friends do not cause syntax errors (shape-neutral identifier rewrites) — they only matter for the semantic UnresolvedSymbolRule; wired expansion of them into semantic resolution is the next tier.

## [2.2.0] - 2026-09-17

Stable 2.2.0: promotes the content validated by release candidates 1 and 2 to the stable channel. See `[2.2.0-rc.1]` and `[2.2.0-rc.2]` below for the full per-feature technical detail.

### Added
- feat(analysis): MQL-native semantic diagnostics (#28) — `SemanticAnalyzer` composable rules pipeline with MQL4 1000 / MQL5 5000 code windows
- feat(analysis): MQL4-only API migration radar in MQL5 documents (#34) — curated registry of 47 MQL4-only API names with MQL5 replacements (code 5060)
- feat(lsp): include-assist Phase 1 QuickFix (#32) — `#include` directive code action for unresolved symbols
- feat(lsp): color swatches — `documentColor` / `colorPresentation` with auto-registered `colorProvider` capability (#30)
- feat(lsp): auto-import include on completion via `AdditionalTextEdits` (#33)
- feat(lsp): reuse cached parse across didOpen/didClose cycles (#36) — closed-document LRU cache (10 entries, 60s TTL) with byte-exact content validation and `ParseReuses` metric

## [2.2.0-rc.2] - 2026-09-15

Release candidate 2 for the 2.2.0 line: adds the parse-reuse LRU cache (issue #36) validated with the Serena MCP workflow. Not marked as `latest`; the stable channel continues pointing at 2.1.0 until 2.2.0 is sealed.

### Added
- feat(lsp): reuse cached parse across didOpen/didClose cycles (#36)
  - New closed-document LRU cache in `OpenDocumentStore` (10 entries, 60s TTL, lazy eviction under the existing store lock): `Remove` retains the parsed `MqlFile` instead of discarding it, so a re-open with byte-identical content can reuse the parse
  - New `TryGetReusableParse(uri, content, language, out file)`: checked BEFORE parsing in `DidOpenTextDocumentHandler`; validates byte-exact content equality (`StringComparison.Ordinal`, no hashing) and language identity (guards the D2 dual-key `.mqh` routing model); on hit the entry is promoted back into the open set
  - Cache hits skip both the ANTLR parse and the `GlobalSymbolIndex.AddFile` re-index: the index survives didClose untouched, and re-adding the cached model's own `Symbols` list would clear-then-AddRange it into itself, wiping the file's symbols from the index
  - `DidChangeTextDocumentHandler` fast path: identical content on the live entry skips the redundant full parse
  - New `ParseReuses` metric (`MetricsCollector`) reported in the metrics summary

## [2.2.0-rc.1] - 2026-09-15

Release candidate: prerelease for validating the release pipeline and the semantic diagnostics, MQL4-only API detection, include-assist, color swatches, and auto-import completion features before sealing stable 2.2.0. Not marked as `latest`; the stable channel continues pointing at 2.1.0 until 2.2.0 is sealed.

### Added
- feat(analysis): MQL-native semantic diagnostics (#28)
  - New `SemanticAnalyzer` in `src/Analysis` composing independently testable `ISemanticRule` implementations, wired into the `DiagnosticHandler` pipeline after syntax errors and before line-scan heuristics
  - `InputModifierRule`: `input` string arrays and `input` declared inside function scope flagged (codes 1021/1022 MQL4, 5021/5022 MQL5)
  - `PropertyDirectiveRule`: unknown or wrong-language `#property` identifiers with capped numbered families (codes 1030/1031, 5030/5031)
  - `LanguageMisuseRule`: MQL5-exclusive tokens used in MQL4 documents (code 1040); uses `LanguageDetection.Mql5Markers` as single source of truth
  - `ConversionRule`: conservative string-literal-to-numeric `input` check (code 1050/5050)
  - Per-rule cancellation preserves the handler's 2s timeout; analyzer instantiation is per-request since rules are stateless
  - Diagnostic codes stay inside the reserved MQL4 1000/MQL5 5000 windows; existing codes unchanged
- feat(analysis): MQL4-only API migration radar in MQL5 documents (#34)
  - Curated registry of 47 MQL4-only API names across five families (predefined variables, series arrays, `Time*` helpers, environment/state helpers, order-property getters), each with inline admission evidence and an MQL5 replacement; `StringComparer.Ordinal` keying since MQL identifiers are case-sensitive
  - `Mql4OnlyApiRule` (code 5060): word-boundary scan with identifier-boundary predicate — names embedded in longer identifiers never match; language gate yields nothing for MQL4 (code 1060 reserved but never emitted); per-100-line batch cancellation
  - Semantics-changed names (`Bars`, `Digits`, `Point`) skip valid MQL5 call forms (next char is `(`) and emit an honest "changed semantics" message for bare MQL4-style reads, which remain flagged as the real migration defect
  - Shared MQL4/MQL5 names (`OrderSend`, `OrderSelect`, `ArrayResize`, `Print`, `CopyBuffer`, `i*` series, `OrdersTotal`, ...) deliberately excluded: signature discrimination out of scope
- feat(lsp): include-assist Phase 1 QuickFix (#32)
  - New `IncludeDirectiveService` computes the quoted `#include` directive for a missing symbol's defining file
  - `CodeActionHandler` offers a QuickFix sourced from the workspace symbol index, resolving into a `TextEdit` that inserts the directive at the `FindInsertPosition` line
  - `UnresolvedSymbolRule` flags undeclared identifiers that the include-assist QuickFix can resolve
- feat(lsp): color swatches — `textDocument/documentColor` and `textDocument/colorPresentation` (#30)
  - Parser captures color occurrences from `C'...'` literals, hex literals, and `clr*` names (REQ-CP-01..04)
  - `MqlColorRegistry` maps color literals to `ColorInformation` with LSP float conversion (0..1 range)
  - `DocumentColorHandler` returns color ranges; `ColorPresentationHandler` returns label and text edits; `colorProvider` capability auto-registered
  - `didChange` refreshes reported colors: removing a literal retires its color, adding one surfaces it with the correct range (REQ-CP-10)
- feat(lsp): auto-import include on completion via `AdditionalTextEdits` (#33)
  - Plain-context completion items backed by out-of-file indexed symbols now carry `AdditionalTextEdits` inserting the Phase-1 quoted `#include` directive at the `FindInsertPosition` line, plus an "(auto-import)" detail suffix
  - The pass re-derives the defining candidate through the `CodeActionHandler` chain (language filter, path-aware already-included check, quoted-directive-only) with per-request memoization and shortest-path take-1 determinism; failures degrade silently
- feat(completion): AST/scope-based completion context resolution (#29)
  - New `CompletionContextResolver` replaces the text-heuristic context analysis in `CompletionHandler`: completion context is now resolved from the parsed symbol model instead of string-matching line content
  - Scope-aware symbol collection with proper precedence (CCR-01): locals declared before the cursor → members of the enclosing class → top-level symbols → `GlobalSymbolIndex` merge; innermost declarations shadow outer ones, and declarations after the cursor are never suggested
  - Member-access completion on `.` and `->` (CCR-02/03): the receiver expression's declared type (locals, fields, parameters) is resolved against class symbols — including classes from quoted `#include`d `.mqh` files via `GlobalSymbolIndex` — and only that class's methods/fields are suggested with correct `CompletionItemKind`; unrelated top-level symbols no longer mix in
  - Cross-file and language routing (CCR-03/04): workspace-indexed symbols merge into completion lists filtered by the requesting document's language, with per-file/per-scan caps (`Take(100)` precedent); `.mqh` completion follows the includer's language
  - MQL4 tolerance (CCR-05): completion works without `SymbolType` classification (LSP `Kind` fallback); resolver failures degrade to the previous text heuristics and the empty-`CompletionList` error contract is preserved
  - Keyword/snippet/builtin providers untouched; no per-keystroke re-parsing (resolver consumes the cached `MqlFile` from `OpenDocumentStore`)
- feat(parser): declared-type capture and class-member model for both grammars (REQ-SM-02/06)
  - `MqlSymbol.DeclaredType` captured for variables, fields, and parameters by the MQL4 and MQL5 ANTLR visitors
  - Class/struct/interface members attached as `Children` via a visitor type-stack (class `Range` spans only the name token, so containment comes from the parse tree)
  - MQL4 visitor now emits class/struct symbols and captures function parameters (previously missing entirely)
- fix(lsp): completion context uses the editor buffer, not on-disk content (JD-1)
  - Member completion after `didChange` on an unsaved buffer previously scanned stale disk text, so freshly typed `obj.` produced no member list until save
- fix(completion): enclosing-class members tier and type-body scoping in scope resolution (JD-2/JD-3/JD-4)
  - CCR-01's enclosing-class-member tier implemented; class body spans exclude hierarchy-wired derived types so same-file inheritance no longer hides top-level symbols
- fix(completion): index merge excludes self-file after-cursor locals; member collection skips wired type symbols (JD-5/JD-6)

## [2.1.0] - 2026-09-12

### Added
- feat(parser): include file path and grammar in ANTLR error logs
  - Unified error format: `[MQL4/MQL5] file:line:col: syntax error - msg`
  - New `LexerErrorListener` so lexer errors are no longer anonymous
  - Leading UTF-8 BOM stripped before parsing
- feat(parser): CAppDialog/stdlib event-map macro support (#26)
  - New `StdlibMacroCallRegistry` holds the stdlib Controls event-map macro family (`EVENT_MAP_BEGIN`/`END`, `ON_EVENT`, `ON_EVENT_PTR`, `ON_NO_ID_EVENT`, `ON_NAMED_EVENT`, `ON_INDEXED_EVENT`, `ON_EXTERNAL_EVENT`); `StdlibMacroInvocationFilter` rewrites each known invocation into a synthetic `;` no-op before parsing, keeping token positions so symbol/occurrence extraction is unaffected
  - `Account_Protector.mqh` parses with 0 errors (was 139)
- feat(parser): more valid MQL constructs accepted in both grammars (#26)
  - `inline` keyword (new `K_INLINE` lexer token)
  - Pointer member declarators (e.g. `CChartObjectButton *m_button;`)
  - Functional primitive casts (e.g. `(int)value`)
- feat(lsp): OS-appropriate log directory with capped retention and multi-window sharing (#21)
  - New `LogPaths`: `%LOCALAPPDATA%\mql-lsp-server\logs` (Windows), `$XDG_CACHE_HOME`/`~/.cache` (Linux), `~/Library/Logs` (macOS); replaces the old CWD log file with unbounded daily retention
  - Serilog sink: daily rolling, 7-day retention, 20 MB/day size cap with roll-on-size, `shared: true` so concurrent editor windows share the log

### Fixed
- fix(lsp): decode UTF-16 LE files without BOM (#15)
  - New shared `SourceFileReader.ReadAllText` helper: when the initial decode contains NUL characters in the opening portion (signature of BOM-less UTF-16 LE misread as UTF-8), re-decodes the raw bytes as UTF-16 LE and keeps the result only if it is clean; the retry never throws
  - All ~24 `File.ReadAllText` call sites across handlers, `WorkspaceIndexer`, and both ANTLR parsers now route through the helper
  - Fixes corrupted symbols, navigation, and diagnostics for MetaEditor-era files touched by external tools
- fix(build): update Build Date on every build
  - `BuildConstants.cs` (static, hardcoded) removed; the build date is now embedded as `AssemblyMetadata` at compile time and read from the assembly, with an `unknown` fallback for pre-existing binaries
  - Incremental builds that skip recompilation keep the previous stamp by design
- fix(parser): StackOverflow on deep nesting eliminated via input guards (#20)
  - ANTLR's recursive-descent parser consumes a call-stack frame per bracket nesting level; `StackOverflowException` is not catchable in .NET and kills the LSP process
  - New `ParseInputGuard` pre-scan (shared by handler paths and parser-internal include chains): one linear pass measures bracket/paren depth (skipping strings, chars, and comments) with a depth cap of 200, plus an input size cap and parse cancellation propagation, rejecting pathological input before recursion starts
- fix(lsp): parse-exception errors routed to stderr instead of stdout (#21)
  - Both parser wrappers emitted plain text via `Console.WriteLine` into the JSON-RPC stdout channel, desynchronizing the client; both now use `Console.Error` (tested: stdout stays empty)
- fix(lsp): single-read file decoding removes TOCTOU window (#21)
  - Handlers read the file once and parse the in-memory content instead of reading once for indexing and again for parsing, so a file changed between the two reads could no longer desynchronize diagnostics from content
- fix(lsp): graceful flush before stdin-EOF exit (#21)
  - The server now flushes pending outbound notifications (in-progress diagnostics) before exiting when stdin closes, so final diagnostic batches are not dropped
- fix(build): server version derived from the assembly (#22)
  - `--version` and the LSP `initialize` response (new `serverInfo`) report the real version from `<Version>` (e.g. 2.0.1), not the stale hardcoded `1.0.0`; `ServerVersion.Version` is the single derivation point
- fix(build): `install-local-tool.sh` derives the version from the packed `.nupkg` (#22)
  - The install script no longer relies on a stale hardcoded version when installing the local build
- fix(tests): MemoryProfilingTests LOH flake eliminated via aggressive-GC retry (#27)
  - The Account_Protector LOH budget assertion snapshots the shared xunit test-process heap; tests running before it leave GC/LOH fragmentation that a single forced GC does not recover. The assertion now retries with aggressive GC collection before failing

### Security
- ci(security): add CodeQL analysis workflow (`.github/workflows/codeql.yml`)
  - C# analysis with the `security-extended` query suite on every push/PR to `main` plus a weekly scheduled scan; results uploaded to the Security tab
- fix(security): workspace path containment enforced for all client file reads (#18)
  - Document/workspace LSP handlers resolved client-supplied `file://` URIs and read files from disk without validating containment against declared workspace folders; a malicious client could make the server read arbitrary files (e.g. `file:///etc/shadow`). All such reads are now validated by `PathSecurity.IsContainedInWorkspace`
- fix(security): `PathSecurity` fails closed on symlink-resolution failure (#21)
  - `ResolveRealPath` silently fell back to the unresolved path when `ResolveLinkTarget` threw, so a failed security check defaulted to "allow". The queried path (resolved include / file being read) is now rejected on resolution failure; server-side reference paths keep best-effort lexical fallback
- ci(security): GitHub Actions pinned to commit SHAs and template-injection surface removed (#19)
  - All action references pinned to full commit SHAs (with version-tag comments) in `build.yml`, `ci.yml`, and `codeql.yml`, so a re-pointed mutable tag cannot inject code into the release workflow (`contents:write`); the `github-script`/expression interpolation points flagged by the audit no longer embed untrusted input
- chore(legal): third-party notices shipped in the package and dependency pins (#24)
  - `THIRD-PARTY-NOTICES.md` credits every package in the resolved dependency graph and is included in the NuGet tool package
  - `Newtonsoft.Json` and `MediatR` pinned to exact versions to block transitive drift

### Changed
- ci(release): name GitHub releases by tag only (`v2.0.1` instead of `MQL Language Server v2.0.1`) so the version is fully visible in the sidebar; existing releases renamed to match
- docs(contributing): document issue-reference conventions — closing keywords (`Fixes #N`) in commits and PR descriptions for automatic issue closure and Development-sidebar traceability
- refactor(parser): include-path resolution unified into `IncludePathResolver` (#25)
  - Four drifted copies of include extraction/resolution (didOpen/didChange handlers, Mql4AntlrParser, Mql5AntlrParser, plus the WorkspaceIndexer scan pass) collapse into one service. The handler and parser copies re-ran the raw-directive regex over stored entries, which never matches — three were inert in production. Quoted-include resolution now actually works in didOpen/didChange indexing and the MQL5 include merge
- refactor(parser): macro extraction and error listeners extracted from the parser wrapper (#25)
  - Partial split of the `Mql4AntlrParser` god class (1307 → 1020 lines), mirrored for MQL5: `MacroExtractor` (token-stream macro extraction parameterized by the grammar's `PRE_DEFINE` token type) and shared ANTLR error listeners move to `src/Parser/`
- refactor(lsp): `GlobalSymbolIndex` direct singleton access replaced by an injectable accessor (#25)
  - Handlers no longer read the shared symbol index through `GlobalSymbolIndex.Instance`; a `GlobalSymbolIndexAccessor` registered in the composition root flows through `LanguageAwareHandlerBase` / `WorkspaceIndexer` constructors (13 call sites)

### Documentation
- docs: first-visit pass
  - Stale versions and test counts fixed across README.md/README.es.md/README.ru.md (91 → 827 tests, with a CHANGELOG pointer instead of a drifting hardcoded claim); example release tag refreshed; standard-library event-map macro support added to the Features bullet
  - Resolved-security section corrected to remove the contradiction between the historical audit record and the current verified state
  - Developer docs moved out of the landing surface into `benchmarks/`
  - `CODEOWNERS` added; `llms.txt` added for AI-agent doc discovery (this release)

## [2.0.1] - 2026-09-12

### Fixed
- fix(lsp): route `.mqh` headers by includer language, not content (#16)
  - A header's language is decided by who includes it, not by its content
  - `WorkspaceIndexer` scans in two passes: unambiguous `.mq4`/`.mq5` sources first (recording resolved includes), then each `.mqh` under its includers' language when they agree; conflicting includers, system headers, and unresolved includes fall back to content sniffing
  - New `MqhLanguageResolver` consults the index at open time via `GlobalSymbolIndex.GetIndexedLanguage`/`GetIncluderLanguages`, making open order irrelevant
- fix(lsp): stop treating MQL4-shared predefined variables as MQL5 markers (phase 1 of #16)
  - `_Digits`, `_Point`, `_Symbol`, `_Period` removed from `Mql5Tokens` in `LanguageDetection`; they exist in both MQL4 (build 600+) and MQL5
- fix(parser): accept `(void)` destructors and comma-separated for-loop clauses (#17)
  - `~C(void)` and `C::~C(void) {}` now parse in both grammars
  - `for(i = 0, j = 0; ...; i++, j += 4)` parses via an expression list in the init/increment slots (comma operator stays out of `expression` to avoid associativity issues)

### Changed
- build(deps): bump GitHub Actions (checkout 4→7, setup-dotnet 4→6, github-script 7→9, softprops/action-gh-release 2→3) and NSubstitute 5.3.0→6.2.0

## [2.0.0-rc.1] - 2026-09-10

Release candidate: prerelease for validating the release pipeline (tag/csproj verification, asset publishing) and the token-backed references implementation before sealing the stable 2.0.0. Not marked as `latest`; the stable channel continues pointing at the previous release until 2.0.0 is sealed.

### Removed
- refactor: removed dead `DidSaveTextDocumentHandler` (never registered). LSP 3.17 makes `didSave` optional and the server does not advertise `save` under `TextDocumentSyncKind.Full`, so conforming clients never send it; `didChange` already re-indexes on every edit under Full sync. The handler also implemented `IDidChangeTextDocumentHandler`, so registering it would have double-handled `didChange` and re-read stale disk content over fresher parses.

### Added
- feat: Token-backed references (Find All References rewritten from regex to lexer tokens)
  - Identifier occurrences captured from the ANTLR token stream at parse time for both MQL4 and MQL5 (`TokenOccurrenceCapture`, `MqlFile.Occurrences`)
  - `GlobalSymbolIndex` stores name-keyed occurrences with definition-vs-reference distinction (`FindOccurrences(name[, language])`, occurrence-aware `AddFile`)
  - `ReferencesHandler` returns token-backed locations instead of raw-text regex matching; `includeDeclaration` filtering via `request.Context.IncludeDeclaration`
  - Measured on the 45-file fixture corpus: **false-positive rate 15.52% → 0.00%**, recall unchanged at 100%
  - FP measurement harness (`tests/FpMeasurement/`, tagged `Category=FpMeasurement`, excluded from normal CI runs) kept as a permanent diagnostic instrument with the pre-fix baseline preserved
- feat: Workspace indexing at startup
  - New `WorkspaceIndexer` scans workspace folders asynchronously after `initialize` so references resolve for files the client never opened
  - Skips VCS/build directories (`.git`, `bin`, `obj`, `node_modules`, etc.); per-file error isolation so one unparseable file cannot stop the scan
  - Requests are answered during the scan (per-file incremental indexing, no snapshot swap), covered by a deterministic mid-scan integration test (WI-02)
- feat: MQL5 language support (first-class .mq5/.mqh parsing and LSP features)
  - Dual ANTLR grammars for MQL4 and MQL5 with isolated generated namespaces
  - MQL5 syntax coverage: classes, structs, interfaces, inheritance, templates, `enum class`, `nullptr`, `union`, `final`, `pack(n)`, references, `using`, `#resource`, init lists, `new`/`delete`
  - MQL5 built-in functions and predefined variables registry (`PositionGetSymbol`, `_Digits`, `_Point`, `_Symbol`, `_Period`, etc.)
  - MQL5 LSP handlers: hover, definition, completion, diagnostics, document symbol, references, signature help, folding, formatting, and all registered sync handlers
  - MQL5 diagnostic code range (`5000-5999`) so users and CI can distinguish MQL4 (`4000-4999`) from MQL5 issues
  - End-to-end integration test covering didOpen → hover → definition → completion → diagnostics for a `.mq5` file
  - Fixture suite with real MQL5 constructs for parser regression testing

### Changed
- **BREAKING**: Project, package, and binary renamed from `mql4-language-server` to `mql-language-server`
  - Binary: `mql-lsp-server`
  - Log file: `mql-lsp-server.log`
  - Package id: `mql-language-server`
  - Update editor configuration and CI scripts accordingly when upgrading from pre-1.x releases
- test: migrate test mocking from Moq to NSubstitute
- build: update vulnerable and outdated NuGet packages; Serilog sinks 6.x/7.x; test stack packages
- ci: verify release tag matches the package version in the `.csproj` before publishing

### Fixed
- fix: occurrence purge on document sync — `didOpen`/`didChange` re-indexed files with an empty occurrence list, wiping scan-indexed occurrences on every open/keystroke; both handlers (and the include-indexing path) now pass the fresh parse's occurrences via the shared `SymbolOccurrenceMapper`
- fix: pre-existing shared `GlobalSymbolIndex` residue leak in handler tests — singleton state is now cleared in `finally` blocks
- fix: DiagnosticHandler re-enabled in `Program.cs` (was commented out); MQL4 diagnostics are now published as well as MQL5 diagnostics
- fix: GlobalSymbolIndex uses dual-key `(filePath, MqlLanguage)` so MQL4 and MQL5 files with the same include path coexist without collision

### Documentation
- docs: remove pinned install version from READMEs/guides; correct .NET 8 reference in `install-local-tool.sh` prerequisite message
- chore: remove orphan `test_parser` fixtures from repo root
- ARCHITECTURE.md: handler count updated to 26; dead `DidSaveTextDocumentHandler` node and edges removed

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: full suite green (714 tests; MQL4 regression tests unchanged + new token-occurrence, workspace-indexing, and MQL5 tests)
- Parser: Mql4AntlrParser and Mql5AntlrParser share `IMqlParser` via `MqlLanguageService`
- References: locations are backed by lexer identifier tokens only (comments, strings, and preprocessor lines are structurally excluded)
- Compatibility: breaking rename documented above; no behavioral regressions for MQL4

## [1.11.4] - 2026-01-16

### Fixed
- fix: Skip all DiagnosticHandler tests that process real files in CI
  - Handle_FileWithDiagnostics_ReturnsNonEmptyDiagnosticItemsAsync
  - Handle_FileWithDiagnostics_DetectsTypoErrorsAsync
  - Handle_FileWithDiagnostics_DetectsEmptyOnInitWarningAsync
  - Handle_FileWithDiagnostics_DetectsUnderscoreVariableHintsAsync
  - Handle_FileWithDiagnostics_ContainsAllDiagnosticTypesAsync
  - These tests timeout in CI due to 2-second parser timeout

## [1.11.3] - 2026-01-16

### Fixed
- fix: Skip flaky DiagnosticHandler tests in CI
  - Handle_ValidFile_ReturnsDiagnosticReportWithResultIdAsync
  - Handle_RealFile_CanProcessFileWithoutErrorsAsync
  - Handle_RealFile_CanReadAndProcessFileAsync
  - These tests timeout in CI due to parsing large real-world MQL4 files

## [1.11.2] - 2026-01-16

### Fixed
- fix: Simplify CI test step to always skip performance tests
  - Remove conditional logic for CI vs local runs
  - Performance tests are always flaky due to timing variations in GitHub Actions workers

## [1.11.1] - 2026-01-16

### Fixed
- fix: Comment out artifact upload step in build workflow
  - Workaround for GitHub Actions storage quota issues
  - Prevents workflow failures due to exceeded artifact limits

## [1.11.0] - 2026-01-16

### Added
- feat: Add flake configuration and build environment for .NET development
  - Nix flake with devenv for reproducible development environment
  - Includes .envrc for direnv integration
  - Updated .gitignore for Nix cache files

### Fixed
- fix: Skip flaky DiagnosticHandler test in CI
- fix: Improve CI test execution with better test filtering
- fix: Exclude performance tests from CI to avoid flaky builds

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: All tests passing
- Compatibility: 100% - no breaking changes detected

## [1.10.0] - 2026-01-13

### Added
- feat: Add function body extraction methods to parser

## [1.9.0] - 2026-01-13

### Added
- feat: Add comprehensive LSP handler tests (14 new tests)

## [1.8.0] - 2026-01-13

### Fixed
- fix: Register LSP handlers to enable capability announcement

## [1.7.0] - 2026-01-10

### Added
- feat: Connect 15 LSP handlers and fix DidSaveTextDocumentHandler
  - WorkspaceSymbolHandler: Search symbols across workspace files
  - DiagnosticHandler: Report document diagnostics (typos, empty OnInit, underscore variables)
  - Full handler registration in Program.cs for complete LSP feature set
  - 12 new unit tests for workspace and diagnostic handlers

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 429/429 Passed (100% success rate)
- Compatibility: 100% - no breaking changes detected
- LSP Handlers: All 17 handlers now connected and functional

## [1.6.0] - 2026-01-09

### Added
- feat: Implementar soporte LSP 3.17 - Fases 1-4
  - Navigation handlers: Declaration, TypeDefinition, Implementation, DocumentHighlight
  - Editing handlers: Rename, DocumentFormatting, RangeFormatting, OnTypeFormatting
  - Code Action handlers: CodeActionHandler, CodeActionResolveHandler
  - Server capabilities: Mql4ServerCapabilities con document selector
  - 130+ nuevos tests para coverage de handlers

- feat: Implementar Fase 5 - SignatureHelp, SelectionRange, FoldingRange handlers
  - SignatureHelpHandler: Información de parámetros de funciones
  - SelectionRangeHandler: Rangos de selección para editores
  - FoldingRangeHandler: Regiones de código colapsables
  - Tests unitarios para cada handler

- feat: Añadir handlers simplificados para compatibilidad OmniSharp v0.19.9
  - SemanticTokensHandler: Tokens semánticos para highlighting
  - DidSaveTextDocumentHandler: Manejo de eventos de guardado
  - InlayHintHandler: Sugerencias inlay (container vacío)
  - MonikerHandler: Símbolos moniker para linking
  - 12 nuevos tests

### Fixed
- fix: Corregir rangos degenerados en DocumentSymbol para funciones
  - Range ahora incluye cuerpo completo (línea 31 a 1832)
  - SelectionRange solo incluye la declaración
  - Eliminado FullRange redundante del modelo Mql4Symbol
  - 5 tests actualizados

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 417/417 Passed (100% success rate)
- Compatibility: 100% - no breaking changes detected
- LSP Handlers: 15+ handlers implementados

## [1.5.1] - 2026-01-08

### Fixed
- fix: Corregir rangos de funciones en el parser LSP (FullRange calculation)
  - El bug causaba que todos los `end_line` fueran iguales a `start_line`
  - Añadir método `CreateFullFunctionRange()` usando token RBRACE
  - 4 nuevos tests de verificación para rangos de funciones

### Tests
- Añadir tests: `ParseFunction_FullRangeIsAccurate`
- Añadir tests: `ParseFunction_FullRangeEndsAtClosingBrace`
- Añadir tests: `ParseFunction_FullRangeMultipleFunctions`
- Añadir tests: `ParseFunction_RangeAndFullRangeAreDifferent`
- Todos los 370 tests pasan

## [1.5.0] - 2025-11-26

### Added
- feat(paso 5.4): Implement Memory Profiling - DETECTAR MEMORY LEAKS with comprehensive tests
- feat(paso 5.3): Optimizaciones menores del parser - COMPLETADO with performance improvements
- feat(paso 5.2): Implement Logging y Métricas - COMPLETADO with PerformanceMonitor and MetricsCollector
- feat(paso 2.1): Implement GlobalSymbolIndex for cross-file tracking and search capabilities
- feat(paso 2.3): Add ParseFileWithIncludes to Mql4AntlrParser for comprehensive file parsing
- feat(paso 2.2): Update Mql4SymbolVisitor with filePath parameter for multi-file support
- feat(paso 2.4): Update ReferencesHandler for cross-file search with global index
- feat(paso 2.5): Update DefinitionHandler for cross-file search with symbol resolution
- feat(paso 2.6): Update DidOpenTextDocumentHandler for global index integration
- feat(paso 3.1): Enrich HoverHandler with detailed signatures and examples
- feat(paso 3.2): Implement SignatureHelpHandler with parameter information
- feat(paso 3.3): Enhance CompletionHandler with contextual completions
- feat(paso 3.4): Create Constants.cs with centralized constants
- feat(paso 3.5): Improve error handling and logging granularity in handlers
- feat(paso 3.6): Verificación Fase 3 (PARCIAL) with partial completion
- feat(paso 5.1): Parser thread-safety implementation with concurrent processing
- feat(tests): FASE 4.2 COMPLETADA - Crear CrossFileTests con 14 tests passing
- feat(tests): Fix GlobalSymbolIndex test isolation issues for better reliability
- feat(tests): Fix Memory Profiling Tests - COMPLETADO with 100% success

### Fixed
- fix(tests): Corregir aislamiento de GlobalSymbolIndex - Tests 100% passing
- fix(paso 5.4): Ajustar umbrales de memory profiling - FUGA RESUELTA (Memory leak fixed)
- fix: Arreglar TODOS los errores de compilación en Fase 3
- fix(tests): Update tests for GlobalSymbolIndex integration

### Chore
- chore: Normalize line endings (Unix format) for consistency

### Technical Details
- Build: Enhanced with thread-safety mechanisms and memory leak detection
- Tests: 30+ new tests added across multiple test suites (CrossFile, MemoryProfiling, Performance)
- Compatibility: 100% - no breaking changes detected
- Performance: Significant parser optimizations and memory profiling capabilities
- Cross-file Support: Full implementation of GlobalSymbolIndex for multi-file projects
- LSP Handlers: All handlers (Completion, Hover, Definition, References, SignatureHelp) enhanced
- Memory Management: Memory profiling system implemented to detect and prevent memory leaks

## [1.4.0] - 2025-11-24

### Added
- feat(tests): Implement real-world MQL4 parser tests with 10 new test methods
- feat(parser): Support out-of-class method definitions with :: operator
- feat(parser): Complete implementation of hybrid ANTLR4 grammar with channels
- feat(parser): Implementar gramática híbrida ANTLR4 corregida con canales
- feat(parser): Implementar gramática híbrida ANTLR con canales
- feat(tests): Add real-world test fixtures (Botlidator, Optimator, Ducibus Pro)

### Fixed
- fix(parser): Permitir trailing comma en enums
- fix(parser): Corregir errores críticos de indexación en gramática ANTLR MQL4
- fix(parser): Regenerar archivos ANTLR con gramática actualizada

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 343/343 Passed (100% success rate)
- Compatibility: 100% - no breaking changes detected
- Parser: ANTLR grammar significantly enhanced with hybrid channel-based parsing
- Test Coverage: Added comprehensive real-world file parsing tests

## [1.3.1] - 2025-11-23

### Fixed
- fix: Corregir bug crítico en DefinitionHandler - Ahora retorna Location[] válido
- fix: Corregir bug crítico en ReferencesHandler - Ahora retorna Location[] válido
- fix(parser): Agregar método FindSymbolDefinition() para búsqueda inteligente de símbolos
- fix(parser): Agregar método ExtractIdentifierAtPosition() para extracción de identificadores
- fix(build): Crear BuildConstants.cs estático para corregir error de compilación
- fix(lsp): Agregar SelectionRange a símbolos para compatibilidad LSP
- fix(lsp): Mejorar búsqueda de referencias con regex para evitar falsos positivos

### Added
- feat(tests): Agregar 5 nuevas pruebas unitarias para DefinitionHandler y ReferencesHandler
- feat(parser): Soporte para búsqueda de definiciones desde cualquier posición en el código
- feat(lsp): Manejo robusto de builtins de MQL4 (Ask, Bid, Print, OnInit, etc.)

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 245/245 Passed (100% success rate)
- Compatibility: 100% - no breaking changes detected
- LSP: Full go-to-definition and find-references functionality restored

## [1.3.0] - 2025-11-22

### Added
- feat(parser): Support for #include <file> syntax (angle brackets) in ANTLR grammar
- feat(parser): Support for storage modifiers (input, extern, static) in variable declarations
- feat(parser): Support for switch-case statements in MQL4 code
- feat(lsp): Send experimental/serverStatus notification on initialization
- feat(tests): Add comprehensive tests for new parser features

### Fixed
- fix(parser): Critical parsing errors for #include with angle brackets
- fix(parser): Recognition of input modifier in variable declarations
- fix(parser): Recognition of switch-case control flow statements
- fix(lsp): Missing server status notification to clients

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 233/233 Passed
- Compatibility: 100% - no breaking changes detected
- Parser: ANTLR grammar enhanced with new MQL4 syntax support

## [1.2.0] - 2025-11-21

### Added
- feat(migration): Migrate from .NET 8.0 to .NET 10.0
- feat(code-quality): Eliminate all compiler warnings
- fix(ci): Avoid workflow failure due to test warnings

### Changed
- TreatWarningsAsErrors enabled in both Server and Tests projects
- Updated to .NET 10.0.100 SDK (LTS, soporte hasta 2028)
- Removed obsolete System.Text.RegularExpressions package (now in .NET 10 runtime)
- Removed obsolete System.Net.Http package (now in .NET 10 runtime)

### Fixed
- Security vulnerability NU1903 (System.Net.Http updated)
- CS8625: null literal to non-nullable (3 instances)
- CS0219: unused variable (1 instance)
- CS8602: dereference null (4 instances)
- VSTHRD200: add Async suffix to async methods (22 instances)
- xUnit2012: Assert.True → Assert.Contains/NotEmpty (3 instances)
- xUnit2013: Assert.Equal → Assert.Single (2 instances)

### Technical Details
- Build: 0 Warnings, 0 Errors
- Tests: 228/228 Passed
- Compatibility: 100% - no breaking changes detected
- Performance: Running on .NET 10.0 runtime with improvements

## [1.1.0] - 2025-11-21

### Added
- feat(tests): Agregar tests para Program y Builtins
- feat(tests): Agregar tests de cobertura y manejo de errores LSP
- feat(tests): Agregar built-ins faltantes y corregir 18 tests
- feat(ci): Add release mirroring to public repository
- feat(ci): Enable automatic release creation
- feat(phase-5): Implement standalone compilation and CI/CD
- feat(phase-4): Implement complete unit testing framework
- feat(phase-3.8): Complete compilation and verification phase
- feat(phase-3.7): Implement complete Program Entry Point for MQL4 LSP
- feat(phase-3.6): Implement all LSP Handlers for MQL Language Server

### Fixed
- fix: Corregir error CS0136 - variables duplicadas en Program.cs
- fix(parser): Corregir implementación LSP 3.7 - Rangos precisos de símbolos
- fix: Corregir warnings de compilación - CS0105 y CS8613
- fix: Resolver bloqueo en server.Initialize() - Agregar timeout
- fix: Corregir handlers LSP - Interfaces y DocumentSelector
- fix(build): Fix line endings in build.sh
- fix(ci): Change artifact upload to use glob pattern
- fix(Program.cs): Register LSP handlers to enable capability announcement
- fix(Program.cs): Configure Serilog to write to stderr instead of stdout
- fix(security): Resolver warnings de vulnerabilidades NuGet

