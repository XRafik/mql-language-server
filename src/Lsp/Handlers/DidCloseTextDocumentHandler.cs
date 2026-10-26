using System;
using System.Threading;
using System.Threading.Tasks;

using MediatR;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for didClose text document notification
/// </summary>
public class DidCloseTextDocumentHandler : LanguageAwareHandlerBase<DidCloseTextDocumentParams, Unit>, IDidCloseTextDocumentHandler
{
    private readonly ILogger<DidCloseTextDocumentHandler> _logger;

    public DidCloseTextDocumentHandler(
        ILogger<DidCloseTextDocumentHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore openFiles,
        IMqlBuiltins[] builtins)
        : base(languageService, openFiles, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("DidCloseTextDocumentHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DidCloseTextDocumentHandler(ILogger<DidCloseTextDocumentHandler> logger, OpenDocumentStore openFiles)
        : this(logger,
               new MqlLanguageService(new Mql4AntlrParser(), new Mql5AntlrParser()),
               openFiles,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public TextDocumentCloseRegistrationOptions GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentCloseRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }

    public Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Task.FromResult(HandleCore(request, cancellationToken));
    }

    /// <summary>
    /// Issue #116: synchronous forwarding entry point used by the built-in
    /// TextDocumentSync handler registered in Program.cs. OmniSharp 0.19.9
    /// routes each textDocument/didClose notification to EXACTLY ONE of the two
    /// handlers registered for the method, and which one wins is decided per
    /// process; this exposes the same synchronous core so the real logic runs
    /// regardless of routing (see DidOpenTextDocumentHandler.HandleSync).
    /// </summary>
    public void HandleSync(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        HandleCore(request, cancellationToken);
    }

    private Unit HandleCore(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return HandleForLanguage(request, language, cancellationToken);
    }

    protected override Unit HandleForLanguage(DidCloseTextDocumentParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri.ToUri();

            _logger.LogDebug("Closing document: {DocumentUri}", documentUri);

            _documentStore.Remove(documentUri);

            _logger.LogDebug("Removed document from open files list");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling didClose for {Uri}", request.TextDocument.Uri);
        }

        return Unit.Value;
    }
}
