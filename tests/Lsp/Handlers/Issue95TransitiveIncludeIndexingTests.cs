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
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #95: workspace/symbol answered [] for symbols in files the client
/// never opened — on the reporter's 60+ file project the symbol lives in a
/// TRANSITIVELY included header (A → mid.mqh → deep.mqh), and didOpen only
/// indexed the DIRECT includes. The header stayed unindexed until the
/// background scan happened to reach it, so a workspace/symbol query in that
/// window returned nothing (and re-opening the header fixed it — didOpen
/// indexes the file itself). The fix makes didOpen index the include chain
/// recursively: everything the document's compilation needs is indexed
/// immediately, cycle-safe and PathSecurity-guarded per hop.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue95TransitiveIncludeIndexingTests : IDisposable
{
    private readonly string _dir;
    private readonly string _mainPath;
    private readonly string _midPath;
    private readonly string _deepPath;

    public Issue95TransitiveIncludeIndexingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue95Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _mainPath = Path.Combine(_dir, "main.mq4");
        _midPath = Path.Combine(_dir, "mid.mqh");
        _deepPath = Path.Combine(_dir, "deep.mqh");

        // deep declares the symbol the reporter queries for; mid includes deep;
        // main includes mid only — deep is TWO hops from the opened document.
        File.WriteAllText(_deepPath,
            "// deep utilities\n" +
            "double DeepUtility(double v)\n" +
            "{\n" +
            "    return(v);\n" +
            "}\n");
        File.WriteAllText(_midPath, "#include \"deep.mqh\"\n");
        File.WriteAllText(_mainPath, "#include \"mid.mqh\"\n");
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

    private DidOpenTextDocumentHandler CreateOpenHandler(OpenDocumentStore store) => new(
        Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
        store,
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
        new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

    private WorkspaceSymbolHandler CreateSymbolHandler() => new(
        Substitute.For<ILogger<WorkspaceSymbolHandler>>(),
        GlobalSymbolIndex.Instance);

    private static async Task DidOpenAsync(DidOpenTextDocumentHandler handler, string path)
    {
        await handler.Handle(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = DocumentUri.FromFileSystemPath(path),
                LanguageId = "mql4",
                Version = 1,
                Text = File.ReadAllText(path)
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DidOpen_IndexesTransitiveIncludes_ImmediatelyAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var openHandler = CreateOpenHandler(store);

        // didOpen main.mq4 ONLY: pre-fix, deep.mqh (two hops away) stayed
        // unindexed until the workspace scan reached it.
        await DidOpenAsync(openHandler, _mainPath);

        var symbolHandler = CreateSymbolHandler();
        var result = await symbolHandler.Handle(new WorkspaceSymbolParams { Query = "DeepUtility" },
            CancellationToken.None);

        Assert.NotNull(result);
        var hit = result!.FirstOrDefault(s => s.Name == "DeepUtility");
        Assert.NotNull(hit);
        Assert.Equal(Path.GetFileName(_deepPath), Path.GetFileName(hit!.Location!.Location!.Uri.GetFileSystemPath()));
    }

    [Fact]
    public async Task DidOpen_RecordsTransitiveDependencyEdgesAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        await DidOpenAsync(CreateOpenHandler(store), _mainPath);

        // The include graph must carry the transitive edges: the #90 rename
        // reachability guard and the #89 type-fallback guard rely on them.
        var midEdges = GlobalSymbolIndex.Instance.GetDependencies(_mainPath);
        Assert.Contains(midEdges, d => string.Equals(d, _midPath, StringComparison.OrdinalIgnoreCase));

        var deepEdges = GlobalSymbolIndex.Instance.GetDependencies(_midPath);
        Assert.Contains(deepEdges, d => string.Equals(d, _deepPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DidOpen_SelfIncludingChain_TerminatesAsync()
    {
        // Cycle safety: a header that (transitively) includes itself must not
        // loop — the processed set terminates the recursion.
        var loopPath = Path.Combine(_dir, "loop.mqh");
        File.WriteAllText(loopPath, "#include \"loop.mqh\"\n");
        GlobalSymbolIndex.Instance.Clear();

        var store = new OpenDocumentStore();
        var mainLoop = Path.Combine(_dir, "mainloop.mq4");
        File.WriteAllText(mainLoop, "#include \"loop.mqh\"\n");

        var openHandler = CreateOpenHandler(store);

        // Must complete (no stack overflow / infinite loop).
        await DidOpenAsync(openHandler, mainLoop);

        var symbolHandler = CreateSymbolHandler();
        var result = await symbolHandler.Handle(new WorkspaceSymbolParams { Query = "whatever" },
            CancellationToken.None);
        Assert.NotNull(result);
    }
}