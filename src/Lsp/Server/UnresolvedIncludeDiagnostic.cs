using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

using MqlLanguageServer.Models;
using MqlLanguageServer.Parser;

using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace MqlLanguageServer.Lsp.Server;

/// <summary>
/// Issue #179 (S4): actionable warning for unresolved angle-bracket
/// <c>#include &lt;...&gt;</c> entries.
///
/// <para>Each angle-bracket include entry that fails to resolve against the
/// currently effective system include roots (<see cref="SystemIncludeRoots.Current"/>)
/// produces exactly one diagnostic: severity Warning, Source "mql-lsp", a
/// range covering the directive, and a message naming the include entry and
/// both remedies (configure the <c>systemIncludeRoots</c> initialization
/// option, or declare the root in a <c>.mql-lsp.json</c> project file).
/// Resolution reuses the existing resolver path (<see cref="IncludePathResolver"/>),
/// the same one angle includes already go through — no new resolution logic
/// here.</para>
///
/// <para>Quoted includes (<c>#include "..."</c>) are out of scope: they keep
/// their relative-to-includer semantics and never warn. The rule is strictly
/// additive: it never suppresses or alters the existing diagnostics — in
/// particular the undeclared-symbol 1070/5070 output is unchanged (user
/// decision L2: warn, do not suppress).</para>
/// </summary>
public static class UnresolvedIncludeDiagnostic
{
    /// <summary>
    /// MQL4 code 1004: <see cref="DocumentDiagnostics.Mql4DiagnosticBase"/> +
    /// 4, the next free offset in the shared numeric scheme (001 typo,
    /// 002 empty OnInit, 003 underscore — rule retired, code reserved;
    /// +70 undeclared symbol; +100 syntax errors).
    /// </summary>
    public const int Mql4Code = DocumentDiagnostics.Mql4DiagnosticBase + 4;

    /// <summary>
    /// MQL5 code 5004: <see cref="DocumentDiagnostics.Mql5DiagnosticBase"/> +
    /// 4 (same shared offset as <see cref="Mql4Code"/>).
    /// </summary>
    public const int Mql5Code = DocumentDiagnostics.Mql5DiagnosticBase + 4;

    /// <summary>
    /// Generate the unresolved-system-include warnings for one document state.
    /// Never throws except on cancellation; a malformed entry simply fails to
    /// resolve and warns (fail-open contract untouched).
    /// </summary>
    public static List<Diagnostic> Generate(
        string content,
        MqlLanguage language,
        string? documentPath,
        CancellationToken token)
    {
        var diagnostics = new List<Diagnostic>();

        if (string.IsNullOrEmpty(content))
        {
            return diagnostics;
        }

        var code = (language == MqlLanguage.Mql5 ? Mql5Code : Mql4Code).ToString();
        // One snapshot per generation: the effective set is fixed for the
        // session, and a stable view keeps the emitted set self-consistent.
        var roots = SystemIncludeRoots.Current;
        var includingFile = documentPath ?? string.Empty;
        var lines = content.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            if (i % 100 == 0) token.ThrowIfCancellationRequested();

            var line = lines[i];

            // A directive cannot follow a line comment; cutting the tail keeps
            // commented-out includes ("// #include <Old.mqh>") from warning.
            var codeEnd = line.IndexOf("//", StringComparison.Ordinal);
            var scan = codeEnd >= 0 ? line.Substring(0, codeEnd) : line;

            foreach (Match match in IncludePathResolver.AngleBracketIncludeRegexAccessor.Matches(scan))
            {
                // Canonical stored-entry shape (same as ExtractFromDirective's
                // angle output), resolved by the shared resolver.
                var entry = $"<{match.Groups[1].Value}>";
                if (IncludePathResolver.ResolveWithSystemRoots(includingFile, entry, roots) != null)
                {
                    continue;
                }

                diagnostics.Add(new Diagnostic
                {
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(i, match.Index, i, match.Index + match.Length),
                    Severity = DiagnosticSeverity.Warning,
                    Message = $"System include {entry} could not be resolved. " +
                              "Configure the systemIncludeRoots initialization option " +
                              "or declare the root in a .mql-lsp.json project file.",
                    Code = code,
                    Source = "mql-lsp"
                });
            }
        }

        return diagnostics;
    }
}
