using System;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Parser;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp;

/// <summary>
/// Issue #116: OmniSharp 0.19.9 routes each textDocument/didOpen|didChange|
/// didClose notification to exactly ONE of the two handlers registered for the
/// method, so the built-in TextDocumentSync handler must delegate to the custom
/// handlers (via <c>HandleSync</c>) instead of running empty lambdas. These
/// tests exercise the synchronous forwarding entry point that the built-in
/// lambdas call and assert the composition root is wired to use it.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue116DidOpenForwardingTests : IDisposable
{
    private readonly GlobalSymbolIndexCollectionFixture _fixture;

    public Issue116DidOpenForwardingTests(GlobalSymbolIndexCollectionFixture fixture)
    {
        _fixture = fixture;
    }

    public void Dispose()
    {
        GlobalSymbolIndex.Instance.Clear();
    }

    [Fact]
    public void HandleSync_ForDidOpen_ParsesStoresAndIndexesDocument()
    {
        // Arrange
        var uri = new Uri("file:///issue116/open-forward.mq4");
        var content = "void ForwardedFunc()\n{\n    int x = 1;\n}\n";
        var store = new OpenDocumentStore();
        var handler = new DidOpenTextDocumentHandler(
            Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);

        var request = new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem
            {
                Uri = uri,
                Text = content,
                Version = 1
            }
        };

        // Act
        handler.HandleSync(request, CancellationToken.None);

        // Assert — the swallowed-didOpen symptom was "store empty and index
        // never written"; both must happen on the built-in forwarding path.
        Assert.True(store.TryGetValue(uri, out var model, out _, out _));
        Assert.NotNull(model);
        Assert.NotEmpty(GlobalSymbolIndex.Instance.FindSymbol("ForwardedFunc"));
    }

    [Fact]
    public void HandleSync_ForDidChange_ReparsesStoresAndReindexesDocument()
    {
        // Arrange
        var uri = new Uri("file:///issue116/change-forward.mq4");
        var filePath = uri.AbsolutePath;
        var store = new OpenDocumentStore();
        var openHandler = new DidOpenTextDocumentHandler(
            Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);
        var changeHandler = new DidChangeTextDocumentHandler(
            Substitute.For<ILogger<DidChangeTextDocumentHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);

        // Seed the document through the real open path.
        openHandler.HandleSync(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem { Uri = uri, Text = "void BeforeFunc() {}\n", Version = 1 }
        }, CancellationToken.None);

        var changeRequest = new DidChangeTextDocumentParams
        {
            TextDocument = new OptionalVersionedTextDocumentIdentifier { Uri = uri, Version = 2 },
            ContentChanges = new Container<TextDocumentContentChangeEvent>(
                new TextDocumentContentChangeEvent { Text = "void AfterFunc() {}\n" })
        };

        // Act
        changeHandler.HandleSync(changeRequest, CancellationToken.None);

        // Assert — the change ran through the forwarding path: store updated
        // and the file's symbols replaced in the index (per-file list).
        Assert.True(store.TryGetValue(uri, out var model, out var storedContent, out _));
        Assert.NotNull(model);
        Assert.Equal("void AfterFunc() {}\n", storedContent);
        Assert.NotEmpty(GlobalSymbolIndex.Instance.FindSymbol("AfterFunc"));
        var indexedSymbols = GlobalSymbolIndex.Instance.GetFileSymbols(filePath);
        Assert.NotNull(indexedSymbols);
        Assert.Contains(indexedSymbols!, s => s.Name == "AfterFunc");
        Assert.DoesNotContain(indexedSymbols!, s => s.Name == "BeforeFunc");
    }

    [Fact]
    public void HandleSync_ForDidClose_RemovesDocumentFromStore()
    {
        // Arrange
        var uri = new Uri("file:///issue116/close-forward.mq4");
        var store = new OpenDocumentStore();
        var openHandler = new DidOpenTextDocumentHandler(
            Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);
        var closeHandler = new DidCloseTextDocumentHandler(
            Substitute.For<ILogger<DidCloseTextDocumentHandler>>(),
            store);

        openHandler.HandleSync(new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem { Uri = uri, Text = "void CloseFunc() {}\n", Version = 1 }
        }, CancellationToken.None);
        Assert.True(store.TryGetValue(uri, out _));

        // Act
        closeHandler.HandleSync(
            new DidCloseTextDocumentParams { TextDocument = new TextDocumentIdentifier { Uri = uri } },
            CancellationToken.None);

        // Assert
        Assert.False(store.TryGetValue(uri, out _));
    }

    [Fact]
    public void Program_Delegates_Builtin_TextDocumentSync_Lambdas_To_Custom_Handlers()
    {
        // Composition-root source scan (Program is not public): the built-in
        // OnTextDocumentSync callbacks must be the forwarders, the forwarders
        // must call HandleSync, and the handlers must be DI-resolved.
        var source = ReadProgramSource();

        Assert.Contains("ForwardDidOpen,", source);
        Assert.Contains("ForwardDidClose,", source);
        Assert.Contains("ForwardDidChange,", source);
        Assert.Contains("handler.HandleSync(request, CancellationToken.None);", source);
        Assert.Contains("GetRequiredService<DidOpenTextDocumentHandler>()", source);
        Assert.Contains("GetRequiredService<DidChangeTextDocumentHandler>()", source);
        Assert.Contains("GetRequiredService<DidCloseTextDocumentHandler>()", source);
    }

    private static string ReadProgramSource()
    {
        // Test assembly lives at <repo>/tests/bin/<tfm>/<cfg>/ — Program.cs is
        // 4 directories up under src/.
        var here = Path.GetDirectoryName(typeof(Issue116DidOpenForwardingTests).Assembly.Location)!;
        var path = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", "src", "Program.cs"));
        Assert.True(File.Exists(path), $"Program.cs not found: {path}");
        return File.ReadAllText(path);
    }
}
