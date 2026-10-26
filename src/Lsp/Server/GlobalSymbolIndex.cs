using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using MqlLanguageServer.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Thread-safe global symbol index for cross-file symbol tracking
/// Tracks symbols across all open MQL files and their includes
/// </summary>
public class GlobalSymbolIndex
{
    private static GlobalSymbolIndex? _instance;
    private static readonly object _lock = new object();

    // Thread-safe storage: (file path, language) -> list of symbols in that file
    private readonly ConcurrentDictionary<(string FilePath, MqlLanguage Language), List<MqlSymbol>> _symbolsByFile = new();

    // Thread-safe index: symbol name -> list of occurrences across (file, language)
    private readonly ConcurrentDictionary<string, List<SymbolLocation>> _symbolsByName = new();

    // A-010: secondary index (name, language) -> locations, for O(1) FindSymbol(name, lang).
    // The primary _symbolsByName is kept for the cross-language FindSymbol(name) overload.
    private readonly ConcurrentDictionary<(string Name, MqlLanguage Language), List<SymbolLocation>> _symbolsByNameAndLanguage = new();

    // OCC-03: occurrence index, name -> occurrences across all indexed files.
    private readonly ConcurrentDictionary<string, List<SymbolOccurrence>> _occurrencesByName = new();

    // OCC-03: per-file occurrence store, (file, language) -> occurrences.
    // Re-AddFile replaces this file's list so stale entries never survive.
    private readonly ConcurrentDictionary<(string FilePath, MqlLanguage Language), List<SymbolOccurrence>> _occurrencesByFile = new();

    // Track includes/dependencies between files
    private readonly ConcurrentDictionary<string, List<string>> _fileDependencies = new();

    private GlobalSymbolIndex() { }

    /// <summary>
    /// Isolated instance for instrumentation (OCC-06 measurement harness):
    /// allows the test assembly to create a fresh, non-singleton index so a
    /// measurement pass cannot mutate or observe production singleton state.
    /// Internal by design; production code always uses <see cref="Instance"/>.
    /// </summary>
    internal GlobalSymbolIndex(bool ctorBypass) { }

    /// <summary>
    /// Singleton instance (thread-safe)
    /// </summary>
    public static GlobalSymbolIndex Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new GlobalSymbolIndex();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Add or update symbols from a file for a specific language.
    /// </summary>
    public void AddFile(string filePath, MqlLanguage language, List<MqlSymbol> symbols)
    {
        AddFile(filePath, language, symbols, null);
    }

