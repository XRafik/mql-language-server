using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #96: LSP 3.17 call hierarchy over the existing navigation machinery.
/// Pinned behaviors:
/// - prepareCallHierarchy resolves the symbol under the cursor — at the
///   declaration AND at a call site of an include-declared function (#92/#90).
/// - incomingCalls distinguishes CALLS from mentions (paren-position check),
///   excludes the definition's own signature (OCC-04 IsDefinition), groups
///   call positions per caller, and finds include-linked consumers.
/// - outgoingCalls lists the callees of the item's body with their call ranges.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue96CallHierarchyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _includePath;
    private readonly string _mainPath;

    public Issue96CallHierarchyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue96Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _includePath = Path.Combine(_dir, "stop_utils.mqh");
        _mainPath = Path.Combine(_dir, "main.mq4");

        File.WriteAllText(_includePath,
            "// Stop utilities\n" +
            "void StopLong(double entryPrice, double stopLossPips)\n" +
            "{\n" +
            "    double stop = entryPrice - stopLossPips * 0.0001;\n" +
            "    Print(\"stop set: \", stop);\n" +
            "}\n");

        File.WriteAllText(_mainPath,
            "#include \"stop_utils.mqh\"\n" +
            "\n" +
            "void OpenPendingOrder_Caesar()\n" +
            "{\n" +
            "    double stopLossPips = 40;\n" +
            "    StopLong(Bid, stopLossPips);\n" +
            "}\n" +
            "\n" +
            "void ManageOrder_Caesar()\n" +
            "{\n" +
            "    double stopLossPips = 50;\n" +
            "    StopLong(Bid, stopLossPips);\n" +
            "    StopLong(Ask, stopLossPips);\n" +
            "}\n");
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
            // best effort
        }
    }

    private (OpenDocumentStore store, CallHierarchyHandler handler) Create()
    {
        var parser = new Mql4AntlrParser();
        var mainFile = parser.ParseFile(File.ReadAllText(_mainPath), _mainPath);
        var includeFile = parser.ParseFile(File.ReadAllText(_includePath), _includePath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_mainPath, MqlLanguage.Mql4, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, _mainPath, MqlLanguage.Mql4));
        GlobalSymbolIndex.Instance.AddFile(_includePath, MqlLanguage.Mql4, includeFile.Symbols,
            SymbolOccurrenceMapper.Map(includeFile, _includePath, MqlLanguage.Mql4));
        GlobalSymbolIndex.Instance.AddDependency(_mainPath, _includePath);

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + _mainPath), mainFile, File.ReadAllText(_mainPath), MqlLanguage.Mql4);
        store.AddOrUpdate(new Uri("file://" + _includePath), includeFile, File.ReadAllText(_includePath), MqlLanguage.Mql4);

        var handler = new CallHierarchyHandler(
            Substitute.For<ILogger<CallHierarchyHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            store,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));
        return (store, handler);
    }

    private CallHierarchyPrepareParams Prepare(string path, int line, int character) => new()
    {
        TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
        Position = new Position(line, character)
    };

    [Fact]
    public async Task Prepare_AtDeclaration_ReturnsTheFunctionItemAsync()
    {
        var (_, handler) = Create();

        // Cursor on StopLong's declaration name (stop_utils.mqh, 0-based 1, col 5).
        var result = await handler.Handle(Prepare(_includePath, 1, 5), CancellationToken.None);

        Assert.NotNull(result);
        var item = result!.Single();
        Assert.Equal("StopLong", item.Name);
        Assert.Equal(SymbolKind.Function, item.Kind);
        Assert.Equal(_includePath, item.Uri.GetFileSystemPath());
        Assert.NotNull(item.SelectionRange);
    }

    [Fact]
    public async Task Prepare_AtIncludeDeclaredCallSite_ReturnsTheDeclaredSymbolAsync()
    {
        var (_, handler) = Create();

        // Cursor on the StopLong CALL in main.mq4 (0-based line 5, col 4):
        // the item must point at the include file's declaration (#92/#90).
        var result = await handler.Handle(Prepare(_mainPath, 5, 4), CancellationToken.None);

        Assert.NotNull(result);
        var item = result!.Single();
        Assert.Equal("StopLong", item.Name);
        Assert.Equal(_includePath, item.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task IncomingCalls_ReturnCallersWithTheirCallRangesAsync()
    {
        var (_, handler) = Create();

        var prepared = (await handler.Handle(Prepare(_includePath, 1, 5), CancellationToken.None))!.Single();

        var incoming = await ((ICallHierarchyIncomingHandler)handler).Handle(
            new CallHierarchyIncomingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(incoming);
        var calls = incoming!.ToList();
        // Two distinct callers: OpenPendingOrder_Caesar (1 call) and
        // ManageOrder_Caesar (2 call positions grouped into one entry).
        Assert.Equal(2, calls.Count);

        var caesar = calls.Single(c => c.From.Name == "OpenPendingOrder_Caesar");
        Assert.Equal(_mainPath, caesar.From.Uri.GetFileSystemPath());
        Assert.Single(caesar.FromRanges);

        var manage = calls.Single(c => c.From.Name == "ManageOrder_Caesar");
        Assert.Equal(2, manage.FromRanges.Count());
    }

    [Fact]
    public async Task IncomingCalls_ExcludesTheDefinitionSignatureAsync()
    {
        var (_, handler) = Create();

        var prepared = (await handler.Handle(Prepare(_includePath, 1, 5), CancellationToken.None))!.Single();
        var incoming = await ((ICallHierarchyIncomingHandler)handler).Handle(
            new CallHierarchyIncomingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(incoming);
        // The definition's own signature (stop_utils.mqh) must never appear as
        // a caller: only main.mq4 functions are here.
        Assert.All(incoming!, c => Assert.Equal(_mainPath, c.From.Uri.GetFileSystemPath()));
    }

    [Fact]
    public async Task OutgoingCalls_ListsCalleesWithCallRangesAsync()
    {
        var (_, handler) = Create();

        // Prepare at the ManageOrder_Caesar declaration (main.mq4, 0-based 8, col 5).
        var prepared = (await handler.Handle(Prepare(_mainPath, 8, 5), CancellationToken.None))!.Single();

        var outgoing = await ((ICallHierarchyOutgoingHandler)handler).Handle(
            new CallHierarchyOutgoingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(outgoing);
        var calls = outgoing!.ToList();
        var stopLong = calls.SingleOrDefault(c => c.To.Name == "StopLong");
        Assert.NotNull(stopLong);
        Assert.Equal(_includePath, stopLong!.To.Uri.GetFileSystemPath());
        // Two call positions grouped in one outgoing call.
        Assert.Equal(2, stopLong.FromRanges.Count());
        Assert.Single(calls);
    }

    [Fact]
    public async Task OutgoingCalls_SkipsBuiltinsAndNonCallsAsync()
    {
        var (_, handler) = Create();

        // OpenPendingOrder_Caesar calls StopLong (user) — the fixture body has
        // no builtin calls; assert no builtin entries and no variable mentions.
        var prepared = (await handler.Handle(Prepare(_mainPath, 2, 5), CancellationToken.None))!.Single();
        var outgoing = await ((ICallHierarchyOutgoingHandler)handler).Handle(
            new CallHierarchyOutgoingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(outgoing);
        var names = outgoing!.Select(c => c.To.Name).ToList();
        Assert.Contains("StopLong", names);
        Assert.DoesNotContain("stopLossPips", names);
        Assert.DoesNotContain("Print", names);
    }
}
