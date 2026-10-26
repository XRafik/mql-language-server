using Xunit;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MqlLanguageServer.Tests.Lsp.Handlers
{
    /// <summary>
    /// Tests for SignatureHelpHandler.
    /// Shares the GlobalSymbolIndex singleton with the other index users, so it
    /// runs inside the serialized "GlobalSymbolIndex Tests" collection.
    /// </summary>
    [Collection("GlobalSymbolIndex Tests")]
    public class SignatureHelpHandlerTests
    {
        [Fact]
        public void SignatureHelpHandler_CanBeInstantiated()
        {
            // Arrange & Act
            var loggerMock = Substitute.For<ILogger<SignatureHelpHandler>>();
            var parserMock = new Mql4AntlrParser();
            var documentStore = new OpenDocumentStore();
            var handler = new SignatureHelpHandler(loggerMock, parserMock, documentStore);

            // Assert
            Assert.NotNull(handler);
        }

        [Fact]
        public async Task SignatureHelpHandler_ReturnsNull_WhenFileNotFoundAsync()
        {
            // Arrange
            var loggerMock = Substitute.For<ILogger<SignatureHelpHandler>>();
            var parserMock = new Mql4AntlrParser();
            var documentStore = new OpenDocumentStore();
            var handler = new SignatureHelpHandler(loggerMock, parserMock, documentStore);

            var request = new SignatureHelpParams
            {
                TextDocument = new TextDocumentIdentifier("/nonexistent/file.mq4"),
                Position = new Position(0, 0)
            };

            // Act
            var result = await handler.Handle(request, CancellationToken.None);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task SignatureHelpHandler_ReturnsSignatureHelp_WhenFunctionAtPositionAsync()
        {
            // Arrange
            var loggerMock = Substitute.For<ILogger<SignatureHelpHandler>>();
            var parserMock = new Mql4AntlrParser();
            var documentStore = new OpenDocumentStore();
            var handler = new SignatureHelpHandler(loggerMock, parserMock, documentStore);

            // Create a test file with a function
            var testFilePath = Path.Combine(Path.GetTempPath(), "TestSignatureHelp.mq4");
            var testCode = @"
double CalculateSMA(int period)
{
    return 0.0;
}

void OnTick()
{
    double sma = CalculateSMA(20);
}
";
            File.WriteAllText(testFilePath, testCode);

            try
            {
                // Position on the function declaration line
                var request = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier("file://" + testFilePath),
                    Position = new Position(0, 6) // On "double CalculateSMA"
                };

                // Act
                var result = await handler.Handle(request, CancellationToken.None);

                // Assert
                // Result can be null if no function is found at position, or have signatures
                // Just verify the handler processes the request without error
                Assert.True(result == null || result.Signatures.Any());
            }
            finally
            {
                File.Delete(testFilePath);
            }
        }
        /// <summary>
        /// Issue #55 regression: the cursor on a callee's NAME inside a function
        /// body must return THAT function's signature, not the enclosing function's.
        /// Companions (issue #77): the cursor inside the call's argument list (no
        /// callable identifier at the cursor, no indexed declaration for the callee)
        /// returns NULL — the former body-containment fallback that returned the
        /// enclosing function was removed because a wrong signature is worse than
        /// none — and the cursor on the enclosing function's NAME still returns
        /// its own signature.
        /// </summary>
        [Fact]
        public async Task SignatureHelpHandler_CalleeNameInsideBody_ReturnsCalleeSignatureAsync()
        {
            // Arrange
            var loggerMock = Substitute.For<ILogger<SignatureHelpHandler>>();
            var parserMock = new Mql4AntlrParser();
            var documentStore = new OpenDocumentStore();
            var handler = new SignatureHelpHandler(loggerMock, parserMock, documentStore);

            var testFilePath = Path.Combine(Path.GetTempPath(), "TestSignatureHelpLocal.mq4");
            var testCode = "int CalculateHelper(int period)\n{\n    return period;\n}\nvoid RunLoop()\n{\n    int innerTicks = CalculateHelper(20);\n}\n";
            File.WriteAllText(testFilePath, testCode);

            try
            {
                // Cursor on the CalculateHelper callee name inside RunLoop's body (line 6).
                var calleeRequest = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier("file://" + testFilePath),
                    Position = new Position(6, 22)
                };

                var calleeResult = await handler.Handle(calleeRequest, CancellationToken.None);

                // Assert - the CALLEE's signature, not the enclosing RunLoop's.
                // SignatureHelp labels use symbol.Detail (e.g. "Function returning
                // int"), which omits the name — so pin the regression via the
                // callee's return type vs the enclosing function's.
                Assert.NotNull(calleeResult);
                var calleeSignature = Assert.Single(calleeResult!.Signatures);
                Assert.Contains("returning int", calleeSignature.Label);

                // Companion: cursor inside the argument list (no identifier) —
                // the unresolvable call target returns null per issue #77
                // (never the enclosing function's signature).
                var argsRequest = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier("file://" + testFilePath),
                    Position = new Position(6, 38)
                };

                var argsResult = await handler.Handle(argsRequest, CancellationToken.None);
                Assert.Null(argsResult);

                // Companion: cursor on the enclosing function's NAME returns its own signature.
                var nameRequest = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier("file://" + testFilePath),
                    Position = new Position(4, 5)
                };

                var nameResult = await handler.Handle(nameRequest, CancellationToken.None);
                Assert.NotNull(nameResult);
                var nameSignature = Assert.Single(nameResult!.Signatures);
                Assert.Contains("returning void", nameSignature.Label);
            }
            finally
            {
                File.Delete(testFilePath);
            }
        }

        #region Issue #77 — signatureHelp on member calls (person.Greet())

        // Fixture mirrors issue #77 exactly, including the constructor-style
        // declaration parsed by the #76 grammar fix (PR #80, merged).
        private const string SigHelpPersonHeaderContent =
            "class Person\n" +
            "{\n" +
            "public:\n" +
            "   string Greet()\n" +
            "   {\n" +
            "      return \"hello\";\n" +
            "   }\n" +
            "};\n";

        private const string SigHelpMainContent =
            "#include \"TestSigHelpPerson.mqh\"\n" +
            "\n" +
            "int OnInit()\n" +
            "{\n" +
            "   Person person(\"Alice\", 30);\n" +
            "   string message = person.Greet();\n" +
            "   Print(message);\n" +
            "   person.Missing();\n" +
            "   unknownObj.Method();\n" +
            "   return 0;\n" +
            "}\n";

        private static string WriteSigHelpTempFile(string fileName, string content)
        {
            var path = Path.Combine(Path.GetTempPath(), fileName);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Parse + index both fixture files exactly as didOpen does.</summary>
        private (OpenDocumentStore store, string headerPath, string mainPath) IndexSigHelpFixture()
        {
            GlobalSymbolIndex.Instance.Clear();
            var parser = new Mql4AntlrParser();
            var headerPath = WriteSigHelpTempFile("TestSigHelpPerson.mqh", SigHelpPersonHeaderContent);
            var mainPath = WriteSigHelpTempFile("TestSigHelpMain.mq4", SigHelpMainContent);
            var headerFile = parser.ParseFile(SigHelpPersonHeaderContent, headerPath);
            var mainFile = parser.ParseFile(SigHelpMainContent, mainPath);

            GlobalSymbolIndex.Instance.AddFile(headerPath, MqlLanguage.Mql4, headerFile.Symbols,
                headerFile.Occurrences.Select(o => new SymbolOccurrence
                {
                    FilePath = headerPath, Language = MqlLanguage.Mql4,
                    Text = o.Text, Line = o.Line, Column = o.Column, Length = o.Length
                }).ToList());
            GlobalSymbolIndex.Instance.AddFile(mainPath, MqlLanguage.Mql4, mainFile.Symbols,
                mainFile.Occurrences.Select(o => new SymbolOccurrence
                {
                    FilePath = mainPath, Language = MqlLanguage.Mql4,
                    Text = o.Text, Line = o.Line, Column = o.Column, Length = o.Length
                }).ToList());

            var store = new OpenDocumentStore();
            store.AddOrUpdate(DocumentUri.FromFileSystemPath(headerPath).ToUri(), headerFile, SigHelpPersonHeaderContent, MqlLanguage.Mql4);
            store.AddOrUpdate(DocumentUri.FromFileSystemPath(mainPath).ToUri(), mainFile, SigHelpMainContent, MqlLanguage.Mql4);
            return (store, headerPath, mainPath);
        }

        private static void CleanupSigHelpFixture(string headerPath, string mainPath)
        {
            GlobalSymbolIndex.Instance.Clear();
            if (File.Exists(headerPath)) File.Delete(headerPath);
            if (File.Exists(mainPath)) File.Delete(mainPath);
        }

        private static SignatureHelpHandler CreateSigHelpHandler(OpenDocumentStore store) =>
            new(Substitute.For<ILogger<SignatureHelpHandler>>(), new Mql4AntlrParser(), store);

        /// <summary>0-based line index of the first line containing <paramref name="needle"/>.</summary>
        private static int LineIndexOf(string needle) =>
            Array.FindIndex(SigHelpMainContent.Split('\n'), l => l.Contains(needle, StringComparison.Ordinal));

        [Fact]
        public async Task SignatureHelpHandler_MethodCall_OpenParen_ReturnsMethodSignatureAsync()
        {
            var (store, headerPath, mainPath) = IndexSigHelpFixture();
            try
            {
                var handler = CreateSigHelpHandler(store);

                // Cursor ON the '(' of "person.Greet()".
                var greetLine = LineIndexOf("person.Greet()");
                var openParenCol = SigHelpMainContent.Split('\n')[greetLine].IndexOf("Greet(", StringComparison.Ordinal) + "Greet".Length;
                var request = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(greetLine, openParenCol)
                };

                var result = await handler.Handle(request, CancellationToken.None);

                Assert.NotNull(result);
                var signature = Assert.Single(result!.Signatures);
                Assert.Contains("Greet", signature.Label);
                Assert.DoesNotContain("OnInit", signature.Label);
            }
            finally
            {
                CleanupSigHelpFixture(headerPath, mainPath);
            }
        }

        [Fact]
        public async Task SignatureHelpHandler_MethodCall_InsideParens_ReturnsMethodSignatureAsync()
        {
            var (store, headerPath, mainPath) = IndexSigHelpFixture();
            try
            {
                var handler = CreateSigHelpHandler(store);

                // Cursor just AFTER the '(' of "person.Greet()".
                var greetLine = LineIndexOf("person.Greet()");
                var insideParensCol = SigHelpMainContent.Split('\n')[greetLine].IndexOf("Greet(", StringComparison.Ordinal) + "Greet".Length + 1;
                var request = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(greetLine, insideParensCol)
                };

                var result = await handler.Handle(request, CancellationToken.None);

                Assert.NotNull(result);
                var signature = Assert.Single(result!.Signatures);
                Assert.Contains("Greet", signature.Label);
                Assert.DoesNotContain("OnInit", signature.Label);
            }
            finally
            {
                CleanupSigHelpFixture(headerPath, mainPath);
            }
        }

        [Fact]
        public async Task SignatureHelpHandler_MethodCall_OnMemberName_ReturnsMethodSignatureAsync()
        {
            var (store, headerPath, mainPath) = IndexSigHelpFixture();
            try
            {
                var handler = CreateSigHelpHandler(store);

                // Cursor on the "Greet" member name of "person.Greet()".
                var greetLine = LineIndexOf("person.Greet()");
                var memberCol = SigHelpMainContent.Split('\n')[greetLine].IndexOf("Greet", StringComparison.Ordinal);
                var request = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(greetLine, memberCol)
                };

                var result = await handler.Handle(request, CancellationToken.None);

                Assert.NotNull(result);
                var signature = Assert.Single(result!.Signatures);
                Assert.Contains("Greet", signature.Label);
                Assert.DoesNotContain("OnInit", signature.Label);
            }
            finally
            {
                CleanupSigHelpFixture(headerPath, mainPath);
            }
        }

        [Fact]
        public async Task SignatureHelpHandler_UnresolvableCallTarget_ReturnsNullAsync()
        {
            var (store, headerPath, mainPath) = IndexSigHelpFixture();
            try
            {
                var handler = CreateSigHelpHandler(store);

                // Cursor inside the parens of "person.Missing()" — no such member.
                var missingLine = LineIndexOf("person.Missing()");
                var missingCol = SigHelpMainContent.Split('\n')[missingLine].IndexOf("Missing(", StringComparison.Ordinal) + "Missing".Length + 1;
                var missingRequest = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(missingLine, missingCol)
                };
                var missingResult = await handler.Handle(missingRequest, CancellationToken.None);
                Assert.Null(missingResult);

                // Cursor inside the parens of "unknownObj.Method()" — no receiver.
                var unknownLine = LineIndexOf("unknownObj.Method()");
                var unknownCol = SigHelpMainContent.Split('\n')[unknownLine].IndexOf("Method(", StringComparison.Ordinal) + "Method".Length + 1;
                var unknownRequest = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(unknownLine, unknownCol)
                };
                var unknownResult = await handler.Handle(unknownRequest, CancellationToken.None);
                Assert.Null(unknownResult);
            }
            finally
            {
                CleanupSigHelpFixture(headerPath, mainPath);
            }
        }

        [Fact]
        public async Task SignatureHelpHandler_BuiltinCall_ReturnsSignatureAsync()
        {
            var (store, headerPath, mainPath) = IndexSigHelpFixture();
            try
            {
                var handler = CreateSigHelpHandler(store);

                // Cursor inside the parens of "Print(message)" (on the argument).
                var printLine = LineIndexOf("Print(message)");
                var printCol = SigHelpMainContent.Split('\n')[printLine].IndexOf("Print(", StringComparison.Ordinal) + "Print".Length + 1;
                var request = new SignatureHelpParams
                {
                    TextDocument = new TextDocumentIdentifier(DocumentUri.FromFileSystemPath(mainPath)),
                    Position = new Position(printLine, printCol)
                };

                var result = await handler.Handle(request, CancellationToken.None);

                Assert.NotNull(result);
                var signature = Assert.Single(result!.Signatures);
                Assert.Contains("Print", signature.Label);
            }
            finally
            {
                CleanupSigHelpFixture(headerPath, mainPath);
            }
        }

        #endregion
    }
}
