using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// Handles textDocument/rename and textDocument/prepareRename requests.
/// </summary>
public class RenameHandler : LanguageAwareHandlerBase<RenameParams, WorkspaceEdit?>, IRenameHandler, IPrepareRenameHandler
{
    private readonly ILogger<RenameHandler> _logger;

    public RenameHandler(
        ILogger<RenameHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null)
        : base(languageService, documentStore, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _logger.LogInformation("RenameHandler initialized");
    }

    // Backward-compatible constructor for existing MQL4 tests.
    public RenameHandler(
        ILogger<RenameHandler> logger,
        Mql4AntlrParser parser,
        OpenDocumentStore documentStore,
        GlobalSymbolIndex globalSymbolIndex)
        : this(logger,
               new MqlLanguageService(parser ?? throw new ArgumentNullException(nameof(parser)), new Mql5AntlrParser()),
               documentStore,
               new IMqlBuiltins[] { new Mql4BuiltinsAdapter() },
               new GlobalSymbolIndexAccessor(globalSymbolIndex))
    {
    }

    // Backward-compatible constructor used by EditingHandlersTests.
    // Issue #25c: routes through the accessor instead of the raw singleton
    // so the handler body has no direct GlobalSymbolIndex.Instance reads.
    public RenameHandler(
        ILogger<RenameHandler> logger,
        Mql4AntlrParser parser,
        OpenDocumentStore documentStore)
        : this(logger, parser, documentStore, new GlobalSymbolIndexAccessor().Index)
    {
    }

    public Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override WorkspaceEdit? HandleForLanguage(RenameParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            var newName = request.NewName;

            _logger.LogDebug("Processing rename request for: {DocumentUri} to '{NewName}' ({Language})",
                documentUri, newName, language);

            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogWarning("File not found: {FilePath}", filePath);
                return null;
            }

