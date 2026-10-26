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
/// LSP 3.17 type hierarchy (issue #123): prepareTypeHierarchy,
/// typeHierarchy/supertypes and typeHierarchy/subtypes, mirroring the
/// callHierarchy handler's structure (#96) and reusing the type-resolution
/// machinery that already exists (#64/#86/#89/#90):
///
/// <para>prepareTypeHierarchy resolves the type symbol under the cursor —
/// in-file first, then include-declared/workspace-indexed. Only type
/// candidates (Class/Struct/Interface, plus MQL4 class symbols whose
/// SymbolType stays null per CCR-05) prepare an item.</para>
///
/// <para>supertypes walks the item's captured <see cref="MqlSymbol.BaseClass"/>
/// (issue #123) through <see cref="TypeDeclarationResolver.TryResolveTypeLocation"/>,
/// which enforces the #89 include-reachability guard for cross-file bases.
/// Bases that do not resolve in the workspace (system stdlib classes such as
/// CObject when stdlib.mqh is not on disk) are skipped — one level per
/// request; clients iterate.</para>
///
/// <para>subtypes scans the workspace index (GetIndexedFiles × GetFileSymbols)
/// for type symbols whose BaseClass matches the item's name
/// (case-insensitive, like the MQL compiler's identifier resolution).</para>
/// </summary>
public class TypeHierarchyHandler : LanguageAwareHandlerBase<TypeHierarchyPrepareParams, Container<TypeHierarchyItem>?>,
    ITypeHierarchyPrepareHandler, ITypeHierarchySupertypesHandler, ITypeHierarchySubtypesHandler
{
    private readonly ILogger<TypeHierarchyHandler> _logger;
    private readonly IMqlBuiltins[] _builtinsRegistry;

    public TypeHierarchyHandler(
        ILogger<TypeHierarchyHandler> logger,
        MqlLanguageService languageService,
        OpenDocumentStore documentStore,
        IMqlBuiltins[] builtins,
        GlobalSymbolIndexAccessor? symbolIndex = null)
        : base(languageService, documentStore, builtins, symbolIndex ?? new GlobalSymbolIndexAccessor())
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _builtinsRegistry = builtins;
        _logger.LogInformation("TypeHierarchyHandler initialized");
    }

    public TypeHierarchyRegistrationOptions GetRegistrationOptions(TypeHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TypeHierarchyRegistrationOptions
        {
            WorkDoneProgress = false
        };
    }

    // Same contract as CallHierarchyHandler: one handler, no fan-out, so a
    // stable id and a no-op capability capture are the correct implementations.
    private readonly Guid _handlerId = Guid.NewGuid();
    Guid OmniSharp.Extensions.LanguageServer.Protocol.Models.ICanBeIdentifiedHandler.Id => _handlerId;
    void OmniSharp.Extensions.LanguageServer.Protocol.ICapability<TypeHierarchyCapability>.SetCapability(
        TypeHierarchyCapability capability, ClientCapabilities clientCapabilities)
    {
        // Nothing to capture: the handler is language-agnostic per request.
    }

    public Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchyPrepareParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.ToUri();
        var language = ResolveLanguage(uri);
        return Task.FromResult(HandleForLanguage(request, language, cancellationToken));
    }

    protected override Container<TypeHierarchyItem>? HandleForLanguage(
        TypeHierarchyPrepareParams request, MqlLanguage language, CancellationToken cancellationToken)
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

            var occurrence = mqlFile.Occurrences.FirstOrDefault(o =>
                o.Line == line0 && o.Column <= character0 && character0 < o.Column + o.Length);
            if (occurrence == null)
            {
                _logger.LogDebug("prepareTypeHierarchy: no identifier token at {Line}:{Character}", line0, character0);
                return null;
            }

            var symbol = parser.FindSymbolsByName(mqlFile, occurrence.Text).FirstOrDefault(IsTypeCandidate);
            if (symbol == null)
            {
                var includeResolved = IncludeSymbolResolver.TryResolve(mqlFile, line0, character0, SymbolIndex.Index);
                symbol = includeResolved != null && IsTypeCandidate(includeResolved) ? includeResolved : null;
            }
            if (symbol == null)
            {
                _logger.LogDebug("prepareTypeHierarchy: identifier '{Identifier}' is not a type", occurrence.Text);
                return null;
            }

            var item = BuildItem(symbol, filePath);
            _logger.LogDebug("prepareTypeHierarchy: prepared '{Name}' at {Uri}", item.Name, item.Uri);
            return new Container<TypeHierarchyItem>(item);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing prepareTypeHierarchy for {Uri}", request.TextDocument.Uri);
            return null;
        }
    }

    public Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySupertypesParams request, CancellationToken cancellationToken)
    {
        try
        {
            var item = request.Item;
            if (item?.Name == null)
            {
                return Task.FromResult<Container<TypeHierarchyItem>?>(null);
            }

            var symbol = ResolveItemSymbol(item);
            if (symbol?.BaseClass == null)
            {
                return Task.FromResult(Empty());
            }

            // Cross-file resolution with the #89 include-reachability guard;
            // the base may sit in the declaring file itself or in an included
            // header. Unresolvable bases (stdlib types not on disk) are skipped.
            var filePath = item.Uri.GetFileSystemPath();
            var currentFile = TryGetFile(filePath, out var file) ? file : null;
            if (currentFile == null)
            {
                return Task.FromResult(Empty());
            }

            if (TypeDeclarationResolver.TryResolveTypeLocation(
                    symbol.BaseClass, currentFile, DocumentUri.File(filePath), SymbolIndex.Index, out var location))
            {
                var supertype = BuildItemFromLocation(symbol.BaseClass, location);
                if (supertype != null)
                {
                    _logger.LogDebug("supertypes for '{Name}': 1 (base {Base})", item.Name, symbol.BaseClass);
                    return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(supertype));
                }
            }

            _logger.LogDebug("supertypes for '{Name}': base '{Base}' does not resolve in the workspace", item.Name, symbol.BaseClass);
            return Task.FromResult(Empty());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing typeHierarchy/supertypes");
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }
    }

    public Task<Container<TypeHierarchyItem>?> Handle(TypeHierarchySubtypesParams request, CancellationToken cancellationToken)
    {
        try
        {
            var item = request.Item;
            if (item?.Name == null)
            {
                return Task.FromResult<Container<TypeHierarchyItem>?>(null);
            }

            // Workspace-wide inverse scan over type symbols: every indexed
            // class/struct/interface whose captured BaseClass matches the
            // item's name (case-insensitive — MQL identifiers resolve that
            // way). Workspace-sized scans are the documented tier-2 behavior;
            // a dedicated base-name bucket is a possible optimization.
            var subtypes = new List<TypeHierarchyItem>();
            foreach (var (filePath, language) in SymbolIndex.Index.GetIndexedFileKeys())
            {
                var symbols = SymbolIndex.Index.GetFileSymbols(filePath, language);
                if (symbols == null)
                {
                    continue;
                }

                foreach (var symbol in EnumerateTypeSymbols(symbols))
                {
                    if (string.Equals(symbol.BaseClass, item.Name, StringComparison.OrdinalIgnoreCase) &&
                        File.Exists(filePath))
                    {
                        subtypes.Add(BuildItem(symbol, filePath));
                    }
                }
            }

            _logger.LogDebug("subtypes for '{Name}': {Count}", item.Name, subtypes.Count);
            return Task.FromResult<Container<TypeHierarchyItem>?>(new Container<TypeHierarchyItem>(subtypes));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing typeHierarchy/subtypes");
            return Task.FromResult<Container<TypeHierarchyItem>?>(null);
        }
    }

    private static Container<TypeHierarchyItem>? Empty() =>
        new(Array.Empty<TypeHierarchyItem>());

    /// <summary>
    /// Re-resolve the symbol the client's item refers to: match by selection
    /// range first (clients echo the item back), else the first same-name
    /// type symbol in the item's file.
    /// </summary>
    private MqlSymbol? ResolveItemSymbol(TypeHierarchyItem item)
    {
        var itemUri = item.Uri;
        if (itemUri is null || item.Name == null)
        {
            return null;
        }
        var filePath = itemUri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || !TryGetFile(filePath, out var file))
        {
            return null;
        }

        var language = ResolveLanguage(itemUri.ToUri());
        var parser = ResolveParser(language);

        return parser.FindSymbolsByName(file, item.Name)
            .Where(IsTypeCandidate)
            .OrderBy(s => SameSelection(s, item) ? 0 : 1)
            .FirstOrDefault()
            ?? SymbolIndex.Index.GetFileSymbols(filePath, language)
                ?.Where(IsTypeCandidate)
                .FirstOrDefault(s => string.Equals(s.Name, item.Name, StringComparison.OrdinalIgnoreCase));
    }

    private bool TryGetFile(string filePath, out MqlFile file)
    {
        var uri = new Uri("file://" + filePath);
        var language = ResolveLanguage(uri);
        var parser = ResolveParser(language);
        return TryGetDocumentContent(uri, filePath, parser, language, out file, out _);
    }

    private TypeHierarchyItem? BuildItemFromLocation(string name, Location location)
    {
        var filePath = location.Uri.GetFileSystemPath();
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath) || !TryGetFile(filePath, out var file))
        {
            return null;
        }

        var symbol = file.Symbols.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) &&
            s.Range.Start?.Line == location.Range.Start?.Line &&
            s.Range.Start?.Character == location.Range.Start?.Character);
        if (symbol == null)
        {
            return null;
        }

        return BuildItem(symbol, filePath);
    }

    private TypeHierarchyItem BuildItem(MqlSymbol symbol, string fallbackFilePath)
    {
        var filePath = !string.IsNullOrEmpty(symbol.FilePath) ? symbol.FilePath : fallbackFilePath;
        return new TypeHierarchyItem
        {
            Name = symbol.Name,
            Kind = symbol.Kind,
            Detail = symbol.Detail,
            Tags = null,
            Uri = DocumentUri.File(filePath),
            Range = symbol.Range,
            SelectionRange = SelectionRangeOrFallback(symbol)
        };
    }

    private static OmniSharp.Extensions.LanguageServer.Protocol.Models.Range SelectionRangeOrFallback(MqlSymbol symbol) =>
        symbol.SelectionRange != null && symbol.SelectionRange.Start != null && symbol.SelectionRange.End != null
            ? symbol.SelectionRange
            : symbol.Range;

    private static bool SameSelection(MqlSymbol symbol, TypeHierarchyItem item) =>
        symbol.SelectionRange?.Start?.Line == item.SelectionRange?.Start?.Line &&
        symbol.SelectionRange?.Start?.Character == item.SelectionRange?.Start?.Character;

    private static bool SameFile(MqlSymbol symbol, string filePath) =>
        string.Equals(symbol.FilePath, filePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Type-candidate filter (CCR-05-aware): Class/Struct/Interface via
    /// SymbolType, plus MQL4 class symbols whose SymbolType stays null.</summary>
    private static bool IsTypeCandidate(MqlSymbol s) =>
        s.SymbolType is SymbolType.Class or SymbolType.Struct or SymbolType.Interface
        || (s.SymbolType == null && s.Kind == SymbolKind.Class);

    /// <summary>Depth-first walk of a file's symbol tree for type symbols
    /// (nested classes included).</summary>
    private static IEnumerable<MqlSymbol> EnumerateTypeSymbols(IEnumerable<MqlSymbol> symbols)
    {
        foreach (var symbol in symbols)
        {
            if (IsTypeCandidate(symbol))
            {
                yield return symbol;
            }

            foreach (var child in EnumerateTypeSymbols(symbol.Children))
            {
                yield return child;
            }
        }
    }
}
