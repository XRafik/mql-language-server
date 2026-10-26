using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using NSubstitute;
using MqlLanguageServer.Mql5.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Tests.Lsp;

using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #86: cross-file typeDefinition/definition degrades permanently after
/// any textDocument/didChange on the queried file, and a didClose + didOpen
/// with full text does not recover. Reproduces the reporter's deterministic
/// matrix on the #62/#78 fixture (main.mq5 includes person.mqh, `Person
/// person("Alice", 30);`), all through the real didOpen/didChange handler
/// path (same parse + store + index path as production).
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class Issue86DidChangeDegradationTests : IDisposable
{
    private const string HeaderContent =
        "// person.mqh\n" +
        "class Person\n" +
        "  {\n" +
        "private:\n" +
        "   string   m_name;\n" +
        "public:\n" +
        "                     Person(string name, int age);\n" +
        "   string            Greet(void) const;\n" +
        "  };\n" +
        "\n" +
        "string Person::Greet(void) const\n" +
        "  {\n" +
        "   return(\"Hello, \" + m_name + \"!\");\n" +
        "  }\n";

    private const string MainContent =
        "// main.mq5\n" +
        "#include \"person.mqh\"\n" +
        "\n" +
        "int OnInit(void)\n" +
        "  {\n" +
        "   Person person(\"Alice\", 30);\n" +
        "   string message = person.Greet();\n" +
        "   Print(message);\n" +
        "   return(INIT_SUCCEEDED);\n" +
        "  }\n";

    // Cursor on "Person" in "Person person(\"Alice\", 30);" (line 5, col 5).
    private const int ProbeLine = 5;
    private const int ProbeCharacter = 5;

    private readonly GlobalSymbolIndex _index;
    private string _dir = null!;
    private string _headerPath = null!;
    private string _mainPath = null!;
    private OpenDocumentStore _store = null!;

    public Issue86DidChangeDegradationTests()
    {
        _index = GlobalSymbolIndex.Instance;
        _index.Clear();
        _store = new OpenDocumentStore();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
        _index.Clear();
    }

    private DidOpenTextDocumentHandler CreateOpenHandler()
    {
        return new DidOpenTextDocumentHandler(
            Substitute.For<ILogger<DidOpenTextDocumentHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store,
            new MqlLanguageServer.Mql4.Builtins.IMqlBuiltins[] { new MqlLanguageServer.Mql4.Builtins.Mql4BuiltinsAdapter() });
    }

    private DidChangeTextDocumentHandler CreateChangeHandler()
    {
        return new DidChangeTextDocumentHandler(
            Substitute.For<ILogger<DidChangeTextDocumentHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store,
            new MqlLanguageServer.Mql4.Builtins.IMqlBuiltins[] { new MqlLanguageServer.Mql4.Builtins.Mql4BuiltinsAdapter() });
    }

    private DidCloseTextDocumentHandler CreateCloseHandler()
    {
        return new DidCloseTextDocumentHandler(
            Substitute.For<ILogger<DidCloseTextDocumentHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store,
            new MqlLanguageServer.Mql4.Builtins.IMqlBuiltins[] { new MqlLanguageServer.Mql4.Builtins.Mql4BuiltinsAdapter() });
    }

    private TypeDefinitionHandler CreateTypeDefinitionHandler()
    {
        return new TypeDefinitionHandler(
            Substitute.For<ILogger<TypeDefinitionHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store,
            new MqlLanguageServer.Mql4.Builtins.IMqlBuiltins[] { new MqlLanguageServer.Mql4.Builtins.Mql4BuiltinsAdapter() });
    }

    private DefinitionHandler CreateDefinitionHandler()
    {
        return new DefinitionHandler(
            Substitute.For<ILogger<DefinitionHandler>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store,
            new MqlLanguageServer.Mql4.Builtins.IMqlBuiltins[] { new MqlLanguageServer.Mql4.Builtins.Mql4BuiltinsAdapter() });
    }

    private (string headerPath, string mainPath) WriteFixture()
    {
        _dir = Path.Combine(Path.GetTempPath(), "Issue86Fixture_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _headerPath = Path.Combine(_dir, "person.mqh");
        _mainPath = Path.Combine(_dir, "main.mq5");
        File.WriteAllText(_headerPath, HeaderContent);
        File.WriteAllText(_mainPath, MainContent);
        return (_headerPath, _mainPath);
    }

    /// <summary>
    /// Production initialize equivalent (Program.cs:245): the background
    /// workspace scan indexes every supported file in the workspace folder
    /// before any didOpen/didChange traffic.
    /// </summary>
    private void RunWorkspaceScan()
    {
        var indexer = new WorkspaceIndexer(
            Substitute.For<ILogger<WorkspaceIndexer>>(),
            new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
            _store);
        indexer.StartIndexingAndWaitForIdle(new[] { _dir });
    }

    private static DidOpenTextDocumentParams OpenParams(string path, string text) => new()
    {
        TextDocument = new TextDocumentItem
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Text = text,
            LanguageId = "mql5"
        }
    };

    private static DidChangeTextDocumentParams ChangeParams(string path, string newContent) => new()
    {
        TextDocument = new OptionalVersionedTextDocumentIdentifier
        {
            Uri = DocumentUri.FromFileSystemPath(path),
            Version = 2
        },
        ContentChanges = new Container<TextDocumentContentChangeEvent>(
            new TextDocumentContentChangeEvent { Text = newContent })
    };

    private static DidCloseTextDocumentParams CloseParams(string path) => new()
    {
        TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(path))
    };

    private async Task<string?> DefinitionTargetAsync(string mainPath, int line = ProbeLine, int character = ProbeCharacter)
    {
        var handler = CreateDefinitionHandler();
        var result = await handler.Handle(new DefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
            Position = new Position(line, character)
        }, CancellationToken.None);

        var location = result?.FirstOrDefault()?.Location;
        return location == null ? null : Path.GetFileName(location.Uri.ToUri().LocalPath);
    }

    private async Task<string?> TypeDefinitionTargetAsync(string mainPath, int line = ProbeLine, int character = ProbeCharacter)
    {
        var handler = CreateTypeDefinitionHandler();
        var result = await handler.Handle(new TypeDefinitionParams
        {
            TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
            Position = new Position(line, character)
        }, CancellationToken.None);

        var location = result?.FirstOrDefault()?.Location;
        return location == null ? null : Path.GetFileName(location.Uri.ToUri().LocalPath);
    }

    /// <summary>
    /// Reporter matrix scenario 1 (sanity baseline, expected to pass): both
    /// files opened with full text; typeDefinition on the instance's type
    /// name resolves to the class in the included header.
    /// </summary>
    [Fact]
    public async Task Sanity_BothOpenedFullText_ResolvesClassInHeader()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Reporter matrix scenario B (first half): after a real edit arrives via
    /// didChange on the queried file, cross-file type resolution must
    /// survive. Reported broken on v2.4.2 (degrades to [] / fallback).
    /// </summary>
    [Fact]
    public async Task TypeDefinition_AfterRealEditViaDidChange_StillResolvesClassInHeader()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);
        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));

        // Real (non no-op) edit: a rename-style content change, same shape as
        // the reporter's scenario B.
        var edited = MainContent.Replace("Print(message);", "Print(message, \"!\");");
        Assert.NotEqual(MainContent, edited);
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Reporter matrix scenario B (second half): didClose + didOpen with full
    /// text must recover the degraded state. Reported broken on v2.4.2.
    /// </summary>
    [Fact]
    public async Task TypeDefinition_ReopenAfterEdit_RecoversResolution()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        var edited = MainContent.Replace("Print(message);", "Print(message, \"!\");");
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        var closeHandler = CreateCloseHandler();
        await closeHandler.Handle(CloseParams(_mainPath), CancellationToken.None);
        // Reopen with the edited content (what the editor still holds).
        await openHandler.Handle(OpenParams(_mainPath, edited), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Issue #86: pure buffer-vs-disk shape (probe on the renamed variable
    /// itself, buffer columns 10-14). The handler must extract the cursor
    /// identifier from the stored buffer content the model was parsed from;
    /// with the stale disk text the extraction ("person") mismatches the
    /// fresh model ("alice") and resolution degrades to null. With the
    /// buffer content, the variable's DeclaredType resolves cross-file to
    /// the class in the included header.
    /// </summary>
    [Fact]
    public async Task TypeDefinition_UnsavedBufferEdit_RenamedVariableResolves()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        var edited = MainContent.Replace("Person person(\"Alice\", 30);", "Person alice(\"Alice\", 30);");
        Assert.NotEqual(MainContent, edited);
        Assert.Contains("Person alice(", edited);
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        // Probe on the renamed variable ("alice", buffer cols 10-14).
        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath, line: 5, character: 10));
    }

    /// <summary>
    /// Issue #86: pure buffer-vs-disk shape for textDocument/definition — a
    /// cursor on the renamed variable keeps resolving to its own declaration
    /// (existing correct semantics) instead of degrading to null as reported.
    /// </summary>
    [Fact]
    public async Task Definition_UnsavedBufferEdit_RenamedVariableStaysOwnDeclaration()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        var edited = MainContent.Replace("Person person(\"Alice\", 30);", "Person alice(\"Alice\", 30);");
        Assert.NotEqual(MainContent, edited);
        Assert.Contains("Person alice(", edited);
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        // Probe on the renamed variable: its own declaration is its definition.
        Assert.Equal("main.mq5", await DefinitionTargetAsync(_mainPath, line: 5, character: 10));
    }

    /// <summary>
    /// Reporter matrix scenario 3: the queried file is delivered through
    /// didOpen("") + didChange(full text) while the include file was opened
    /// with full text. typeDefinition must resolve cross-file.
    /// </summary>
    [Fact]
    public async Task TypeDefinition_MainOpenedViaEmptyOpenPlusChange_ResolvesClassInHeader()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, ""), CancellationToken.None);

        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, MainContent), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Issue #86: an unsaved editor buffer (didChange content differing from
    /// disk) with the edit ON the probe line must not degrade cross-file
    /// typeDefinition. The handler must resolve against the stored buffer
    /// content the model was parsed from, not the stale disk text (issue #86:
    /// buffer-vs-disk mismatch makes the cursor identifier extraction
    /// disagree with the fresh model).
    /// </summary>
    [Fact]
    public async Task TypeDefinition_UnsavedBufferEditOnProbeLine_StillResolvesClassInHeader()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        // Unsaved editor buffer: the variable on the probe line is renamed in
        // the buffer only; the disk file still holds the original text, exactly
        // like any edit the client has not saved yet. The probe stays on the
        // "Person" type name (columns 3-8), which the rename does not move.
        var edited = MainContent.Replace("Person person(\"Alice\", 30);", "Person alice(\"Alice\", 30);");
        Assert.NotEqual(MainContent, edited);
        Assert.Contains("Person alice(", edited);
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Issue #86: same unsaved-buffer shape for textDocument/definition — the
    /// didChange content differs from disk on the probe line, and cross-file
    /// definition must still resolve to the class in the included header.
    /// </summary>
    [Fact]
    public async Task Definition_UnsavedBufferEditOnProbeLine_StillResolvesClassInHeader()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_headerPath, HeaderContent), CancellationToken.None);
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        var edited = MainContent.Replace("Person person(\"Alice\", 30);", "Person alice(\"Alice\", 30);");
        Assert.NotEqual(MainContent, edited);
        Assert.Contains("Person alice(", edited);
        var changeHandler = CreateChangeHandler();
        await changeHandler.Handle(ChangeParams(_mainPath, edited), CancellationToken.None);

        Assert.Equal("person.mqh", await DefinitionTargetAsync(_mainPath));
    }

    /// <summary>
    /// Reporter matrix scenario 5: the include file is never opened; only
    /// main.mq5 is opened with full text. The didOpen include loop parses the
    /// header from disk, so resolution must still work.
    /// </summary>
    [Fact]
    public async Task TypeDefinition_IncludeNeverOpened_ResolvesViaDisk()
    {
        WriteFixture();
        RunWorkspaceScan();
        var openHandler = CreateOpenHandler();
        await openHandler.Handle(OpenParams(_mainPath, MainContent), CancellationToken.None);

        Assert.Equal("person.mqh", await TypeDefinitionTargetAsync(_mainPath));
    }
}