using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MediatR;
using Microsoft.Extensions.Logging;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Mql5.Parser;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Handler for didOpen text document notification
/// </summary>
public class DidOpenTextDocumentHandler : LanguageAwareHandlerBase<DidOpenTextDocumentParams, Unit>, IDidOpenTextDocumentHandler
{
    private readonly ILogger<DidOpenTextDocumentHandler> _logger;
    private readonly DiagnosticPublisher? _diagnosticPublisher;

    public DidOpenTextDocumentHandler(
        ILogger<DidOpenTextDocumentHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore openFiles,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null,
        DiagnosticPublisher? diagnosticPublisher = null)
        : base(languageService, openFiles, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _diagnosticPublisher = diagnosticPublisher;
        _logger.LogInformation("DidOpenTextDocumentHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public DidOpenTextDocumentHandler(
        ILogger<DidOpenTextDocumentHandler> logger,
        Mql4AntlrParser parser,
        OpenDocumentStore openFiles,
        GlobalSymbolIndex globalSymbolIndex)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               openFiles,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() },
               new GlobalSymbolIndexAccessor(globalSymbolIndex))
    {
    }

    public TextDocumentOpenRegistrationOptions GetRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentOpenRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector()
        };
    }

    public Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        return Task.FromResult(HandleCore(request, cancellationToken));
    }

    /// <summary>
    /// Issue #116: synchronous forwarding entry point used by the built-in
    /// TextDocumentSync handler registered in Program.cs. OmniSharp 0.19.9
    /// routes each textDocument/didOpen notification to EXACTLY ONE of the two
    /// handlers registered for the method — this custom handler, or the built-in
    /// delegating handler whose lambdas previously did nothing — and which one
    /// wins is decided per process. The built-in lambdas are synchronous
    /// <see cref="Action{T}"/> delegates, so this method exposes the same
    /// synchronous core (no Task hop) and guarantees the real logic runs no
    /// matter which handler the router picks.
    /// </summary>
    public void HandleSync(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        HandleCore(request, cancellationToken);
    }

    private Unit HandleCore(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        var documentUri = request.TextDocument.Uri.ToUri();
        var content = request.TextDocument.Text;
        var languageId = request.TextDocument.LanguageId;

        // Issue #16 Phase 2: an indexed .mqh is routed by its includers, not
        // by content. Content sniffing (REQ-LD-02, D2) only applies when the
        // header is not indexed or the index evidence is ambiguous — this
        // makes open-order irrelevant.
        var language = MqhLanguageResolver.TryResolve(documentUri, SymbolIndex.Index, out var indexedLanguage)
            ? indexedLanguage
            : LanguageDetection.Detect(documentUri, languageId, content);

        return HandleForLanguage(request, language, cancellationToken);
    }

    protected override Unit HandleForLanguage(DidOpenTextDocumentParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri.ToUri();
            var content = request.TextDocument.Text;

            _logger.LogDebug("Opening document: {DocumentUri} ({Language})", documentUri, language);

            if (content != null)
            {
                var filePath = documentUri.AbsolutePath ?? "unknown";

                // Issue #36: a didOpen that follows a didClose on unchanged
                // content reuses the parse retained by the store's LRU cache.
                // Content is byte-identical, so re-indexing is usually a no-op.
                if (_documentStore.TryGetReusableParse(documentUri, content, language, out var cachedFile))
                {
                    _logger.LogDebug("Reusing cached parse for {DocumentUri} ({Language})", documentUri, language);

                    // Issue #116 (defense layer): a reusable parse can come from
                    // a store entry that was NEVER indexed. Several request
                    // handlers (completion, diagnostics, semantic tokens, ...)
                    // parse a not-yet-opened document and put the model in the
                    // OpenDocumentStore without touching GlobalSymbolIndex; the
                    // workspace scan then skips that document as "open", so the
                    // index stays permanently empty for it. Healing here keeps
                    // the reuse path from sealing that hole for the session.
                    //
                    // A didClose does NOT remove index entries: the close
                    // handler only drops the store entry, and GlobalSymbolIndex
                    // .RemoveFile is never called in production, so the index
                    // survives didClose untouched. When symbols are already
                    // present for this (file, language) key the cached parse is
                    // known good and we skip the redundant AddFile.
                    //
                    // On the missing case, mirror the fresh-parse path exactly.
                    // Pass a DEFENSIVE COPY (Symbols.ToList()): AddFile stores
                    // the list it receives BY REFERENCE and its updater runs
                    // existing.Clear() before AddRange, so handing it the
                    // store's own model list would let a later re-index self-
                    // wipe the file's symbols (#36). SymbolOccurrenceMapper.Map
                    // supplies occurrences (the null overload would purge scan-
                    // indexed entries; CRITICAL-2).
                    var indexedSymbols = SymbolIndex.Index.GetFileSymbols(filePath, language);
                    if (indexedSymbols == null || indexedSymbols.Count == 0)
                    {
                        SymbolIndex.Index.AddFile(
                            filePath, language, cachedFile!.Symbols.ToList(),
                            SymbolOccurrenceMapper.Map(cachedFile!, filePath, language));

                        // Issue #116 (defense layer): the include chain is
                        // healed on the SAME branch, and only here. When this
                        // store entry was never indexed, its includes were not
                        // indexed by whoever created it either, so the sibling
                        // hole has to be closed together with the root. When
                        // the root IS indexed, its includes were indexed by the
                        // same operation that indexed the root (the didOpen
                        // sequence via IndexIncludesRecursively, or the
                        // workspace scan; didChange re-parses only the root and
                        // leaves the existing chain in place) and didClose
                        // never removes them, so re-walking
                        // the chain would be redundant work. Prefer the
                        // smallest correct change: heal the chain only when the
                        // root is missing.
                        IndexIncludesRecursively(
                            ResolveParser(language), filePath, cachedFile!, language, cancellationToken);
                    }

                    _logger.LogDebug("Parsed {SymbolCount} symbols from opened document", cachedFile!.Symbols.Count);
                    return Unit.Value;
                }

                var parser = ResolveParser(language);

                var mqlFile = parser.ParseFile(content, filePath, cancellationToken);
                mqlFile.Language = language;

                _documentStore.AddOrUpdate(documentUri, mqlFile, content, language);

                // OCC-03: re-index with the fresh parse's token occurrences so
                // the wholesale per-file occurrence replacement swaps old for
                // new instead of purging scan-indexed entries with an empty
                // list (CRITICAL-2 fix; identical mapping to the workspace scan).
                SymbolIndex.Index.AddFile(
                    filePath, language, mqlFile.Symbols,
                    SymbolOccurrenceMapper.Map(mqlFile, filePath, language));

                // D2 include resolution: parse included .mqh files under the includer's language key.
                //
                // DESIGN WARNING (D2 — intentional, do not change without revisiting the design):
                // Shared `.mqh` headers are parsed with the includer's parser (Mql4AntlrParser when the
                // includer is an .mq4 file). This means a `.mqh` that uses MQL5-only syntax (nullptr,
                // union, enum class, etc.) will produce silent parse errors when included from an MQL4
                // source. This is an accepted constraint of the dual-key coexistence model: shared
                // headers MUST be written in the MQL4-compatible subset of MQL5. Do NOT switch the
                // include parser based on content sniffing here — that would break the single-language
                // symbol keying that D2 relies on. If a header needs MQL5-only constructs, it must be
                // included only from MQL5 sources.
                IndexIncludesRecursively(parser, filePath, mqlFile, language, cancellationToken);

                _logger.LogDebug("Parsed {SymbolCount} symbols from opened document", mqlFile.Symbols.Count);

                // Issue #91: push-model diagnostics for push-only clients (the
                // majority of editors and MCP agents). Published AFTER the include
                // indexing above so cross-file suppression (#44) sees the same
                // index state the pull model observes. Null in legacy/test
                // constructors: no push, no behavior change.
                _diagnosticPublisher?.Publish(
                    documentUri, filePath, mqlFile, content, language, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling didOpen for {Uri}", request.TextDocument.Uri);
        }

        return Unit.Value;
    }

    // Issue #25a: ExtractIncludePath/ResolveIncludePath/IsContainedInWorkspace
    // private copies were replaced by the single IncludePathResolver service
    // (regex extraction + relative resolution + PathSecurity containment).

    /// <summary>
    /// Issue #95: index the opened document's include chain RECURSIVELY.
    ///
    /// <para>The old loop indexed only the DIRECT includes, so in a project
    /// with nested includes (A → mid.mqh → deep.mqh), opening A left deep.mqh
    /// unindexed until the background workspace scan happened to reach it —
    /// on a 60+ file tree that window is minutes of empty workspace/symbol
    /// results for include-reachable symbols (the reporter's case: open A,
    /// query for a symbol declared in the transitively-included B → []).
    /// The chain is exactly what the document's compilation needs, so it is
    /// indexable immediately, cycle-safe, and PathSecurity-guarded per
    /// hop (same TryResolveContained containment as before).</para>
    ///
    /// <para>D2 semantics are unchanged: every header in the chain is parsed
    /// and indexed under the ROOT document's language key (single-language
    /// symbol keying — see the DESIGN WARNING above), and each hop records
    /// its dependency edge (the include graph the #90 rename guard and the
    /// #89 type-fallback guard rely on).</para>
    /// </summary>
    private void IndexIncludesRecursively(
        IMqlParser parser, string rootFilePath, MqlFile rootFile, MqlLanguage rootLanguage,
        CancellationToken cancellationToken)
    {
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Guard against a self-including cycle on the root document.
            Path.GetFullPath(rootFilePath)
        };
        IndexIncludes(parser, rootFilePath, rootFile, rootLanguage, processed, cancellationToken);
    }

    private void IndexIncludes(
        IMqlParser parser, string includerFilePath, MqlFile includerFile, MqlLanguage language,
        HashSet<string> processed, CancellationToken cancellationToken)
    {
        foreach (var include in includerFile.Includes)
        {
            // Issue #25a: `Includes` entries are the extracted paths stored by
            // the symbol visitors (bare path for quoted includes, <path> for
            // angle-bracket system includes) — NOT raw directive text.
            // IncludePathResolver consumes the stored-entry shape, applies
            // the PathSecurity containment guard, and returns false for
            // system includes.
            if (!IncludePathResolver.TryResolveContained(includerFilePath, include, out var includeFullPath))
            {
                // Angle-bracket system includes (<Trade\Trade.mqh>): resolve
                // against the configured system include roots (the terminal's
                // MQL5/Include / MQL4/Include dirs, delivered via the
                // systemIncludeRoots initialization option) and LAZILY index
                // the header. No roots configured or unresolvable header:
                // today's behavior (skip, fail open).
                if (!IncludePathResolver.TryResolveSystemInclude(include, out includeFullPath))
                {
                    continue;
                }

                // Dedup: system headers are process-lifetime index entries —
                // an already-indexed header (same language key) is not
                // re-parsed, mirroring the store's cached-parse reuse. The
                // dependency edge is still refreshed so the #90 rename and
                // #89 type-fallback reachability guards see it.
                var existingSymbols = SymbolIndex.Index.GetFileSymbols(includeFullPath, language);
                if (existingSymbols != null && existingSymbols.Count > 0)
                {
                    SymbolIndex.Index.AddDependency(includerFilePath, includeFullPath);
                    continue;
                }
            }

            // Cycle/diamond safety: each header is indexed once per open.
            if (!processed.Add(Path.GetFullPath(includeFullPath)))
            {
                continue;
            }

            var includeContent = SourceFileReader.ReadAllText(includeFullPath);
            // Force the includer's language for shared headers so symbols are
            // indexed under one key (D2 dual-key coexistence). The include's
            // own sniffed language is intentionally ignored here.
            var includeFile = parser.ParseFile(includeContent, includeFullPath, cancellationToken);
            includeFile.Language = language;
            // OCC-03: occurrence-aware re-index, same rationale as AddFile above.
            SymbolIndex.Index.AddFile(
                includeFullPath, language, includeFile.Symbols,
                SymbolOccurrenceMapper.Map(includeFile, includeFullPath, language));
            SymbolIndex.Index.AddDependency(includerFilePath, includeFullPath);

            // Issue #95: recurse into the include's own includes.
            IndexIncludes(parser, includeFullPath, includeFile, language, processed, cancellationToken);
        }
    }
}
