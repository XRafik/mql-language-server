# issue-163: MQL5 builtin golden list misses File* family and TIME_*/FILE_* constants

Issue: https://github.com/davalillo/mql-language-server/issues/163

## Problem

False 5070 (UnresolvedSymbol) diagnostics in MQL5 documents using file I/O or
datetime string formatting: `data/builtins/mql5.json` is missing the `File*`
function family and the `TIME_*` / `FILE_*` / `SEEK_*` / `CP_*` constants.
`UnresolvedSymbolRule` consults the dialect registry (`Mql5Builtins`), which is
built from the embedded golden list, so the fix is a data-only change per the
#136 contract.

Verified gap in `mql5.json` (139 fns / 345 vars at review time):

- 16 functions: `FileOpen`, `FileClose`, `FileRead`, `FileWrite`,
  `FileWriteString`, `FileReadString`, `FileIsEnding`, `FolderCreate`,
  `FolderDelete`, `FileDelete`, `FileCopy`, `FileMove`, `FileSeek`,
  `FileSize`, `FileTell`, `FileFlush`
- 14 constants: `TIME_DATE`, `TIME_MINUTES`, `TIME_SECONDS`, `FILE_COMMON`,
  `FILE_WRITE`, `FILE_READ`, `FILE_TXT`, `FILE_ANSI`, `FILE_CSV`, `SEEK_SET`,
  `SEEK_CUR`, `SEEK_END`, `CP_UTF8`, `CP_ACP`

Correction vs the issue: `FILE_BIN`, `FILE_SHARE_READ`, `FILE_SHARE_WRITE` are
already present in `mql5.json` (the issue overcounted by 3).

MQL4 symmetry (same failure class, smaller gap — all names exist in MQL4
build 600+):

- functions: `FileWriteString`, `FileReadString`, `FolderCreate`,
  `FolderDelete`, `FileCopy`, `FileMove`, `FileTell`
- constants: `TIME_MINUTES`, `FILE_COMMON`, `FILE_TXT`, `FILE_ANSI`,
  `SEEK_SET`, `SEEK_CUR`, `CP_UTF8`, `CP_ACP`

## Tasks

- [ ] T1: Regression tests first (red): new
      `tests/Analysis/Issue163BuiltinRegistryCoverageTests.cs` pinning all
      missing MQL5 + MQL4 names, plus an end-to-end `UnresolvedSymbolRule`
      test over the issue repro snippet asserting no 5070 for the new builtins.
- [ ] T2: Add the 16 functions + 14 constants to `data/builtins/mql5.json`
      with signatures/descriptions from official docs, `apiTier: shared`,
      section-level `docUrl`, `provenance: issue-163-documented`.
- [ ] T3: Add the 7 functions + 8 constants to `data/builtins/mql4.json`
      (same metadata style; MQL4 docUrls per existing conventions).
- [ ] T4: Run focused tests, then full suite + build.
- [ ] T5: Update CHANGELOG.md (user-visible diagnostics fix).
- [ ] T6: Work-unit commit(s) on feature branch; record commit identity here.

## Commit evidence

(appended after each work-unit commit)
