# VS Code extension bundling/downloading the language server (issue #70)

Branch: `feat/70-vscode-extension` · Issue: #70 · Related: #70-#75 distribution roadmap

## Decisions (user-approved 2026-)

- Binary resolution: **download on activation** from GitHub Releases + SHA256 verification, stored in `globalStorage`. Single multi-platform VSIX.
- Publishing: **VSIX as GitHub Release asset**. Marketplace publish deferred (needs publisher account + `VSCE_PAT` secret).
- Rationale: 1 VSIX per release, zero per-platform maintenance; package managers (#71-#75) deferred.

## Design

- New `vscode/mql-language-server/` extension (TypeScript, `vscode-languageclient`).
- Activation: `.mq4`/`.mq5`/`.mqh` documents.
- Binary resolution order: setting `mqlLanguageServer.serverPath` (dev override) → cached binary in globalStorage (verified) → download from `releases/latest/download/mql-lsp-server-<asset-name>` with SHA256 from `CHECKSUMS.txt` (same release, verified against the expected hash line).
- Asset naming maps VS Code platform (`win32-x64`, `win32-arm64`, `linux-x64`, `linux-arm64`, `darwin-x64`, `darwin-arm64`) → release asset names (incl. `.exe` on Windows).
- Client starts server with `--stdio`; language configuration + basic grammar registration for mql4/mql5.
- CI: build job compiles + `vsce package` → uploads `mql-language-server-<version>.vsix` to the release (build.yml).
- Docs: DISTRIBUTION.md + new VSCODE_EXTENSION guide + README pointers.

## Plan

1. [ ] Branch + ODD tracking doc + Engram mirror
2. [ ] Scaffold `vscode/mql-language-server` (package.json, tsconfig, language config, minimal grammar registration)
3. [ ] Extension main: settings + platform mapping + cached-binary resolution
4. [ ] Downloader: GitHub Releases fetch + CHECKSUMS.txt SHA256 verification + globalStorage install
5. [ ] LSP client wiring (`--stdio`, initialization options, log level setting)
6. [ ] Unit tests for platform mapping + checksum verification (vitest)
7. [ ] CI: build + package VSIX on release, upload asset; lint/typecheck job on PR
8. [ ] Docs (DISTRIBUTION.md, VSCODE_EXTENSION.md, READMEs) + issue #70 progress comment
9. [ ] detect_changes + work-unit commits

## Evidence log

- (pending)