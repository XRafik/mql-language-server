# Feature: issue-120-diagnostic-provider-capability + release 2.5.0

GitHub issue: https://github.com/davalillo/mql-language-server/issues/120
Bug: v2.5.0-rc.5 answers textDocument/diagnostic but never declares diagnosticProvider
in initialize (nor registers it dynamically), so spec-conformant clients gate pull off
and never send it. Root cause: the same OmniSharp 0.19.9 capability-derivation behavior
already worked around for #95 (workspaceSymbolProvider) and #96 (callHierarchyProvider)
— the registration-options descriptor lookup consults the CLIENT's declared capability.
DiagnosticHandler's GetRegistrationOptions is complete but nobody forces the capability
in OnInitialized (Program.cs).

## Tasks

- [ ] T1 Branch fix/120-diagnostic-provider-capability from main; declare
      result.Capabilities.DiagnosticProvider unconditionally in the OnInitialized
      delegate of src/Program.cs, mirroring the #95/#96 pattern with the same options
      DiagnosticHandler.GetRegistrationOptions produces (DocumentSelector,
      InterFileDependencies=false, WorkspaceDiagnostics=false).
- [ ] T2 Contract test: initialize with a client that declares no capabilities must
      include diagnosticProvider (mirror the #95/#96 test style).
- [ ] T3 Verify: full test suite green; raw-stdio probe shows diagnosticProvider in
      the initialize result and a live textDocument/diagnostic answer.
- [ ] T4 PR for the fix (issue-first: status:approved + type:bug).
- [x] T5 Release 2.5.0 stable: PR #122 merged (347532c), tag v2.5.0 pushed,
      Build-and-Release workflow succeeded, GitHub release published as STABLE
      (isPrerelease=false) with 6 binaries (linux/osx/win × x64/arm64), nupkg and
      CHECKSUMS.txt. Tag↔csproj gate passed.

## Evidence log

- (entries added only after observed results)

## Commit evidence

- 7f8b076 fix(lsp): declare diagnosticProvider unconditionally in initialize (issue #120)
  — PR #121 merged (a1dcdad); native review review-545d72b46f59c322 approved + acknowledged
- 485e633 chore(release): bump version to 2.5.0 — PR #122 merged (347532c)
- Tag v2.5.0 pushed; release https://github.com/davalillo/mql-language-server/releases/tag/v2.5.0
