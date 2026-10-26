/**
 * Downloads and installs the mql-lsp-server release binary for the current
 * platform, verifying its SHA256 against the published CHECKSUMS.txt.
 *
 * All side effects (network access, filesystem, hashing) are injected through
 * {@link DownloaderDeps} so the orchestration logic can be exercised without
 * the VS Code API. `defaultDeps` wires the Node.js implementation.
 *
 * This module must stay free of VS Code API imports.
 */

import { createHash } from "node:crypto";
import { chmod as fsChmod, access, mkdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";

import { parseChecksums, verifyChecksum } from "./checksum";
import { assetForPlatform } from "./platform";

export const GITHUB_RELEASE_BASE_URL =
  "https://github.com/davalillo/mql-language-server/releases/latest/download";
export const CHECKSUMS_URL = `${GITHUB_RELEASE_BASE_URL}/CHECKSUMS.txt`;

/** Metadata recorded next to an installed binary so later activations can reuse it. */
export interface InstallMetadata {
  asset: string;
  sha256: string;
}

/** Result of a successful download-and-install run. */
export interface InstallResult extends InstallMetadata {
  binaryPath: string;
}

/** Typed error carrying an actionable message for download/install failures. */
export class DownloadError extends Error {
  constructor(message: string, readonly cause?: unknown) {
    super(message);
    this.name = "DownloadError";
  }
}

/** Side-effectful collaborators used by {@link downloadAndInstall}. */
export interface DownloaderDeps {
  /** Platform id in `<os>-<arch>` form, e.g. `linux-x64`. */
  platform: string;
  fetchText: (url: string) => Promise<string>;
  fetchBinary: (url: string) => Promise<Uint8Array>;
  sha256Hex: (data: Uint8Array) => string;
  ensureDir: (dir: string) => Promise<void>;
  writeFile: (path: string, data: Uint8Array) => Promise<void>;
  chmod: (path: string, mode: number) => Promise<void>;
  fileExists: (path: string) => Promise<boolean>;
  readTextFile: (path: string) => Promise<string>;
}

export function sha256Hex(data: Uint8Array): string {
  return createHash("sha256").update(data).digest("hex");
}

export function defaultDeps(platform: string): DownloaderDeps {
  return {
    platform,
    fetchText: async (url) => {
      const response = await fetch(url);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status} for ${url}`);
      }
      return response.text();
    },
    fetchBinary: async (url) => {
      const response = await fetch(url);
      if (!response.ok) {
        throw new Error(`HTTP ${response.status} for ${url}`);
      }
      return new Uint8Array(await response.arrayBuffer());
    },
    sha256Hex,
    ensureDir: (dir) => mkdir(dir, { recursive: true }).then(() => undefined),
    writeFile: (path, data) => writeFile(path, data),
    chmod: (path, mode) => fsChmod(path, mode),
    fileExists: async (path) => {
      try {
        await access(path);
        return true;
      } catch {
        return false;
      }
    },
    readTextFile: (path) => readFile(path, "utf8"),
  };
}

function releaseAssetUrl(asset: string): string {
  return `${GITHUB_RELEASE_BASE_URL}/${asset}`;
}

/**
 * Resolves a previously installed binary in `targetDir`. Returns the binary
 * path only when cached metadata exists, the recorded asset matches the
 * current platform, and the binary file is still present.
 */
export async function resolveCachedBinary(
  targetDir: string,
  platform: string,
  deps: DownloaderDeps,
): Promise<string | null> {
  const expectedAsset = assetForPlatform(platform);
  if (!expectedAsset) {
    return null;
  }
  let metadata: InstallMetadata;
  try {
    metadata = JSON.parse(
      await deps.readTextFile(join(targetDir, "metadata.json")),
    ) as InstallMetadata;
  } catch {
    return null;
  }
  if (metadata.asset !== expectedAsset) {
    return null;
  }
  const binaryPath = join(targetDir, expectedAsset);
  return (await deps.fileExists(binaryPath)) ? binaryPath : null;
}

/**
 * Downloads the server binary for the current platform, verifies its SHA256
 * against the published CHECKSUMS.txt, installs it into `targetDir` (chmod
 * 0o755 on non-Windows), and records install metadata for future activations.
 */
export async function downloadAndInstall(
  targetDir: string,
  deps: DownloaderDeps,
): Promise<InstallResult> {
  const asset = assetForPlatform(deps.platform);
  if (!asset) {
    throw new DownloadError(
      `Unsupported platform "${deps.platform}". The mql-language-server release pipeline publishes ` +
        `binaries for win32-x64, win32-arm64, linux-x64, linux-arm64, darwin-x64 and darwin-arm64. ` +
        `To continue, build mql-lsp-server yourself and point the "mqlLanguageServer.serverPath" ` +
        `setting at the binary.`,
    );
  }

  let checksumsText: string;
  try {
    checksumsText = await deps.fetchText(CHECKSUMS_URL);
  } catch (error) {
    throw new DownloadError(
      `Network failure while downloading the release checksums (${CHECKSUMS_URL}). ` +
        `Check your internet connection or proxy settings, then reload the window to retry. ` +
        `Alternatively, set "mqlLanguageServer.serverPath" to a manually installed binary.`,
      error instanceof Error ? error : undefined,
    );
  }
  const checksums = parseChecksums(checksumsText);
  const expected = checksums.get(asset);
  if (!expected) {
    throw new DownloadError(
      `The published CHECKSUMS.txt does not contain an entry for "${asset}". ` +
        `The latest release may be incomplete; retry later or set "mqlLanguageServer.serverPath" ` +
        `to a manually installed binary.`,
    );
  }

  let data: Uint8Array;
  try {
    data = await deps.fetchBinary(releaseAssetUrl(asset));
  } catch (error) {
    throw new DownloadError(
      `Network failure while downloading "${asset}" (${releaseAssetUrl(asset)}). ` +
        `Check your internet connection or proxy settings, then reload the window to retry. ` +
        `Alternatively, set "mqlLanguageServer.serverPath" to a manually installed binary.`,
      error instanceof Error ? error : undefined,
    );
  }
  const actual = deps.sha256Hex(data);
  if (!verifyChecksum(expected, actual)) {
    throw new DownloadError(
      `Checksum mismatch for "${asset}": expected sha256 ${expected}, actual ${actual}. ` +
        `The download may be corrupted or tampered with; reload the window to retry, ` +
        `or set "mqlLanguageServer.serverPath" to a manually installed binary.`,
    );
  }

  await deps.ensureDir(targetDir);
  const binaryPath = join(targetDir, asset);
  await deps.writeFile(binaryPath, data);
  if (!deps.platform.startsWith("win32")) {
    await deps.chmod(binaryPath, 0o755);
  }
  const metadataPath = join(targetDir, "metadata.json");
  const metadata: InstallMetadata = { asset, sha256: expected };
  await deps.writeFile(
    metadataPath,
    new TextEncoder().encode(JSON.stringify(metadata, null, 2)),
  );

  return { ...metadata, binaryPath };
}