    /// <summary>
    /// Add or update symbols and identifier occurrences from a file
    /// (OCC-03). Occurrences are upserted name-keyed alongside definitions;
    /// the file's previous occurrence list is replaced wholesale so stale
    /// entries do not survive content changes.
    /// </summary>
    public void AddFile(string filePath, MqlLanguage language, List<MqlSymbol> symbols, List<SymbolOccurrence>? occurrences)
    {
        if (string.IsNullOrEmpty(filePath) || symbols == null)
            return;

        var key = (filePath, language);
        var occList = occurrences ?? new List<SymbolOccurrence>();

        // OCC-04/D2: mark occurrences that fall inside a definition's
        // SelectionRange of the same name in the same file as definitions
        // (fallback: inside Range). No query-time recomputation.
        // Issue #126 follow-up: the definition/name-index sets include enum
        // members (see CollectNameIndexedSymbols) so nested declarations are
        // resolvable workspace-wide without leaking function/method locals.
        var definitionsByName = new Dictionary<string, List<MqlSymbol>>(StringComparer.OrdinalIgnoreCase);
        var nameIndexedSymbols = new List<MqlSymbol>();
        CollectNameIndexedSymbols(symbols, nameIndexedSymbols);
        foreach (var symbol in nameIndexedSymbols)
        {
            if (string.IsNullOrEmpty(symbol.Name))
                continue;
            if (!definitionsByName.TryGetValue(symbol.Name, out var list))
            {
                list = new List<MqlSymbol>();
                definitionsByName[symbol.Name] = list;
            }
            list.Add(symbol);
        }

        lock (_lock)
        {
            // Store symbols by (file, language)
            _symbolsByFile.AddOrUpdate(key, symbols, (key, existing) =>
            {
                existing.Clear();
                existing.AddRange(symbols);
                return existing;
            });

            // OCC-03: replace the file's occurrence list wholesale.
            _occurrencesByFile.AddOrUpdate(key, occList, (key, existing) =>
            {
                // Remove this file's occurrences from the name buckets.
                foreach (var old in existing)
                {
                    if (string.IsNullOrEmpty(old.Text))
                        continue;
                    _occurrencesByName.AddOrUpdate(old.Text, new List<SymbolOccurrence>(), (k, bucket) =>
                    {
                        lock (bucket)
                        {
                            bucket.RemoveAll(o => o.FilePath == filePath && o.Language == language);
                            if (!bucket.Any())
                                _occurrencesByName.TryRemove(k, out _);
                            return bucket;
                        }
                    });
                }
                existing.Clear();
                existing.AddRange(occList);
                return existing;
            });

            // Refresh name buckets for the new occurrences.
            foreach (var occurrence in occList)
            {
                if (string.IsNullOrEmpty(occurrence.Text))
                    continue;

                // OCC-04: definition marking at index time via SelectionRange overlap.
                occurrence.IsDefinition = definitionsByName.TryGetValue(occurrence.Text, out var defs) &&
                    definitionsByName.ContainsKey(occurrence.Text) &&
                    definitionsByName[occurrence.Text].Any(d => IsPositionInOverlap(occurrence.Line, occurrence.Column, d));

                _occurrencesByName.AddOrUpdate(occurrence.Text, new List<SymbolOccurrence> { occurrence }, (k, bucket) =>
                {
                    lock (bucket)
                    {
                        bucket.RemoveAll(o => o.FilePath == filePath && o.Language == language && ReferenceEquals(o, occurrence) == false && o.Line == occurrence.Line && o.Column == occurrence.Column);
                        if (!bucket.Any(o => ReferenceEquals(o, occurrence)))
                        {
                            bucket.Add(occurrence);
                        }
                        return bucket;
                    }
                });
            }

            // Update symbol name index
            // Issue #126 follow-up: iterate the name-indexed set (top-level
            // symbols plus enum members) instead of the top-level list only.
            foreach (var symbol in nameIndexedSymbols)
            {
                if (string.IsNullOrEmpty(symbol.Name))
                    continue;

                var location = new SymbolLocation
                {
                    FilePath = filePath,
                    Language = language,
                    Symbol = symbol
                };

                _symbolsByName.AddOrUpdate(symbol.Name, new List<SymbolLocation> { location }, (key, existing) =>
                {
                    lock (existing)
                    {
                        // Remove old location for this (file, language) if it exists
                        existing.RemoveAll(loc => loc.FilePath == filePath && loc.Language == language);
                        existing.Add(location);
                        return existing;
                    }
                });

                // A-010: secondary (name, language) index for O(1) language-filtered lookup.
                var langKey = (symbol.Name, language);
                _symbolsByNameAndLanguage.AddOrUpdate(langKey, new List<SymbolLocation> { location }, (key, existing) =>
                {
                    lock (existing)
                    {
                        existing.RemoveAll(loc => loc.FilePath == filePath && loc.Language == language);
                        existing.Add(location);
                        return existing;
                    }
                });
            }
        }
    }

    /// <summary>
    /// Issue #126 follow-up: symbols eligible for the workspace NAME index.
    /// Top-level symbols always qualify; among children, only enum members
    /// (Kind EnumMember, plus members of nested enums) cross the boundary.
    /// Deliberately NOT flattened: class/struct fields and method locals —
    /// indexing them would change completion/references/rename behavior and
    /// needs its own impact analysis. The file symbol tree itself
    /// (<c>_symbolsByFile</c>) is unaffected and keeps the full hierarchy.
    /// </summary>
    private static void CollectNameIndexedSymbols(IEnumerable<MqlSymbol> symbols, List<MqlSymbol> into)
    {
        foreach (var symbol in symbols)
        {
            into.Add(symbol);

            if (symbol.Children is not { Count: > 0 })
            {
                continue;
            }

            foreach (var child in symbol.Children)
            {
                if (child.Kind == OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.EnumMember)
                {
                    into.Add(child);
                }
                else if (child.Kind == OmniSharp.Extensions.LanguageServer.Protocol.Models.SymbolKind.Enum)
                {
                    // Nested enum: descend so its members are indexed too.
                    CollectNameIndexedSymbols(new[] { child }, into);
                }
            }
        }
    }

