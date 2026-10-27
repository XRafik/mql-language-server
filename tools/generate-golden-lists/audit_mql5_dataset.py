#!/usr/bin/env python3
"""Audit data/builtins/mql5.json for MQL4-only names (dataset hygiene).

Reverse direction of audit_mql4_dataset.py: collect the evidence and the
adjudication behind the removal of MQL4-only names that leaked into the
MQL5 golden list (odd/tasks/mql5-dataset-hygiene.md).

Authority: the official MQL5 Reference. Evidence sources, in order:

1. Function pages — the pdftotext dumps at
   tools/generate-golden-lists/data/pdf/mql5-reference-*.txt
   (mql5-reference-raw.txt is the primary; layout/md are optional
   corroboration). Every documented MQL5 function has a reference page,
   so a \\bName\\b count of zero across the dumps is strong evidence of
   absence — EXCEPT the dumps have known holes (FileRead is
   MQL5-documented with docUrl from #163 yet counts zero; enum member
   tables like DEAL_BUY did not survive the extraction). Therefore a
   zero count is INDICATIVE ONLY, and every function removal below
   carries an explicit adjudication from the task document instead of a
   mechanical zero-count rule. Non-zero counts that come only from
   CTrade class-method mentions in the Standard Library section
   (OrderModify, OrderDelete) are adjudicated the same way: the free
   functions are MQL4-only.
2. Live mql5.com docs (web verification, session 2026-10-21) — recorded
   as the adjudication text per name.

CONSTANTS/VARIABLES are NOT derivable from the dumps (enum member tables
missing). This script deliberately does NOT attempt a full variable
audit; it carries the four adjudicated MODE_* entries as explicit
judgment entries and defers the rest of the variable space to a better
constants dump or live-crawl audit (see variables_scope_note in the
output).

The removal set (14 functions + 4 variables) is encoded exactly as the
task document lists it. Each name is verified against data/builtins/
mql5.json (presence in the expected section), data/builtins/mql4.json
(presence — MQL4-legitimate names must exist there; phantom names must
not), and the dump occurrence counts (functions only). The script is
read-only with respect to the golden lists.

Output: data/mql5-dataset-audit.json (the evidence artifact for the
removal set and the regression gate in
tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs; untracked local
evidence, same convention as the sibling dumps).

Usage:
    python3 audit_mql5_dataset.py [--pdf-dir DIR] [--output PATH]
"""

from __future__ import annotations

import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MQL5_GOLDEN = REPO_ROOT / "data" / "builtins" / "mql5.json"
MQL4_GOLDEN = REPO_ROOT / "data" / "builtins" / "mql4.json"
DEFAULT_PDF_DIR = Path(__file__).resolve().parent / "data" / "pdf"
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "data" / "mql5-dataset-audit.json"

PRIMARY_DUMP = "mql5-reference-raw.txt"

