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
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #116 (defense layer): a request-path parse of a document that was
/// never opened (didOpen swallowed, or the request arrives first) must be
/// indexed into <see cref="GlobalSymbolIndex"/>, not merely placed in the
/// OpenDocumentStore. Otherwise the workspace scan sees the store entry,
/// skips the file as "open", and the file stays permanently absent from the
/// index — the permanent-null symptom of issue #116.
///
/// Runs inside the shared GlobalSymbolIndex Tests collection: the fixture and
/// Dispose use GlobalSymbolIndex.Instance.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue116RequestPathIndexingTests : IDisposable
{
    private const string ProviderContent =
        "int ComputeTotal_Caesar(int a, int b)\n" +
        "{\n" +
        "    return a + b;\n" +
        "}\n";

    private const string ConsumerContent =
        "void OnStart_Caesar()\n" +
        "{\n" +
        "    int total = ComputeTotal_Caesar(1, 2);\n" +
        "}\n";

    // 0-based position of the declaration name in ProviderContent.
    private const int DeclarationLine = 0;
    private const int DeclarationColumn = 5; // inside "ComputeTotal_Caesar" (cols 4..21)

    // 0-based position of the call name in ConsumerContent.
    private const int CallSiteLine = 2;
    private const int CallSiteColumn = 16; // start of "ComputeTotal_Caesar"

    private readonly string _dir;
    private readonly string _providerPath;
    private readonly string _consumerPath;

    public Issue116RequestPathIndexingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue116Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _providerPath = Path.Combine(_dir, "provider.mq4");
        _consumerPath = Path.Combine(_dir, "consumer.mq4");
        File.WriteAllText(_providerPath, ProviderContent);
        File.WriteAllText(_consumerPath, ConsumerContent);
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

    private static IMqlBuiltins[] CreateBuiltins() =>
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() };

    private static MqlLanguageService CreateLanguageService() =>
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser());

    private static HoverHandler CreateHoverHandler(OpenDocumentStore store, MqlLanguageService languageService) =>
        new HoverHandler(
            Substitute.For<ILogger<HoverHandler>>(),
            languageService,
            store,
            CreateBuiltins());

    private static DefinitionHandler CreateDefinitionHandler(OpenDocumentStore store, MqlLanguageService languageService) =>
        new DefinitionHandler(
            Substitute.For<ILogger<DefinitionHandler>>(),
            languageService,
            store,
            CreateBuiltins(),
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

    [Fact]
    public async Task RequestPathParse_NeverOpenedFile_IndexesSymbolsAndOccurrences()
    {
        // The provider is never didOpen'd and never scanned.
        var store = new OpenDocumentStore();
        var handler = CreateHoverHandler(store, CreateLanguageService());

        await handler.Handle(
            new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_providerPath)),
                Position = new Position(DeclarationLine, DeclarationColumn)
            },
            CancellationToken.None);

        // The request-path parse must now be in the index (symbols)...
        var indexed = GlobalSymbolIndex.Instance.GetFileSymbols(_providerPath, MqlLanguage.Mql4);
        Assert.NotNull(indexed);
        Assert.Contains(indexed!, s => s.Name == "ComputeTotal_Caesar");

        // ...and so must its occurrences (proves SymbolOccurrenceMapper.Map was
        // used, not the null overload that would leave an empty occurrence list).
        var occurrences = GlobalSymbolIndex.Instance.FindOccurrences("ComputeTotal_Caesar", MqlLanguage.Mql4);
        Assert.Contains(occurrences, o => o.FilePath == _providerPath);
    }

    [Fact]
    public async Task CrossFileDefinition_ResolvesThroughRequestPathIndexedProvider()
    {
        var store = new OpenDocumentStore();
        var languageService = CreateLanguageService();

        // 1. First request is against the never-opened provider: this indexes it.
        await CreateHoverHandler(store, languageService).Handle(
            new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_providerPath)),
                Position = new Position(DeclarationLine, DeclarationColumn)
            },
            CancellationToken.None);

        // 2. A definition request from the consumer must resolve to the
        //    provider declaration via the global index.
        var result = await CreateDefinitionHandler(store, languageService).Handle(
            new DefinitionParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_consumerPath)),
                Position = new Position(CallSiteLine, CallSiteColumn)
            },
            CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(_providerPath, location!.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task RenameAsFirstRequest_DoesNotSelfClearIndexedSymbols()
    {
        // Regression for the aliasing hazard: RenameHandler is the one handler
        // that re-indexes a never-opened document immediately AFTER
        // TryGetDocumentContent (its `wasOpenInStore` guard was captured before
        // the call). If TryGetDocumentContent passed the store's own Symbols
        // list to AddFile, the second AddFile would run
        // existing.Clear(); existing.AddRange(symbols) on that same instance
        // and wipe the file's symbols (#36). The defensive copy prevents it.
        var store = new OpenDocumentStore();
        var languageService = CreateLanguageService();
        var handler = new RenameHandler(
            Substitute.For<ILogger<RenameHandler>>(),
            languageService,
            store,
            CreateBuiltins(),
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

        await handler.Handle(
            new RenameParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(_providerPath)),
                Position = new Position(DeclarationLine, DeclarationColumn),
                NewName = "ComputeTotal_Renamed"
            },
            CancellationToken.None);

        var indexed = GlobalSymbolIndex.Instance.GetFileSymbols(_providerPath, MqlLanguage.Mql4);
        Assert.NotNull(indexed);
        Assert.Contains(indexed!, s => s.Name == "ComputeTotal_Caesar");
    }
}