    /// <summary>
    /// OCC-04/D2: true when (line, column) falls inside the symbol's
    /// SelectionRange (preferred) or its single-line Range fallback,
    /// mirroring the FP harness definition-tagging logic.
    /// </summary>
    private static bool IsPositionInOverlap(int line, int column, MqlSymbol symbol)
    {
        var sel = symbol.SelectionRange;
        if (sel?.Start != null && sel.End != null &&
            sel.Start.Line == line && column >= sel.Start.Character && column <= sel.End.Character)
        {
            return true;
        }

        var range = symbol.Range;
        if (range?.Start != null && range.End != null &&
            range.Start.Line == line && line == range.End.Line &&
            column >= range.Start.Character && column <= range.End.Character)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Find identifier occurrences of the given name across all languages
    /// (OCC-03). Returns a snapshot copy; callers may hold it safely.
    /// Name-keyed by design: same-name symbols and builtin-name collisions
    /// are documented ambiguity, not heuristically filtered (OCC-05).
    /// </summary>
    public List<SymbolOccurrence> FindOccurrences(string symbolName)
    {
        if (string.IsNullOrEmpty(symbolName))
            return new List<SymbolOccurrence>();

        return _occurrencesByName.TryGetValue(symbolName, out var bucket)
            ? bucket.ToList()
            : new List<SymbolOccurrence>();
    }

    /// <summary>
    /// Find identifier occurrences of the given name restricted to a
    /// language (OCC-03/REQ-SM-04).
    /// </summary>
    public List<SymbolOccurrence> FindOccurrences(string symbolName, MqlLanguage language)
    {
        if (string.IsNullOrEmpty(symbolName))
            return new List<SymbolOccurrence>();

        return _occurrencesByName.TryGetValue(symbolName, out var bucket)
            ? bucket.Where(o => o.Language == language).ToList()
            : new List<SymbolOccurrence>();
    }

    /// <summary>
    /// Add or update symbols from a file (defaults to MQL4 for backward compatibility).
    /// </summary>
    public void AddFile(string filePath, List<MqlSymbol> symbols)
    {
        AddFile(filePath, MqlLanguage.Mql4, symbols);
    }

    /// <summary>
    /// Remove a file from the index for a specific language.
    /// </summary>
    public void RemoveFile(string filePath, MqlLanguage language)
    {
        if (string.IsNullOrEmpty(filePath))
            return;

        var key = (filePath, language);

        lock (_lock)
        {
            // Remove from symbols by (file, language)
            _symbolsByFile.TryRemove(key, out var removedSymbols);

            // OCC-03: remove the file's occurrences symmetrically.
            _occurrencesByFile.TryRemove(key, out var removedOccurrences);
            if (removedOccurrences != null)
            {
                foreach (var occurrence in removedOccurrences)
                {
                    if (string.IsNullOrEmpty(occurrence.Text))
                        continue;

                    _occurrencesByName.AddOrUpdate(occurrence.Text, new List<SymbolOccurrence>(), (key, bucket) =>
                    {
                        lock (bucket)
                        {
                            bucket.RemoveAll(o => o.FilePath == filePath && o.Language == language);
                            if (!bucket.Any())
                                _occurrencesByName.TryRemove(key, out _);
                            return bucket;
                        }
                    });
                }
            }

            // Remove from name index
            if (removedSymbols != null)
            {
                foreach (var symbol in removedSymbols)
                {
                    if (string.IsNullOrEmpty(symbol.Name))
                        continue;

                    _symbolsByName.AddOrUpdate(symbol.Name, new List<SymbolLocation>(), (key, existing) =>
                    {
                        lock (existing)
                        {
                            existing.RemoveAll(loc => loc.FilePath == filePath && loc.Language == language);

                            // Remove the entry entirely if no more locations
                            if (!existing.Any())
                            {
                                _symbolsByName.TryRemove(key, out _);
                            }
                            return existing;
                        }
                    });

                    // A-010: remove from secondary (name, language) index.
                    var langKey = (symbol.Name, language);
                    _symbolsByNameAndLanguage.AddOrUpdate(langKey, new List<SymbolLocation>(), (key, existing) =>
                    {
                        lock (existing)
                        {
                            existing.RemoveAll(loc => loc.FilePath == filePath && loc.Language == language);
                            if (!existing.Any())
                            {
                                _symbolsByNameAndLanguage.TryRemove(key, out _);
                            }
                            return existing;
                        }
                    });
                }
            }

            // Remove dependencies
            _fileDependencies.TryRemove(filePath, out _);
            foreach (var kvp in _fileDependencies.ToList())
            {
                kvp.Value.RemoveAll(dep => dep == filePath);
            }
        }
    }

    /// <summary>
    /// Remove a file from the index (defaults to MQL4 for backward compatibility).
    /// </summary>
    public void RemoveFile(string filePath)
    {
        RemoveFile(filePath, MqlLanguage.Mql4);
    }

    /// <summary>
    /// Find all symbols with the given name restricted to a specific language.
    /// Uses the secondary (name, language) index for O(1) lookup (A-010).
    /// </summary>
    public List<SymbolLocation> FindSymbol(string symbolName, MqlLanguage language)
    {
        if (string.IsNullOrEmpty(symbolName))
            return new List<SymbolLocation>();

        return _symbolsByNameAndLanguage.TryGetValue((symbolName, language), out var locations)
            ? locations.ToList()
            : new List<SymbolLocation>();
    }

    /// <summary>
    /// Find all symbols with the given name across all languages (cross-language workspace query).
    /// </summary>
    public List<SymbolLocation> FindSymbol(string symbolName)
    {
        if (string.IsNullOrEmpty(symbolName))
            return new List<SymbolLocation>();

        return _symbolsByName.TryGetValue(symbolName, out var locations)
            ? locations.ToList()
            : new List<SymbolLocation>();
    }

    /// <summary>
    /// Find all references to a symbol restricted to a specific language.
    /// </summary>
    public List<SymbolLocation> FindAllReferences(string symbolName, MqlLanguage language)
    {
        return FindSymbol(symbolName, language);
    }

    /// <summary>
    /// Find all references to a symbol across all languages.
    /// </summary>
    public List<SymbolLocation> FindAllReferences(string symbolName)
    {
        return FindSymbol(symbolName);
    }

    /// <summary>
    /// Get all symbols from a specific file and language.
    /// </summary>
    public List<MqlSymbol>? GetFileSymbols(string filePath, MqlLanguage language)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        var key = (filePath, language);
        return _symbolsByFile.TryGetValue(key, out var symbols)
            ? symbols.ToList()
            : null;
    }

