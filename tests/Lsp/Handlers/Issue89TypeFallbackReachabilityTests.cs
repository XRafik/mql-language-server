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
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #89 (follow-up of #86/PR #87): the type-usage fallback in
/// typeDefinition/definition resolved an identifier with no in-file
/// declaration to ANY exact-case Class/Struct/Interface/Enum in the
/// workspace index — including a same-named type in an UNRELATED,
/// never-included file (the reporter's case: free function Format(string)
/// in an included helper + foreign class Format in Utils.mqh — the answer
/// became the foreign class instead of null).
///
/// The fix restricts cross-file type candidates to files linked to the
/// queried document through the include graph (either direction), with a
/// degraded-graph fallback: when the queried document has no include edges
/// at all (no scan, never opened), the name-keyed resolution of #64 keeps
/// working — the guard must never resolve LESS than before.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue89TypeFallbackReachabilityTests : IDisposable
{
    private readonly string _dir;

    public Issue89TypeFallbackReachabilityTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue89Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
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

    /// <summary>
    /// The reporter's exact layout. main.mq5 includes helper.mqh (free
    /// function Format); Utils.mqh (foreign class Format) is indexed but
    /// never included by anything. didOpen of main.mq5 populates the
    /// main→helper edge; Utils.mqh is index-only (the workspace scan shape).
    /// </summary>
    private (string mainPath, string helperPath, string utilsPath) WriteReportersFixture(out OpenDocumentStore store)
    {
        var mainPath = Path.Combine(_dir, "main.mq5");
        var helperPath = Path.Combine(_dir, "helper.mqh");
        var utilsPath = Path.Combine(_dir, "Utils.mqh");

        File.WriteAllText(helperPath, "void Format(string s)\n{\n    Print(s);\n}\n");
        File.WriteAllText(utilsPath, "class Format { };\n");
        File.WriteAllText(mainPath,
            "#include \"helper.mqh\"\n" +
            "\n" +
            "void OnStart()\n" +
            "{\n" +
            "    Format(\"x\");\n" +
            "}\n");

        var parser = new Mql5AntlrParser();
        var mainFile = parser.ParseFile(File.ReadAllText(mainPath), mainPath);
        var helperFile = parser.ParseFile(File.ReadAllText(helperPath), helperPath);
        var utilsFile = parser.ParseFile(File.ReadAllText(utilsPath), utilsPath);

        GlobalSymbolIndex.Instance.Clear();
        // didOpen of main.mq5: index main + its include, record the edge.
        GlobalSymbolIndex.Instance.AddFile(mainPath, MqlLanguage.Mql5, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, mainPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddFile(helperPath, MqlLanguage.Mql5, helperFile.Symbols,
            SymbolOccurrenceMapper.Map(helperFile, helperPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddDependency(mainPath, helperPath);
        // Utils.mqh: workspace-scan-shaped entry (indexed, no include edges).
        GlobalSymbolIndex.Instance.AddFile(utilsPath, MqlLanguage.Mql5, utilsFile.Symbols,
            SymbolOccurrenceMapper.Map(utilsFile, utilsPath, MqlLanguage.Mql5));

        store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + mainPath), mainFile, File.ReadAllText(mainPath), MqlLanguage.Mql5);
        store.AddOrUpdate(new Uri("file://" + helperPath), helperFile, File.ReadAllText(helperPath), MqlLanguage.Mql5);
        return (mainPath, helperPath, utilsPath);
    }

    private static DefinitionHandler CreateDefinitionHandler(OpenDocumentStore store) => new(
        Substitute.For<ILogger<DefinitionHandler>>(),
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
        store,
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() },
        new GlobalSymbolIndexAccessor(GlobalSymbolIndex.Instance));

    private static TypeDefinitionHandler CreateTypeDefinitionHandler(OpenDocumentStore store) => new(
        Substitute.For<ILogger<TypeDefinitionHandler>>(),
        new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
        store,
        new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

    [Fact]
    public async Task TypeUsageFallback_IgnoresSameNameTypeInUnrelatedFileAsync()
    {
        var (mainPath, helperPath, utilsPath) = WriteReportersFixture(out var store);

        // Cursor on the Format("x") call (main.mq5, 0-based line 4, col 4).
        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
            Position = new Position(4, 4)
        };

        var result = await CreateDefinitionHandler(store).Handle(request, CancellationToken.None);

        // The answer must NEVER be the foreign class in Utils.mqh. With the
        // #92 include fallback it resolves the free function in helper.mqh
        // (the correct declaration); the pre-#89-guard bug answered Utils.mqh.
        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.NotEqual(DocumentUri.FromFileSystemPath(utilsPath).ToString(), location!.Uri.ToString());
        Assert.Equal(Path.GetFileName(helperPath), Path.GetFileName(location.Uri.GetFileSystemPath()));
    }

    [Fact]
    public async Task TypeUsageFallback_StillResolvesReachableTypeDeclarationAsync()
    {
        // The #64/#86 behavior the guard must NOT regress: a class declared in
        // an INCLUDED file still resolves through the type-usage fallback.
        var mainPath = Path.Combine(_dir, "main2.mq5");
        var personPath = Path.Combine(_dir, "person.mqh");

        File.WriteAllText(personPath, "class Person\n{\n    string m_name;\n};\n");
        File.WriteAllText(mainPath,
            "#include \"person.mqh\"\n" +
            "\n" +
            "void OnStart()\n" +
            "{\n" +
            "    Person person;\n" +
            "}\n");

        var parser = new Mql5AntlrParser();
        var mainFile = parser.ParseFile(File.ReadAllText(mainPath), mainPath);
        var personFile = parser.ParseFile(File.ReadAllText(personPath), personPath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(mainPath, MqlLanguage.Mql5, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, mainPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddFile(personPath, MqlLanguage.Mql5, personFile.Symbols,
            SymbolOccurrenceMapper.Map(personFile, personPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddDependency(mainPath, personPath);

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + mainPath), mainFile, File.ReadAllText(mainPath), MqlLanguage.Mql5);

        // Cursor on "Person" in "Person person;" (0-based line 4, col 4).
        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
            Position = new Position(4, 4)
        };

        var result = await CreateDefinitionHandler(store).Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(Path.GetFileName(personPath), Path.GetFileName(location!.Uri.GetFileSystemPath()));
    }

    [Fact]
    public async Task TypeUsageFallback_DegradedGraph_KeepsNameKeyedResolutionAsync()
    {
        // No include edges at all (single-file project, no scan): the guard
        // falls back to the documented tier-2 name-keyed resolution so the
        // #64 behavior is preserved instead of regressing to always-null.
        var mainPath = Path.Combine(_dir, "main3.mq5");
        var foreignPath = Path.Combine(_dir, "foreign.mqh");

        File.WriteAllText(foreignPath, "class Widget { };\n");
        File.WriteAllText(mainPath,
            "void OnStart()\n" +
            "{\n" +
            "    Widget w;\n" +
            "}\n");

        var parser = new Mql5AntlrParser();
        var mainFile = parser.ParseFile(File.ReadAllText(mainPath), mainPath);
        var foreignFile = parser.ParseFile(File.ReadAllText(foreignPath), foreignPath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(mainPath, MqlLanguage.Mql5, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, mainPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddFile(foreignPath, MqlLanguage.Mql5, foreignFile.Symbols,
            SymbolOccurrenceMapper.Map(foreignFile, foreignPath, MqlLanguage.Mql5));
        // NOTE: no AddDependency — degraded graph.

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + mainPath), mainFile, File.ReadAllText(mainPath), MqlLanguage.Mql5);

        // Cursor on "Widget" (0-based line 2, col 4): no in-file declaration,
        // no edges — name-keyed fallback resolves the foreign class (#64).
        var request = new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
            Position = new Position(2, 4)
        };

        var result = await CreateDefinitionHandler(store).Handle(request, CancellationToken.None);

        Assert.NotNull(result);
        var location = result!.First().Location;
        Assert.NotNull(location);
        Assert.Equal(Path.GetFileName(foreignPath), Path.GetFileName(location!.Uri.GetFileSystemPath()));
    }
}