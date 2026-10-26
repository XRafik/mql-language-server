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

1. [x] Branch + ODD tracking doc + Engram mirror
2. [x] Scaffold `vscode/mql-language-server` (package.json, tsconfig, language config, minimal grammar registration)
3. [x] Extension main: settings + platform mapping + cached-binary resolution
4. [x] Downloader: GitHub Releases fetch + CHECKSUMS.txt SHA256 verification + globalStorage install
5. [x] LSP client wiring (`--stdio`, initialization options, log level setting)
6. [x] Unit tests for platform mapping + checksum verification (vitest 18/18; tsc clean)
7. [x] CI: build + package VSIX on release (build.yml), always-on vscode-extension job (ci.yml)
8. [x] Docs (DISTRIBUTION.md, VSCODE_EXTENSION.md, docs/README.md, CHANGELOG) + issue #70 progress comment
9. [x] detect_changes + work-unit commits (ab373d1 feat, b3f5f5d ci, 1e79d76 docs)

## Evidence log

- Worker 1 (muebxg4u-1-n3sc): extension implemented; vitest 18/18, tsc strict clean, esbuild bundle 779.6 KB, vsce package 0.1.0 (120 KB). Parent re-verified compile + tests (18/18) and reviewed downloader/extension/package.json.
- Worker 2 (muec8i1n-2-yicw): CI wiring + docs + LICENSE copy; YAML validated; packaging now includes LICENSE.txt, vsce warning gone.
- GitNexus impact pre-edit: build.yml not graph-indexed (UNKNOWN) → verified by inspection (tag-triggered workflow, additive changes only); detect_changes scope all: low risk, no partial/truncated, only docs sections touched, C# code untouched.
- Note: VSIX asset is NOT in CHECKSUMS.txt (glob covers server binaries only); the extension verifies the server binary it downloads, not the VSIX itself — potential follow-up if users want to verify the VSIX.
- Not yet verified: live VS Code host run (needs manual smoke test) and Marketplace publish (deferred).
- Known limitation (verifier finding, docs corrected): cached binary is reused indefinitely while metadata.asset matches the platform — no server-version check on activation. Follow-up option: record the release tag in metadata.json and compare against the latest release tag (one GitHub API call per activation) to auto-update; deliberately deferred.
- Independent verifier: APPROVED (8/8 checks, task muechaog-3-yz7v).