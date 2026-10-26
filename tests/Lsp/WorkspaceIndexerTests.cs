using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// WI-01..WI-05: WorkspaceIndexer startup scan. Temp-dir workspaces verify
/// extension filtering (.mq4/.mq5/.mqh with .mqh sniff), excluded
/// directories, per-file failure tolerance, open-document skip, and
/// non-blocking StartIndexing.
/// Runs inside the GlobalSymbolIndex Tests collection so the shared
/// GlobalSymbolIndex singleton is not mutated concurrently by tests in
/// other collections (same isolation model as CrossFileTests).
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class WorkspaceIndexerTests : IDisposable
{
    private readonly string _workspace;
    private readonly GlobalSymbolIndex _index = GlobalSymbolIndex.Instance;

    public WorkspaceIndexerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "workspace-indexer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        _index.Clear();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_workspace))
                Directory.Delete(_workspace, recursive: true);
        }
        catch
        {
            // best effort cleanup
        }
        finally
        {
            _index.Clear();
        }
    }

    private WorkspaceIndexer CreateIndexer(OpenDocumentStore? documentStore = null)
    {
        var languageService = new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser());
        return new WorkspaceIndexer(
            Substitute.For<ILogger<WorkspaceIndexer>>(),
            languageService,
            documentStore ?? new OpenDocumentStore());
    }

    private static SymbolOccurrence ToOccurrence(TokenOccurrence o, string path, MqlLanguage lang)
        => new()
        {
            FilePath = path,
            Language = lang,
            Text = o.Text,
            Line = o.Line,
            Column = o.Column,
            Length = o.Length
        };

    // ------------------------------------------------------------------
    // WI-01: scan indexes supported files, unopened
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_IndexesSupportedExtensions()
    {
        var mq4 = Path.Combine(_workspace, "ind4.mq4");
        File.WriteAllText(mq4, "int Alpha4 = 1;\n");
        var mq5 = Path.Combine(_workspace, "ind5.mq5");
        File.WriteAllText(mq5, "int Alpha5 = 2;\n");
        var mqh = Path.Combine(_workspace, "ind_h.mqh");
        File.WriteAllText(mqh, "int AlphaH = 3;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(mq4, indexed);
        Assert.Contains(mq5, indexed);
        Assert.Contains(mqh, indexed);
    }

    [Fact]
    public void Scan_IgnoresUnsupportedExtensions()
    {
        var txt = Path.Combine(_workspace, "notes.txt");
        File.WriteAllText(txt, "int NotIndexed = 1;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.DoesNotContain(txt, _index.GetIndexedFiles());
    }

    [Fact]
    public void SniffedMqh_IsIndexedWithResolvedLanguage()
    {
        // WI-03/.mqh sniff: MQL5 token content is detected as Mql5.
        var mqh = Path.Combine(_workspace, "mql5_sniff.mqh");
        File.WriteAllText(mqh, "union Payload { int x; };\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var symbols = _index.GetFileSymbols(mqh, MqlLanguage.Mql5);
        Assert.NotNull(symbols);
        Assert.Contains(symbols!, s => s.Name == "Payload");
    }

    // ------------------------------------------------------------------
    // WI-04: VCS/build directories skipped
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_SkipsExcludedDirectories()
    {
        foreach (var dir in new[] { ".git", "bin", "obj", "node_modules", "TestResults" })
        {
            var dirPath = Path.Combine(_workspace, dir);
            Directory.CreateDirectory(dirPath);
            File.WriteAllText(Path.Combine(dirPath, "excluded.mq5"), "int Excluded = 1;\n");
        }

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Empty(_index.GetIndexedFiles());
    }

    [Fact]
    public void Scan_SkipsHiddenDotDirectories()
    {
        var dotDir = Path.Combine(_workspace, ".hidden");
        Directory.CreateDirectory(dotDir);
        File.WriteAllText(Path.Combine(dotDir, "hidden.mq5"), "int Hidden = 1;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Empty(_index.GetIndexedFiles());
    }

    // ------------------------------------------------------------------
    // WI-05: unparseable file does not stop the scan
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_ContinuesPastUnparseableFile()
    {
        var bad = Path.Combine(_workspace, "broken.mq5");
        File.WriteAllText(bad, "int = = = }}} garbage :\n");
        var good = Path.Combine(_workspace, "good.mq5");
        File.WriteAllText(good, "int GoodSymbol = 2;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Contains(good, _index.GetIndexedFiles());
    }

    [Fact]
    public void Scan_DoesNotThrowWhenWorkspaceMissing()
    {
        var missing = Path.Combine(_workspace, "does-not-exist");
        var exception = Record.Exception(() =>
            CreateIndexer().StartIndexingAndWaitForIdle(new[] { missing }));
        Assert.Null(exception);
    }

    // ------------------------------------------------------------------
    // Issue #95: the recursion must survive symlink cycles (a Wine prefix's
    // dosdevices/z: symlink re-enters the workspace and previously aborted
    // the ENTIRE scan on UnauthorizedAccessException, leaving an empty index
    // — workspace/symbol answered [] for everything not opened) and must
    // prune inaccessible subtrees instead of dying on them.
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_SkipsReparsePointDirectories_SymlinkCycleDoesNotAbortScan()
    {
        var source = Path.Combine(_workspace, "src");
        Directory.CreateDirectory(source);
        var header = Path.Combine(source, "riesgo_utils_mqh.mqh");
        File.WriteAllText(header, "double CalculaRiesgoTicks(int id){ return id; }\n");

        // The Wine-prefix shape: dosdevices/z: is a symlink re-entering the
        // workspace root itself. Following it used to loop forever (and die
        // on /proc or other unreadable paths reached through it).
        var dosdevices = Path.Combine(_workspace, "wine-mt4", "dosdevices");
        Directory.CreateDirectory(dosdevices);
        Directory.CreateSymbolicLink(Path.Combine(dosdevices, "z:"), _workspace);

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Contains(header, _index.GetIndexedFiles());
        Assert.Contains(_index.GetAllSymbols(),
            s => s.Symbols.Any(sym => sym.Name == "CalculaRiesgoTicks"));
    }

    [Fact]
    public void Scan_ContinuesPastInaccessibleSubdirectory()
    {
        var before = Path.Combine(_workspace, "before");
        var locked = Path.Combine(_workspace, "locked");
        var after = Path.Combine(_workspace, "after");
        Directory.CreateDirectory(before);
        Directory.CreateDirectory(locked);
        Directory.CreateDirectory(after);

        File.WriteAllText(Path.Combine(before, "before.mq4"), "int BeforeSymbol = 1;\n");
        File.WriteAllText(Path.Combine(locked, "locked.mq4"), "int LockedSymbol = 2;\n");
        File.WriteAllText(Path.Combine(after, "after.mq4"), "int AfterSymbol = 3;\n");

        // Prune the locked subtree from enumeration (non-root on Linux).
        if (!OperatingSystem.IsLinux())
        {
            return; // UnixFileMode-based lock simulation is Linux-only
        }
        var originalMode = File.GetUnixFileMode(locked);
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });
        }
        finally
        {
            File.SetUnixFileMode(locked, originalMode);
        }

        var indexed = _index.GetIndexedFiles().ToList();
        Assert.Contains(Path.Combine(before, "before.mq4"), indexed);
        Assert.Contains(Path.Combine(after, "after.mq4"), indexed);
        Assert.DoesNotContain(Path.Combine(locked, "locked.mq4"), indexed);
    }

    [Fact]
    public void Scan_NotifiesLifecycleStartedAndFinished()
    {
        var header = Path.Combine(_workspace, "riesgo_utils_mqh.mqh");
        File.WriteAllText(header, "double CalculaRiesgoTicks(int id){ return id; }\n");

        var notifications = new List<(LogLevel Level, string Message)>();
        var indexer = CreateIndexer();
        indexer.ScanNotifier = (level, message) => notifications.Add((level, message));
        indexer.StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Contains(notifications, n => n.Level == LogLevel.Information && n.Message.Contains("scan started"));
        Assert.Contains(notifications, n => n.Level == LogLevel.Information && n.Message.Contains("scan finished") && n.Message.Contains("1/1 files indexed"));
    }

    // ------------------------------------------------------------------
    // WI-05: open documents skipped (client buffer is authoritative)
    // ------------------------------------------------------------------

    [Fact]
    public void Scan_SkipsOpenDocuments()
    {
        var path = Path.Combine(_workspace, "open.mq5");
        var content = "int OpenDocSymbol = 1;\n";
        File.WriteAllText(path, content);

        var documentStore = new OpenDocumentStore();
        var uri = new Uri(path);
        var parsed = new Mql5AntlrParser().ParseFile(content, path);
        documentStore.AddOrUpdate(uri, parsed, content, MqlLanguage.Mql5);

        CreateIndexer(documentStore).StartIndexingAndWaitForIdle(new[] { _workspace });

        // Not indexed by the scan (would be indexed by didOpen in real use).
        Assert.DoesNotContain(path, _index.GetIndexedFiles());
    }

    // ------------------------------------------------------------------
    // WI-02: non-blocking start (fire-and-forget)
    // ------------------------------------------------------------------

    [Fact]
    public void StartIndexing_ReturnsBeforeScanCompletes()
    {
        for (var i = 0; i < 5; i++)
        {
            File.WriteAllText(Path.Combine(_workspace, $"f{i}.mq5"), $"int S{i} = {i};\n");
        }

        // StartIndexing returns without waiting for the scan (WI-02).
        CreateIndexer().StartIndexing(new[] { _workspace });
    }

    // ------------------------------------------------------------------
    // WI-02: requests answered during scan (mid-scan window)
    // ------------------------------------------------------------------

    /// <summary>
    /// WI-02 "Requests answered during scan": a references request issued
    /// while the startup scan is still running must be answered (possibly
    /// with partial results) from GlobalSymbolIndex, and the results must
    /// grow monotonically until the scan completes.
    ///
    /// Deterministic handshake (no sleeps): the onFileIndexed callback parks
    /// the scan thread right after file1 is indexed. Being parked inside the
    /// per-file loop proves the scan is mid-flight (onScanCompleted cannot
    /// have fired). While parked, the test issues a references request
    /// through ReferencesHandler.Handle (the production path), asserts the
    /// request is answered, then releases the scan and awaits completion.
    /// Every wait is timeout-bounded (30s, matching StartIndexingAndWaitForIdle).
    /// </summary>
    [Fact]
    public async Task MidScan_ReferencesRequestAnswered()
    {
        // Fixtures: file1 declares Alpha; file2 uses Alpha and declares Beta;
        // file3 is filler. Enumeration order is unspecified, but the
        // parked-in-callback invariant holds for any order: parked implies
        // the scan is provably incomplete.
        var file1 = Path.Combine(_workspace, "mid1.mq4");
        File.WriteAllText(file1, "int Alpha = 1;\n");
        var file2 = Path.Combine(_workspace, "mid2.mq4");
        File.WriteAllText(file2, "int AlphaUse = Alpha;\nint Beta = 2;\n");
        var file3 = Path.Combine(_workspace, "mid3.mq4");
        File.WriteAllText(file3, "int Filler = 3;\n");

        var midSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new SemaphoreSlim(0, 1);
        var scanCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Production path: references go through ReferencesHandler.Handle.
        var references = new ReferencesHandler(
            Substitute.For<ILogger<ReferencesHandler>>(),
            new Mql4AntlrParser(),
            new OpenDocumentStore(),
            _index);

        var indexer = CreateIndexer();
        indexer.StartIndexing(
            new[] { _workspace },
            () => scanCompleted.TrySetResult(),
            path =>
            {
                if (Path.GetFileName(path) == "mid1.mq4")
                {
                    midSignal.TrySetResult();
                    // Park the scan thread mid-scan; the test resumes it.
                    releaseScan.Wait(TimeSpan.FromSeconds(30));
                }
            });

        var parked = await Task.WhenAny(midSignal.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(parked == midSignal.Task, "Scan never reached file1 (onFileIndexed did not fire).");

        // Position on "Alpha" in the declaration (line 0, 0-based col 4).
        var request = new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(file1)),
            Position = new Position(0, 4),
            Context = new ReferenceContext { IncludeDeclaration = true }
        };
        var response = await references.Handle(request, CancellationToken.None);

        // The request must be answered mid-scan: file1's declaration of Alpha.
        var locations = response?.ToList() ?? new List<Location>();
        Assert.True(locations.Count >= 1, "References request was not answered during the scan.");
        Assert.Contains(locations, l =>
            l.Range.Start.Line == 0 && l.Range.Start.Character == 4);

        var midScanCount = _index.FindOccurrences("Alpha").Count;
        Assert.True(midScanCount >= 1, "Mid-scan occurrence lookup returned no Alpha occurrences.");

        releaseScan.Release();
        var completed = await Task.WhenAny(scanCompleted.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(completed == scanCompleted.Task, "Scan did not complete after the gate was released.");

        // Monotonic growth: final results never shrink below the mid-scan count.
        var finalCount = _index.FindOccurrences("Alpha").Count;
        Assert.True(finalCount >= midScanCount, "Occurrence results shrank after scan completion.");
    }

    // ------------------------------------------------------------------
    // Issue #157: project-local .mqlignore — exclude paths from the scan.
    // The ignore is a third prune source: a matched directory is pruned
    // entirely (zero enumeration cost), dir-only patterns never match
    // files and vice versa, and the last matching pattern wins. The file
    // is read once per scan (no watcher). The ignore governs the workspace
    // index ONLY — didOpen on an ignored file still gets full document-level
    // analysis (pinned by MqlIgnore_IgnoredFileDidOpen_StillAnalyzed).
    // ------------------------------------------------------------------

    private void WriteIgnoreFile(string content, bool withBom = false)
    {
        var path = Path.Combine(_workspace, ".mqlignore");
        File.WriteAllText(path, content, withBom
            ? new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)
            : new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [Fact]
    public void MqlIgnore_BareName_ExcludesWholeSubtree()
    {
        WriteIgnoreFile("backup-utf8\n");
        var ignored = Path.Combine(_workspace, "backup-utf8");
        Directory.CreateDirectory(ignored);
        File.WriteAllText(Path.Combine(ignored, "old.mq4"), "int OldSymbol = 1;\n");
        var live = Path.Combine(_workspace, "live.mq4");
        File.WriteAllText(live, "int LiveSymbol = 2;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(live, indexed);
        Assert.DoesNotContain(Path.Combine(ignored, "old.mq4"), indexed);
        Assert.DoesNotContain(_index.GetAllSymbols(), s => s.Symbols.Any(sym => sym.Name == "OldSymbol"));
    }

    [Fact]
    public void MqlIgnore_TrailingSlash_IsDirectoryOnly()
    {
        // "backup-utf8/" excludes the directory but must not exclude a
        // same-named FILE.
        WriteIgnoreFile("backup-utf8/\n");
        var dir = Path.Combine(_workspace, "backup-utf8");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "old.mq5"), "int OldSymbol = 1;\n");
        var file = Path.Combine(_workspace, "backup-utf8.mq5");
        File.WriteAllText(file, "int SameNamedFile = 2;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(file, indexed);
        Assert.DoesNotContain(Path.Combine(dir, "old.mq5"), indexed);
    }

    [Fact]
    public void MqlIgnore_LeadingSlash_AnchorsAtRoot_UnanchoredMatchesAnyDepth()
    {
        WriteIgnoreFile("/src/gen.mq4\nnotes.mq4\n");

        var anchored = Path.Combine(_workspace, "src", "gen.mq4");
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(anchored, "int AnchoredGen = 1;\n");

        // Same leaf name deeper down: the anchored pattern must NOT match it.
        var deeper = Path.Combine(_workspace, "other", "src", "gen.mq4");
        Directory.CreateDirectory(Path.Combine(_workspace, "other", "src"));
        File.WriteAllText(deeper, "int DeeperGen = 2;\n");

        // Unanchored: matches at any depth.
        var notesRoot = Path.Combine(_workspace, "notes.mq4");
        var notesNested = Path.Combine(_workspace, "deep", "notes.mq4");
        Directory.CreateDirectory(Path.Combine(_workspace, "deep"));
        File.WriteAllText(notesRoot, "int NotesRoot = 3;\n");
        File.WriteAllText(notesNested, "int NotesNested = 4;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(deeper, indexed);
        Assert.DoesNotContain(anchored, indexed);
        Assert.DoesNotContain(notesRoot, indexed);
        Assert.DoesNotContain(notesNested, indexed);
    }

    [Fact]
    public void MqlIgnore_DoubleStar_Patterns()
    {
        // Trailing '/**' matches everything INSIDE logs (the directory
        // itself is not the match — every file below is).
        // '**/generated/**' excludes a 'generated' directory at ANY depth.
        WriteIgnoreFile("logs/**\n**/generated/**\n");

        var logsA = Path.Combine(_workspace, "logs", "a");
        Directory.CreateDirectory(logsA);
        var inLogs = Path.Combine(logsA, "x.mq4");
        File.WriteAllText(inLogs, "int InLogs = 1;\n");

        var inGenerated = Path.Combine(_workspace, "src", "generated", "g.mq4");
        Directory.CreateDirectory(Path.Combine(_workspace, "src", "generated"));
        File.WriteAllText(inGenerated, "int InGenerated = 2;\n");

        var live = Path.Combine(_workspace, "live.mq5");
        File.WriteAllText(live, "int LiveSymbol = 3;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(live, indexed);
        Assert.DoesNotContain(inLogs, indexed);
        Assert.DoesNotContain(inGenerated, indexed);
    }

    [Fact]
    public void MqlIgnore_Negation_Reincludes_LastMatchWins()
    {
        // The directory is NOT excluded (the pattern targets its files), so
        // each file is checked individually and the negation can re-include.
        WriteIgnoreFile("backups/*.mq4\n!backups/keep.mq4\n");
        var dir = Path.Combine(_workspace, "backups");
        Directory.CreateDirectory(dir);
        var dropped = Path.Combine(dir, "old.mq4");
        var kept = Path.Combine(dir, "keep.mq4");
        File.WriteAllText(dropped, "int OldSymbol = 1;\n");
        File.WriteAllText(kept, "int KeepSymbol = 2;\n");

        // Last-match-wins across the whole file: keep.mq4 excluded by the
        // first pattern, re-included by the last.
        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(kept, indexed);
        Assert.DoesNotContain(dropped, indexed);
    }

    [Fact]
    public void MqlIgnore_Negation_CannotResurrectBuiltinExclusions()
    {
        // Pin the v1 decision: '!' re-includes only paths matched by earlier
        // .mqlignore patterns — never ExcludedDirectoryNames or dot-dirs.
        WriteIgnoreFile("!.git/\n!.hidden/\n!bin/\n");
        foreach (var dir in new[] { ".git", ".hidden", "bin" })
        {
            Directory.CreateDirectory(Path.Combine(_workspace, dir));
            File.WriteAllText(Path.Combine(_workspace, dir, "x.mq5"), $"int Sym_{dir.Replace(".", "_")} = 1;\n");
        }

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        Assert.Empty(_index.GetIndexedFiles());
    }

    [Fact]
    public void MqlIgnore_FilePattern_ExcludesSingleFile()
    {
        WriteIgnoreFile("secret.mq4\n");
        var secret = Path.Combine(_workspace, "secret.mq4");
        File.WriteAllText(secret, "int SecretSymbol = 1;\n");
        var live = Path.Combine(_workspace, "live.mq4");
        File.WriteAllText(live, "int LiveSymbol = 2;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(live, indexed);
        Assert.DoesNotContain(secret, indexed);
    }

    [Fact]
    public void MqlIgnore_Comments_BlankLines_Crlf_Bom()
    {
        // Windows-authored file: BOM + CRLF + comments + blank lines.
        WriteIgnoreFile("\uFEFF# legacy copies (git-tracked, must not be indexed)\r\n\r\nbackup-utf16/\r\n   \r\n# trailing comment block\r\nold-stuff\r\n", withBom: true);

        var backup = Path.Combine(_workspace, "backup-utf16");
        Directory.CreateDirectory(backup);
        var legacy = Path.Combine(backup, "legacy.mq4");
        File.WriteAllText(legacy, "int LegacySymbol = 1;\n");
        var oldStuff = Path.Combine(_workspace, "old-stuff");
        Directory.CreateDirectory(oldStuff);
        var old = Path.Combine(oldStuff, "old.mq4");
        File.WriteAllText(old, "int OldSymbol = 2;\n");
        var live = Path.Combine(_workspace, "live.mq5");
        File.WriteAllText(live, "int LiveSymbol = 3;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(live, indexed);
        Assert.DoesNotContain(legacy, indexed);
        Assert.DoesNotContain(old, indexed);
    }

    [Fact]
    public void MqlIgnore_MissingFile_BehaviorUnchanged()
    {
        // Control: no .mqlignore — byte-for-byte today's behavior.
        var a = Path.Combine(_workspace, "a.mq4");
        var b = Path.Combine(_workspace, "sub", "b.mq5");
        Directory.CreateDirectory(Path.Combine(_workspace, "sub"));
        File.WriteAllText(a, "int ASymbol = 1;\n");
        File.WriteAllText(b, "int BSymbol = 2;\n");

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });

        var indexed = _index.GetIndexedFiles();
        Assert.Contains(a, indexed);
        Assert.Contains(b, indexed);
    }

    [Fact]
    public void MqlIgnore_NotifyCounts_ReflectPrunedCandidates()
    {
        WriteIgnoreFile("wine-mt4/\n");
        var wine = Path.Combine(_workspace, "wine-mt4");
        Directory.CreateDirectory(wine);
        File.WriteAllText(Path.Combine(wine, "terminal.mq4"), "int Terminal = 1;\n");
        var live = Path.Combine(_workspace, "live.mq4");
        File.WriteAllText(live, "int LiveSymbol = 2;\n");

        var notifications = new List<(LogLevel Level, string Message)>();
        var indexer = CreateIndexer();
        indexer.ScanNotifier = (level, message) => notifications.Add((level, message));
        indexer.StartIndexingAndWaitForIdle(new[] { _workspace });

        // Only the live file is a scan candidate: the ignored subtree's cost
        // is zero, not deferred.
        Assert.Contains(notifications,
            n => n.Level == LogLevel.Information && n.Message.Contains("scan started") && n.Message.Contains("(1 candidate files)"));
        Assert.Contains(notifications,
            n => n.Level == LogLevel.Information && n.Message.Contains("scan finished") && n.Message.Contains("1/1 files indexed"));
    }

    [Fact]
    public async Task MqlIgnore_IgnoredFileDidOpen_StillAnalyzed()
    {
        // Contract (issue #157, point 4): the ignore governs the workspace
        // INDEX, never the document pipeline. didOpen on an ignored file
        // still yields full document-level diagnostics.
        WriteIgnoreFile("legacy/\n");
        var ignored = Path.Combine(_workspace, "legacy");
        Directory.CreateDirectory(ignored);
        var path = Path.Combine(ignored, "header.mq5");
        var content = "int Declared = UndefinedSymbol;\n";
        File.WriteAllText(path, content);

        CreateIndexer().StartIndexingAndWaitForIdle(new[] { _workspace });
        Assert.DoesNotContain(path, _index.GetIndexedFiles());

        // didOpen-equivalent state: the open buffer is authoritative.
        var documentStore = new OpenDocumentStore();
        var parsed = new Mql5AntlrParser().ParseFile(content, path);
        documentStore.AddOrUpdate(new Uri(path), parsed, content, MqlLanguage.Mql5);

        var handler = new DiagnosticHandler(
            Substitute.For<ILogger<DiagnosticHandler>>(),
            Substitute.For<IServiceProvider>(),
            documentStore);
        var report = await handler.Handle(
            new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)) },
            CancellationToken.None);

        Assert.NotNull(report);
        var full = Assert.IsType<RelatedFullDocumentDiagnosticReport>(report);
        Assert.Equal(DocumentDiagnosticReportKind.Full, full.Kind);
        // Full analysis ran: the reference to UndefinedSymbol is reported
        // even though the file is excluded from the workspace index.
        Assert.Contains(full.Items, d => d.Message.Contains("UndefinedSymbol"));
    }
}

/// <summary>
/// Test helper: wraps StartIndexing and blocks until the background scan
/// finishes (deterministic verification without sleeps). Uses the CTS the
/// indexer exposes for shutdown; the wait is bounded by a timeout so a
/// defect cannot hang the test run forever.
/// </summary>
public static class WorkspaceIndexerTestExtensions
{
    public static void StartIndexingAndWaitForIdle(this WorkspaceIndexer indexer, IEnumerable<string> folders)
    {
        using var completed = new ManualResetEventSlim(false);
        indexer.StartIndexing(folders, () => completed.Set());
        if (!completed.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("WorkspaceIndexer scan did not complete within 30s.");
        }
    }
}