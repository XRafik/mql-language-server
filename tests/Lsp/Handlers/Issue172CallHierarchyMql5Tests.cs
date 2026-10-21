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
/// Issue #172: callHierarchy/incomingCalls returns no callers for any MQL5
/// file (reported as a template-function symptom).
///
/// Production indexes symbols under (file, MqlLanguage.Mql5), but the
/// incoming-calls caller resolution uses the 1-arg
/// GlobalSymbolIndex.GetFileSymbols overload that defaults to Mql4 — so every
/// caller of an .mq5 (or includer-Mql5 .mqh) symbol is dropped. Both tests
/// index their fixtures under MqlLanguage.Mql5 (the .mqh routed by its MQL5
/// includer, per the D2 include design) and must find callers; on the broken
/// build they see empty incoming calls.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue172CallHierarchyMql5Tests : IDisposable
{
    private readonly string _dir;
    private readonly string _includePath;
    private readonly string _mainPath;
    private readonly string _plainPath;

    public Issue172CallHierarchyMql5Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue172Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _includePath = Path.Combine(_dir, "stats.mqh");
        _mainPath = Path.Combine(_dir, "main.mq5");
        _plainPath = Path.Combine(_dir, "plain.mq5");

        File.WriteAllText(_includePath,
            "template<typename T>\n" +
            "T mean(T &arr[])\n" +
            "{\n" +
            "    T sum = 0;\n" +
            "    for(int i = 0; i < ArraySize(arr); i++)\n" +
            "        sum += arr[i];\n" +
            "    return sum / ArraySize(arr);\n" +
            "}\n");

        File.WriteAllText(_mainPath,
            "#include \"stats.mqh\"\n" +
            "\n" +
            "void FillArray(int &arr[])\n" +
            "{\n" +
            "    for(int i = 0; i < ArraySize(arr); i++)\n" +
            "        arr[i] = i;\n" +
            "}\n" +
            "\n" +
            "void regression()\n" +
            "{\n" +
            "    int arr1[3];\n" +
            "    int arr2[5];\n" +
            "    FillArray(arr1);\n" +
            "    FillArray(arr2);\n" +
            "    mean(arr1);\n" +
            "    mean(arr2);\n" +
            "}\n");
        File.WriteAllText(_plainPath,
            "void helper(int x)\n" +
            "{\n" +
            "    int y = x + 1;\n" +
            "    Print(\"helper: \", y);\n" +
            "}\n" +
            "\n" +
            "void OnStart()\n" +
            "{\n" +
            "    helper(1);\n" +
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

    /// <summary>
    /// Mirrors Issue96CallHierarchyTests.Create() but indexes BOTH files under
    /// MqlLanguage.Mql5 (the .mqh under its includer's language key) and parses
    /// them with the Mql5 parser — the production indexing shape for .mq5.
    /// </summary>
    private (OpenDocumentStore store, CallHierarchyHandler handler) Create()
    {
        var parser = new Mql5AntlrParser();
        var mainFile = parser.ParseFile(File.ReadAllText(_mainPath), _mainPath);
        var includeFile = parser.ParseFile(File.ReadAllText(_includePath), _includePath);

        GlobalSymbolIndex.Instance.Clear();
        GlobalSymbolIndex.Instance.AddFile(_mainPath, MqlLanguage.Mql5, mainFile.Symbols,
            SymbolOccurrenceMapper.Map(mainFile, _mainPath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddFile(_includePath, MqlLanguage.Mql5, includeFile.Symbols,
            SymbolOccurrenceMapper.Map(includeFile, _includePath, MqlLanguage.Mql5));
        GlobalSymbolIndex.Instance.AddDependency(_mainPath, _includePath);

        var plainFile = parser.ParseFile(File.ReadAllText(_plainPath), _plainPath);
        GlobalSymbolIndex.Instance.AddFile(_plainPath, MqlLanguage.Mql5, plainFile.Symbols,
            SymbolOccurrenceMapper.Map(plainFile, _plainPath, MqlLanguage.Mql5));

        var store = new OpenDocumentStore();
        store.AddOrUpdate(new Uri("file://" + _mainPath), mainFile, File.ReadAllText(_mainPath), MqlLanguage.Mql5);
        store.AddOrUpdate(new Uri("file://" + _includePath), includeFile, File.ReadAllText(_includePath), MqlLanguage.Mql5);
        store.AddOrUpdate(new Uri("file://" + _plainPath), plainFile, File.ReadAllText(_plainPath), MqlLanguage.Mql5);

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
    public async Task IncomingCalls_Mql5PlainFunction_FindsCallerAsync()
    {
        var (_, handler) = Create();

        // Plain .mq5 scenario (no includes): cursor on helper's declaration
        // name (0-based line 0, col 5). OnStart calls helper once.
        var prepared = (await handler.Handle(Prepare(_plainPath, 0, 5), CancellationToken.None))!.Single();
        Assert.Equal("helper", prepared.Name);

        var incoming = await ((ICallHierarchyIncomingHandler)handler).Handle(
            new CallHierarchyIncomingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(incoming);
        var calls = incoming!.ToList();
        Assert.Single(calls);

        var caller = calls.Single();
        Assert.Equal("OnStart", caller.From.Name);
        Assert.Equal(_plainPath, caller.From.Uri.GetFileSystemPath());
        // One fromRange covering the helper call token (0-based line 8, col 4).
        var range = Assert.Single(caller.FromRanges);
        Assert.Equal(8, range.Start.Line);
        Assert.Equal(4, range.Start.Character);
    }

    [Fact]
    public async Task IncomingCalls_Mql5TemplateInInclude_FindsCallerAsync()
    {
        var (_, handler) = Create();

        // Issue #172 scenario: cursor on the template function name `mean` in
        // the includer-routed .mqh (0-based line 1, col 3). The .mq5 caller
        // `regression` calls mean(arr1) and mean(arr2) — two call positions.
        var prepared = (await handler.Handle(Prepare(_includePath, 1, 3), CancellationToken.None))!.Single();
        Assert.Equal("mean", prepared.Name);

        var incoming = await ((ICallHierarchyIncomingHandler)handler).Handle(
            new CallHierarchyIncomingCallsParams { Item = prepared }, CancellationToken.None);

        Assert.NotNull(incoming);
        var calls = incoming!.ToList();
        Assert.Single(calls);

        var caller = calls.Single();
        Assert.Equal("regression", caller.From.Name);
        Assert.Equal(_mainPath, caller.From.Uri.GetFileSystemPath());
        // Both call sites (mean(arr1), mean(arr2)) grouped under one caller.
        Assert.Equal(2, caller.FromRanges.Count());
    }
}
