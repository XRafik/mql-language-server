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
/// LSP 3.17 call hierarchy (issue #96): prepareCallHierarchy,
/// callHierarchy/incomingCalls and callHierarchy/outgoingCalls, built on the
/// machinery the navigation handlers already share — the name-keyed
/// workspace occurrence index (OCC-03/04/05, the ReferencesHandler source),
/// the include/dependency graph (#90/#89), and the open-document store
/// (the #86/#88 store-first rule).
///
/// <para>Call-vs-mention distinction: an occurrence is a CALL when the token
/// is immediately followed by an open parenthesis on the same line; the
/// definition's own signature is excluded via the index's IsDefinition mark
/// (OCC-04, SelectionRange overlap at AddFile time). Incoming calls group
/// every call position by its enclosing function, resolved from the index's
/// per-file symbol lists (no parse at query time); outgoing calls walk the
/// item's own function range.</para>
///
/// <para>Include-declared symbols resolve through the #92/#90 machinery: a
/// cursor on a call site of a function declared in an included file yields
/// the item pointing at the declaration file, and incoming calls from every
/// include-linked consumer are found.</para>
/// </summary>
public class CallHierarchyHandler : LanguageAwareHandlerBase<CallHierarchyPrepareParams, Container<CallHierarchyItem>?>,
    ICallHierarchyPrepareHandler, ICallHierarchyIncomingHandler, ICallHierarchyOutgoingHandler
{
    private readonly ILogger<CallHierarchyHandler> _logger;
    private readonly IMqlBuiltins[] _builtinsRegistry;

    public CallHierarchyHandler(
        ILogger<CallHierarchyHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null)
        : base(languageService, documentStore, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _builtinsRegistry = builtins;
        _logger.LogInformation("CallHierarchyHandler initialized");
    }

    public CallHierarchyRegistrationOptions GetRegistrationOptions(CallHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CallHierarchyRegistrationOptions
        {
            WorkDoneProgress = false
        };
    }

    // OmniSharp's generated incoming/outgoing call interfaces require the
    // handler identity + capability hooks (they fan out per registration);
    // this server registers one handler with no fan-out, so a stable id and
    // a no-op capability capture are the correct implementations.
    private readonly Guid _handlerId = Guid.NewGuid();
    Guid OmniSharp.Extensions.LanguageServer.Protocol.Models.ICanBeIdentifiedHandler.Id => _handlerId;
    void OmniSharp.Extensions.LanguageServer.Protocol.ICapability<CallHierarchyCapability>.SetCapability(
        CallHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        // Nothing to capture: the handler is language-agnostic per request.
    }

    public Task<Container<CallHierarchyItem>?> Handle(CallHierarchyPrepareParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override Container<CallHierarchyItem>? HandleForLanguage(
        CallHierarchyPrepareParams request, MqlLanguage language, CancellationToken cancellationToken)
    {
        try
        {
            var documentUri = request.TextDocument.Uri;
            var filePath = documentUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogDebug("File not found: {FilePath}", filePath);
                return null;
            }

            var uri = documentUri.ToUri();
            var parser = ResolveParser(language);
            TryGetDocumentContent(uri, filePath, parser, language, out var mqlFile, out var content);

            var line0 = request.Position.Line;
            var character0 = request.Position.Character;

            // The symbol under the cursor: in-file declaration by name first
            // (the FindSymbolDefinition builtin short-circuit would swallow
            // user-declared event handlers), then the include-declared
            // fallback (#92/#90) so a cursor on a call site of an
            // include-declared function prepares the DECLARED symbol.
            var occurrence = mqlFile.Occurrences.FirstOrDefault(o =>
                o.Line == line0 && o.Column <= character0 && character0 < o.Column + o.Length);
            if (occurrence == null)
            {
                _logger.LogDebug("prepareCallHierarchy: no identifier token at {Line}:{Character}", line0, character0);
                return null;
            }

            if (ResolveBuiltins(language).IsBuiltin(occurrence.Text))
            {
                // Skip the in-file/inclusion lookup ONLY for pure builtin calls;
                // a user-declared name that is also a builtin (OnInit) still
                // resolves below when declared in this file.
                var declared = parser.FindSymbolsByName(mqlFile, occurrence.Text).FirstOrDefault();
                if (declared == null)
                {
                    return null;
                }

                return new Container<CallHierarchyItem>(BuildItem(declared, filePath));
            }

            var symbol = parser.FindSymbolsByName(mqlFile, occurrence.Text).FirstOrDefault()
                        ?? IncludeSymbolResolver.TryResolve(mqlFile, line0, character0, SymbolIndex.Index);
            if (symbol == null)
            {
                _logger.LogDebug("prepareCallHierarchy: identifier '{Identifier}' does not resolve", occurrence.Text);
                return null;
            }

            var item = BuildItem(symbol, filePath);
            _logger.LogDebug("prepareCallHierarchy: prepared '{Name}' at {Uri}", item.Name, item.Uri);
            return new Container<CallHierarchyItem>(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing prepareCallHierarchy for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public Task<Container<CallHierarchyIncomingCall>?> Handle(CallHierarchyIncomingCallsParams request, CancellationToken cancellationToken)
    {
        try
        {
            var item = request.Item;
            if (item?.Name == null)
            {
                return Task.FromResult<Container<CallHierarchyIncomingCall>?>(null);
            }

            if (IsBuiltinAny(item.Name))
            {
                return Task.FromResult<Container<CallHierarchyIncomingCall>?>(new Container<CallHierarchyIncomingCall>(Array.Empty<CallHierarchyIncomingCall>()));
            }

            // Workspace-wide, name-keyed (the references contract); definitions
            // are excluded via the OCC-04 IsDefinition mark — the signature's
            // own name token is not a call site.
            var callOccurrences = SymbolIndex.Index.FindOccurrences(item.Name)
                .Where(o => !o.IsDefinition)
                .GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => (FilePath: g.Key, Spans: CollectCallSpans(g.Key, g)))
                .Where(x => x.Spans.Count > 0)
                .ToList();

            var incoming = new Dictionary<(string File, string Selection), List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>>();

            foreach (var (filePath, spans) in callOccurrences)
            {
                foreach (var span in spans)
                {
                    var caller = FindEnclosingFunction(filePath, span.Line, span.Column);
                    if (caller == null)
                    {
                        continue;
                    }

                    var key = (filePath, SelectionKey(caller));
                    if (!incoming.TryGetValue(key, out var ranges))
                    {
                        incoming[key] = ranges = new List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>();
                    }

                    ranges.Add(span.Range);
                }
            }

            var result = incoming
                .Select(kvp => (From: BuildItemFromIndex(kvp.Key.File, kvp.Key.Selection), kvp.Value))
                .Where(x => x.From != null)
                .Select(x => new CallHierarchyIncomingCall
                {
                    From = x.From!,
                    FromRanges = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>(x.Value)
                })
                .ToList();

            _logger.LogDebug("incomingCalls for '{Name}': {Count} caller(s)", item.Name, result.Count);
            return Task.FromResult<Container<CallHierarchyIncomingCall>?>(new Container<CallHierarchyIncomingCall>(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing callHierarchy/incomingCalls");
            return Task.FromResult<Container<CallHierarchyIncomingCall>?>(null);
        }
    }

    public Task<Container<CallHierarchyOutgoingCall>?> Handle(CallHierarchyOutgoingCallsParams request, CancellationToken cancellationToken)
    {
        try
        {
            var item = request.Item;
            if (item?.Name == null)
            {
                return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
            }

            var itemUri = item.Uri;
            var filePath = itemUri.GetFileSystemPath();
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
            }

            var language = ResolveLanguage(itemUri.ToUri());
            var parser = ResolveParser(language);
            TryGetDocumentContent(itemUri.ToUri(), filePath, parser, language, out var mqlFile, out var content);

            // Locate the function the item refers to: match by selection range
            // first (the client echoes the item back), else the first
            // function-like same-name symbol.
            var function = parser.FindSymbolsByName(mqlFile, item.Name)
                .Where(IsFunctionLike)
                .OrderBy(f => SameSelection(f, item) ? 0 : 1)
                .FirstOrDefault();
            if (function == null)
            {
                _logger.LogDebug("outgoingCalls: '{Name}' not found in {Uri}", item.Name, itemUri);
                return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
            }

            var outgoing = new Dictionary<(string File, string Selection), (CallHierarchyItem To, List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range> Ranges)>();

            foreach (var occurrence in mqlFile.Occurrences)
            {
                if (!Contains(function.Range, occurrence.Line, occurrence.Column))
                {
                    continue;
                }

                // The function's own declaration name token sits inside its
                // Range and is followed by '(' — it is the signature, not a
                // call site (mirror of incoming's IsDefinition exclusion).
                if (Contains(function.SelectionRange, occurrence.Line, occurrence.Column))
                {
                    continue;
                }

                if (!TryGetCallSpan(content, occurrence.Line, occurrence.Column, occurrence.Length, out var callSpan))
                {
                    continue;
                }

                var callee = ResolveCallee(mqlFile, parser, occurrence.Text, filePath);
                if (callee is not { } resolvedCallee)
                {
                    continue;
                }

                var key = (resolvedCallee.File, resolvedCallee.Selection);
                if (!outgoing.TryGetValue(key, out var entry))
                {
                    outgoing[key] = entry = (resolvedCallee.Item, new List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>());
                }

                entry.Ranges.Add(callSpan);
            }

            var result = outgoing.Values
                .Select(e => new CallHierarchyOutgoingCall
                {
                    To = e.To,
                    FromRanges = new Container<OmniSharp.Extensions.LanguageServer.Protocol.Models.Range>(e.Ranges)
                })
                .ToList();

            _logger.LogDebug("outgoingCalls for '{Name}': {Count} callee(s)", item.Name, result.Count);
            return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(new Container<CallHierarchyOutgoingCall>(result));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing callHierarchy/outgoingCalls");
            return Task.FromResult<Container<CallHierarchyOutgoingCall>?>(null);
        }
    }

    private CallHierarchyItem BuildItem(MqlSymbol symbol, string fallbackFilePath)
    {
        var filePath = !string.IsNullOrEmpty(symbol.FilePath) ? symbol.FilePath : fallbackFilePath;
        return new CallHierarchyItem
        {
            Name = symbol.Name,
            Kind = symbol.Kind,
            Detail = symbol.Detail,
            Uri = DocumentUri.File(filePath),
            Range = symbol.Range,
            SelectionRange = SelectionRangeOrFallback(symbol)
        };
    }

    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range SelectionRangeOrFallback(MqlSymbol symbol) =>
        symbol.SelectionRange != null && symbol.SelectionRange.Start != null && symbol.SelectionRange.End != null
            ? symbol.SelectionRange
            : symbol.Range;

    private static string SelectionKey(MqlSymbol symbol) =>
        $"{symbol.SelectionRange.Start?.Line}:{symbol.SelectionRange.Start?.Character}";

    private static bool SameSelection(MqlSymbol symbol, CallHierarchyItem item) =>
        symbol.SelectionRange?.Start?.Line == item.SelectionRange?.Start?.Line &&
        symbol.SelectionRange?.Start?.Character == item.SelectionRange?.Start?.Character;

    /// <summary>
    /// For every call-position occurrence in one file, yield the token's range.
    /// The file content is resolved store-first (open buffers win, #88) and
    /// read at most once per file per request.
    /// </summary>
    private List<CallSpan> CollectCallSpans(string filePath, IEnumerable<SymbolOccurrence> occurrences)
    {
        if (!TryGetContent(filePath, out var content))
        {
            return new List<CallSpan>();
        }

        var spans = new List<CallSpan>();
        foreach (var occurrence in occurrences)
        {
            if (TryGetCallSpan(content, occurrence.Line, occurrence.Column, occurrence.Length, out var callRange))
            {
                spans.Add(new CallSpan(occurrence.Line, occurrence.Column, occurrence.Length, callRange));
            }
        }

        return spans;
    }

    private bool TryGetContent(string filePath, out string content)
    {
        try
        {
            if (_documentStore.TryGetValue(new Uri("file://" + filePath), out _, out var stored, out _) &&
                stored != null)
            {
                content = stored;
                return true;
            }

            content = SourceFileReader.ReadAllText(filePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read occurrence file: {FilePath}", filePath);
            content = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Call-vs-mention: the identifier token is a call when the next
    /// non-whitespace character on the same line is '('.
    /// </summary>
    private static bool TryGetCallSpan(string content, int line0, int column0, int length, out OmniSharp.Extensions.LanguageServer.Protocol.Models.Range callSpan)
    {
        callSpan = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(new Position(0, 0), new Position(0, 0));
        var lines = content.Split('\n');
        if (line0 < 0 || line0 >= lines.Length)
        {
            return false;
        }

        var line = lines[line0];
        var end = column0 + length;
        if (end > line.Length)
        {
            return false;
        }

        for (var i = end; i < line.Length; i++)
        {
            var c = line[i];
            if (c == ' ' || c == '\t' || c == '\r')
            {
                continue;
            }

            if (c == '(')
            {
                callSpan = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(line0, column0),
                    new Position(line0, end));
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// Issue #172: per-file symbol lookup for a query-time file path. The
    /// index keys symbols by (file, language), so the language must be
    /// resolved with the same machinery used at indexing time
    /// (open-document store → .mqh includer routing → disk sniffing) instead
    /// of the 1-arg GetFileSymbols overload, which defaults to Mql4 and
    /// returns null for every MQL5 file.
    /// </summary>
    private List<MqlSymbol>? GetFileSymbolsForQuery(string filePath)
    {
        var language = ResolveLanguage(new Uri("file://" + filePath));
        return SymbolIndex.Index.GetFileSymbols(filePath, language);
    }

    private MqlSymbol? FindEnclosingFunction(string filePath, int line, int column)
    {
        var symbols = GetFileSymbolsForQuery(filePath);
        if (symbols == null)
        {
            return null;
        }

        return symbols.Where(IsFunctionLike)
            .Where(f => Contains(f.Range, line, column))
            .OrderBy(SizeOf)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolve a callee name to (file, selectionKey, item): an in-file
    /// declaration first, then the workspace index (include-declared and
    /// scan-indexed definitions). Unresolvable names (pure builtins,
    /// unknowns) return null.
    /// </summary>
    private (string File, string Selection, CallHierarchyItem Item)? ResolveCallee(
        MqlFile mqlFile, IMqlParser parser, string name, string callerFilePath)
    {
        var inCallee = parser.FindSymbolsByName(mqlFile, name).FirstOrDefault();
        if (inCallee != null)
        {
            var item = BuildItem(inCallee, callerFilePath);
            return (item.Uri.GetFileSystemPath(), SelectionKey(inCallee), item);
        }

        foreach (var candidate in SymbolIndex.Index.FindSymbol(name))
        {
            if (candidate.Symbol == null || string.IsNullOrEmpty(candidate.FilePath) ||
                !IsFunctionLike(candidate.Symbol))
            {
                continue;
            }

            if (!File.Exists(candidate.FilePath))
            {
                continue;
            }

            var item = BuildItem(candidate.Symbol, callerFilePath);
            return (candidate.FilePath, SelectionKey(candidate.Symbol), item);
        }

        return null;
    }

    private CallHierarchyItem? BuildItemFromIndex(string filePath, string selectionKey)
    {
        var symbols = GetFileSymbolsForQuery(filePath);
        if (symbols == null)
        {
            return null;
        }

        var match = symbols.FirstOrDefault(s => IsFunctionLike(s) && SelectionKey(s) == selectionKey)
                    ?? symbols.FirstOrDefault(s => SelectionKey(s) == selectionKey);
        if (match == null)
        {
            return null;
        }

        return new CallHierarchyItem
        {
            Name = match.Name,
            Kind = match.Kind,
            Detail = match.Detail,
            Uri = DocumentUri.File(filePath),
            Range = match.Range,
            SelectionRange = SelectionRangeOrFallback(match)
        };
    }

    private bool IsBuiltinAny(string name) =>
        _builtinsRegistry.Any(b => b.IsBuiltin(name));

    private static bool IsFunctionLike(MqlSymbol s) =>
        s.Kind == SymbolKind.Function || s.Kind == SymbolKind.Method;

    private static int SizeOf(MqlSymbol s) =>
        (s.Range.End?.Line ?? 0) - (s.Range.Start?.Line ?? 0);

    private static bool Contains(OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range, int line, int column)
    {
        if (range?.Start == null || range.End == null)
        {
            return false;
        }

        if (line < range.Start.Line || line > range.End.Line)
        {
            return false;
        }

        if (line == range.Start.Line && column < range.Start.Character)
        {
            return false;
        }

        if (line == range.End.Line && column > range.End.Character)
        {
            return false;
        }

        return true;
    }

    private readonly record struct CallSpan(int Line, int Column, int Length, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range Range);
}