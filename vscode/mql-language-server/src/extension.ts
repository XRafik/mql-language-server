/**
 * Thin VS Code glue: resolves the mql-lsp-server binary (explicit setting,
 * cached download, or fresh download) and starts the language client.
 * All real logic lives in platform.ts / checksum.ts / downloader.ts.
 */

import * as vscode from "vscode";
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  Trace,
} from "vscode-languageclient/node";

import {
  DownloadError,
  defaultDeps,
  downloadAndInstall,
  resolveCachedBinary,
} from "./downloader";
import { assetForPlatform } from "./platform";

let client: LanguageClient | undefined;

const DOWNLOAD_PROGRESS_TITLE = "Downloading MQL language server";

function logLevelToTrace(logLevel: string): Trace {
  switch (logLevel) {
    case "debug":
      return Trace.Verbose;
    case "info":
      return Trace.Messages;
    default:
      return Trace.Off;
  }
}

async function resolveBinaryPath(
  context: vscode.ExtensionContext,
): Promise<string> {
  const config = vscode.workspace.getConfiguration("mqlLanguageServer");
  const serverPath = config.get<string>("serverPath")?.trim();
  if (serverPath) {
    return serverPath;
  }

  const platform = `${process.platform}-${process.arch}`;
  const deps = defaultDeps(platform);
  const storageDir = context.globalStorageUri.fsPath;

  const cached = await resolveCachedBinary(storageDir, platform, deps);
  if (cached) {
    return cached;
  }

  if (!assetForPlatform(platform)) {
    throw new DownloadError(
      `MQL language server: unsupported platform "${platform}". The automatic download ` +
        `only ships binaries for win32 (x64/arm64), linux (x64/arm64) and darwin (x64/arm64). ` +
        `To continue, build mql-lsp-server yourself and set "mqlLanguageServer.serverPath" to the binary path.`,
    );
  }

  await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: DOWNLOAD_PROGRESS_TITLE },
    () => downloadAndInstall(storageDir, deps),
  );

  const installed = await resolveCachedBinary(storageDir, platform, deps);
  if (!installed) {
    throw new DownloadError(
      "MQL language server was downloaded but could not be found on disk. Reload the window to retry.",
    );
  }
  return installed;
}

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  const config = vscode.workspace.getConfiguration("mqlLanguageServer");
  const logLevel = config.get<string>("logLevel") ?? "info";

  let binaryPath: string;
  try {
    binaryPath = await resolveBinaryPath(context);
  } catch (error) {
    const message =
      error instanceof Error ? error.message : String(error);
    void vscode.window.showErrorMessage(
      `MQL language server could not be started: ${message}`,
    );
    throw error;
  }

  const serverOptions: ServerOptions = {
    command: binaryPath,
    args: ["--stdio"],
  };

  const clientOptions: LanguageClientOptions = {
    documentSelector: [
      { language: "mql4" },
      { language: "mql5" },
    ],
    outputChannel:
      logLevel === "off" ? undefined : vscode.window.createOutputChannel("MQL Language Server"),
  };

  client = new LanguageClient("mqlLanguageServer", "MQL Language Server", serverOptions, clientOptions);
  context.subscriptions.push(client);
  if (logLevel !== "off") {
    await client.setTrace(logLevelToTrace(logLevel));
  }
  await client.start();
}

export function deactivate(): Thenable<void> | undefined {
  if (!client) {
    return undefined;
  }
  const stopping = client.stop();
  client = undefined;
  return stopping;
}
