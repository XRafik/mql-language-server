using System;
using System.Collections.Generic;
using System.Threading;

using Microsoft.Extensions.Logging;
using MqlLanguageServer.Analysis;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Shared document-diagnostics generation (issue #91).
///
/// <para>The exact rule set previously private to <c>DiagnosticHandler</c>
/// (pull model): parser syntax errors, MQL-native semantic rules (#28) with
/// workspace-correlated suppression (#44), the line-scan heuristics
/// (typo / empty OnInit / underscore variable), and the unresolved
/// system-include warnings (#179). Extracted so the push-model
/// publisher (<see cref="DiagnosticPublisher"/>, issue #91) emits the SAME
/// diagnostics over <c>textDocument/publishDiagnostics</c> as the pull model
/// returns over <c>textDocument/diagnostic</c> — one rule set, two
/// channels.</para>
///
/// <para>A-007: LSP 3.17 diagnostic codes are string|number. We emit numeric
/// codes in dedicated ranges so clients can route/interpret them without
/// parsing prefixes: MQL4 diagnostics 1000-1999, MQL5 5000-5999. Offsets are
/// shared across both languages (001 typo, 002 empty OnInit, 003 underscore —
/// rule retired, code reserved; 004 unresolved system include (#179); +100
/// for syntax errors).</para>
/// </summary>
public static class DocumentDiagnostics
{
    public const int Mql4DiagnosticBase = 1000;
    public const int Mql5DiagnosticBase = 5000;

    /// <summary>
    /// Parse-level diagnostics only (issue #150): the ANTLR syntax errors of a
    /// parsed model, already projected to the dialect's diagnostic code range
    /// (1100 for MQL4, 5100 for MQL5).
    ///
    /// <para>Used by the pull channel as the degraded-but-honest answer when
    /// the full rule set overruns its generation budget: parse errors are the
    /// cheapest and most actionable signal, and returning them instead of an
    /// empty report guarantees a budget overrun can never masquerade as
    /// "document is clean".</para>
    /// </summary>
    public static List<Diagnostic> GenerateSyntaxErrors(
        MqlFile? mqlFile,
        MqlLanguage language,
        CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();
        var baseCode = language == MqlLanguage.Mql5 ? Mql5DiagnosticBase : Mql4DiagnosticBase;

        // ANTLR uses 1-based lines and 0-based columns; LSP uses 0-based for both.
        if (mqlFile?.SyntaxErrors == null)
        {
            return diagnostics;
        }

        foreach (var syntaxError in mqlFile.SyntaxErrors)
        {
            token.ThrowIfCancellationRequested();

            var length = syntaxError.OffendingSymbol?.Length ?? 1;
            diagnostics.Add(new Diagnostic
            {
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    syntaxError.Line - 1, syntaxError.Column,
                    syntaxError.Line - 1, syntaxError.Column + length),
                Severity = DiagnosticSeverity.Error,
                Message = syntaxError.Message,
                Code = (baseCode + 100).ToString(),  // 1100 for MQL4, 5100 for MQL5
                Source = "mql-lsp"
            });
        }

        return diagnostics;
    }

    /// <summary>
    /// Generate the full diagnostic set for one document state (model +
    /// content MUST come from the same source — for open documents, the
    /// store's buffer, per the #86 buffer-vs-disk rule).
    /// </summary>
    public static List<Diagnostic> Generate(
        ILogger logger,
        MqlFile? mqlFile,
        string content,
        MqlLanguage language,
        CancellationToken token,
        string? documentPath,
        SemanticAnalyzer? semanticAnalyzer,
        GlobalSymbolIndex symbolIndex)
    {
        if (logger == null) throw new ArgumentNullException(nameof(logger));
        if (symbolIndex == null) throw new ArgumentNullException(nameof(symbolIndex));

        var diagnostics = new List<Diagnostic>();
        var lines = content.Split('\n');
        var baseCode = language == MqlLanguage.Mql5 ? Mql5DiagnosticBase : Mql4DiagnosticBase;

        // Publish real syntax errors from the parser.
        diagnostics.AddRange(GenerateSyntaxErrors(mqlFile, language, token));

        // Issue #28: MQL-native semantic rules run after syntax errors and
        // before the line-scan heuristics. Failures inside the analyzer are
        // swallowed per-rule (best-effort) and must not disturb the existing
        // syntax/typo/underscore diagnostics.
        var analyzer = semanticAnalyzer ?? new SemanticAnalyzer();
        diagnostics.AddRange(ApplyCrossFileSuppression(
            logger, analyzer.Analyze(mqlFile, content, language, token),
            mqlFile, documentPath, language, token, symbolIndex));

        // Issue #179 (S4): each unresolved angle-bracket system include gets
        // exactly one actionable warning naming both remedies. Strictly
        // additive — nothing here suppresses or alters the existing
        // syntax/semantic diagnostics.
        diagnostics.AddRange(UnresolvedIncludeDiagnostic.Generate(
            content, language, documentPath, token));

        for (int i = 0; i < lines.Length; i++)
        {
            if (i % 100 == 0) token.ThrowIfCancellationRequested();

            var line = lines[i];

            if (line.Contains("UnkownFunction") || line.Contains("UnkownVariable"))
            {
                diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(i, 0, i, line.Length),
                    Severity = DiagnosticSeverity.Error,
                    Message = "Potential typo: 'Unkown' should be 'Unknown'",
                    Code = (baseCode + 1).ToString(),
                    Source = "mql-lsp"
                });
            }

            if (line.Contains("int OnInit()") && i + 1 < lines.Length)
            {
                var nextLine = lines[i + 1].Trim();
                if (string.IsNullOrEmpty(nextLine) || nextLine == "{}")
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(i, 0, i, line.Length),
                        Severity = DiagnosticSeverity.Warning,
                        Message = "OnInit function appears to be empty.",
                        Code = (baseCode + 2).ToString(),
                        Source = "mql-lsp"
                    });
                }
            }
        }

        return diagnostics;
    }

    /// <summary>
    /// Issue #44: workspace-correlated suppression of cross-file unresolved-symbol
    /// false positives. The rule itself stays document-local (REQ-IA-02); this
    /// downstream filter suppresses base+70 diagnostics whose symbol is declared
    /// in an included header (tier 1) or anywhere in the indexed workspace (tier 2).
    /// Best-effort (fail open): any correlator failure keeps the document-local
    /// diagnostics, and cancellation aborts the request as before.
    /// </summary>
    private static IReadOnlyList<Diagnostic> ApplyCrossFileSuppression(
        ILogger logger,
        IReadOnlyList<Diagnostic> semanticDiagnostics,
        MqlFile? mqlFile,
        string? documentPath,
        MqlLanguage language,
        CancellationToken token,
        GlobalSymbolIndex symbolIndex)
    {
        try
        {
            return CrossFileSymbolCorrelator.SuppressWorkspaceResolvable(
                semanticDiagnostics, mqlFile, documentPath, language, symbolIndex, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cross-file symbol suppression failed; keeping document-local diagnostics.");
            return semanticDiagnostics;
        }
    }
}