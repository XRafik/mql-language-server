# MQL4/MQL5 Language Server (VS Code extension)

VS Code client for [mql-lsp-server](https://github.com/davalillo/mql-language-server),
a .NET 10 Language Server for MQL4/MQL5 (MetaTrader).

## How it works

On activation over an `.mq4`, `.mq5`, or `.mqh` document, the extension resolves
the `mql-lsp-server` binary in this order:

1. The `mqlLanguageServer.serverPath` setting, if set.
2. A previously downloaded binary in the extension's global storage (reused when
   its recorded asset matches the current platform).
3. An automatic download from the
   [GitHub releases](https://github.com/davalillo/mql-language-server/releases)
   of the matching binary for the current platform, verified against the
   published `CHECKSUMS.txt` (SHA256).

The server is then started with `--stdio` for the `mql4` and `mql5` languages.

Unsupported platforms are reported with an error message pointing to the
`mqlLanguageServer.serverPath` escape hatch.

## Settings

| Setting | Description |
| --- | --- |
| `mqlLanguageServer.serverPath` | Path to a custom `mql-lsp-server` binary. Overrides automatic download. |
| `mqlLanguageServer.logLevel` | Client output channel verbosity: `off`, `error`, `info` (default), `debug`. |

## Development

```sh
npm install
npm run compile   # tsc typecheck/build to dist/
npm test          # vitest (pure logic only, no VS Code API)
npm run package   # esbuild bundle + vsce package -> davalillo-mql-language-server-<version>.vsix
```
