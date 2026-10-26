using Xunit;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Tests for DiagnosticHandler
/// Compliant with LSP 3.17 specification for textDocument/diagnostic
/// </summary>
public class DiagnosticHandlerTests
{
    private readonly string _testFilePath;

    public DiagnosticHandlerTests()
    {
        _testFilePath = GetFixtureFilePath("samples/ExpertAdvisor.mq4");
    }

    #region Constructor Tests

    [Fact]
    public void DiagnosticHandler_CanBeInstantiated()
    {
        // Arrange & Act
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        // Assert
        Assert.NotNull(handler);
    }

    #endregion

    #region File Not Found Tests

    [Fact]
    public async Task Handle_FileNotFound_ReturnsEmptyDiagnosticReportAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        // Use a non-existent file path
        var documentUri = DocumentUri.FromFileSystemPath("/nonexistent/file.mq4");
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.Equal(DocumentDiagnosticReportKind.Full, report.Kind);
        Assert.Null(report.ResultId); // No ResultId when file not found
        Assert.Empty(report.Items);
    }

    #endregion

    #region Valid File Tests

    [Fact]
    public async Task Handle_ValidFile_ReturnsDiagnosticReportWithResultIdAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var parser = new Mql4AntlrParser();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var documentUri = DocumentUri.FromFileSystemPath(_testFilePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.Equal(DocumentDiagnosticReportKind.Full, report.Kind);
        // ResultId should be generated when file exists and is read successfully
        Assert.NotNull(report.ResultId);
        Assert.NotEmpty(report.ResultId);
    }

    [Fact]
    public async Task Handle_RealFile_CanProcessFileWithoutErrorsAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var parser = new Mql4AntlrParser();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var documentUri = DocumentUri.FromFileSystemPath(_testFilePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.NotNull(report.Items);
        // This file doesn't have underscore-prefixed variables, so Items may be empty
        // The test verifies the handler can process the file without errors
        Assert.NotNull(report.ResultId);
    }

    [Fact]
    public async Task Handle_SameFileTwice_ReturnsConsistentReportAsync()
    {
        // Skip if running in CI (this test can be flaky due to timing)

        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var parser = new Mql4AntlrParser();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var documentUri = DocumentUri.FromFileSystemPath(_testFilePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result1 = await handler.Handle(request, CancellationToken.None);
        var result2 = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);

        var report1 = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result1);
        var report2 = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result2);

        // Both should have kind = Full
        Assert.Equal(DocumentDiagnosticReportKind.Full, report1.Kind);
        Assert.Equal(DocumentDiagnosticReportKind.Full, report2.Kind);

        // Both should have ResultId
        Assert.NotNull(report1.ResultId);
        Assert.NotNull(report2.ResultId);

        // Both should have a valid report structure
        Assert.NotNull(report1.Items);
        Assert.NotNull(report2.Items);
        // ResultId changes on each call (new GUID generated)
        Assert.NotEqual(report1.ResultId, report2.ResultId);
    }

    [Fact]
    public async Task Handle_RealFile_CanReadAndProcessFileAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        
        var parser = new Mql4AntlrParser();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        // Verify file exists before making request
        Assert.True(File.Exists(_testFilePath), $"Test file should exist at: {_testFilePath}");

        var documentUri = DocumentUri.FromFileSystemPath(_testFilePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.Equal(DocumentDiagnosticReportKind.Full, report.Kind);
        // The file was read and processed, ResultId should be generated
        Assert.NotNull(report.ResultId);
        Assert.NotEmpty(report.ResultId);
        Assert.NotNull(report.Items);
    }

    #endregion

    #region Documents With Diagnostics Tests

    [Fact]
    public async Task Handle_FileWithDiagnostics_ReturnsNonEmptyDiagnosticItemsAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var parser = new Mql4AntlrParser();
        var documentStore = new OpenDocumentStore();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var fileWithDiagnosticsPath = GetFixtureFilePath("samples/WithDiagnostics.mq4");
        Assert.True(File.Exists(fileWithDiagnosticsPath), $"Test file should exist at: {fileWithDiagnosticsPath}");

        var documentUri = DocumentUri.FromFileSystemPath(fileWithDiagnosticsPath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        Assert.Equal(DocumentDiagnosticReportKind.Full, report.Kind);
        Assert.NotNull(report.ResultId);
        // File with intentional errors should have diagnostics
        Assert.NotEmpty(report.Items);
    }

    [Fact]
    public async Task Handle_FileWithDiagnostics_DetectsTypoErrorsAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var parser = new Mql4AntlrParser();
        var documentStore = new OpenDocumentStore();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var fileWithDiagnosticsPath = GetFixtureFilePath("samples/WithDiagnostics.mq4");
        var documentUri = DocumentUri.FromFileSystemPath(fileWithDiagnosticsPath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        // Should have at least 2 typo errors (UnkownVariable and UnkownFunction)
        var typoDiagnostics = report.Items.Where(d => d.Code?.String == "1001").ToList();
        Assert.NotEmpty(typoDiagnostics);
        Assert.True(typoDiagnostics.Count >= 2, $"Expected at least 2 typo diagnostics, got {typoDiagnostics.Count}");
        // All should be errors
        Assert.All(typoDiagnostics, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
    }

    [Fact]
    public async Task Handle_FileWithDiagnostics_DetectsEmptyOnInitWarningAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var parser = new Mql4AntlrParser();
        var documentStore = new OpenDocumentStore();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var fileWithDiagnosticsPath = GetFixtureFilePath("samples/WithDiagnostics.mq4");
        var documentUri = DocumentUri.FromFileSystemPath(fileWithDiagnosticsPath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        // Should have warning for empty OnInit
        var onInitWarning = report.Items.FirstOrDefault(d => d.Code?.String == "1002");
        Assert.NotNull(onInitWarning);
        Assert.Equal(DiagnosticSeverity.Warning, onInitWarning.Severity);
        Assert.Contains("OnInit", onInitWarning.Message);
    }

    [Fact]
    public async Task Handle_FileWithDiagnostics_DoesNotFlagUnderscoreVariablesAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var parser = new Mql4AntlrParser();
        var documentStore = new OpenDocumentStore();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var fileWithDiagnosticsPath = GetFixtureFilePath("samples/WithDiagnostics.mq4");
        var documentUri = DocumentUri.FromFileSystemPath(fileWithDiagnosticsPath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        // Issue #109 fourth sweep: underscore-prefixed variable names are legal
        // MQL identifiers (Botlidator uses `string _str`, `uchar& _arr[]`) and
        // MetaEditor does not warn on them — the legacy hint rule produced
        // false positives on real code and was removed.
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        var underscoreHint = report.Items.FirstOrDefault(d => d.Code?.String == "1003");
        Assert.Null(underscoreHint);
    }

    [Fact]
    public async Task Handle_FileWithDiagnostics_ContainsAllDiagnosticTypesAsync()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var parser = new Mql4AntlrParser();
        var documentStore = new OpenDocumentStore();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        serviceProviderMock.GetService(typeof(Mql4AntlrParser)).Returns(parser);
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var fileWithDiagnosticsPath = GetFixtureFilePath("samples/WithDiagnostics.mq4");
        var documentUri = DocumentUri.FromFileSystemPath(fileWithDiagnosticsPath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(documentUri) };

        // Act
        var result = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(result);
        // Verify all diagnostic codes are present (DiagnosticCode has String property)
        // A-007: codes are now numeric (MQL4 base 1000, MQL5 base 5000).
        var codes = report.Items.Select(d => d.Code?.String).Distinct().ToList();
        Assert.Contains("1001", codes); // Typo errors
        Assert.Contains("1002", codes); // Empty OnInit warning
        Assert.DoesNotContain("1003", codes); // underscore hint removed (issue #109 fourth sweep)
        // Verify we have diagnostics of different severities
        Assert.Contains(DiagnosticSeverity.Error, report.Items.Select(d => d.Severity));
        Assert.Contains(DiagnosticSeverity.Warning, report.Items.Select(d => d.Severity));
        Assert.DoesNotContain(DiagnosticSeverity.Hint, report.Items.Select(d => d.Severity));
    }

    #endregion

    #region Capability Tests

    [Fact]
    public void GetRegistrationOptions_ReturnsValidDocumentSelector()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        var capability = new DiagnosticClientCapabilities();
        var clientCapabilities = new ClientCapabilities();

        // Act
        var options = handler.GetRegistrationOptions(capability, clientCapabilities);

        // Assert
        Assert.NotNull(options);
        Assert.NotNull(options.DocumentSelector);
        Assert.NotEmpty(options.DocumentSelector);
        // Verify document selector includes MQL4 patterns
        var patterns = options.DocumentSelector.Select(f => f.Pattern).ToList();
        Assert.Contains("**/*.mq4", patterns);
        Assert.Contains("**/*.mqh", patterns);
    }

    [Fact]
    public void Handler_ImplementsIDocumentDiagnosticHandler()
    {
        // Arrange & Act
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        // Assert
        Assert.IsAssignableFrom<IDocumentDiagnosticHandler>(handler);
    }

    [Fact]
    public void GetRegistrationOptions_NotNullForNullCapabilities()
    {
        // Arrange
        var loggerMock = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProviderMock = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(loggerMock, serviceProviderMock, documentStore);

        // Act
        var options = handler.GetRegistrationOptions(null!, null!);

        // Assert
        Assert.NotNull(options);
        Assert.NotNull(options.DocumentSelector);
    }

    #endregion

    #region Helper Methods

    private static string GetBaseFixturesPath()
    {
        var basePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        var fixturesPath = Path.Combine(basePath ?? "", "..", "..", "..", "..", "tests", "fixtures");
        return Path.GetFullPath(fixturesPath);
    }

    private static string GetFixtureFilePath(string fileName)
    {
        return Path.Combine(GetBaseFixturesPath(), fileName);
    }

    #endregion
}