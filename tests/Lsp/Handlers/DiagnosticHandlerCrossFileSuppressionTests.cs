using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Newtonsoft.Json.Linq;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using Xunit;
using MqlLanguageServer.Lsp.Handlers;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using Range = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace MqlLanguageServer.Tests.Lsp.Handlers;

/// <summary>
/// Issue #44: workspace-correlated suppression of cross-file unresolved-symbol
/// false positives in <see cref="DiagnosticHandler"/> (handler-level filter,
/// architecture B). Tier 1 = symbol declared in the document's include closure;
/// tier 2 = symbol declared anywhere in the indexed workspace (same language).
/// Uses the cross-file fixtures under tests/fixtures/cross-file and the shared
/// <see cref="GlobalSymbolIndex.Instance"/> singleton (the backward-compatible
/// test constructors fall back to it through <see cref="GlobalSymbolIndexAccessor"/>),
/// so every test runs inside the "GlobalSymbolIndex Tests" collection and clears
/// the index first for isolation.
/// </summary>
[Collection("GlobalSymbolIndex Tests")]
public class DiagnosticHandlerCrossFileSuppressionTests
{
    private const string UnresolvedSymbolCodeMql4 = "1070"; // Mql4DiagnosticBase + UnresolvedSymbolOffset

    #region Tier 1 — include closure

