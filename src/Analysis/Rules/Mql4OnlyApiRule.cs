using System;
using System.Collections.Generic;
using MqlLanguageServer.Lsp.Server;
using MqlLanguageServer.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Analysis.Rules;

/// <summary>
/// Semantic rule (issue #34, base+60 range): flags MQL4-only standard-library
/// API usage (functions and predefined variables from the curated
/// <see cref="Mql4OnlyApiRegistry"/>) inside an MQL5-routed document, turning
/// each touchpoint into a migration warning with a suggested MQL5
/// replacement — the reverse direction of <see cref="LanguageMisuseRule"/>
/// (issue #28, base+40), which it mirrors structurally.
///
/// Detection is occurrence-driven (issue #176): the rule walks the identifier
/// token occurrences captured during parsing
/// (<see cref="MqlFile.Occurrences"/>, filled by TokenOccurrenceCapture) and
/// flags those whose text matches a registry key. String-literal contents and
/// comments are excluded by construction — the capture emits only
/// default-channel IDENTIFIER tokens (comment and preprocessor text live on
/// hidden lexer channels) — so no textual stripping or per-line heuristic is
/// needed. No type inference, no regex. A null file degrades to no
/// diagnostics (same contract as <see cref="UnresolvedSymbolRule"/>).
/// Semantics-changed entries (Bars, Digits,
/// Point — still valid MQL5 names) skip valid MQL5 call forms `Name(...)`
/// and flag only MQL4-style bare reads, with an honest "changed semantics"
/// message instead of "not available". The rule emits nothing for MQL4
/// documents: the MQL5-exclusive direction stays owned by
/// <see cref="LanguageMisuseRule"/>.
/// </summary>
public sealed class Mql4OnlyApiRule : ISemanticRule
{
    // Offset within the language base code (1000/5000).
    internal const int Mql4OnlyApiInMql5Offset = 60;

    /// <inheritdoc/>
    public IEnumerable<Diagnostic> Check(SemanticRuleContext context)
    {
        if (context.Language != MqlLanguage.Mql5)
        {
            // MQL4-only API detection applies to MQL5 documents only; the
            // MQL4 base (1000) + 60 → "1060" is reserved but never emitted.
            yield break;
        }

        var occurrences = context.File?.Occurrences;
        if (occurrences is null)
        {
            // No parsed file (or occurrences not captured): degrade to no
            // diagnostics, mirroring UnresolvedSymbolRule.
            yield break;
        }

        var entries = Mql4OnlyApiRegistry.Entries;
        var lines = context.Content.Split('\n');

        for (var i = 0; i < occurrences.Count; i++)
        {
            if (i % 100 == 0)
            {
                context.Token.ThrowIfCancellationRequested();
            }

            var occurrence = occurrences[i];

            // Case-sensitive (Ordinal) lookup against the curated registry:
            // MQL identifiers are case-sensitive like C++, so `TimeHour` and
            // `timehour` are distinct identifiers (REQ-MA-02/03).
            if (!entries.TryGetValue(occurrence.Text, out var entry))
            {
                continue;
            }

            // Defensive: an occurrence must land inside the document text.
            if (occurrence.Line < 0 || occurrence.Line >= lines.Length)
            {
                continue;
            }

            var line = lines[occurrence.Line];

            // Identifier-boundary checks (REQ-MA-03), retained defensively:
            // occurrences are whole IDENTIFIER tokens so they are never
            // embedded in a longer identifier, but a stale or hand-built
            // occurrence must not flag. Identifier chars are letters, digits
            // and '_'.
            var before = occurrence.Column - 1;
            if (before >= 0 && before < line.Length && IsIdentChar(line[before]))
            {
                continue;
            }

            var after = occurrence.Column + occurrence.Length;
            if (after < line.Length && IsIdentChar(line[after]))
            {
                continue;
            }

            // Option C (Judgment-Day ledger JD-1): semantics-changed names
            // (Bars/Digits/Point) still exist in MQL5 as functions. A call
            // form `Name(...)` is valid MQL5 and must not warn; only
            // MQL4-style bare reads are defects. The next non-whitespace
            // character after the identifier disambiguates without type info.
            if (entry.SemanticsChanged && NextNonSpaceCharIsOpenParen(line, after))
            {
                continue;
            }

            var message = entry.SemanticsChanged
                ? $"MQL5 {entry.Kind.ToString().ToLowerInvariant()} '{occurrence.Text}' has changed semantics: {entry.Reason}. Use {entry.Replacement} instead."
                : $"MQL4-only {entry.Kind.ToString().ToLowerInvariant()} '{occurrence.Text}' is not available in MQL5; {entry.Reason}. Use {entry.Replacement} instead.";

            yield return new Diagnostic
            {
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(occurrence.Line, occurrence.Column, occurrence.Line, occurrence.Column + occurrence.Length),
                Severity = DiagnosticSeverity.Warning,
                Message = message,
                Code = (context.BaseCode + Mql4OnlyApiInMql5Offset).ToString(),
                Source = "mql-lsp"
            };
        }
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// True when the next non-whitespace character after <paramref name="start"/>
    /// is `(` — i.e. the identifier is in call position. Used only for
    /// semantics-changed entries, where the MQL5 call form is valid.
    /// </summary>
    private static bool NextNonSpaceCharIsOpenParen(string line, int start)
    {
        for (var j = start; j < line.Length; j++)
        {
            if (char.IsWhiteSpace(line[j]))
            {
                continue;
            }

            return line[j] == '(';
        }

        return false;
    }
}