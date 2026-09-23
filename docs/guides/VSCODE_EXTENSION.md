# VS Code Extension — MQL Language Server

This guide documents the official VS Code extension for the MQL Language Server (MQL4/MQL5). It bundles a language client that starts and manages `mql-lsp-server` for you — no manual editor configuration needed.

## 📦 What It Is

- A VS Code extension (`davalillo.mql-language-server`) that activates on `.mq4`, `.mq5`, and `.mqh` files
- Provides completions, diagnostics, and navigation via the .NET 10 language server
- Resolves and downloads the correct `mql-lsp-server` binary for your platform automatically
- Source lives in [`vscode/mql-language-server/`](../../vscode/mql-language-server/)

## 🚀 Installation (GitHub Releases)

Marketplace publishing is not available yet, so today you install from a GitHub Release asset:

**Option A — Install from VSIX (recommended):**

1. Download `mql-language-server-<tag>.vsix` from the
   [Releases page](https://github.com/davalillo/mql-language-server/releases)
   (for example, in the `v1.0.17` release).
2. In VS Code, open the Command Palette (`Ctrl+Shift+P` / `Cmd+Shift+P`) and run
   **Extensions: Install from VSIX…**, then select the downloaded file.

**Option B — Command line:**

```bash
code --install-extension mql-language-server-v1.0.17.vsix
```

## ⚙️ How Binary Resolution Works

On activation over an MQL document, the extension resolves the `mql-lsp-server` binary in this order:

1. **`mqlLanguageServer.serverPath` setting** — if set, that binary is used directly and nothing is downloaded.
2. **Cached binary** — a previously downloaded binary stored in the extension's global storage is reused when its recorded asset matches the current platform.
3. **Automatic download** — otherwise the extension downloads the matching binary for the current platform (`mql-lsp-server-<rid>` from the
   [GitHub releases](https://github.com/davalillo/mql-language-server/releases)) and verifies its SHA256 against the published `CHECKSUMS.txt` before installing it.

The server is then started with `--stdio`. The extension version (`0.1.0`) is independent of the server version: each new release ships a fresh VSIX, and the extension picks up new server binaries on demand.

## 🔧 Settings

| Setting | Default | Description |
|---------|---------|-------------|
| `mqlLanguageServer.serverPath` | `""` (empty) | Path to a custom `mql-lsp-server` binary. Overrides automatic download. |
| `mqlLanguageServer.logLevel` | `info` | Client output channel verbosity: `off`, `error`, `info`, `debug`. |

## 🩺 Troubleshooting

**Offline on first run.**
The first activation without a cached binary needs network access to download it from GitHub Releases. If you are offline, the activation fails with a download error. Fix: pre-download the binary for your platform from a release (see [Distribution](DISTRIBUTION.md)), place it anywhere on disk, and point `mqlLanguageServer.serverPath` at it. Once a binary is cached, subsequent runs work offline.

**Unsupported platform.**
The release pipeline publishes `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, and `win-arm64`. On any other platform the extension shows an error message and does not download anything. Escape hatch: build or obtain a binary yourself and set `mqlLanguageServer.serverPath` to its path.

**Checksum mismatch.**
If the downloaded binary does not match the SHA256 published in `CHECKSUMS.txt`, the extension discards it and reports an error instead of running unverified code. This usually means a corrupted download or a tampered asset. Fix: retry the download; if it persists, report it in the [issue tracker](https://github.com/davalillo/mql-language-server/issues) and use `mqlLanguageServer.serverPath` with a manually verified binary in the meantime.

## 🛠 Development

See [`vscode/mql-language-server/README.md`](../../vscode/mql-language-server/README.md) for the extension architecture, binary-resolution details, and development commands:

```sh
cd vscode/mql-language-server
npm ci
npm run compile   # tsc typecheck/build to dist/
npm test          # vitest (pure logic only, no VS Code API)
npm run package   # esbuild bundle + vsce package -> *.vsix
```

CI runs the same steps in the `vscode-extension` job of [`.github/workflows/ci.yml`](../../.github/workflows/ci.yml), and the release workflow packages and uploads the VSIX in [`.github/workflows/build.yml`](../../.github/workflows/build.yml).

## 🗺 Marketplace Publishing (Deferred)

Publishing to the VS Code Marketplace is intentionally deferred: it requires a
[publisher account](https://marketplace.visualstudio.com/manage) and a Personal Access Token (`VSCE_PAT`) with Marketplace manage permissions. Once configured, `vsce publish` can be added to the release workflow.

---

**Version**: 0.1.0
**Author**: MQL Language Server Team