    /// <summary>
    /// Get all symbols from a specific file (defaults to MQL4).
    /// </summary>
    public List<MqlSymbol>? GetFileSymbols(string filePath)
    {
        return GetFileSymbols(filePath, MqlLanguage.Mql4);
    }

    /// <summary>
    /// Get all indexed files (returns distinct file paths).
    /// </summary>
    public List<string> GetIndexedFiles()
    {
        return _symbolsByFile.Keys.Select(k => k.FilePath).Distinct().ToList();
    }

    /// <summary>
    /// Get all indexed (file, language) keys.
    /// </summary>
    public List<(string FilePath, MqlLanguage Language)> GetIndexedFileKeys()
    {
        return _symbolsByFile.Keys.ToList();
    }

    /// <summary>
    /// Issue #16 Phase 2: get the language a file was indexed under, when it
    /// is indexed under exactly one language. Returns null when the file is
    /// not indexed, or when it is indexed under both languages (conflicting
    /// keys must not pick an arbitrary winner).
    /// </summary>
    public MqlLanguage? GetIndexedLanguage(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return null;

        MqlLanguage? found = null;
        foreach (var key in _symbolsByFile.Keys)
        {
            if (!string.Equals(key.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                continue;

            if (found.HasValue && found.Value != key.Language)
                return null; // conflicting keys: no unambiguous answer

            found = key.Language;
        }

        return found;
    }

    /// <summary>
    /// Issue #16 Phase 2: get the languages of all indexed files that include
    /// the given file, based on the dependency edges added via AddDependency.
    /// Used to route an unindexed .mqh by its includers at open time. Returns
    /// an empty list when nothing includes it (or the index has no edges).
    /// </summary>
    public List<MqlLanguage> GetIncluderLanguages(string filePath)
    {
        var result = new List<MqlLanguage>();
        if (string.IsNullOrEmpty(filePath))
            return result;

        foreach (var kvp in _fileDependencies)
        {
            if (!kvp.Value.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                continue;

            var language = GetIndexedLanguage(kvp.Key);
            if (language.HasValue && !result.Contains(language.Value))
                result.Add(language.Value);
        }

        return result;
    }

    /// <summary>
    /// Add dependency relationship (file A includes file B)
    /// </summary>
    public void AddDependency(string includingFile, string includedFile)
    {
        if (string.IsNullOrEmpty(includingFile) || string.IsNullOrEmpty(includedFile))
            return;

        _fileDependencies.AddOrUpdate(includingFile, new List<string> { includedFile },
            (key, existing) =>
            {
                if (!existing.Contains(includedFile))
                {
                    existing.Add(includedFile);
                }
                return existing;
            });
    }

    /// <summary>
    /// Get files included by the given file
    /// </summary>
    public List<string> GetDependencies(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new List<string>();

        return _fileDependencies.TryGetValue(filePath, out var deps)
            ? deps.ToList()
            : new List<string>();
    }

    /// <summary>
    /// Issue #90: get files that INCLUDE the given file (reverse lookup of
    /// <see cref="GetDependencies"/>). Used by the rename reachability guard:
    /// a cross-file rename edit is only emitted when the queried file and the
    /// occurrence file are connected through the include graph (either
    /// direction). Mirrors GetIncluderLanguages' linear scan; the graph is
    /// small (one edge list per indexed file).
    /// </summary>
    public List<string> GetDependentFiles(string filePath)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(filePath))
            return result;

        foreach (var kvp in _fileDependencies)
        {
            if (kvp.Value.Contains(filePath, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(kvp.Key);
            }
        }

        return result;
    }

    /// <summary>
    /// Clear all indexed data
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _symbolsByFile.Clear();
            _symbolsByName.Clear();
            _symbolsByNameAndLanguage.Clear();
            _occurrencesByName.Clear();
            _occurrencesByFile.Clear();
            _fileDependencies.Clear();
        }
    }