# Removal set, encoded exactly as odd/tasks/mql5-dataset-hygiene.md lists
# it. All function entries are `hand-curated-registry-v2.5.1-rc.1`
# provenance except StringToDateTime/CryptMethod (phantom names with zero
# evidence in ANY dialect reference).
REMOVE_FUNCTIONS = {
    "OrderClose":
        "Documented MQL4-only (docs.mql4.com). In the MQL5 dumps the name "
        "appears only as a CTrade class method (Standard Library resolves "
        "via includes per the #150 scope convention); as a free function "
        "it has no MQL5 reference page.",
    "OrderCloseBy":
        "Documented MQL4-only (docs.mql4.com). Zero occurrences in the "
        "MQL5 dumps; no MQL5 free-function reference page.",
    "OrderModify":
        "Documented MQL4-only (docs.mql4.com). The MQL5 dump occurrences "
        "are CTrade class-method mentions only; no MQL5 free-function "
        "reference page exists.",
    "OrderDelete":
        "Documented MQL4-only (docs.mql4.com). The MQL5 dump occurrences "
        "are CTrade class-method mentions only; no MQL5 free-function "
        "reference page exists.",
    "WindowExpertName":
        "Documented MQL4-only (docs.mql4.com Window* family). Zero "
        "occurrences in the MQL5 dumps; no MQL5 reference page.",
    "AccountBalance":
        "Documented MQL4-only (docs.mql4.com Account* family). Zero "
        "occurrences in the MQL5 dumps; MQL5 uses AccountInfoDouble("
        "ACCOUNT_BALANCE).",
    "AccountEquity":
        "Documented MQL4-only. Zero occurrences in the MQL5 dumps; MQL5 "
        "uses AccountInfoDouble(ACCOUNT_EQUITY).",
    "AccountFreeMargin":
        "Documented MQL4-only. Zero occurrences in the MQL5 dumps; MQL5 "
        "uses AccountInfoDouble(ACCOUNT_MARGIN_FREE).",
    "AccountMargin":
        "Documented MQL4-only. Zero occurrences in the MQL5 dumps; MQL5 "
        "uses AccountInfoDouble(ACCOUNT_MARGIN).",
    "StringGetChar":
        "Documented in the MQL4 Reference (docs.mql4.com/strings/"
        "stringgetchar) only; MQL5 documents StringGetCharacter instead. "
        "Zero occurrences in the MQL5 dumps.",
    "StringSetChar":
        "Documented in the MQL4 Reference (docs.mql4.com/strings/"
        "stringsetchar) only; MQL5 documents StringSetCharacter instead. "
        "Zero occurrences in the MQL5 dumps.",
    "CryptMethod":
        "Phantom name: zero evidence in ANY dialect reference. Removed "
        "from mql4.json by the MQL4-dataset-hygiene change; the MQL5 "
        "entry has no reference page either (the compiler bundles "
        "Crypt.mqh; CryptEncode/CryptDecode remain MQL5-legitimate).",
    "DateTimeToString":
        "Phantom in MQL5: the documented MQL5 name is TimeToString. Zero "
        "occurrences in the MQL5 dumps and no MQL4 reference page either "
        "(MQL4 uses TimeToStr/TimeToString).",
    "StringToDateTime":
        "Phantom name: zero evidence in ANY dialect reference. Removed "
        "from mql4.json by the MQL4-dataset-hygiene change; the MQL5 "
        "entry has no reference page either (MQL5 uses StringToTime).",
}

REMOVE_VARIABLES = {
    "MODE_MAIN":
        "MQL4 indicator line constant. Live mql5.com docs (Indicators "
        "Lines) document MAIN_LINE/SIGNAL_LINE/... — MODE_MAIN is not "
        "there; MQL5 forum threads confirm 'MODE_MAIN' - undeclared "
        "identifier compiler errors. Web-verified MQL4-only "
        "(session 2026-10-21).",
    "MODE_SIGNAL":
        "MQL4 indicator line constant. Live mql5.com docs document "
        "SIGNAL_LINE; MODE_SIGNAL is not there. Web-verified MQL4-only "
        "(session 2026-10-21).",
    "MODE_UPPER":
        "MQL4 indicator bands constant. Live mql5.com docs document "
        "UPPER_BAND; MODE_UPPER is not there. Web-verified MQL4-only "
        "(session 2026-10-21).",
    "MODE_LOWER":
        "MQL4 indicator bands constant. Live mql5.com docs document "
        "LOWER_BAND; MODE_LOWER is not there. Web-verified MQL4-only "
        "(session 2026-10-21).",
}

