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
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #94: the initialize result declared
/// renameProvider: {prepareProvider: true} but the server did not implement
/// textDocument/prepareRename — capability-driven clients sent the request
/// and hit an unexpected -32601 mid-flow. The handler now answers with the
/// identifier range + placeholder (the current name) where a rename would
/// produce edits, and null ("rename not valid at this position") otherwise.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue94PrepareRenameTests : IDisposable
{
    private readonly string _dir;
    private readonly string _includePath;
    private readonly string _mainPath;

    public Issue94PrepareRenameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue94Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _includePath = Path.Combine(_dir, "stop_utils.mqh");
        _mainPath = Path.Combine(_dir, "main.mq4");
        File.WriteAllText(_includePath,
            "// Stop/take utilities\n" +
            "double StopLong(double price, int pips)\n" +
            "  {\n" +
            "   return(NormalizeDouble(price - pips * Point, Digits));\n" +
            "  }\n");
        File.WriteAllText(_mainPath,
            "#include \"stop_utils.mqh\"\n" +
            "\n" +
            "void OpenPendingOrder_Caesar(double stopLossPips)\n" +
            "  {\n" +
            "   double sl = StopLong(Bid, stopLossPips);\n" +
            "   Print(\"caesar sl=\", sl);\n" +
            "  }\n");
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

    private (OpenDocumentStore store, RenameHandler handler) CreateHandler()
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

        var handler = new RenameHandler(
            Substitute.For<ILogger<RenameHandler>>(),
            new Mql4AntlrParser(),
            store,
            GlobalSymbolIndex.Instance);
        return (store, handler);
    }

    private static PrepareRenameParams Prepare(string path, int line, int character) => new()
    {
        TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
        Position = new Position(line, character)
    };

    [Fact]
    public async Task PrepareRename_AtInFileDeclaration_ReturnsRangeAndPlaceholderAsync()
    {
        var (_, handler) = CreateHandler();

        // Cursor on OpenPendingOrder_Caesar (main.mq4, line 2, col 5).
        var result = await ((IPrepareRenameHandler)handler).Handle(Prepare(_mainPath, 2, 5), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.IsPlaceholderRange);
        var range = result.PlaceholderRange!.Range;
        Assert.Equal(2, range.Start.Line);
        Assert.Equal(5, range.Start.Character);
        Assert.Equal(5 + "OpenPendingOrder_Caesar".Length, range.End.Character);
        Assert.Equal("OpenPendingOrder_Caesar", result.PlaceholderRange!.Placeholder);
    }

    [Fact]
    public async Task PrepareRename_AtIncludeDeclaredUsage_ReturnsTokenRangeInRequestedDocumentAsync()
    {
        var (_, handler) = CreateHandler();

        // Cursor on the StopLong call site (main.mq4, 0-based line 4, col 15):
        // the symbol is declared in the include, but the prompt range must be
        // the token in the REQUESTED document (issue #92/#90 resolution).
        var result = await ((IPrepareRenameHandler)handler).Handle(Prepare(_mainPath, 4, 15), CancellationToken.None);

        Assert.NotNull(result);
        Assert.True(result!.IsPlaceholderRange);
        var range = result.PlaceholderRange!.Range;
        Assert.Equal(4, range.Start.Line);
        Assert.Equal(15, range.Start.Character);
        Assert.Equal(15 + "StopLong".Length, range.End.Character);
        Assert.Equal("StopLong", result.PlaceholderRange!.Placeholder);
    }

    [Fact]
    public async Task PrepareRename_OnWhitespace_ReturnsNullAsync()
    {
        var (_, handler) = CreateHandler();

        // Line 1 is blank; nothing to rename.
        var result = await ((IPrepareRenameHandler)handler).Handle(Prepare(_mainPath, 1, 0), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PrepareRename_OnBuiltin_ReturnsNullAsync()
    {
        var (_, handler) = CreateHandler();

        // Cursor on Print (builtin): not renamable, so the spec-shaped answer
        // is null rather than prompting the user to rename a builtin.
        var result = await ((IPrepareRenameHandler)handler).Handle(Prepare(_mainPath, 5, 11), CancellationToken.None);

        Assert.Null(result);
    }
}