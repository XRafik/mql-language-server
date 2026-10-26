using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using MqlLanguageServer.Builtins;

namespace MqlLanguageServer.Analysis;

/// <summary>
/// Kind of curated MQL4-only API entry (issue #34).
/// </summary>
public enum Mql4OnlyApiKind
{
    /// <summary>A MQL4-only function.</summary>
    Function,

    /// <summary>A MQL4-only predefined variable or series array.</summary>
    Variable
}

/// <summary>
/// One curated MQL4-only API entry.
/// </summary>
/// <param name="Name">Canonical spelling (registry key is case-sensitive).</param>
/// <param name="Kind">Function or predefined variable.</param>
/// <param name="Reason">Why the name is MQL4-only (REQ-MA-02 admission).</param>
/// <param name="Replacement">Concrete MQL5 suggestion.</param>
/// <param name="SemanticsChanged">
/// True when the name still exists in MQL5 with changed semantics (option C
/// of the Judgment-Day ledger JD-1): the rule then only flags MQL4-style
/// bare variable reads, skipping valid MQL5 call forms `Name(...)` — the
/// next non-whitespace character after the identifier is `(`.
/// </param>
public readonly record struct Mql4OnlyApiEntry(
    string Name,
    Mql4OnlyApiKind Kind,
    string Reason,
    string Replacement,
    bool SemanticsChanged = false);

/// <summary>
/// Registry of MQL4-only standard-library API names (issues #34 + #136).
///
/// Since #136 this table is NOT hand-curated here: it is projected from the
/// documentation-driven golden list (<c>data/builtins/mql4.json</c>), where
/// every admitted entry carries explicit <c>mql4OnlyApi: true</c> metadata
/// plus an <c>mql5Mapping</c> object. Admission policy (REQ-MA-02) is
/// unchanged and enforced at data-maintenance time:
///
/// An entry is admissible only if the name is demonstrably rejected by the
/// MQL5 compiler, or present in MQL5 with changed semantics such that
/// unchanged MQL4 usage is a defect. Names shared by both languages with
/// UNCHANGED semantics (OrderSend, OrderSelect, OrderClose, ArrayResize,
/// Print, CopyBuffer, FileOpen, OrdersTotal, HistoryTotal, the i* series,
/// IsStopped, …) are deliberately excluded: call-site signature
/// discrimination is out of scope and flagging them would be a false
/// positive. Semantics-changed names (Bars, Digits, Point) are admitted with
/// <c>SemanticsChanged = true</c>: the rule skips their valid MQL5 call
/// forms and flags only MQL4-style bare reads, so no valid MQL5 usage is
/// flagged. This table is explicitly NOT a diff of Mql4Builtins /
/// Mql5Builtins (asymmetric, shared names).
///
/// Keyed Ordinal (case-sensitive): MQL identifiers are case-sensitive
/// like C++, so `TimeHour` and `timehour` are distinct identifiers.
/// </summary>
internal static class Mql4OnlyApiRegistry
{
    private static readonly Lazy<FrozenDictionary<string, Mql4OnlyApiEntry>> LazyEntries =
        new(() => BuiltinGoldenData.Load("mql4").Admissions
            .ToDictionary(
                admission => admission.Name,
                admission => new Mql4OnlyApiEntry(
                    admission.Name,
                    admission.IsFunction ? Mql4OnlyApiKind.Function : Mql4OnlyApiKind.Variable,
                    admission.Reason,
                    admission.Replacement,
                    admission.SemanticsChanged),
                StringComparer.Ordinal)
            .ToFrozenDictionary(StringComparer.Ordinal));

    /// <summary>
    /// MQL4-only API table, keyed by canonical name with
    /// case-sensitive (Ordinal) semantics.
    /// </summary>
    public static FrozenDictionary<string, Mql4OnlyApiEntry> Entries => LazyEntries.Value;

    /// <summary>
    /// Case-sensitive lookup of a MQL4-only API entry.
    /// </summary>
    public static bool TryGetEntry(string name, out Mql4OnlyApiEntry entry) =>
        Entries.TryGetValue(name, out entry);
}
