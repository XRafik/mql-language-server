import { describe, expect, it } from "vitest";

import { assetForPlatform } from "../src/platform";

describe("assetForPlatform", () => {
  it.each([
    ["win32-x64", "mql-lsp-server-win-x64.exe"],
    ["win32-arm64", "mql-lsp-server-win-arm64.exe"],
    ["linux-x64", "mql-lsp-server-linux-x64"],
    ["linux-arm64", "mql-lsp-server-linux-arm64"],
    ["darwin-x64", "mql-lsp-server-osx-x64"],
    ["darwin-arm64", "mql-lsp-server-osx-arm64"],
  ])("maps supported platform %s to %s", (platform, expected) => {
    expect(assetForPlatform(platform)).toBe(expected);
  });

  it.each(["freebsd-x64", "sunos-arm64", "win32", "darwin", ""])(
    "returns null for unsupported platform %s",
    (platform) => {
      expect(assetForPlatform(platform)).toBeNull();
    },
  );
});
