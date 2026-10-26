using System;
using MqlLanguageServer.Mql5.Parser;
using MqlLanguageServer.Parser;
using Serilog;

namespace MqlLanguageServer
{
    /// <summary>
    /// Background warmup for the parse pipelines (issue #93).
    ///
    /// <para>OmniSharp 0.19.9 routes every request and notification through a
    /// single serial queue: the FIRST real parse (didOpen) pays the one-time
    /// process costs — ANTLR serialized-ATN deserialization for both grammars
    /// plus JIT of the whole parse pipeline (lexer, parser, visitors, macro
    /// filters, occurrence capture) — while holding that queue. On a cold
    /// machine (self-contained binary, first run, no JIT cache) that stall
    /// measured 15–25 s on the reporter's setup, during which requests sent
    /// by pipelining clients were not lost but far too late for their
    /// observation windows, and requests sent during the initialize handling
    /// window were silently dropped (see Program.OnInitialize).</para>
    ///
    /// <para>This warmup parses a tiny in-memory snippet with BOTH parsers
    /// (MQL4 and MQL5) on a background thread as soon as the client sends
    /// <c>initialize</c>, so those one-time costs are paid concurrently with
    /// the initialize round-trip instead of on the first didOpen. The parsers
    /// are stateless (ANTLR state is process-wide static), so warming with
    /// throwaway instances warms the shared state the real requests will
    /// use. Failure is logged and swallowed: this is strictly best-effort.</para>
    /// </summary>
    internal static class ParserWarmup
    {
        private const string Mql4Snippet =
            "int OnInit()\n{\n    Print(\"warmup\");\n    return(INIT_SUCCEEDED);\n}\n";

        private const string Mql5Snippet =
            "int OnInit()\n{\n    Print(\"warmup\");\n    return(INIT_SUCCEEDED);\n}\n";

        /// <summary>
        /// Warm both parse pipelines. Returns true when both snippets parsed
        /// without syntax errors (a false here means the warmup became useless
        /// — e.g. a grammar change broke the snippets — and the first real
        /// didOpen would pay the cold-start cost again). Failures are logged
        /// and swallowed: this is strictly best-effort.
        /// </summary>
        public static bool WarmUp()
        {
            try
            {
                var mql4 = new Mql4AntlrParser();
                var file4 = mql4.ParseFile(Mql4Snippet, "warmup.mq4");
                // Touch the builtins registry too (lazy static dictionaries)
                // so the first request that calls IsBuiltin is also warm.
                _ = mql4.IsBuiltin("Print");
                _ = file4.Symbols.Count;
                _ = file4.Occurrences.Count;

                var mql5 = new Mql5AntlrParser();
                var file5 = mql5.ParseFile(Mql5Snippet, "warmup.mq5");
                _ = mql5.IsBuiltin("Print");
                _ = file5.Symbols.Count;
                _ = file5.Occurrences.Count;

                var warmed = (file4.SyntaxErrors?.Count ?? 0) == 0 && (file5.SyntaxErrors?.Count ?? 0) == 0;
                if (warmed)
                {
                    Log.Information("Parser warmup completed (MQL4 + MQL5 pipelines ready)");
                }
                else
                {
                    Log.Warning("Parser warmup completed with parse errors; the first parse will not be fully warm");
                }

                return warmed;
            }
            catch (Exception ex)
            {
                // Best effort only: the server must start regardless.
                Log.Warning(ex, "Parser warmup failed; the first parse will pay the cold-start cost");
                return false;
            }
        }
    }
}