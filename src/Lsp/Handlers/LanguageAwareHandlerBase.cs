using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using MqlLanguageServer.Mql4.Builtins;
using MqlLanguageServer.Mql5.Builtins;
using MqlLanguageServer.Parser;

namespace MqlLanguageServer.Lsp.Handlers;

/// <summary>
/// Abstract base for handlers that need to branch on MQL language.
/// Provides parser + builtins resolution while forcing each concrete handler
/// to implement language-specific handling.
/// </summary>
public abstract class LanguageAwareHandlerBase<TParams, TResult>
{
    protected readonly MqlLanguageService _languageService;
    protected readonly OpenDocumentStore _documentStore;
    private readonly IMqlBuiltins[] _builtins;

    /// <summary>
    /// Issue #25c: injected access to the shared symbol index. Defaults to
    /// the <see cref="GlobalSymbolIndexAccessor"/> parameterless overload
    /// (which falls back to <see cref="GlobalSymbolIndex.Instance"/>) so
    /// existing base-constructor call sites keep compiling; the production
    /// composition root injects the DI-registered singleton.
    /// </summary>
    protected GlobalSymbolIndexAccessor SymbolIndex { get; }

    protected LanguageAwareHandlerBase(
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins)
        : this(languageService, documentStore, builtins, new GlobalSymbolIndexAccessor())
    {
    }

    protected LanguageAwareHandlerBase(
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor symbolIndex)
    {
        _languageService = languageService ?? throw new ArgumentNullException(nameof(languageService));
        _documentStore = documentStore ?? throw new ArgumentNullException(nameof(documentStore));
        _builtins = builtins ?? throw new ArgumentNullException(nameof(builtins));
        SymbolIndex = symbolIndex ?? throw new ArgumentNullException(nameof(symbolIndex));
    }

    /// <summary>
    /// Resolve the parser for the requested language.
    /// </summary>
    protected IMqlParser ResolveParser(MqlLanguage language)
    {
        return _languageService.ResolveParser(language);
    }

    /// <summary>
    /// Resolve the built-in registry for the requested language.
    /// </summary>
    protected IMqlBuiltins ResolveBuiltins(MqlLanguage language)
    {
        IMqlBuiltins? match = language == MqlLanguage.Mql5
            ? _builtins.OfType<Mql5Builtins>().FirstOrDefault()
            : _builtins.OfType<Mql4BuiltinsAdapter>().FirstOrDefault();

        // Fallback to the first available registry if language-specific one is missing.
        return match ?? _builtins.FirstOrDefault()
               ?? throw new InvalidOperationException("No IMqlBuiltins registry registered.");
    }

    /// <summary>
    /// Resolve the language for a document URI, defaulting to MQL4.
    /// For .mqh headers, an indexed file is routed by its includers first
    /// (issue #16 Phase 2); content sniffing from disk applies only when the
    /// header is not indexed or the index evidence is ambiguous. For files
    /// not yet opened via didOpen, reads the file from disk (when it exists)
    /// so that fallback sniffing still routes to the correct parser.
    /// </summary>
    protected MqlLanguage ResolveLanguage(Uri uri)
    {
        if (_documentStore.TryGetLanguage(uri, out var language))
            return language;

        // Issue #16 Phase 2: indexed .mqh headers route by includer.
        if (MqhLanguageResolver.TryResolve(uri, SymbolIndex.Index, out var indexedLanguage))
            return indexedLanguage;

        // For files not yet opened via didOpen, read content from disk for
        // accurate sniffing.
        if (uri.IsFile && File.Exists(uri.AbsolutePath))
        {
            try
            {
                var content = SourceFileReader.ReadAllText(uri.AbsolutePath);
                return LanguageDetection.Detect(uri, null, content);
            }
            catch
            {
                // Fall through to default detection if the file cannot be read.
            }
        }

        return LanguageDetection.Detect(uri, null, string.Empty);
    }

    /// <summary>
    /// Issue #86: resolve the document text and parsed model for a request,
    /// preferring the open-document store over the disk file. didOpen/didChange
    /// feed the editor buffer into the store, so for an open document the
    /// stored content is the exact text the stored model was parsed from;
    /// reading the disk file instead desynchronizes the cursor-identifier
    /// extraction (stale text) from the fresh model and permanently degrades
    /// cross-file resolution until restart. When the document is not open,
    /// read from disk and register the parse result in the store so later
    /// requests behave identically.
    /// </summary>
    protected bool TryGetDocumentContent(
        Uri uri,
        string filePath,
        IMqlParser parser,
        MqlLanguage language,
        out MqlFile mqlFile,
        out string content)
    {
        if (_documentStore.TryGetValue(uri, out var storedFile, out var storedContent) &&
            storedFile != null && storedContent != null)
        {
            mqlFile = storedFile;
            content = storedContent;
            return true;
        }

        content = SourceFileReader.ReadAllText(filePath);
        mqlFile = parser.ParseFile(content, filePath);
        _documentStore.AddOrUpdate(uri, mqlFile, content, language);

        // Issue #116 (defense layer): a request-path parse must be indexed, not
        // merely stored. The workspace scan skips any document the store holds,
        // so before this fix a request that parsed a never-opened file (or one
        // whose didOpen was swallowed) left the OpenDocumentStore as the only
        // record; the scan then skipped it and the file stayed permanently
        // absent from GlobalSymbolIndex. Mirror didOpen/the scan exactly:
        // symbols plus the occurrence-aware SymbolOccurrenceMapper.Map overload
        // (passing null would purge scan-indexed occurrences with an empty list
        // — the CRITICAL-2 hazard documented in DidOpenTextDocumentHandler).
        //
        // Language choice: use the caller-resolved `language`, not
        // _documentStore.TryGetLanguage. This branch runs only on a store miss,
        // so TryGetLanguage would always report false and fall back to the MQL4
        // default, mis-keying MQL5 documents. Handlers resolve the language via
        // ResolveLanguage before calling here, which for an unopened file sniffs
        // the on-disk content (and routes indexed .mqh headers by includer).
        //
        // Defensive copy of Symbols: AddOrUpdate just stored mqlFile, and
        // GlobalSymbolIndex.AddFile stores the list it is given BY REFERENCE.
        // If a later AddFile receives that same instance (RenameHandler
        // re-indexes a never-opened document after this method returns), the
        // updater runs existing.Clear(); existing.AddRange(symbols) against the
        // identical list and wipes the file's symbols from the index (#36). The
        // copy keeps the index's list distinct from the store's model, so the
        // redundant second AddFile replaces content instead of self-clearing.
        SymbolIndex.Index.AddFile(
            filePath, language, mqlFile.Symbols.ToList(),
            SymbolOccurrenceMapper.Map(mqlFile, filePath, language));

        return true;
    }

    /// <summary>
    /// Language-specific handling to be implemented by each concrete handler.
    /// </summary>
    protected abstract TResult HandleForLanguage(TParams request, MqlLanguage language, CancellationToken cancellationToken);
}
