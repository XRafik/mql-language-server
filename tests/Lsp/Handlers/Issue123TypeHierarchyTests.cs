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
/// Issue #123: LSP 3.17 type hierarchy. Pinned behaviors:
/// - the MQL4/MQL5 visitors capture the base class from the grammar's
///   `COLON accessModifier qualifiedName` clause into MqlSymbol.BaseClass
///   (class in both dialects; struct/interface in MQL5 only — the MQL4
///   grammar does not parse struct inheritance);
/// - prepareTypeHierarchy prepares the type symbol under the cursor;
/// - supertypes resolves the captured base (same file or include-linked)
///   and skips unresolvable bases (stdlib types not on disk);
/// - subtypes finds every indexed type whose BaseClass matches, workspace-wide.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue123TypeHierarchyTests : IDisposable
{
    private readonly string _dir;
    private readonly string _basePath;
    private readonly string _derivedPath;
    private readonly string _mql5DerivedPath;
    private readonly string _mql5BasePath;

    public Issue123TypeHierarchyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue123Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _basePath = Path.Combine(_dir, "base_class.mqh");
        _derivedPath = Path.Combine(_dir, "main.mq4");
        _mql5BasePath = Path.Combine(_dir, "mql5_base.mqh");
        _mql5DerivedPath = Path.Combine(_dir, "mql5_main.mq5");

        File.WriteAllText(_basePath,
            "// Trade helper base\n" +
            "class TradeHelperBase\n" +
            "{\n" +
            "public:\n" +
            "    double virtual GetLotSize() { return 0.01; }\n" +
            "};\n");

        File.WriteAllText(_derivedPath,
            "#include \"base_class.mqh\"\n" +
            "\n" +
            "class StopManager : public TradeHelperBase\n" +
            "{\n" +
            "public:\n" +
            "    double GetLotSize() { return 0.10; }\n" +
            "};\n" +
            "\n" +
            "class TrailingManager : public TradeHelperBase\n" +
            "{\n" +
            "};\n");

        File.WriteAllText(_mql5BasePath,
            "struct Frame\n" +
            "{\n" +
            "    long id;\n" +
            "};\n");

        File.WriteAllText(_mql5DerivedPath,
            "#include \"mql5_base.mqh\"\n" +
            "\n" +
            "struct TradeFrame : Frame\n" +
            "{\n" +
            "    double volume;\n" +
            "};\n" +
            "\n" +
            "interface IExecutor\n" +
            "{\n" +
            "    void Execute();\n" +
            "};\n" +
            "\n" +
            "class MarketExecutor : IExecutor\n" +
            "{\n" +
            "public:\n" +
            "    void Execute() { }\n" +
            "};\n");
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

    // --- Visitor capture -------------------------------------------------

    [Fact]
    public void Mql4Visitor_CapturesBaseClassFromInheritanceClause()
    {
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile(File.ReadAllText(_derivedPath), _derivedPath);

        var stopManager = file.Symbols.Single(s => s.Name == "StopManager");
        Assert.Equal(SymbolKind.Class, stopManager.Kind);
        Assert.Equal("TradeHelperBase", stopManager.BaseClass);
        Assert.Equal("class StopManager : TradeHelperBase", stopManager.Detail);

        var trailing = file.Symbols.Single(s => s.Name == "TrailingManager");
        Assert.Equal("TradeHelperBase", trailing.BaseClass);
    }

    [Fact]
    public void Mql4Visitor_ClassWithoutBase_HasNullBaseClass()
    {
        var parser = new Mql4AntlrParser();
        var file = parser.ParseFile(File.ReadAllText(_basePath), _basePath);

        var baseSymbol = file.Symbols.Single(s => s.Name == "TradeHelperBase");
        Assert.Null(baseSymbol.BaseClass);
    }

    [Fact]
    public void Mql5Visitor_CapturesBaseClassForStructAndInterface()
    {
        var parser = new Mql5AntlrParser();
        var file = parser.ParseFile(File.ReadAllText(_mql5DerivedPath), _mql5DerivedPath);

        var tradeFrame = file.Symbols.Single(s => s.Name == "TradeFrame");
        Assert.Equal("Frame", tradeFrame.BaseClass);

        var executor = file.Symbols.Single(s => s.Name == "MarketExecutor");
        Assert.Equal("IExecutor", executor.BaseClass);
        Assert.Equal(SymbolType.Interface, file.Symbols.Single(s => s.Name == "IExecutor").SymbolType);
    }

    // --- Handler ----------------------------------------------------------

    private (TypeHierarchyHandler handler, Mql4AntlrParser parser) Create()
    {
        var mql4Parser = new Mql4AntlrParser();
        var mql5Parser = new Mql5AntlrParser();
        var baseFile = mql4Parser.ParseFile(File.ReadAllText(_basePath), _basePath);
        var derivedFile = mql4Parser.ParseFile(File.ReadAllText(_derivedPath), _derivedPath);
        var mql5BaseFile = mql5Parser.ParseFile(File.ReadAllText(_mql5BasePath), _mql5BasePath);
        var mql5DerivedFile = mql5Parser.ParseFile(File.ReadAllText(_mql5DerivedPath), _mql5DerivedPath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_basePath, MqlLanguage.Mql4, baseFile.Symbols,
            SymbolOccurrenceMapper.Map(baseFile, _basePath, MqlLanguage.Mql4));
        GlobalSymbolIndex.Instance.AddFile(_derivedPath, MqlLanguage.Mql4, derivedFile.Symbols,
            SymbolOccurrenceMapper.Map(derivedFile, _derivedPath, MqlLanguage.Mql4));
        GlobalSymbolIndex.Instance.AddFile(_mql5BasePath, MqlLanguage.Mql5, mql5BaseFile.Symbols,
            SymbolOccurrenceMapper.Map(mql5BaseFile, _mql5BasePath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddFile(_mql5DerivedPath, MqlLanguage.Mql5, mql5DerivedFile.Symbols,
            SymbolOccurrenceMapper.Map(mql5DerivedFile, _mql5DerivedPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddDependency(_derivedPath, _basePath);
        GlobalSymbolIndex.Instance.AddDependency(_mql5DerivedPath, _mql5BasePath);

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + _basePath), baseFile, File.ReadAllText(_basePath), MqlLanguage.Mql4);
        store.AddOrUpdate(new Uri("file://" + _derivedPath), derivedFile, File.ReadAllText(_derivedPath), MqlLanguage.Mql4);
        store.AddOrUpdate(new Uri("file://" + _mql5BasePath), mql5BaseFile, File.ReadAllText(_mql5BasePath), MqlLanguage.Mql5);
        store.AddOrUpdate(new Uri("file://" + _mql5DerivedPath), mql5DerivedFile, File.ReadAllText(_mql5DerivedPath), MqlLanguage.Mql5);

        var handler = new TypeHierarchyHandler(
            Substitute.For<ILogger<TypeHierarchyHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            store,
            new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
            new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));
        return (handler, mql4Parser);
    }

    private static TypeHierarchyPrepareParams Prepare(string path, int line, int character) => new()
    {
        TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path)),
        Position = new Position(line, character)
    };

    [Fact]
    public async Task Prepare_AtClassDeclaration_ReturnsTheClassItemAsync()
    {
        var (handler, _) = Create();

        // Cursor on StopManager's declaration name (main.mq4, 0-based line 2, col 6).
        var result = await handler.Handle(Prepare(_derivedPath, 2, 6), CancellationToken.None);

        Assert.NotNull(result);
        var item = result!.Single();
        Assert.Equal("StopManager", item.Name);
        Assert.Equal(SymbolKind.Class, item.Kind);
        Assert.Equal(_derivedPath, item.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task Prepare_AtNonTypeIdentifier_ReturnsNullAsync()
    {
        var (handler, _) = Create();

        // Cursor on the base file's method name (line 3, col 19): a function
        // symbol, not a type — typeHierarchy must refuse it.
        var result = await handler.Handle(Prepare(_basePath, 3, 19), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task Supertypes_ResolvesIncludeDeclaredBaseAsync()
    {
        var (handler, _) = Create();

        var prepared = await handler.Handle(Prepare(_derivedPath, 2, 6), CancellationToken.None);
        Assert.NotNull(prepared);

        var supertypes = await handler.Handle(
            new TypeHierarchySupertypesParams { Item = prepared!.Single() }, CancellationToken.None);

        Assert.NotNull(supertypes);
        var baseItem = supertypes!.Single();
        Assert.Equal("TradeHelperBase", baseItem.Name);
        Assert.Equal(_basePath, baseItem.Uri.GetFileSystemPath());
    }

    [Fact]
    public async Task Supertypes_ClassWithoutBase_ReturnsEmptyAsync()
    {
        var (handler, _) = Create();

        // Cursor on TradeHelperBase's declaration (base_class.mqh, 0-based 1, col 6).
        var prepared = await handler.Handle(Prepare(_basePath, 1, 6), CancellationToken.None);
        Assert.NotNull(prepared);

        var supertypes = await handler.Handle(
            new TypeHierarchySupertypesParams { Item = prepared!.Single() }, CancellationToken.None);

        Assert.NotNull(supertypes);
        Assert.Empty(supertypes!);
    }

    [Fact]
    public async Task Subtypes_FindsAllWorkspaceDerivedClassesAsync()
    {
        var (handler, _) = Create();

        // Prepare the base, then ask for subtypes: both MQL4 derived classes
        // in main.mq4 must appear (the MQL5 types do not inherit from it).
        var prepared = await handler.Handle(Prepare(_basePath, 1, 6), CancellationToken.None);
        Assert.NotNull(prepared);

        var subtypes = await handler.Handle(
            new TypeHierarchySubtypesParams { Item = prepared!.Single() }, CancellationToken.None);

        Assert.NotNull(subtypes);
        var names = subtypes!.Select(i => i.Name).OrderBy(n => n).ToList();
        Assert.Equal(new[] { "StopManager", "TrailingManager" }, names);
        Assert.All(subtypes!, i => Assert.Equal(_derivedPath, i.Uri.GetFileSystemPath()));
    }

    [Fact]
    public async Task Subtypes_Mql5StructHierarchy_ResolvesAcrossDialectsAsync()
    {
        var (handler, _) = Create();

        // Cursor on Frame's declaration in mql5_base.mqh (0-based 0, col 7).
        var prepared = await handler.Handle(Prepare(_mql5BasePath, 0, 7), CancellationToken.None);
        Assert.NotNull(prepared);
        Assert.Equal("Frame", prepared!.Single().Name);

        var subtypes = await handler.Handle(
            new TypeHierarchySubtypesParams { Item = prepared!.Single() }, CancellationToken.None);

        Assert.NotNull(subtypes);
        var item = Assert.Single(subtypes!);
        Assert.Equal("TradeFrame", item.Name);
        Assert.Equal(_mql5DerivedPath, item.Uri.GetFileSystemPath());
    }
}
