import { describe, expect, it } from "vitest";

import { parseChecksums, verifyChecksum } from "../src/checksum";

// Realistic excerpt of the CHECKSUMS.txt published by the release pipeline
// (header comment lines, CRLF line endings, `sha256sum` output).
const CHECKSUMS_TEXT = [
  "# SHA256 checksums for v1.2.3",
  "# Generated: 2025-01-01",
  "",
  "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  mql-lsp-server-linux-x64",
  "1111111111111111111111111111111111111111111111111111111111111111  mql-lsp-server-linux-arm64",
  "2222222222222222222222222222222222222222222222222222222222222222  mql-lsp-server-osx-x64",
  "3333333333333333333333333333333333333333333333333333333333333333  mql-lsp-server-osx-arm64",
  "4444444444444444444444444444444444444444444444444444444444444444  mql-lsp-server-win-x64.exe",
  "5555555555555555555555555555555555555555555555555555555555555555  mql-lsp-server-win-arm64.exe",
  "",
].join("\r\n");

describe("parseChecksums", () => {
  it("parses a realistic CHECKSUMS.txt with header comments and CRLF endings", () => {
    const checksums = parseChecksums(CHECKSUMS_TEXT);
    expect(checksums.size).toBe(6);
    expect(checksums.get("mql-lsp-server-linux-x64")).toBe(
      "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    );
    expect(checksums.get("mql-lsp-server-win-x64.exe")).toBe(
      "4444444444444444444444444444444444444444444444444444444444444444",
    );
    expect(checksums.get("mql-lsp-server-osx-arm64")).toBe(
      "3333333333333333333333333333333333333333333333333333333333333333",
    );
  });

  it("normalizes uppercase hex to lowercase", () => {
    const text = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789  binary";
    const checksums = parseChecksums(text);
    expect(checksums.get("binary")).toBe(
      "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
    );
  });

  it("skips comments, blank lines, and malformed lines", () => {
    const text = [
      "# comment",
      "",
      "no-hash-here",
      "deadbeef  too-short",
      "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "   ",
      "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  valid-file",
    ].join("\n");
    const checksums = parseChecksums(text);
    expect(checksums.size).toBe(1);
    expect(checksums.has("valid-file")).toBe(true);
  });

  it("returns an empty map for empty input", () => {
    expect(parseChecksums("").size).toBe(0);
  });
});

describe("verifyChecksum", () => {
  const expected = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

  it("accepts a matching digest regardless of case", () => {
    expect(verifyChecksum(expected, expected)).toBe(true);
    expect(verifyChecksum(expected, expected.toUpperCase())).toBe(true);
  });

  it("rejects a mismatched digest", () => {
    const actual = "1111111111111111111111111111111111111111111111111111111111111111";
    expect(verifyChecksum(expected, actual)).toBe(false);
  });

  it("rejects invalid or truncated digests", () => {
    expect(verifyChecksum(expected, "deadbeef")).toBe(false);
    expect(verifyChecksum("", expected)).toBe(false);
    expect(verifyChecksum("not hex at all", "0123456789abcdef")).toBe(false);
  });
});