    /// <summary>
    /// Get statistics about the index
    /// </summary>
    public IndexStatistics GetStatistics()
    {
        return new IndexStatistics
        {
            FileCount = _symbolsByFile.Count,
            SymbolCount = _symbolsByName.Sum(kvp => kvp.Value.Count),
            UniqueSymbolCount = _symbolsByName.Count,
            DependencyCount = _fileDependencies.Sum(kvp => kvp.Value.Count)
        };
    }

    /// <summary>
    /// Get all symbols from all indexed files as (Uri, symbols) pairs
    /// </summary>
    public IEnumerable<(Uri Uri, MqlLanguage Language, List<MqlSymbol> Symbols)> GetAllSymbols()
    {
        foreach (var kvp in _symbolsByFile)
        {
            if (Uri.TryCreate(kvp.Key.FilePath, UriKind.Absolute, out var uri))
            {
                yield return (uri, kvp.Key.Language, kvp.Value);
            }
        }
    }
}

/// <summary>
/// Represents a symbol location (file + language + symbol data)
/// </summary>
public class SymbolLocation
{
    public string FilePath { get; set; } = string.Empty;
    public MqlLanguage Language { get; set; }
    public MqlSymbol Symbol { get; set; } = new();
}

/// <summary>
/// OCC-04: index payload for a single identifier occurrence (code position).
/// IsDefinition is marked at AddFile time via SelectionRange overlap (D2).
/// </summary>
public class SymbolOccurrence
{
    public string FilePath { get; set; } = string.Empty;
    public MqlLanguage Language { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Line { get; set; }
    public int Column { get; set; }
    public int Length { get; set; }
    public bool IsDefinition { get; set; }
}

/// <summary>
/// Statistics about the global symbol index
/// </summary>
public class IndexStatistics
{
    public int FileCount { get; set; }
    public int SymbolCount { get; set; }
    public int UniqueSymbolCount { get; set; }
    public int DependencyCount { get; set; }

    public override string ToString()
    {
        return $"Files: {FileCount}, Symbols: {SymbolCount}, Unique: {UniqueSymbolCount}, Dependencies: {DependencyCount}";
    }
}
