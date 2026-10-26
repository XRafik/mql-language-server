using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handles textDocument/foldingRange requests.
/// </summary>
public class FoldingRangeHandler : LanguageAwareHandlerBase<FoldingRangeRequestParam, Container<FoldingRange>?>, IFoldingRangeHandler
{
    private readonly ILogger<FoldingRangeHandler> _logger;

    public FoldingRangeHandler(
        ILogger<FoldingRangeHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : base(languageService, documentStore, builtins)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("FoldingRangeHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public FoldingRangeHandler(ILogger<FoldingRangeHandler> logger, Mql4AntlrParser parser, OpenDocumentStore documentStore)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() })
    {
    }

    public Task<Container<FoldingRange>?> Handle(FoldingRangeRequestParam request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override Container<FoldingRange>? HandleForLanguage(FoldingRangeRequestParam request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            _logger.LogDebug("Processing folding range request for: {DocumentUri} ({Language})", documentUri, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
            }

            var parser = ResolveParser(language);
            var uri = documentUri.ToUri();
            // Issue #88 (the #86 pattern): store-first — the stored content is
            // the exact text the stored model was parsed from (the editor
            // buffer), while the disk file can be stale for unsaved edits.
            // Reading disk text against the fresh model desynchronized
            // position-based resolution until a restart (#86).
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);
            var lines = content.Split('\n');

            var foldingRanges = new List<FoldingRange>();

            foreach (var symbol in mqlFile.Symbols.Where(s => s.Kind == SymbolKind.Function))
            {
                if (symbol.Range.Start.Line > 0 && symbol.Range.End.Line > symbol.Range.Start.Line)
                {
                    foldingRanges.Add(new FoldingRange
                    {
                        StartLine = symbol.Range.Start.Line - 1,
                        EndLine = symbol.Range.End.Line - 1,
                        Kind = FoldingRangeKind.Region,
                        StartCharacter = 0,
                        EndCharacter = 0
                    });
                }
            }

            for (int i = 0; i < lines.Length - 1; i++)
            {
                var line = lines[i].TrimEnd();

                if (line.EndsWith("{") || line.EndsWith("{ //") || line.EndsWith("{ /*"))
                {
                    var closingBraceLine = FindMatchingBraceLine(lines, i);
                    if (closingBraceLine > i + 1)
                    {
                        foldingRanges.Add(new FoldingRange
                        {
                            StartLine = i,
                            EndLine = closingBraceLine - 1,
                            Kind = FoldingRangeKind.Region,
                            StartCharacter = 0,
                            EndCharacter = 0
                        });
                    }
                }
            }

            _logger.LogDebug("Returning {Count} folding ranges", foldingRanges.Count);

            return new Container<FoldingRange>(foldingRanges);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing folding range for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    private int FindMatchingBraceLine(string[] lines, int openBraceLine)
    {
        var braceCount = 1;
        for (int i = openBraceLine + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            foreach (var c in line)
            {
                if (c == '{') braceCount++;
                else if (c == '}')
                {
                    braceCount--;
                    if (braceCount == 0) return i;
                }
            }
        }
        return lines.Length;
    }

    public FoldingRangeRegistrationOptions GetRegistrationOptions(FoldingRangeCapability capability, ClientCapabilities clientCapabilities)
    {
        return new FoldingRangeRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }
}
