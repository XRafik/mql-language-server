/**
 * Pure mapping from VS Code platform identifiers (`process.platform`-`process.arch`)
 * to the release asset names published by the mql-language-server release pipeline.
 *
 * This module must stay free of VS Code API imports so it can be tested with vitest.
 */

const ASSET_BY_PLATFORM: Readonly<Record<string, string>> = {
  "win32-x64": "mql-lsp-server-win-x64.exe",
  "win32-arm64": "mql-lsp-server-win-arm64.exe",
  "linux-x64": "mql-lsp-server-linux-x64",
  "linux-arm64": "mql-lsp-server-linux-arm64",
  "darwin-x64": "mql-lsp-server-osx-x64",
  "darwin-arm64": "mql-lsp-server-osx-arm64",
};

/**
 * Returns the release asset name for the given VS Code platform, or `null`
 * when the platform is not supported by the release pipeline.
 *
 * @param platform Platform identifier in `<os>-<arch>` form, e.g. `linux-x64`.
 */
export function assetForPlatform(platform: string): string | null {
  return Object.prototype.hasOwnProperty.call(ASSET_BY_PLATFORM, platform)
    ? ASSET_BY_PLATFORM[platform]
    : null;
}