            var parser = ResolveParser(language);
            // Issue #88 (rename is the highest-impact handler of the sweep):
            // resolve the document from the open-document store first — the
            // stored content is the exact text the stored model was parsed
            // from (the editor buffer), while the disk file can be stale for
            // unsaved edits. A rename computed from stale text against a
            // fresh model silently renames the wrong spans.
            var uri = documentUri.ToUri();
            // Capture BEFORE TryGetDocumentContent: it registers a parse for
            // never-opened documents, which would make the check below lie.
            var wasOpenInStore = _documentStore.TryGetValue(uri, out _, out _);
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);

            var line = request.Position.Line + 1;
            var character = request.Position.Character + 1;

            // Issue #45 (tier 1, coordinator-authorized deviation): resolve the
            // symbol by the identifier text at the cursor, exactly as
            // ReferencesHandler does. FindSymbolAtPosition returns the FIRST
            // symbol whose Range contains the position, so any cursor inside a
            // function body resolves to the function itself — the helper below
            // could never see the queried name. Behavior change: rename now
            // requires the cursor to be on an identifier token (e.g. renaming
            // a function requires the cursor on its name token); a cursor on
            // whitespace/punctuation resolves to no symbol.
            var symbol = parser.FindSymbolDefinition(mqlFile, content, line, character);
            if (symbol == null)
            {
                // Issue #90 (via #92): the cursor may sit on a call site of a
                // function declared in an included file — in-file lookup finds
                // nothing. Resolve the cursor identifier against the global
                // index so renaming from the usage works too.
                symbol = IncludeSymbolResolver.TryResolve(mqlFile, line - 1, character - 1, SymbolIndex.Index);
            }
            if (symbol == null)
            {
                _logger.LogDebug("No symbol found at position {Line}:{Character}", line, character);
                return null;
            }

            // A closed or never-opened file is parsed here without passing
            // through didOpen/didChange; index it exactly as they would
            // (identical SymbolOccurrenceMapper mapping) so the occurrence
            // query below sees this file's identifier tokens. Open documents
            // were already indexed by didOpen/didChange with the same buffer
            // state — skip (their index entries may carry IsDefinition marks
            // derived from SelectionRanges that a blind re-AddFile would
            // replace differently than the didOpen path).
            if (!wasOpenInStore)
            {
                SymbolIndex.Index.AddFile(
                    filePath, language, mqlFile.Symbols,
                    SymbolOccurrenceMapper.Map(mqlFile, filePath, language));
            }

            // Token-backed rename edits (OCC-03): occurrences are name-keyed
            // identifier tokens with 0-based line/column, so they map directly
            // onto LSP positions (same conversion as ReferencesHandler).
            // Issue #90: rename and references must agree — references returns
            // the workspace-wide name-keyed set (BindAndFilter only prunes
            // same-file shadowing), so rename edits the SAME set, grouped per
            // file into a cross-file WorkspaceEdit. The previous same-file-only
            // filter silently dropped the cross-file call sites that references
            // demonstrably returns (the "silent partial rename" of issue #90).
            //
            // Reachability guard (issue #89's lesson): rename is destructive, so
            // cross-file edits are additionally restricted to files linked to the
            // queried document through the include graph (either direction). A
            // same-name identifier in an unrelated file must NOT be renamed. When
            // the queried document has no include edges at all (degraded graph —
            // e.g. no scan, never opened), the guard falls back to the plain
            // name-keyed set: never worse than references, never silently empty.
            IEnumerable<SymbolOccurrence> occurrences = SymbolIndex.Index.FindOccurrences(symbol.Name);

            occurrences = ScopeOccurrenceFilter.BindAndFilter(
                mqlFile,
                symbol.Name,
                request.Position.Line,
                request.Position.Character,
                occurrences);

            occurrences = FilterCrossFileToReachable(occurrences, filePath);

            var changes = occurrences
                .GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => DocumentUri.File(g.Key),
                    g => (IEnumerable<TextEdit>)g.Select(o => ToTextEdit(o, newName)).ToList());

            if (changes.Count == 0)
            {
                return null;
            }

            var edit = new WorkspaceEdit
            {
                Changes = changes
            };

            _logger.LogDebug("Created rename edit with {Count} changes across {Files} files",
                changes.Sum(g => g.Value.Count()), changes.Count);

            return edit;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing rename request for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    /// <summary>
    /// Issue #90 reachability guard: keep same-file occurrences unconditionally
    /// (BindAndFilter already pruned same-file shadowing); keep cross-file
    /// occurrences only when the queried document and the occurrence file are
    /// linked through the include graph (either direction). When the queried
    /// document has no include edges at all (degraded graph — no workspace
    /// scan, never opened via didOpen), fall back to the plain name-keyed set
    /// so rename never agrees LESS with references than before.
    /// </summary>
    private IEnumerable<SymbolOccurrence> FilterCrossFileToReachable(
        IEnumerable<SymbolOccurrence> occurrences, string filePath)
    {
        var all = occurrences as IReadOnlyList<SymbolOccurrence> ?? occurrences.ToList();
        if (all.Count == 0)
        {
            return all;
        }

        var hasCrossFile = false;
        foreach (var occurrence in all)
        {
            if (!string.Equals(occurrence.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                hasCrossFile = true;
                break;
            }
        }

        if (!hasCrossFile)
        {
            return all;
        }

        var index = SymbolIndex.Index;
        var includes = index.GetDependencies(filePath);
        var includers = index.GetDependentFiles(filePath);
        if (includes.Count == 0 && includers.Count == 0)
        {
            // Degraded graph: never return less than references would.
            return all;
        }

        return all.Where(o =>
            string.Equals(o.FilePath, filePath, StringComparison.OrdinalIgnoreCase) ||
            includes.Contains(o.FilePath, StringComparer.OrdinalIgnoreCase) ||
            includers.Contains(o.FilePath, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private TextEdit ToTextEdit(SymbolOccurrence occurrence, string newName)
    {
        return new TextEdit
        {
            NewText = newName,
            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                new Position(occurrence.Line, occurrence.Column),
                new Position(occurrence.Line, occurrence.Column + occurrence.Length))
        };
    }

    public RenameRegistrationOptions GetRegistrationOptions(RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = MqlServerCapabilities.GetDocumentSelector(),
            PrepareProvider = true
        };
    }

    /// <summary>
    /// Issue #94: the registration options above declare prepareProvider: true,
    /// but the server never implemented textDocument/prepareRename —
    /// capability-driven clients sent the request and hit an unexpected
    /// -32601 mid-flow. The spec-shaped answer for "rename not valid at this
    /// position" is a null result, which is exactly what this returns when
    /// the cursor is not on a renamable identifier.
    /// </summary>
    // Public overload (implicit interface implementation): the two Handle
    // methods differ by params type, so plain overloading binds both
    // IRenameHandler and IPrepareRenameHandler.
    public Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return Task.FromResult<RangeOrPlaceholderRange?>(null);
            }

            var uri = documentUri.ToUri();
            var language = ResolveLanguage(uri);
            var parser = ResolveParser(language);
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);

            var line0 = request.Position.Line;
            var character0 = request.Position.Character;

            // The rename target range is the identifier TOKEN under the cursor
            // (0-based occurrence index — model text, immune to the #86
            // buffer-vs-disk divergence), not the definition range: for an
            // include-declared symbol renamed from a usage, the prompt must
            // cover the token the user sees, in the requested document.
            var occurrence = mqlFile.Occurrences.FirstOrDefault(o =>
                o.Line == line0 && o.Column <= character0 && character0 < o.Column + o.Length);
            if (occurrence == null)
            {
                _logger.LogDebug("prepareRename: no identifier token at {Line}:{Character}", line0, character0);
                return Task.FromResult<RangeOrPlaceholderRange?>(null);
            }

            // The identifier must resolve to a DECLARED symbol — mirroring
            // what rename itself would do at this position, so the prompt only
            // appears where a rename would actually produce edits:
            //   1. an in-file declaration by name (bypasses FindSymbolDefinition's
            //      builtin short-circuit: a USER-declared OnInit/OnTick is
            //      renamable even though the name is also a builtin);
            //   2. else an include-declared symbol (issues #90/#92).
            // A pure builtin call (Print, OrderSend, ...) matches neither and
            // yields null — the spec-shaped "rename not valid here".
            var declaredInFile = parser.FindSymbolsByName(mqlFile, occurrence.Text).FirstOrDefault();
            var symbol = declaredInFile
                        ?? IncludeSymbolResolver.TryResolve(mqlFile, line0, character0, SymbolIndex.Index);

            if (symbol == null)
            {
                _logger.LogDebug("prepareRename: identifier '{Identifier}' does not resolve to a renamable symbol", occurrence.Text);
                return Task.FromResult<RangeOrPlaceholderRange?>(null);
            }

            return Task.FromResult<RangeOrPlaceholderRange?>(new RangeOrPlaceholderRange(
                new PlaceholderRange
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(occurrence.Line, occurrence.Column),
                        new Position(occurrence.Line, occurrence.Column + occurrence.Length)),
                    Placeholder = occurrence.Text
                }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing prepareRename request for {Uri}", request.TextDocument.Uri);
            // A null result is the spec-shaped "rename not valid here" answer;
            // never leak an error for a position probe.
            return Task.FromResult<RangeOrPlaceholderRange?>(null);
        }
    }
}
