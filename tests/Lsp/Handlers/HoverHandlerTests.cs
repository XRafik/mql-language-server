using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Xunit;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Tests for HoverHandler — verifies the MarkupContent response shape for MQL4.
/// The handler was switched from MarkedString to MarkupContent during the
/// add-mql5-support change (Slice 3). This test locks the new wire format
/// for MQL4 clients (S-006 follow-up).
/// </summary>
public class HoverHandlerTests
{
    private static ILogger<T> MockLogger<T>() where T : class => Substitute.For<ILogger<T>>();

    [Fact]
    public async Task HoverHandler_Mql4_Returns_MarkupContent_ShapeAsync()
    {
        // Arrange - MQL4 code using a builtin with a known signature and example
        var content = "void OnTick()\n{\n    OrderSend(Symbol(), OP_BUY, 1.0, Ask, 3, 0, 0, \"c\", 0, 0, clrBlue);\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverMql4.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(2, 5) // on OrderSend
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert - the response must use MarkupContent (not MarkedString)
            Assert.NotNull(result);
            var hover = Assert.IsType<Hover>(result);

            // S-006: verify MarkupContent shape, not MarkedStrings
            Assert.NotNull(hover.Contents);
            Assert.NotNull(hover.Contents.MarkupContent);
            Assert.Equal(MarkupKind.Markdown, hover.Contents.MarkupContent!.Kind);

            // Content should include the builtin name and MQL4 context
            var contentString = hover.Contents.MarkupContent!.Value;
            Assert.Contains("OrderSend", contentString);
            Assert.Contains("MQL4 Built-in", contentString);

            // The markdown fence should use the mql4 language tag
            Assert.Contains("```mql4", contentString);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #55 regression: a cursor on a USE of a function-local variable must
    /// hover the local (via the FindSymbolDefinition refinement), not the
    /// containing function. Companion: the cursor on the function's NAME still
    /// hovers the function.
    /// </summary>
    [Fact]
    public async Task HoverHandler_LocalUse_HoversLocal_NotContainingFunctionAsync()
    {
        // Arrange - a function with a distinctly named local variable
        var content = "void OnTick()\n{\n    int innerTicks = 0;\n    innerTicks = innerTicks + 1;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverLocal.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            // Cursor on the innerTicks use inside the body (line 3).
            var useRequest = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(3, 5)
            };

            var useResult = await handler.Handle(useRequest, CancellationToken.None);

            // Assert - hover shows the local variable, not the containing OnTick.
            Assert.NotNull(useResult);
            var useContent = useResult!.Contents.MarkupContent!.Value;
            Assert.Contains("## innerTicks", useContent);
            Assert.Contains("**Kind:** Variable", useContent);
            Assert.DoesNotContain("## OnTick", useContent);

            // Companion: cursor on the function's NAME still hovers OnTick.
            var nameRequest = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(0, 5)
            };

            var nameResult = await handler.Handle(nameRequest, CancellationToken.None);