# Deliberately KEPT (verified MQL5-legitimate), recorded so the audit is
# explicit about what was NOT removed and why.
KEPT_VERIFIED = {
    "OrderSend":
        "Free MQL5 trade API (MqlTradeRequest/MqlTradeResult), high PDF "
        "count in the dumps; legitimate in both dialects with split "
        "signatures.",
    "OrderSelect":
        "Free MQL5 trade API (selects from open orders / history), high "
        "PDF count; legitimate in both dialects with split semantics.",
    "OrdersTotal":
        "Free MQL5 trade API (open market orders count), high PDF count; "
        "legitimate in both dialects with split semantics.",
    "FileRead":
        "MQL5-documented (docUrl from #163) yet ZERO occurrences in the "
        "dumps — a documented dump hole, NOT evidence of absence. Kept.",
    "TERMINAL_DATA_PATH":
        "MQL5-legitimate (documented terminal constant); placement normalized "
        "2026-10-27 (issue #174): now registered under `variables` alongside "
        "the other TERMINAL_* constants.",
    "TERMINAL_DLLS_ALLOWED":
        "MQL5-legitimate (documented terminal constant); placement normalized "
        "2026-10-27 (issue #174): now registered under `variables` alongside "
        "the other TERMINAL_* constants.",
}

DUMP_HOLE_CAVEAT = (
    "Zero \\bName\\b occurrence counts over the MQL5 reference dumps are "
    "INDICATIVE ONLY: the dumps have documented holes (FileRead is "
    "MQL5-documented with docUrl from #163 yet counts zero; enum member "
    "tables like DEAL_BUY did not survive the extraction). Zero-count "
    "functions were therefore individually adjudicated (see "
    "removal_evidence[*].adjudication), and the dump is NOT used as "
    "evidence for constants at all."
)

VARIABLES_SCOPE_NOTE = (
    "mql5.json constants cannot be systematically audited with the "
    "current PDF dumps (enum member tables are missing: DEAL_BUY et al. "
    "are documented MQL5 but zero-count). A full variable audit is "
    "DEFERRED until a better MQL5 constants dump or a live-crawl audit "
    "exists; only the four adjudicated MODE_* judgment entries above are "
    "removed from mql5.json variables in this change."
)


def load_dumps(pdf_dir: Path) -> dict[str, str]:
    """Load the MQL5 reference pdftotext dumps (raw first if present)."""
    dumps: dict[str, str] = {}
    for path in sorted(pdf_dir.glob("mql5-reference-*.txt")):
        dumps[path.name] = path.read_text(encoding="utf-8", errors="replace")
    if not dumps:
        raise FileNotFoundError(f"no mql5-reference-*.txt dumps in {pdf_dir}")
    return dumps


def count_occurrences(name: str, dumps: dict[str, str]) -> dict[str, int]:
    """Case-sensitive \\bName\\b occurrence counts per dump."""
    pattern = re.compile(r"\b%s\b" % re.escape(name))
    return {fname: len(pattern.findall(text))
            for fname, text in dumps.items()}


