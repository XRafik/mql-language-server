using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #90: textDocument/rename dropped cross-file occurrences that
/// textDocument/references demonstrably returns — a rename applied by an
/// editor or agent client reported success while leaving the call sites
/// referencing the old name (silent partial rename, the most dangerous
/// failure mode for agent-driven editing).
///
/// The fix makes rename edit the SAME occurrence set references returns
/// (workspace-wide name-keyed, document-local scope filtering), grouped
/// per file into a cross-file WorkspaceEdit, with a reachability guard:
/// cross-file edits only go to files linked through the include graph
/// (either direction; falls back to the plain name-keyed set when the
/// queried document has no edges, so rename never agrees LESS with
/// references).
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue90CrossFileRenameTests : IDisposable
{
    private const string IncludeContent =
        "// Stop/take utilities\n" +
        "double StopLong(double price, int pips)\n" +
        "  {\n" +
        "   if(pips <= 0)\n" +
        "      return(0.0);\n" +
        "   return(NormalizeDouble(price - pips * Point, Digits));\n" +
        "  }\n";

    private const string MainContent =
        "//+------------------------------------------------------------------+\n" +
        "#include \"stop_utils.mqh\"\n" +
        "\n" +
        "void OpenPendingOrder_Caesar(double stopLossPips)\n" +
        "  {\n" +
        "   double sl = StopLong(Bid, stopLossPips);\n" +
        "   Print(\"caesar sl=\", sl);\n" +
        "  }\n" +
        "\n" +
        "void OpenPendingOrder_Alexander(double stopLossPips)\n" +
        "  {\n" +
        "   double sl = StopLong(Bid, stopLossPips);\n" +
        "   Print(\"alexander sl=\", sl);\n" +
        "  }\n" +
        "\n" +
        "void OpenPendingOrder_Hannibal(double stopLossPips)\n" +
        "  {\n" +
        "   double sl = StopLong(Bid, stopLossPips);\n" +
        "   Print(\"hannibal sl=\", sl);\n" +
        "  }\n";

    // 0-based line of the StopLong declaration in IncludeContent (the name token).
    private const int DefinitionLine = 1;
    private const int DefinitionColumn = 7;

    // 0-based call sites in MainContent (name token on each line).
    private static readonly (int Line, int Col)[] CallSites =
    {
        (5, 15), (11, 15), (17, 15)
    };

    private readonly string _dir;
    private readonly string _includePath;
    private readonly string _mainPath;
    private readonly string _mainUri;
    private readonly string _includeUri;

    public Issue90CrossFileRenameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue90Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _includePath = Path.Combine(_dir, "stop_utils.mqh");
        _mainPath = Path.Combine(_dir, "main.mq4");
        File.WriteAllText(_includePath, IncludeContent);
        File.WriteAllText(_mainPath, MainContent);
        _mainUri = "file://" + _mainPath;
        _includeUri = "file://" + _includePath;
    }

    public void Dispose()
    {
        GlobalSymbolIndex.Instance.Clear();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // best effort on temp files
        }
    }

    /// <summary>
    /// Simulate didOpen of both files exactly as the server does: parse,
    /// index symbols + occurrences, and record the include edge so the
    /// reachability guard sees the graph the didOpen path builds.
    /// </summary>
    private (OpenDocumentStore store, MqlFile mainFile) OpenAndIndexBothFiles()
    {
        var parser = new Mql4AntlrParser();
        var mainFile = parser.ParseFile(MainContent, _mainPath);
        var includeFile = parser.ParseFile(IncludeContent, _includePath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_mainPath, MqlLanguage.Mql4, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, _mainPath, MqlLanguage.Mql4));
        GlobalSymbolIndex.Instance.AddFile(_includePath, MqlLanguage.Mql4, includeFile.Symbols,
            SymbolOccurrenceMapper.Map(includeFile, _includePath, MqlLanguage.Mql4));
        // didOpen's include resolution records the dependency edge.
        GlobalSymbolIndex.Instance.AddDependency(_mainPath, _includePath);

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri(_mainUri), mainFile, MainContent, MqlLanguage.Mql4);
        store.AddOrUpdate(new Uri(_includeUri), includeFile, IncludeContent, MqlLanguage.Mql4);
        return (store, mainFile);
    }

    private static RenameHandler CreateHandler(OpenDocumentStore store) =>
        new(
            Substitute.For<ILogger<RenameHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);

    [Fact]
    public async Task RenameAtDefinition_IncludesCrossFileCallSitesAsync()
    {
        var (store, _) = OpenAndIndexBothFiles();
        var handler = CreateHandler(store);

        // The issue's exact scenario: rename at the StopLong DEFINITION in the
        // include file (same position references finds all 3 call sites from).
        var request = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_includePath)),
            Position = new Position(DefinitionLine, DefinitionColumn),
            NewName = "StopLongPips"
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.NotNull(result!.Changes);

        // Edits for BOTH files.
        Assert.True(result.Changes!.ContainsKey(DocumentUri.FromFileSystemPath(_includePath)),
            "the definition edit must be present in the include file");
        var mainEdits = result.Changes[DocumentUri.FromFileSystemPath(_mainPath)]?.ToList();
        Assert.NotNull(mainEdits);

        // The 3 cross-file call sites the pre-fix rename silently dropped.
        Assert.Equal(3, mainEdits!.Count);
        foreach (var (line, col) in CallSites)
        {
            Assert.Contains(mainEdits, e =>
                e.Range.Start.Line == line && e.Range.Start.Character == col);
        }
        Assert.All(mainEdits, e => Assert.Equal("StopLongPips", e.NewText));

        // The definition edit in the include file.
        var includeEdits = result.Changes[DocumentUri.FromFileSystemPath(_includePath)]?.ToList();
        Assert.Contains(includeEdits!, e =>
            e.Range.Start.Line == DefinitionLine && e.Range.Start.Character == DefinitionColumn);
    }

    [Fact]
    public async Task RenameAtCallSite_ResolvesIncludeDeclaredSymbolAsync()
    {
        // Issue #90 via #92: renaming from a USAGE in main.mq4 (cursor on a
        // StopLong call site) previously resolved no symbol (in-file lookup
        // misses the include-declared definition) and returned null.
        var (store, _) = OpenAndIndexBothFiles();
        var handler = CreateHandler(store);

        var (callLine, callCol) = CallSites[0];
        var request = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(callLine, callCol),
            NewName = "StopLongPips"
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        // Definition + all 3 call sites across the two files.
        var total = result!.Changes!.Values.Sum(edits => edits.Count());
        Assert.Equal(4, total);
    }

    [Fact]
    public async Task RenameSkips_SameNameSymbolInUnrelatedFileAsync()
    {
        // Issue #89's lesson applied to rename: a same-name symbol in a file
        // NOT linked through the include graph must not be renamed (rename is
        // destructive). With edges present (main→include), the unrelated
        // file's occurrences are filtered out.
        var (store, _) = OpenAndIndexBothFiles();

        var unrelatedPath = Path.Combine(_dir, "unrelated.mq4");
        var unrelatedContent = "void StopLong()\n{\n}\n";
        File.WriteAllText(unrelatedPath, unrelatedContent);
        var parser = new Mql4AntlrParser();
        var unrelatedFile = parser.ParseFile(unrelatedContent, unrelatedPath);
        GlobalSymbolIndex.Instance.AddFile(unrelatedPath, MqlLanguage.Mql4, unrelatedFile.Symbols,
            SymbolOccurrenceMapper.Map(unrelatedFile, unrelatedPath, MqlLanguage.Mql4));

        var handler = CreateHandler(store);

        var request = new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_mainPath)),
            Position = new Position(CallSites[0].Line, CallSites[0].Col),
            NewName = "StopLongPips"
        };

        var result = await handler.Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var unrelatedUri = DocumentUri.FromFileSystemPath(unrelatedPath);
        Assert.False(result!.Changes!.ContainsKey(unrelatedUri),
            "same-name occurrences in a file not linked through the include graph must not be renamed");
    }

    [Fact]
    public async Task RenameAndReferences_AgreeAtTheSamePositionAsync()
    {
        // The issue's core demand: "rename and references must agree".
        var (store, _) = OpenAndIndexBothFiles();

        var renameHandler = CreateHandler(store);
        var referencesHandler = new ReferencesHandler(
            Substitute.For<ILogger<ReferencesHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);

        // Both at the definition position (where the issue observed the divergence).
        var position = new Position(DefinitionLine, DefinitionColumn);
        var includeUri = DocumentUri.FromFileSystemPath(_includePath);

        var renameResult = await renameHandler.Handle(new RenameParams
        {
            TextDocument = new TextDocumentIdentifier(includeUri),
            Position = position,
            NewName = "X"
        }, CancellationToken.None);

        var referencesResult = await referencesHandler.Handle(new ReferenceParams
        {
            TextDocument = new TextDocumentIdentifier(includeUri),
            Position = position,
            Context = new ReferenceContext { IncludeDeclaration = true }
        }, CancellationToken.None);

        Assert.NotNull(renameResult);
        Assert.NotNull(referencesResult);

        var renamePositions = renameResult!.Changes!
            .SelectMany(kv => kv.Value.Select(e => (Uri: kv.Key.ToString(), e.Range.Start.Line, e.Range.Start.Character)))
            .ToHashSet();
        var referencePositions = referencesResult!
            .Select(l => (Uri: l.Uri.ToString(), l.Range.Start.Line, l.Range.Start.Character))
            .ToHashSet();

        // Same occurrence set (rename edits each position references reports).
        Assert.Equal(referencePositions.Count, renamePositions.Count);
        Assert.Superset(referencePositions, renamePositions);
    }
}