            Assert.NotNull(nameResult);
            var nameContent = nameResult!.Contents.MarkupContent!.Value;
            Assert.Contains("## OnTick", nameContent);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #182 regression: hovering a user-declared local whose name matches a
    /// builtin case-insensitively must show the user declaration, not the builtin
    /// pseudo-hover (registry type/signature/description).
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql4_LocalNamedLikeBuiltin_HoversUserSymbolAsync()
    {
        // Arrange - local 'ask' shadows the builtin variable 'Ask'
        var content = "int OnInit()\n{\n    double ask = 1.0;\n    Print(ask);\n    return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverLocalShadowsBuiltinMql4.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            // Cursor on the local 'ask' use inside Print(...)
            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(3, 10)
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert - the user declaration wins over the builtin registry
            Assert.NotNull(result);
            var hoverContent = result!.Contents.MarkupContent!.Value;
            Assert.Contains("## ask", hoverContent);
            Assert.Contains("**Type:** User Defined", hoverContent);
            Assert.DoesNotContain("MQL4 Built-in", hoverContent);
            Assert.DoesNotContain("Current Ask price", hoverContent);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #182 (case-insensitivity): MQL resolves identifiers case-insensitively,
    /// so a declaration named "Ask" must cover a use spelled "ask" — the registry
    /// must not take over just because the spelling differs from the declaration.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql4_CaseVariantUseKeepsUserDeclarationAsync()
    {
        // Arrange - declaration 'Ask' (builtin spelling), use 'ask'
        var content = "int OnInit()\n{\n    double Ask = 1.0;\n    Print(ask);\n    return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverCaseVariantMql4.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(3, 10)
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            var hoverContent = result!.Contents.MarkupContent!.Value;
            Assert.Contains("## Ask", hoverContent);
            Assert.Contains("**Type:** User Defined", hoverContent);
            Assert.DoesNotContain("Current Ask price", hoverContent);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #182 (MQL5 symmetry): a user-declared local named like a builtin
    /// function must hover as the user symbol in an .mq5 document.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql5_LocalNamedLikeBuiltin_HoversUserSymbolAsync()
    {
        // Arrange - local 'bars' shadows the builtin function 'Bars()'
        var content = "int OnInit()\n{\n   int bars = 10;\n   Print(bars);\n   return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverLocalShadowsBuiltinMql5.mq5");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(
                MockLogger<HoverHandler>(),
                new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
                documentStore,
                new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

            // Cursor on the local 'bars' use inside Print(...)
            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(3, 9)
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert - the user declaration wins over the builtin registry
            Assert.NotNull(result);
            var hoverContent = result!.Contents.MarkupContent!.Value;
            Assert.Contains("## bars", hoverContent);
            Assert.Contains("**Type:** User Defined", hoverContent);
            Assert.DoesNotContain("MQL5 Built-in", hoverContent);
            Assert.DoesNotContain("int Bars(", hoverContent);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #180 regression: hovering a built-in FUNCTION must surface the
    /// registered signature from the dialect registry through the handler.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql4_BuiltinFunction_IncludesRegisteredSignatureAsync()
    {
        // Arrange - MQL4 code calling a builtin function with a known signature
        var content = "int OnInit()\n{\n    int total = HistoryTotal();\n    return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverBuiltinFnMql4.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(2, 16) // on HistoryTotal
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            var contentString = result!.Contents.MarkupContent!.Value;
            Assert.Contains("HistoryTotal", contentString);
            Assert.Contains("int HistoryTotal()", contentString);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #180 regression: hovering a built-in VARIABLE must surface the
    /// registered description from the dialect registry through the handler.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql4_BuiltinVariable_IncludesRegisteredDescriptionAsync()
    {
        // Arrange - MQL4 code reading a builtin variable with a known description
        var content = "void OnTick()\n{\n    double price = Ask;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverBuiltinVarMql4.mq4");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(MockLogger<HoverHandler>(), new Mql4AntlrParser(), documentStore);

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(2, 19) // on Ask
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            var contentString = result!.Contents.MarkupContent!.Value;
            Assert.Contains("Ask", contentString);
            Assert.Contains("Current Ask price", contentString);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #180 (MQL5 symmetry): hovering a built-in FUNCTION in an .mq5
    /// document must surface the registered MQL5 signature.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql5_BuiltinFunction_IncludesRegisteredSignatureAsync()
    {
        // Arrange - MQL5 code calling a builtin function with a known signature
        var content = "int OnInit()\n{\n    double squared = MathPow(2.0, 3.0);\n    return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverBuiltinFnMql5.mq5");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(
                MockLogger<HoverHandler>(),
                new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
                documentStore,
                new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(2, 21) // on MathPow
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            var contentString = result!.Contents.MarkupContent!.Value;
            Assert.Contains("MathPow", contentString);
            Assert.Contains("double MathPow(double base, double exponent)", contentString);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }

    /// <summary>
    /// Issue #180 (MQL5 symmetry): hovering a built-in VARIABLE in an .mq5
    /// document must surface the registered MQL5 description.
    /// </summary>
    [Fact]
    public async Task HoverHandler_Mql5_BuiltinVariable_IncludesRegisteredDescriptionAsync()
    {
        // Arrange - MQL5 code reading a builtin variable with a known description
        var content = "int OnInit()\n{\n    int digits = _Digits;\n    return 0;\n}\n";
        var testFilePath = Path.Combine(Path.GetTempPath(), "TestHoverBuiltinVarMql5.mq5");
        File.WriteAllText(testFilePath, content);

        try
        {
            var documentStore = new OpenDocumentStore();
            var handler = new HoverHandler(
                MockLogger<HoverHandler>(),
                new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
                documentStore,
                new IMqlBuiltins[] { new Mql4BuiltinsAdapter(), new Mql5Builtins() });

            var request = new HoverParams
            {
                TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(testFilePath)),
                Position = new Position(2, 17) // on _Digits
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.NotNull(result);
            var contentString = result!.Contents.MarkupContent!.Value;
            Assert.Contains("_Digits", contentString);
            Assert.Contains("Number of decimal places in the current symbol prices", contentString);
        }
        finally
        {
            if (File.Exists(testFilePath)) File.Delete(testFilePath);
        }
    }
}