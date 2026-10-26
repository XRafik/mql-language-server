# signatureHelp returns the containing function's signature at method calls (issue #77)

Branch: `fix/77-signature-help-member-call` (base: `origin/main`) · Issue: #77 · Related: #55, #62, #64 (PR #68), #67

## Problem

`textDocument/signatureHelp` at `person.Greet()` (cursor on `(` or inside the
parens) falls back to `FindSymbolAtPosition`, which matches by body containment
and therefore resolves to the enclosing function (`OnInit`). Returning a wrong
signature is worse than returning none. Also, cursor on the member name
(`Greet`) finds nothing because the method lives in `person.mqh`.

## Root cause

`SignatureHelpHandler.HandleForLanguage` (src/Lsp/Handlers/SignatureHelpHandler.cs:94-95):

```csharp
var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character)
    ?? parser.FindSymbolAtPosition(mqlFile, line, character);
```

The containment fallback is acceptable as an internal step (cf. #67 for
documentHighlight) but not as a `signatureHelp` answer.

## Plan

1. [x] ODD tracking doc + Engram mirror
2. [x] Impact analysis before edits (gitnexus: SignatureHelpHandler UNKNOWN — DI dispatch boundary; text-confirmed callers: Program.cs:154 registration + tests; detect_changes before commit: clean run, risk_level high from HandleForLanguage hub in shared Handle→ParseFile flows — only the signatureHelp dispatch step changed, full suite green)
3. [x] Call-target extraction: dotted identifier chain immediately before the
       nearest `(` at/left of the cursor (cursor on callee name, on `(`, or
       inside the parens)
4. [x] Resolution: current-file `FindSymbolDefinition` first (builtins +
       identifier case, preserves #55); then plain name → workspace global
       index (function-like validation, mirroring DefinitionHandler); then
       `receiver.member` → receiver `DeclaredType` →
       `TypeDeclarationResolver` (same file, then indexed header) → type
       symbol `Children` → member (Function/Method kinds)
5. [x] Removed the `FindSymbolAtPosition` containment fallback: unresolvable
       call target → `null` (supersedes the #55 argument-list containment
       fallback; that regression test updated to assert null)
6. [x] Regression tests in SignatureHelpHandlerTests (fixture pattern of
       NavigationHandlersTests.IndexClassFixture: seed GlobalSymbolIndex +
       OpenDocumentStore, Mql4AntlrParser): member call resolves method
       signature (cursor on `(` / inside parens / on member name),
       unresolvable target → null (missing member + unknown receiver),
       builtin call → non-null; test class joined the serialized
       "GlobalSymbolIndex Tests" collection
7. [x] Full test suite green (1228 passed / 0 failed / 0 skipped, writer-run); detect_changes scope all clean run before commit
8. [x] Work-unit commit 8fd7ab9 on fix/77-signature-help-member-call

## Evidence log

- Fixture constraint: PR #80 (#76 ctor-style grammar) is NOT merged; the test
  fixture uses `Person person;` (parses on main) instead of
  `Person person("Alice", 30);`.
- Model facts: methods are attached as `Children` of the type symbol (both
  visitors) and also live in the flat `Symbols` list with `Kind=Method`
  (MQL4) / `SymbolType.Method` (MQL5); variables/parameters carry
  `DeclaredType`; `Mql4File`/`Mql4Symbol` are global aliases of the unified
  `MqlFile`/`MqlSymbol` (src/Models/CompatShim.cs).
- Writer deviations (all compiler/test-proven, accepted):
  1. Label = `Name: Detail` when Detail exists — the parser's method Detail
     ("Function returning string") omits the member name, so the required
     "Label contains Greet" assertion was unsatisfiable under `Detail ?? Name`.
  2. `IsCallable` carries `[NotNullWhen(true)]` for nullable flow analysis.
  3. `ResolveTypeSymbol` is a private helper mirroring
     TryResolveTypeLocation but returning the symbol (with Children).
  4. Fixture adds `person.Missing();` / `unknownObj.Method();` statements as
     homes for the negative tests (parse cleanly).
- Validation: dotnet build clean (0 errors / 0 warnings); focused
  SignatureHelpHandler filter 9/9; full `dotnet test` 1228/1228.
- Commit 8fd7ab9 `fix(lsp): resolve the called function's signature in
  signatureHelp (#77)` on fix/77-signature-help-member-call (base origin/main 007d2d5).
