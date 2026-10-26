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
/// Issue #116 (defense layer): the didOpen parse-reuse path (issue #36) used
/// to return before touching GlobalSymbolIndex. When the reusable model came
/// from an OpenDocumentStore entry that was never indexed — a sibling request
/// handler (completion, diagnostics, semantic tokens, ...) that parses and
/// stores without indexing, or a legacy pre-P2-2 request-path parse — the
/// workspace scan then skips the document as "open" and the file stays
/// permanently absent from the index. The reuse path now heals that hole.
///
/// A didClose is NOT such a source: the close handler only removes the store
/// entry and never calls GlobalSymbolIndex.RemoveFile, so the index survives
/// the close intact. The healing therefore skips the redundant AddFile when
/// the (file, language) key is already populated.
///
/// Runs inside the shared GlobalSymbolIndex Tests collection: the fixture and
/// Dispose use GlobalSymbolIndex.Instance.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue116ReusePathIndexHealingTests : IDisposable
{
    private const string RootContent =
        "void ReuseRootSymbol()\n" +
        "{\n" +
        "}\n";

    private const string ChainContent =
        "#include \"reuse-helper.mqh\"\n" +
        "void ReuseChainSymbol()\n" +
        "{\n" +
        "}\n";

    private const string HelperContent =
        "int ReuseHelperSymbol()\n" +
        "{\n" +
        "    return 1;\n" +
        "}\n";

    private readonly string _dir;
    private readonly string _rootPath;
    private readonly string _chainPath;
    private readonly string _helperPath;

    public Issue116ReusePathIndexHealingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue116Reuse_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _rootPath = Path.Combine(_dir, "reuse-root.mq4");
        _chainPath = Path.Combine(_dir, "reuse-chain.mq4");
        _helperPath = Path.Combine(_dir, "reuse-helper.mqh");

        File.WriteAllText(_rootPath, RootContent);
        File.WriteAllText(_chainPath, ChainContent);
        File.WriteAllText(_helperPath, HelperContent);
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

    private static DidOpenTextDocumentHandler CreateOpenHandler(OpenDocumentStore store) => new(
        Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
        store,
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
        new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

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

    /// <summary>
    /// Reproduce the unindexed-store-entry scenario: a sibling request handler
    /// parses and stores the model, but never calls GlobalSymbolIndex.AddFile.
    /// </summary>
    private static MqlFile StoreUnindexed(OpenDocumentStore store, string path, string content)
    {
        var model = new Mql4AntlrParser().ParseFile(content, path);
        model.Language = MqlLanguage.Mql4;
        store.AddOrUpdate(DocumentUri.FromFileSystemPath(path).ToUri(), model, content, MqlLanguage.Mql4);
        return model;
    }

    [Fact]
    public async Task DidOpen_ReusedNeverIndexedStoreEntry_HealsRootSymbolIndexAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var model = StoreUnindexed(store, _rootPath, RootContent);

        // Precondition: the store holds the parse, the index does not.
        Assert.Null(GlobalSymbolIndex.Instance.GetFileSymbols(_rootPath, MqlLanguage.Mql4));

        await DidOpenAsync(CreateOpenHandler(store), _rootPath);

        // The parse was reused (same MqlFile instance; no fresh parse).
        Assert.True(store.TryGetValue(DocumentUri.FromFileSystemPath(_rootPath).ToUri(),
            out var stored, out var storedContent, out _));
        Assert.True(ReferenceEquals(model, stored));
        Assert.Equal(RootContent, storedContent);

        // The index was healed: symbols and occurrences are now present.
        var indexed = GlobalSymbolIndex.Instance.GetFileSymbols(_rootPath, MqlLanguage.Mql4);
        Assert.NotNull(indexed);
        Assert.Contains(indexed!, s => s.Name == "ReuseRootSymbol");
        Assert.Contains(
            GlobalSymbolIndex.Instance.FindOccurrences("ReuseRootSymbol", MqlLanguage.Mql4),
            o => o.FilePath == _rootPath);
    }

    [Fact]
    public async Task DidOpen_ReusedNeverIndexedStoreEntry_HealsIncludeChainAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var content = File.ReadAllText(_chainPath);
        StoreUnindexed(store, _chainPath, content);

        // Precondition: neither the root nor its included header is indexed.
        Assert.Null(GlobalSymbolIndex.Instance.GetFileSymbols(_chainPath, MqlLanguage.Mql4));
        Assert.Null(GlobalSymbolIndex.Instance.GetFileSymbols(_helperPath, MqlLanguage.Mql4));

        await DidOpenAsync(CreateOpenHandler(store), _chainPath);

        // The sibling hole is closed too: the include chain is indexed under
        // the root's language key, and the dependency edge is recorded.
        var helperSymbols = GlobalSymbolIndex.Instance.GetFileSymbols(_helperPath, MqlLanguage.Mql4);
        Assert.NotNull(helperSymbols);
        Assert.Contains(helperSymbols!, s => s.Name == "ReuseHelperSymbol");
        Assert.Contains(GlobalSymbolIndex.Instance.GetDependencies(_chainPath),
            d => string.Equals(d, _helperPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DidOpen_HealedReuseEntry_DoesNotAliasStoreModelSymbolListAsync()
    {
        GlobalSymbolIndex.Instance.Clear();
        var store = new OpenDocumentStore();
        var model = StoreUnindexed(store, _rootPath, RootContent);

        await DidOpenAsync(CreateOpenHandler(store), _rootPath);

        // The healed index must hold its own list, never the store model's own
        // list (issue #36 aliasing hazard)...
        var indexList = GlobalSymbolIndex.Instance.GetAllSymbols()
            .First(t => t.Language == MqlLanguage.Mql4 && t.Uri.LocalPath == _rootPath)
            .Symbols;
        Assert.False(ReferenceEquals(indexList, model.Symbols));

        // ...so a later re-index that passes the store model's list replaces
        // content instead of clear-then-AddRange self-wiping it.
        GlobalSymbolIndex.Instance.AddFile(_rootPath, MqlLanguage.Mql4, model.Symbols);
        Assert.Contains(GlobalSymbolIndex.Instance.GetFileSymbols(_rootPath, MqlLanguage.Mql4)!,
            s => s.Name == "ReuseRootSymbol");
    }
}