    [Fact]
    public async Task Handle_SymbolDeclaredInIncludedHeader_IsSuppressed()
    {
        GlobalSymbolIndex.Instance.Clear();

        var libPath = GetFixtureFilePath("cross-file/trade_lib.mqh");
        var utilPath = GetFixtureFilePath("cross-file/nested/util.mqh");
        IndexHeader(libPath, "TradeOpen");
        IndexHeader(utilPath, "NormalizeVolume");

        var items = await GetDiagnosticsAsync(GetFixtureFilePath("cross-file/ea_main.mq4"));

        // The header-declared symbols (first- and second-level include) are suppressed.
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "TradeOpen"));
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "NormalizeVolume"));
    }

    [Fact]
    public async Task Handle_GenuinelyUndeclaredSymbol_StillYieldsDiagnostic()
    {
        GlobalSymbolIndex.Instance.Clear();

        var libPath = GetFixtureFilePath("cross-file/trade_lib.mqh");
        var utilPath = GetFixtureFilePath("cross-file/nested/util.mqh");
        IndexHeader(libPath, "TradeOpen");
        IndexHeader(utilPath, "NormalizeVolume");

        var items = await GetDiagnosticsAsync(GetFixtureFilePath("cross-file/ea_main.mq4"));

        // No blanket suppression: a name declared nowhere in the closure or
        // workspace keeps its unresolved-symbol diagnostic.
        Assert.Contains(items, d => IsUnresolvedFor(d, "TotallyMissingSymbol"));
    }

    #endregion

    #region Fallback — empty index

    [Fact]
    public async Task Handle_EmptyIndex_ColdOpen_KeepsDiagnosticsUnchanged()
    {
        GlobalSymbolIndex.Instance.Clear();

        var items = await GetDiagnosticsAsync(GetFixtureFilePath("cross-file/ea_main.mq4"));

        // D5 monotonicity: a cold open behaves exactly like the document-local
        // analysis — header-declared symbols are still flagged.
        Assert.Contains(items, d => IsUnresolvedFor(d, "TradeOpen"));
        Assert.Contains(items, d => IsUnresolvedFor(d, "NormalizeVolume"));
    }

    #endregion

    #region Tier 2 — workspace correlation

    [Fact]
    public async Task Handle_SymbolDeclaredOnlyInNonIncludedWorkspaceFile_IsSuppressed()
    {
        GlobalSymbolIndex.Instance.Clear();

        // Indexed under a path that is NOT part of ea_main.mq4's include
        // closure (the file need not exist on disk: the index is name-keyed,
        // like the synthetic paths used in GlobalSymbolIndex tests).
        var unrelatedPath = Path.Combine(GetFixtureFilePath("cross-file"), "unrelated_workspace_file.mqh");
        IndexHeader(unrelatedPath, "TotallyMissingSymbol");

        var items = await GetDiagnosticsAsync(GetFixtureFilePath("cross-file/ea_main.mq4"));

        // Tier 2: declared only in a non-included workspace file, same language.
        Assert.DoesNotContain(items, d => IsUnresolvedFor(d, "TotallyMissingSymbol"));
        // Tier 1 miss: TradeOpen is not indexed at all, so it stays flagged.
        Assert.Contains(items, d => IsUnresolvedFor(d, "TradeOpen"));
    }

    #endregion

    #region Correlator unit tests — fail-open payload handling

    [Fact]
    public void Correlator_MalformedOrAbsentDataPayload_KeepsDiagnostic()
    {
        GlobalSymbolIndex.Instance.Clear();
        var index = GlobalSymbolIndex.Instance;
        // Tier 2 will resolve this name, so only a payload failure can keep a
        // diagnostic for it.
        index.AddFile(
            "/ws/other/header.mqh", MqlLanguage.Mql4,
            new List<MqlSymbol> { new() { Name = "TradeOpen", FilePath = "/ws/other/header.mqh" } });

        var wellFormed = CreateUnresolvedDiagnostic("TradeOpen", Data("{\"symbol\":\"TradeOpen\"}"));
        var malformedJson = CreateUnresolvedDiagnostic("TradeOpen", Data("broken json {{{"));
        var missingProperty = CreateUnresolvedDiagnostic("TradeOpen", Data("{\"other\": 1}"));
        var nullData = CreateUnresolvedDiagnostic("TradeOpen", null);

        var result = CrossFileSymbolCorrelator.SuppressWorkspaceResolvable(
            new[] { wellFormed, malformedJson, missingProperty, nullData },
            mqlFile: null,
            documentPath: GetFixtureFilePath("cross-file/ea_main.mq4"),
            MqlLanguage.Mql4,
            index,
            CancellationToken.None);

        // Well-formed payload suppressed (tier 2); every payload failure is
        // kept (fail open).
        var kept = result.ToList();
        Assert.DoesNotContain(kept, d => ReferenceEquals(d, wellFormed));
        Assert.Contains(kept, d => ReferenceEquals(d, malformedJson));
        Assert.Contains(kept, d => ReferenceEquals(d, missingProperty));
        Assert.Contains(kept, d => ReferenceEquals(d, nullData));
    }

    [Fact]
    public void Correlator_NullDocumentPath_ReturnsDiagnosticsUnchanged()
    {
        GlobalSymbolIndex.Instance.Clear();
        var index = GlobalSymbolIndex.Instance;
        index.AddFile(
            "/ws/other/header.mqh", MqlLanguage.Mql4,
            new List<MqlSymbol> { new() { Name = "TradeOpen", FilePath = "/ws/other/header.mqh" } });

        var diagnostic = CreateUnresolvedDiagnostic("TradeOpen", Data("{\"symbol\":\"TradeOpen\"}"));

        var result = CrossFileSymbolCorrelator.SuppressWorkspaceResolvable(
            new[] { diagnostic },
            mqlFile: null,
            documentPath: null,
            MqlLanguage.Mql4,
            index,
            CancellationToken.None);

        Assert.Single(result);
        Assert.Same(diagnostic, result[0]);
    }

    #endregion

    #region Helpers

    private static async Task<List<Diagnostic>> GetDiagnosticsAsync(string filePath)
    {
        var logger = Substitute.For<ILogger<DiagnosticHandler>>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var documentStore = new OpenDocumentStore();
        var handler = new DiagnosticHandler(logger, serviceProvider, documentStore);

        var uri = DocumentUri.FromFileSystemPath(filePath);
        var request = new DocumentDiagnosticParams { TextDocument = new TextDocumentIdentifier(uri) };
        var report = Assert.IsType<RelatedFullDocumentDiagnosticReport>(
            await handler.Handle(request, CancellationToken.None));

        return report.Items.ToList();
    }

    private static void IndexHeader(string filePath, params string[] symbolNames)
    {
        var symbols = symbolNames
            .Select(name => new MqlSymbol { Name = name, FilePath = filePath })
            .ToList();
        GlobalSymbolIndex.Instance.AddFile(filePath, MqlLanguage.Mql4, symbols);
    }

    /// <summary>
    /// True when the diagnostic is an unresolved-symbol (base+70) diagnostic
    /// whose structured Data payload names <paramref name="symbol"/>.
    /// </summary>
    private static bool IsUnresolvedFor(Diagnostic diagnostic, string symbol)
    {
        if (diagnostic.Code != UnresolvedSymbolCodeMql4 || diagnostic.Data == null)
        {
            return false;
        }

        return string.Equals(diagnostic.Data["symbol"]?.ToString(), symbol, StringComparison.Ordinal);
    }

    private static Diagnostic CreateUnresolvedDiagnostic(string symbol, JToken? data)
    {
        return new Diagnostic
        {
            Range = new Range(9, 4, 9, 4 + symbol.Length),
            Severity = DiagnosticSeverity.Error,
            Message = $"Undeclared symbol '{symbol}' is not defined in this document.",
            Code = UnresolvedSymbolCodeMql4,
            Source = "mql-lsp",
            Data = data
        };
    }

    /// <summary>
    /// Wraps raw payload text in a JValue so <c>Data.ToString()</c> yields
    /// exactly that text: valid JSON strings parse in the correlator, malformed
    /// ones fail <c>JsonDocument.Parse</c> (the fail-open path).
    /// </summary>
    private static JToken Data(string raw) => new JValue(raw);

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
