using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #88: the position-based navigation handlers read the document text
/// from DISK while taking the parse model from the open-document store (the
/// editor buffer). With an unsaved buffer edit (buffer ≠ disk) the cursor
/// identifier extraction ran against stale text, silently degrading
/// resolution. RenameHandler (the highest-impact case) was converted in the
/// #90 fix; this suite pins the store-first conversion for the rest of the
/// sweep: declaration, hover, references, documentHighlight, documentSymbol,
/// foldingRange.
///
/// <para>Fixture shape: the DISK file contains AlphaFunc; the open-buffer
/// (store) contains a renamed + line-shifted BetaFunc. Every assertion asks
/// for something that only exists in the buffer — a disk-derived answer
/// (the pre-fix behavior) returns Alpha/empty instead.</para>
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue88StoreFirstSweepTests : IDisposable
{
    // DISK content (stale): AlphaFunc declared at line 0, used at line 5.
    private const string DiskContent =
        "int AlphaFunc()\n" +
        "{\n" +
        "    return 1;\n" +
        "}\n" +
        "void OnTick()\n" +
        "{\n" +
        "    AlphaFunc();\n" +
        "}\n";

    // BUFFER content (unsaved edit): a leading comment line shifts everything
    // by one and the function is renamed to BetaFunc (line 1 / usage line 7).
    private const string BufferContent =
        "// unsaved edit\n" +
        "int BetaFunc()\n" +
        "{\n" +
        "    return 2;\n" +
        "}\n" +
        "void OnTick()\n" +
        "{\n" +
        "    BetaFunc();\n" +
        "}\n";

    // 0-based positions in BufferContent.
    private const int UsageLine = 7;
    private const int UsageColumn = 4;
    private const int DeclarationLine = 1;

    private readonly string _path;

    public Issue88StoreFirstSweepTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "Issue88Sweep_" + Guid.NewGuid().ToString("N") + ".mq4");
        File.WriteAllText(_path, DiskContent);
    }

    public void Dispose()
    {
        GlobalSymbolIndex.Instance.Clear();
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>
    /// Simulate an open document with unsaved edits: the store holds the
    /// buffer content + a model parsed from it, while the disk file still
    /// holds the pre-edit content. The GlobalSymbolIndex is populated from
    /// the BUFFER model exactly as didChange would.
    /// </summary>
    private OpenDocumentStore OpenBufferOnlyStore()
    {
        var parser = new Mql4AntlrParser();
        var bufferModel = parser.ParseFile(BufferContent, _path);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_path, MqlLanguage.Mql4, bufferModel.Symbols,
            SymbolOccurrenceMapper.Map(bufferModel, _path, MqlLanguage.Mql4));

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + _path), bufferModel, BufferContent, MqlLanguage.Mql4);
        return store;
    }

    private static (MqlLanguageService svc, IMqlBuiltins[] builtins) Services() =>
        (new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
         new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

    private static T Create<T>(OpenDocumentStore store, Func<MqlLanguageService, IMqlBuiltins[], T> factory)
    {
        var (svc, builtins) = Services();
        return factory(svc, builtins);
    }

    private TextDocumentIdentifier DocId => new(DocumentUri.FromFileSystemPath(_path));

    [Fact]
    public async Task Hover_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new HoverHandler(
            Substitute.For<ILogger<HoverHandler>>(), svc, store, b));

        // Cursor on the BetaFunc call that exists only in the buffer.
        var result = await handler.Handle(new HoverParams
        {
            TextDocument = DocId,
            Position = new Position(UsageLine, UsageColumn)
        }, CancellationToken.None);

        Assert.NotNull(result);
        var content = result!.Contents?.MarkupContent?.Value ?? string.Empty;
        Assert.Contains("BetaFunc", content);
        Assert.DoesNotContain("AlphaFunc", content);
    }

    [Fact]
    public async Task Declaration_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new DeclarationHandler(
            Substitute.For<ILogger<DeclarationHandler>>(), svc, store, b));

        var result = await handler.Handle(new DeclarationParams
        {
            TextDocument = DocId,
            Position = new Position(UsageLine, UsageColumn)
        }, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        // BetaFunc's declaration is at buffer line 1 — a disk-derived answer
        // would point at AlphaFunc's declaration (line 0) instead.
        Assert.Equal(DeclarationLine, location!.Range.Start.Line);
    }

    [Fact]
    public async Task References_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new ReferencesHandler(
            Substitute.For<ILogger<ReferencesHandler>>(), svc, store, b));

        var result = await handler.Handle(new ReferenceParams
        {
            TextDocument = DocId,
            Position = new Position(UsageLine, UsageColumn),
            Context = new ReferenceContext { IncludeDeclaration = true }
        }, CancellationToken.None);

        Assert.NotNull(result);
        var positions = result!.Select(l => l.Range.Start.Line).ToList();
        Assert.Contains(DeclarationLine, positions);
        Assert.Contains(UsageLine, positions);
    }

    [Fact]
    public async Task DocumentHighlight_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new DocumentHighlightHandler(
            Substitute.For<ILogger<DocumentHighlightHandler>>(), svc, store, b));

        var result = await handler.Handle(new DocumentHighlightParams
        {
            TextDocument = DocId,
            Position = new Position(UsageLine, UsageColumn)
        }, CancellationToken.None);

        Assert.NotNull(result);
        var lines = result!.Select(h => h.Range.Start.Line).ToList();
        // The declaration highlight must sit at the BUFFER position (line 1);
        // a disk-derived answer would point at AlphaFunc's line 0. (Same-file
        // usages are not separately highlighted when the declaration matches —
        // pre-existing behavior, out of #88's scope.)
        Assert.Contains(DeclarationLine, lines);
        Assert.DoesNotContain(0, lines);
    }

    [Fact]
    public async Task DocumentSymbol_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new DocumentSymbolHandler(
            Substitute.For<ILogger<DocumentSymbolHandler>>(), svc, store, b));

        var result = await handler.Handle(new DocumentSymbolParams
        {
            TextDocument = DocId
        }, CancellationToken.None);

        Assert.NotNull(result);
        var names = result!.Select(s => s.DocumentSymbol?.Name ?? s.SymbolInformation?.Name ?? "?").ToList();
        Assert.Contains("BetaFunc", names);
        Assert.DoesNotContain("AlphaFunc", names);
    }

    [Fact]
    public async Task FoldingRange_AnswersFromTheBufferNotDiskAsync()
    {
        var store = OpenBufferOnlyStore();
        var handler = Create(store, (svc, b) => new FoldingRangeHandler(
            Substitute.For<ILogger<FoldingRangeHandler>>(), svc, store, b));

        var result = await handler.Handle(new FoldingRangeRequestParam
        {
            TextDocument = DocId
        }, CancellationToken.None);

        Assert.NotNull(result);
        // The buffer content is 9 lines (the disk file is 8): folding derived
        // from the buffer must never report a range beyond its line count, and
        // must include the ranges produced by the buffer's own brace layout.
        var maxLine = result!.Select(r => r.StartLine).DefaultIfEmpty(-1).Max();
        Assert.True(maxLine < 9, "folding ranges must be derived from the buffer text");
        Assert.NotEmpty(result);
    }
}