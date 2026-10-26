using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Models;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;

namespace MqlLanguageServer.Lsp.Handlers;

public class DiagnosticHandler : LanguageAwareHandlerBase<DocumentDiagnosticParams, RelatedDocumentDiagnosticReport>, IDocumentDiagnosticHandler
{
    private readonly ILogger<DiagnosticHandler> _logger;
    private readonly IServiceProvider? _serviceProvider;

    /// <summary>
    /// Optional semantic analyzer (issue #28). Defaults to null and is created
    /// internally on first use, following the optional-dependency style of the
    /// backward-compatible test constructors.
    /// </summary>
    private readonly SemanticAnalyzer? _semanticAnalyzer;

    public DiagnosticHandler(
        ILogger<DiagnosticHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        SemanticAnalyzer? semanticAnalyzer = null)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _semanticAnalyzer = semanticAnalyzer;
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DiagnosticHandler(
        ILogger<DiagnosticHandler> logger,
        IServiceProvider serviceProvider,
        OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(
                   serviceProvider?.GetService(typeof(Mql4AntlrParser)) as Mql4AntlrParser ?? new Mql4AntlrParser(),
                   serviceProvider?.GetService(typeof(Mql5AntlrParser)) as Mql5AntlrParser ?? new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
        _serviceProvider = serviceProvider;
    }

    public Task<RelatedDocumentDiagnosticReport> Handle(
        DocumentDiagnosticParams request,
        CancellationToken cancellationToken)
    {
        var language = ResolveLanguage(request.TextDocument.Uri.ToUri());
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    private RelatedFullDocumentDiagnosticReport GenerateReport(MqlFile? file, string content, MqlLanguage language, CancellationToken token, string? documentPath)
    {
        // Issue #91: the rule set lives in the shared DocumentDiagnostics service
        // (identical output to the push channel — one rule set, two channels).
        var diagnostics = DocumentDiagnostics.Generate(
            _logger, file, content, language, token, documentPath, _semanticAnalyzer, SymbolIndex.Index);

        return new RelatedFullDocumentDiagnosticReport
        {
            ResultId = Guid.NewGuid().ToString(),
            Items = diagnostics
        };
    }

    /// <summary>
    /// Budget for the fallback parse of a document that is not in the store
    /// (issue #20: the pre-parse guard plus this timeout bound the worst
    /// case; oversized input is rejected by the parser as a regular
    /// SyntaxError, which the generation publishes).
    /// </summary>
    internal const int ParseTimeoutMilliseconds = 2000;

    /// <summary>
    /// Budget for the FULL rule-set generation over an already-parsed model
    /// (issue #150). Deliberately much larger than the parse budget: the
    /// generation includes cross-file suppression against the workspace
    /// index, which on real-world projects (documents with thousands of
    /// symbols indexed from sibling sources) can legitimately take longer
    /// than a bare parse. Before #150 this shared the 2s parse budget and
    /// the overrun was answered with an EMPTY report — indistinguishable
    /// from a clean document, which silently killed the pull channel for
    /// exactly the projects where diagnostics matter most.
    /// </summary>
    internal const int GenerationTimeoutMilliseconds = 10000;

    protected override RelatedDocumentDiagnosticReport HandleForLanguage(DocumentDiagnosticParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var uri = request.TextDocument.Uri.ToUri();

            string? content = null;
            MqlFile? mqlFile = null;

            if (_documentStore.TryGetValue(uri, out var storedFile, out var storedContent, out _))
            {
                mqlFile = storedFile;
                content = storedContent;
            }

            if (string.IsNullOrEmpty(content))
            {
                var fsPath = request.TextDocument.Uri.GetFileSystemPath();
                if (!string.IsNullOrEmpty(fsPath) && System.IO.File.Exists(fsPath))
                {
                    try
                    {
                        content = SourceFileReader.ReadAllText(fsPath);
                    }
                    catch (System.IO.IOException) { }
                }
            }

            if (string.IsNullOrEmpty(content))
            {
                return CreateEmptyReport();
            }

            if (mqlFile == null)
            {
                var parser = ResolveParser(language);
                var filePath = request.TextDocument.Uri.GetFileSystemPath() ?? (language == MqlLanguage.Mql5 ? "unknown.mq5" : "unknown.mq4");
                // Issue #20: thread the (linked) cancellation token into the
                // parse so the parse budget can abort between pre-scan, lexing,
                // and the recursive-descent pass. Oversized/deeply nested
                // input is rejected by the parser's pre-parse guard as a
                // regular SyntaxError, which GenerateDiagnostics publishes.
                using var parseCts = new CancellationTokenSource(ParseTimeoutMilliseconds);
                using var linkedParseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, parseCts.Token);
                mqlFile = parser.ParseFile(content, filePath, linkedParseCts.Token);
                _documentStore.AddOrUpdate(uri, mqlFile, content, language);
            }

            // Issue #150: the generation budget is separate from (and much
            // larger than) the parse budget, and its overrun degrades to
            // parse-only diagnostics instead of an empty report.
            try
            {
                using var generationCts = new CancellationTokenSource(GenerationTimeoutMilliseconds);
                using var linkedGenerationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generationCts.Token);
                return GenerateReport(mqlFile, content, language, linkedGenerationCts.Token, request.TextDocument.Uri.GetFileSystemPath());
            }
            catch (OperationCanceledException)
            {
                // Issue #150: a budget overrun must NEVER look like "no
                // problems". Parse-level syntax errors are still real signal
                // (and are what the 2s-era path silently dropped), so answer
                // with them rather than an empty report.
                _logger.LogWarning(
                    "Diagnostic generation exceeded {BudgetMs}ms for {Uri}; answering with parse-only diagnostics.",
                    GenerationTimeoutMilliseconds, request.TextDocument.Uri);
                return CreateParseOnlyReport(mqlFile, language);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Diagnostic parse timed out.");
            return CreateEmptyReport();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnostic handler failed.");
            return CreateEmptyReport();
        }
    }

    private RelatedFullDocumentDiagnosticReport CreateEmptyReport()
    {
        return new RelatedFullDocumentDiagnosticReport
        {
            ResultId = null,
            Items = Array.Empty<Diagnostic>()
        };
    }

    /// <summary>
    /// Issue #150 degraded answer: parse-level diagnostics only. Non-empty
    /// whenever the parse found real syntax errors, so a budget overrun can
    /// never masquerade as a clean document.
    /// </summary>
    private RelatedFullDocumentDiagnosticReport CreateParseOnlyReport(MqlFile? mqlFile, MqlLanguage language)
    {
        List<Diagnostic> parseDiagnostics;
        try
        {
            parseDiagnostics = DocumentDiagnostics.GenerateSyntaxErrors(
                mqlFile, language, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Parse-only diagnostic fallback failed.");
            parseDiagnostics = new List<Diagnostic>();
        }

        return new RelatedFullDocumentDiagnosticReport
        {
            ResultId = Guid.NewGuid().ToString(),
            Items = parseDiagnostics
        };
    }

    public DiagnosticsRegistrationOptions GetRegistrationOptions(
        DiagnosticClientCapabilities capability,
        ClientCapabilities clientCapabilities)
    {
        return new DiagnosticsRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector(),
            InterFileDependencies = false,
            WorkspaceDiagnostics = false
        };
    }

}
