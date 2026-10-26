using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Scans the client's workspace folders at initialization and indexes every
/// supported source file so symbol/occurrence lookups resolve for files the
/// client never opened (WI-01, WI-03).
///
/// Concurrency (D5): each per-file AddFile upsert uses GlobalSymbolIndex's
/// existing global lock for sub-millisecond work — no snapshot swap, no
/// reader/writer lock, no batching. A references request mid-scan sees a
/// partially populated index (accepted behavior; results grow monotonically
/// until the scan completes).
///
/// Execution (D6/WI-02): StartIndexing is fire-and-forget (Task.Run with
/// internal try/catch) — it never delays the initialize response and never
/// surfaces a client-visible error (WI-05).
/// </summary>
public class WorkspaceIndexer
{
    /// <summary>
    /// Directories the scan never descends into (WI-04): VCS metadata,
    /// IDE caches, and build output. Any directory starting with '.' is also
    /// skipped by the hidden-directory rule.
    /// </summary>
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".hg", ".bzr", ".vs", ".idea", ".gitnexus", ".codegraph",
        "bin", "obj", "node_modules", "packages", "coverage", "TestResults"
    };

    /// <summary>
    /// Supported source extensions (WI assumption, confirmed in design):
    /// .mq4/.mq5 sources plus .mqh headers, which define symbols consumed by
    /// references (the FP corpus includes .mqh).
    /// </summary>
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mq4", ".mq5", ".mqh"
    };

    private readonly ILogger<WorkspaceIndexer> _logger;
    private readonly MqlLanguageService _languageService;
    private readonly OpenDocumentStore _documentStore;
    private readonly GlobalSymbolIndexAccessor _symbolIndex;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// Issue #95: optional client-facing scan-lifecycle notifier. Null by
    /// default (production wires it in Program.cs; tests may capture it).
    /// The indexer stays decoupled from the LSP facade — the caller maps
    /// (LogLevel, message) onto window/logMessage. Notifications are
    /// best-effort on both sides.
    /// </summary>
    public Action<LogLevel, string>? ScanNotifier { get; set; }

    /// <summary>
    /// Issue #160: optional client-facing scan-progress hook. Null by
    /// default (production wires it in Program.cs to a $/progress
    /// workDoneProgress observer; tests may capture it). Invoked with a
    /// percentage 0-100 computed across ALL workspace folders plus a short
    /// message, when the folder enumeration completes (0%), after each
    /// per-file index attempt (both passes) and at folder completion (100%).
    /// Best-effort on both sides, like <see cref="ScanNotifier"/>.
    /// </summary>
    public Action<int?, string>? ScanProgressNotifier { get; set; }

    private void Notify(LogLevel level, string message)
    {
        try
        {
            ScanNotifier?.Invoke(level, message);
        }
        catch
        {
            // Observability is best-effort; never break the scan (WI-05).
        }
    }

    public WorkspaceIndexer(
        ILogger<WorkspaceIndexer> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore)
        : this(logger, languageService, documentStore, new GlobalSymbolIndexAccessor())
    {
    }

    public WorkspaceIndexer(
        ILogger<WorkspaceIndexer> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        GlobalSymbolIndexAccessor symbolIndex)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _languageService = languageService ?? throw new ArgumentNullException(nameof(languageService));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _symbolIndex = symbolIndex ?? throw new ArgumentNullException(nameof(symbolIndex));
    }

    /// <summary>
    /// Start the background scan (fire-and-forget). Returns immediately; the
    /// initialize response is never delayed (WI-02).
    /// </summary>
    /// <param name="workspaceFolders">Client-declared workspace folders.</param>
    public void StartIndexing(IEnumerable<string> workspaceFolders)
    {
        StartIndexing(workspaceFolders, onScanCompleted: null);
    }

    /// <summary>
    /// StartIndexing overload with optional test hooks, used by tests to
    /// observe the scan without polling. <paramref name="onScanCompleted"/>
    /// fires in the scan's finally block; <paramref name="onFileIndexed"/>
    /// fires synchronously on the scan thread after each IndexFile attempt
    /// (success or failure), before the next file. Both are null in
    /// production (Program.cs calls the 1-arg overload), so the production
    /// path is identical to today.
    /// </summary>
    public void StartIndexing(IEnumerable<string> workspaceFolders,
        Action? onScanCompleted, Action<string>? onFileIndexed = null)
    {
        var folders = (workspaceFolders ?? Array.Empty<string>()).ToArray();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _ = Task.Run(() =>
        {
            try
            {
                for (var folderIndex = 0; folderIndex < folders.Length; folderIndex++)
                {
                    var folder = folders[folderIndex];
                    if (token.IsCancellationRequested)
                        break;

                    // Issue #95: a folder-level failure (inaccessible root,
                    // pathological tree) must not prevent the remaining folders
                    // from being scanned — WI-05 extended from files to folders.
                    try
                    {
                        ScanWorkspaceFolder(folder, folderIndex, folders.Length, token, onFileIndexed);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Workspace scan failed for folder {Folder}; continuing with the next folder.", folder);
                        Notify(LogLevel.Warning, $"Workspace scan failed for folder '{folder}'; continuing with a partial index. ({ex.GetType().Name})");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown — expected, never client-visible
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Workspace scan failed; continuing with partial index.");
            }
            finally
            {
                onScanCompleted?.Invoke();
            }
        }, token);
    }

    /// <summary>
    /// Cancel the running scan (called on shutdown).
    /// </summary>
    public void StopIndexing()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Scan a single workspace folder: enumerate, filter, parse, and index.
    /// <paramref name="folderIndex"/> and <paramref name="folderCount"/>
    /// locate the folder inside the whole scan so progress percentages cover
    /// all folders (issue #160).
    /// </summary>
    private void ScanWorkspaceFolder(string folder, int folderIndex, int folderCount, CancellationToken token,
        Action<string>? onFileIndexed)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            _logger.LogDebug("Workspace folder missing or inaccessible, skipping: {Folder}", folder);
            return;
        }

        // Issue #157: project-local exclusions. A .mqlignore at the folder
        // root is parsed once and threaded into the enumeration; each
        // workspace folder gets its own spec — no global state. An absent or
        // unreadable file behaves exactly as if the feature did not exist
        // (fail-open, WI-05 spirit). The file is read once per scan; there is
        // no watcher, so editing it requires a restart (documented).
        MqlIgnoreSpec? ignore = null;
        var ignorePath = Path.Combine(folder, ".mqlignore");
        if (File.Exists(ignorePath) &&
            MqlIgnoreSpec.TryParseFile(ignorePath, out var parsedIgnore) &&
            parsedIgnore != null)
        {
            ignore = parsedIgnore;
            _logger.LogInformation("Loaded .mqlignore from {Folder} ({PatternCount} patterns)", folder, ignore.PatternCount);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var files = ScanDirectory(folder, options, token, new HashSet<string>(StringComparer.OrdinalIgnoreCase), folder, ignore).ToList();

        _logger.LogInformation("Workspace scan started: {Folder} ({FileCount} candidate files)", folder, files.Count);
        Notify(LogLevel.Information, $"Workspace scan started: {folder} ({files.Count} candidate files)");

        // Issue #160: 0% for this folder the moment its enumeration is done.
        NotifyProgress(OverallPercentage(folderIndex, folderCount, 0),
            $"Indexing {folder}: {files.Count} candidate files");

        // Issue #16 Phase 2: a .mqh file's language is decided by who includes
        // it, not by its content. Pass A indexes only the unambiguous
        // .mq4/.mq5 sources and records their resolved includes; pass B routes
        // each .mqh by its includers' languages, falling back to content
        // sniffing only when includers disagree or none resolve.
        var includerLanguagesByMqh = new Dictionary<string, HashSet<MqlLanguage>>(StringComparer.OrdinalIgnoreCase);
        var mqhPaths = new List<string>();

        var indexed = 0;
        var processed = 0;
        foreach (var path in files)
        {
            if (token.IsCancellationRequested)
                break;

            var extension = Path.GetExtension(path);

            // Pass A: unambiguous-by-extension sources only.
            if (!extension.Equals(".mqh", StringComparison.OrdinalIgnoreCase))
            {
                // WI-05: a single file failure never aborts the scan.
                try
                {
                    IndexFile(path, token, includerLanguagesByMqh);
                    indexed++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to index workspace file, continuing: {FilePath}", path);
                }

                processed++;
                NotifyProgress(OverallPercentage(folderIndex, folderCount, processed, files.Count),
                    $"Indexing {Path.GetFileName(path)} ({processed}/{files.Count})");

                // Test seam (WI-02): per-file hook invoked on the scan thread
                // after each IndexFile attempt. Null in production; no locks,
                // D5 untouched.
                onFileIndexed?.Invoke(path);
            }
            else
            {
                // Pass B candidates are deferred until all sources are indexed.
                mqhPaths.Add(path);
            }
        }

        // Pass B: route each .mqh by its includers when the evidence is
        // unambiguous; otherwise fall back to content sniffing.
        foreach (var mqhPath in mqhPaths)
        {
            if (token.IsCancellationRequested)
                break;

            try
            {
                IndexMqhFile(mqhPath, includerLanguagesByMqh, token);
                indexed++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index workspace file, continuing: {FilePath}", mqhPath);
            }

            processed++;
            NotifyProgress(OverallPercentage(folderIndex, folderCount, processed, files.Count),
                $"Indexing {Path.GetFileName(mqhPath)} ({processed}/{files.Count})");

            onFileIndexed?.Invoke(mqhPath);
        }

        _logger.LogInformation("Workspace scan finished: {Folder} ({IndexedCount}/{FileCount} files indexed)", folder, indexed, files.Count);
        Notify(LogLevel.Information, $"Workspace scan finished: {indexed}/{files.Count} files indexed in {folder}");
        NotifyProgress(OverallPercentage(folderIndex, folderCount, 1),
            $"Workspace scan finished: {indexed}/{files.Count} files indexed in {folder}");
    }

    /// <summary>
    /// Issue #160: overall scan percentage — the completed folders count
    /// fully, the current folder contributes its own processed/total
    /// fraction. <paramref name="folderFraction"/> is 0 (starting),
    /// processed/total mid-folder, or 1 (finished).
    /// </summary>
    private static int OverallPercentage(int folderIndex, int folderCount, double folderFraction)
    {
        if (folderCount <= 0)
            return 100;
        return (int)Math.Round(100.0 * (folderIndex + Math.Clamp(folderFraction, 0.0, 1.0)) / folderCount);
    }

    private static int OverallPercentage(int folderIndex, int folderCount, int processed, int total)
        => total <= 0
            ? OverallPercentage(folderIndex, folderCount, 1)
            : OverallPercentage(folderIndex, folderCount, (double)processed / total);

    private void NotifyProgress(int? percentage, string message)
    {
        try
        {
            ScanProgressNotifier?.Invoke(percentage, message);
        }
        catch
        {
            // Observability is best-effort; never break the scan (WI-05).
        }
    }

    /// <summary>
    /// Recursively enumerate supported files, pruning excluded directories
    /// (WI-04) and hidden dot-directories.
    ///
    /// <para>Issue #95 hardening, driven by a real-project failure where a
    /// Wine prefix inside the workspace (dosdevices/z: → the filesystem
    /// root) made the recursion re-enter the project infinitely and the scan
    /// abort on UnauthorizedAccessException with an EMPTY index —
    /// indistinguishable, from the client's side, from a silent failure:</para>
    ///
    /// <list type="bullet">
    /// <item>Reparse points (symlinks, junctions, wine dosdevices links) are
    /// never followed — a symlinked directory cannot re-enter the tree.</item>
    /// <item>A cycle guard on the resolved final link target catches loops
    /// that are not plain symlinks (bind mounts, duplicated trees).</item>
    /// <item>Enumeration errors are contained per directory: an inaccessible
    /// or unreadable subtree is pruned, never aborting the whole scan
    /// (WI-05 extended from files to directories). The per-directory eager
    /// materialization is what makes the containment possible — the old
    /// lazy iterator surfaced exceptions at MoveNext() and killed the
    /// entire scan.</item>
    /// </list>
    ///
    /// <para>Issue #157: <paramref name="ignore"/> (the folder's parsed
    /// .mqlignore, matched relative to <paramref name="root"/>) is a THIRD
    /// prune source alongside the name-based rules. It is checked with the
    /// cheap name rules and before the #95 hardening below — which still runs
    /// before any descent, so .mqlignore only ADDS pruning and never
    /// re-enables it. A matched directory is pruned entirely (the ignored
    /// subtree's enumeration cost is zero, not deferred); dir-only patterns
    /// never match files and vice versa. Negation can only re-include paths
    /// matched by earlier .mqlignore patterns — never the built-in
    /// exclusions. A live file whose include resolves into an ignored
    /// directory is still indexed, and the dependency edge to the
    /// non-indexed target is still recorded (harmless: no symbols are found
    /// there).</para>
    /// </summary>
    private static IEnumerable<string> ScanDirectory(string directory, EnumerationOptions options, CancellationToken token,
        HashSet<string> visitedDirectories, string root, MqlIgnoreSpec? ignore)
    {
        string[] entries;
        try
        {
            // Per-directory eager materialization: the containment catch below
            // can only cover exceptions raised inside it, so a lazy enumerator
            // would defeat it. Directories are small; the cost is bounded by
            // the folder's breadth.
            entries = Directory.GetFileSystemEntries(directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            // Inaccessible directory: prune the subtree, never abort the
            // scan (WI-05).
            yield break;
        }

        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested)
                yield break;

            bool isDirectory;
            try
            {
                isDirectory = Directory.Exists(entry);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            if (isDirectory)
            {
                var name = Path.GetFileName(entry);
                if (ExcludedDirectoryNames.Contains(name) || name.StartsWith(".", StringComparison.Ordinal))
                    continue;

                // Issue #157: third prune source — checked with the cheap
                // name rules, before the #95 hardening (which still runs
                // before any descent; .mqlignore only adds pruning). On
                // match, do not descend: the ignored subtree costs zero.
                if (ignore != null && ignore.IsMatch(GetRelativePath(root, entry), isDirectory: true))
                    continue;

                DirectoryInfo info;
                try
                {
                    info = new DirectoryInfo(entry);

                    // Issue #95: never follow reparse points. A Wine prefix's
                    // dosdevices/z: symlink points at the filesystem root —
                    // following it re-enters the workspace (and /proc, and
                    // everything else) and previously aborted the whole scan.
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    // Cycle guard on the resolved final target: catches loops
                    // that are not a single symlink (bind mounts, duplicated
                    // trees reached through several links).
                    var finalTarget = info.ResolveLinkTarget(returnFinalTarget: true);
                    var key = (finalTarget?.FullName ?? info.FullName).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (!visitedDirectories.Add(key))
                        continue;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                foreach (var nested in ScanDirectory(entry, options, token, visitedDirectories, root, ignore))
                    yield return nested;
            }
            else if (SupportedExtensions.Contains(Path.GetExtension(entry)))
            {
                // Issue #157: dir-only patterns must not match files (and
                // file patterns never match directories).
                if (ignore != null && ignore.IsMatch(GetRelativePath(root, entry), isDirectory: false))
                    continue;

                yield return entry;
            }
        }
    }

    /// <summary>
    /// Parse and index one .mq4/.mq5 source file (pass A). Language comes from
    /// the extension via LanguageDetection; resolved .mqh includes are recorded
    /// into <paramref name="includerLanguagesByMqh"/> so pass B can route each
    /// header by its includers (issue #16 Phase 2). Open documents are
    /// skipped — the client buffer (didOpen/didChange) is authoritative and
    /// the store upsert is idempotent anyway (D6).
    /// </summary>
    private void IndexFile(string path, CancellationToken token,
        IDictionary<string, HashSet<MqlLanguage>> includerLanguagesByMqh)
    {
        var content = SourceFileReader.ReadAllText(path);
        var uri = new Uri(path);

        // OpenDocumentStore is authoritative for open buffers; never overwrite.
        if (_documentStore.TryGetValue(uri, out _))
        {
            _logger.LogDebug("Skipping open document during scan: {FilePath}", path);
            return;
        }

        var language = LanguageDetection.Detect(uri, null, content);
        var parser = _languageService.ResolveParser(language);

        // Issue #20: thread the scan's cancellation token into the parse.
        var parsedFile = parser.ParseFile(content, path, token);
        parsedFile.Language = language;

        // Record which language includes each resolved .mqh so pass B can
        // route headers without content sniffing. Resolution is bounded by
        // PathSecurity so a traversal include never expands the map.
        foreach (var include in parsedFile.Includes)
        {
            var resolved = ResolveWorkspaceInclude(path, include);
            if (resolved == null)
                continue;

            // Issue #90: record the include edge in the dependency graph too —
            // the scan sees every file, so this completes the graph beyond
            // the didOpen-populated edges (rename's cross-file reachability
            // guard, and #96 call-hierarchy navigation later).
            _symbolIndex.Index.AddDependency(path, resolved);

            if (!includerLanguagesByMqh.TryGetValue(resolved, out var languages))
            {
                languages = new HashSet<MqlLanguage>();
                includerLanguagesByMqh[resolved] = languages;
            }

            languages.Add(language);
        }

        // Index-only write: no OpenDocumentStore pollution (D6). Occurrences
        // are mapped by the shared helper (also used by didOpen/didChange).
        _symbolIndex.Index.AddFile(
            path, language, parsedFile.Symbols,
            SymbolOccurrenceMapper.Map(parsedFile, path, language));
    }

    /// <summary>
    /// Parse and index one .mqh include file (pass B). The language is the
    /// includer-voted language when every includer agrees (issue #16 Phase 2);
    /// conflicting or missing includers fall back to LanguageDetection content
    /// sniffing (Phase 1 rules). Never throws: every failure degrades to
    /// sniffing or skipping.
    /// </summary>
    private void IndexMqhFile(string path,
        IReadOnlyDictionary<string, HashSet<MqlLanguage>> includerLanguagesByMqh,
        CancellationToken token)
    {
        var content = SourceFileReader.ReadAllText(path);
        var uri = new Uri(path);

        // OpenDocumentStore is authoritative for open buffers; never overwrite.
        if (_documentStore.TryGetValue(uri, out _))
        {
            _logger.LogDebug("Skipping open document during scan: {FilePath}", path);
            return;
        }

        var language = ResolveMqhLanguage(path, includerLanguagesByMqh, content);
        var parser = _languageService.ResolveParser(language);

        // Issue #20: thread the scan's cancellation token into the parse.
        var parsedFile = parser.ParseFile(content, path, token);
        parsedFile.Language = language;

        _symbolIndex.Index.AddFile(
            path, language, parsedFile.Symbols,
            SymbolOccurrenceMapper.Map(parsedFile, path, language));
    }

    /// <summary>
    /// Issue #16 Phase 2: choose a .mqh's language from its includers. When
    /// all includers agree, that language wins. When includers conflict (both
    /// MQL4 and MQL5 include it) or none resolve (system headers included
    /// with angle brackets, include resolution failure, includer outside the
    /// workspace), fall back to LanguageDetection content sniffing (Phase 1).
    /// </summary>
    private static MqlLanguage ResolveMqhLanguage(string path,
        IReadOnlyDictionary<string, HashSet<MqlLanguage>> includerLanguagesByMqh,
        string content)
    {
        if (includerLanguagesByMqh.TryGetValue(path, out var languages) && languages.Count == 1)
        {
            return languages.First();
        }

        return LanguageDetection.Detect(new Uri(path), null, content);
    }

    /// <summary>
    /// Resolve one #include entry recorded by the parsers to an absolute file
    /// path inside the workspace. Entries are stored as the extracted path:
    /// bare (<c>lib\nested.mqh</c>) for quoted includes, or wrapped in angle
    /// brackets (<c>&lt;Controls\Dialog.mqh&gt;</c>) for system-library includes,
    /// which are intentionally not resolved because they live in the
    /// terminal's standard library outside the workspace. Returns null for
    /// angle-bracket includes, path-security rejections, and missing targets.
    /// </summary>
    private static string? ResolveWorkspaceInclude(string includingFile, string includeEntry)
    {
        // Issue #25a: delegate to the single include-resolution service. The
        // unified semantics are identical to the behavior this copy already
        // had (quoted → relative to includer dir + GetFullPath + containment
        // guard; angle-bracket → system include, not resolved) — this copy is
        // the one whose entry-shape handling was correct and became canonical.
        if (!IncludePathResolver.TryResolveContained(includingFile, includeEntry, out var resolved))
        {
            return null;
        }

        return resolved;
    }

    /// <summary>
    /// Issue #157: entry path relative to the scanned workspace-folder root,
    /// '/'-separated for .mqlignore matching (never cwd-relative).
    /// </summary>
    private static string GetRelativePath(string root, string entry)
        => Path.GetRelativePath(root, entry).Replace(Path.DirectorySeparatorChar, '/');
}