def build_report(dumps: dict[str, str], mql5: dict, mql4: dict) -> dict:
    dump_files = sorted(dumps)

    def function_evidence(name: str) -> dict:
        counts = count_occurrences(name, dumps)
        total = sum(counts.values())
        if total == 0:
            strength = "zero-count (indicative only — see dump_hole_caveat)"
        else:
            strength = ("non-zero occurrences; adjudicated as CTrade "
                        "class-method / Standard-Library mentions only, "
                        "not a free-function reference page")
        return {
            "kind": "function",
            "pdf_occurrences": {**counts, "total": total},
            "evidence_strength": strength,
            "adjudication": REMOVE_FUNCTIONS[name],
            "in_mql5": {
                "functions": name in mql5["functions"],
                "variables": name in mql5["variables"],
            },
            "in_mql4": {
                "functions": name in mql4["functions"],
                "variables": name in mql4["variables"],
            },
        }

    def variable_evidence(name: str) -> dict:
        return {
            "kind": "variable",
            "pdf_occurrences": None,
            "evidence_strength": (
                "not dump-derivable (enum member tables missing); "
                "judgment entry, live mql5.com web verification only"),
            "adjudication": REMOVE_VARIABLES[name],
            "in_mql5": {
                "functions": name in mql5["functions"],
                "variables": name in mql5["variables"],
            },
            "in_mql4": {
                "functions": name in mql4["functions"],
                "variables": name in mql4["variables"],
            },
        }

    removal_functions = sorted(REMOVE_FUNCTIONS)
    removal_variables = sorted(REMOVE_VARIABLES)
    removal_evidence = {name: function_evidence(name)
                        for name in removal_functions}
    removal_evidence.update({name: variable_evidence(name)
                             for name in removal_variables})

    kept_verified = {}
    for name, note in KEPT_VERIFIED.items():
        entry: dict = {"note": note}
        if name in mql5["functions"] or name in mql5["variables"]:
            counts = count_occurrences(name, dumps)
            entry["pdf_occurrences"] = {
                **counts, "total": sum(counts.values())}
        kept_verified[name] = entry

    return {
        "rule": (
            "Remove a golden-list name iff it is listed in the "
            "mql5-dataset-hygiene removal set: a name present in "
            "mql5.json that the official MQL5 Reference does not document "
            "for MQL5 (MQL4-only free function / MQL4-only constant / "
            "phantom name in every dialect). Function evidence is the "
            "\\bName\\b occurrence count over the MQL5 reference dumps, "
            "INDICATIVE ONLY at zero (documented dump holes), paired with "
            "a per-name adjudication; constants are NOT dump-derivable "
            "and are judgment entries only. See "
            "odd/tasks/mql5-dataset-hygiene.md."
        ),
        "authority": (
            "official MQL5 Reference — pdftotext dumps at "
            "tools/generate-golden-lists/data/pdf/mql5-reference-*.txt "
            f"({len(dump_files)} files, primary: {PRIMARY_DUMP}); "
            "constants adjudicated against the live mql5.com docs "
            "(web verification, session 2026-10-21); MQL4-only leakage "
            "decided against data/builtins/mql5.json"
        ),
        "generated_utc": datetime.now(timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%SZ"),
        "dataset_state": {
            "functions": len(mql5["functions"]),
            "variables": len(mql5["variables"]),
        },
        "dumps": {
            "files": dump_files,
            "primary": PRIMARY_DUMP,
        },
        "dump_hole_caveat": DUMP_HOLE_CAVEAT,
        "variables_scope_note": VARIABLES_SCOPE_NOTE,
        "removal_set": {
            "functions": removal_functions,
            "variables": removal_variables,
            "count": len(removal_functions) + len(removal_variables),
        },
        "removal_evidence": removal_evidence,
        "kept_verified": kept_verified,
    }


def main() -> int:
    def opt(flag: str, default: str) -> str:
        if flag in sys.argv:
            return sys.argv[sys.argv.index(flag) + 1]
        return default

    pdf_dir = Path(opt("--pdf-dir", str(DEFAULT_PDF_DIR)))
    output = Path(opt("--output", str(DEFAULT_OUTPUT)))

    if not pdf_dir.is_dir():
        print(f"error: PDF dump directory not found: {pdf_dir}",
              file=sys.stderr)
        return 1
    mql5 = json.loads(MQL5_GOLDEN.read_text(encoding="utf-8"))
    mql4 = json.loads(MQL4_GOLDEN.read_text(encoding="utf-8"))

    report = build_report(dumps=load_dumps(pdf_dir), mql5=mql5, mql4=mql4)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")

    rs = report["removal_set"]
    print(f"Audit written to {output}")
    print(f"  removal set: {rs['count']} "
          f"({len(rs['functions'])} functions, {len(rs['variables'])} variables)")
    missing = [n for n, e in report["removal_evidence"].items()
               if not (e["in_mql5"]["functions"] or e["in_mql5"]["variables"])]
    if missing:
        print(f"  WARNING: removal names absent from mql5.json "
              f"(already removed?): {', '.join(missing)}")
    print(f"  kept-verified names: {len(report['kept_verified'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
