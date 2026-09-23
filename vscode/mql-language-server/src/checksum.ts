/**
 * Pure parsing and verification helpers for the CHECKSUMS.txt files published
 * with every mql-language-server release.
 *
 * Format (produced by `sha256sum` in the release pipeline):
 *   # comment/header lines start with '#'
 *   <64 hex chars>  <filename>          (two spaces, but any whitespace run is accepted)
 *
 * This module must stay free of VS Code API and Node imports so it can be
 * tested with vitest.
 */

const HASH_LENGTH = 64;
const HEX_RE = /^[0-9a-f]+$/;

/**
 * Parses a CHECKSUMS.txt document into a map of filename -> lowercase sha256 hex.
 * Blank lines and lines starting with '#' (comments/headers) are skipped.
 * CRLF line endings are tolerated. Malformed lines are skipped rather than
 * throwing, so a stray note in the file cannot break a valid download entry.
 */
export function parseChecksums(text: string): Map<string, string> {
  const checksums = new Map<string, string>();
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (line.length === 0 || line.startsWith("#")) {
      continue;
    }
    const separatorIndex = line.search(/\s/);
    if (separatorIndex <= 0) {
      continue;
    }
    const hash = line.slice(0, separatorIndex).toLowerCase();
    const filename = line.slice(separatorIndex).trim();
    if (
      hash.length !== HASH_LENGTH ||
      !HEX_RE.test(hash) ||
      filename.length === 0
    ) {
      continue;
    }
    checksums.set(filename, hash);
  }
  return checksums;
}

/**
 * Returns true when `actualSha256Hex` matches the expected hash,
 * comparing case-insensitively and requiring both values to be valid
 * 64-character hex digests.
 */
export function verifyChecksum(
  expected: string,
  actualSha256Hex: string,
): boolean {
  const expectedLower = expected.toLowerCase();
  const actualLower = actualSha256Hex.toLowerCase();
  return (
    expectedLower.length === HASH_LENGTH &&
    actualLower.length === HASH_LENGTH &&
    HEX_RE.test(expectedLower) &&
    HEX_RE.test(actualLower) &&
    expectedLower === actualLower
  );
}
