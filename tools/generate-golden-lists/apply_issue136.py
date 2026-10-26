#!/usr/bin/env python3
"""
Issue #136, Fase 1 (enrichment): applies the documentation-driven supplement
to the seeded golden lists.

1. Adds the documented names surfaced by #126/#136 (probe + real-corpus
   residue) with per-name documentation URLs and apiTier.
2. Migrates the 47 curated Mql4OnlyApiRegistry (issue #34) admissions into
   mql5Mapping + mql4OnlyApi metadata on the MQL4 golden list (REQ-MA-02
   admission is preserved as explicit metadata, never auto-derived).
3. Stamps apiTier=legacy for the known MQL4-legacy-only surface.

Usage:  python3 apply_issue136.py   (run from repo root)
Edits:  data/builtins/mql4.json, data/builtins/mql5.json
"""

import json
import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
DATA = REPO / "data/builtins"
MQL4ONLY_REGISTRY = REPO / "src/Analysis/Mql4OnlyApiRegistry.cs"


def load(dialect: str) -> dict:
    return json.loads((DATA / f"{dialect}.json").read_text(encoding="utf-8"))


def save(doc: dict) -> None:
    (DATA / f"{doc['dialect']}.json").write_text(
        json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def add_function(doc, name, signature, api_tier, doc_url, mql5_mapping=None, provenance="issue-136-documented"):
    if name in doc["functions"]:
        raise SystemExit(f"refusing to overwrite existing function {name}")
    entry = {"signature": signature, "apiTier": api_tier, "docUrl": doc_url, "provenance": provenance}
    if mql5_mapping:
        entry["mql5Mapping"] = mql5_mapping
    doc["functions"][name] = entry


def add_variable(doc, name, description, api_tier, doc_url, provenance="issue-136-documented"):
    if name in doc["variables"]:
        raise SystemExit(f"refusing to overwrite existing variable {name}")
    doc["variables"][name] = {
        "description": description, "apiTier": api_tier, "docUrl": doc_url, "provenance": provenance,
    }


def apply_issue136_supplement() -> None:
    """Documented names from #136 (26-name probe) and the #126 real-corpus residue."""
    m4 = load("mql4")
    m5 = load("mql5")

    # --- Core functions, both dialects (documented MQL4 + MQL5) -------------
    both = [
        ("EventSetTimer", "void EventSetTimer(int seconds)",
         "https://docs.mql4.com/event_handling/eventsettimer",
         "https://www.mql5.com/en/docs/event_handling/eventsettimer"),
        ("EventKillTimer", "void EventKillTimer()",
         "https://docs.mql4.com/event_handling/eventkilltimer",
         "https://www.mql5.com/en/docs/event_handling/eventkilltimer"),
        ("EventSetMillisecondTimer", "bool EventSetMillisecondTimer(int milliseconds)",
         "https://docs.mql4.com/event_handling/eventsetmillisecondtimer",
         "https://www.mql5.com/en/docs/event_handling/eventsetmillisecondtimer"),
        ("StringInit", "int StringInit(string &string_var, int new_len=0, ushort fill_symbol=' ')",
         "https://docs.mql4.com/strings/stringinit",
         "https://www.mql5.com/en/docs/strings/stringinit"),
    ]
    for name, sig, url4, url5 in both:
        add_function(m4, name, sig, "build600", url4)
        add_function(m5, name, sig, "shared", url5)

    # --- MQL4 functions documented in the MQL4 reference --------------------
    mql4_only_functions = [
        # (name, signature, apiTier, docUrl, mql5Mapping|None)
        ("iBars", "int iBars(string symbol, int timeframe)",
         "legacy", "https://docs.mql4.com/series/ibars",
         {"replacement": "iBars(_Symbol, _Period)", "kind": "rename", "semanticsChanged": False,
          "note": "Name shared with MQL5 (function form); MQL4 bare series array Bars is the pre-600 form"}),
        ("GlobalVariableSetOnCondition", "bool GlobalVariableSetOnCondition(string name, double value, double check_value)",
         "shared", "https://docs.mql4.com/globals/globalvariablesetoncondition", None),
        ("IsConnected", "bool IsConnected()",
         "legacy", "https://docs.mql4.com/check/isconnected",
         {"replacement": "TerminalInfoInteger(TERMINAL_CONNECTED)", "kind": "rename", "semanticsChanged": False,
          "note": "MQL4-only; migrated from issue #34 migration registry"}),
        ("WindowFind", "int WindowFind(string window_name)",
         "legacy", "https://docs.mql4.com/window/windowfind",
         {"replacement": "ChartWindowFind()", "kind": "rename", "semanticsChanged": False,
          "note": "MQL4-only window index API; migrated from issue #34 migration registry"}),
        ("TimeToStr", "string TimeToStr(datetime value, int mode=TIME_DATE|TIME_MINUTES)",
         "legacy", "https://docs.mql4.com/convert/timetostr",
         {"replacement": "TimeToString", "kind": "rename", "semanticsChanged": False,
          "note": "Idéntica conversión, distinto nombre"}),
        ("StrToTime", "datetime StrToTime(string value)",
         "legacy", "https://docs.mql4.com/convert/strtotime",
         {"replacement": "StringToTime", "kind": "rename", "semanticsChanged": False,
          "note": "Idéntica conversión, distinto nombre"}),
    ]
    for name, sig, tier, url, mapping in mql4_only_functions:
        add_function(m4, name, sig, tier, url, mapping)

    # --- Underscore predefined variables (MQL4 build 600+) ------------------
    for name, desc, url, in_mql5 in [
        ("_LastError", "Last error code (build 600+ form of GetLastError())",
         "https://docs.mql4.com/predefined/_lasterror", True),
        ("_StopFlag", "True when Stop() was requested (build 600+ form of IsStopped())",
         "https://docs.mql4.com/predefined/_stopflag", True),
        ("_UninitReason", "Uninitialization reason code of OnDeinit",
         "https://docs.mql4.com/predefined/_uninitreason", True),
        ("_AppliedTo", "Chart timeframe/mode the EA is applied to",
         "https://docs.mql4.com/predefined/_appliedto", False),
    ]:
        add_variable(m4, name, desc, "build600", url)
        if in_mql5:
            add_variable(m5, name, desc, "shared",
                         "https://www.mql5.com/en/docs/predefined/" + name.lower())

    # --- Stdlib enum constants missing on the #126 real corpus --------------
    reasons = {
        "REASON_TEMPLATE": "EA recompiled or template re-applied",
        "REASON_CHARTCHANGE": "Symbol or chart period changed",
        "REASON_REMOVE": "EA removed from the chart",
        "REASON_PROGRAM": "ExpertRemove() called",
        "REASON_ACCOUNT": "Account changed",
        "REASON_INITFAILED": "OnInit() returned INIT_FAILED",
        "REASON_CLOSE": "Terminal closed",
        "REASON_PARAMETERS": "Input parameters changed",
    }
    reason_url4 = "https://docs.mql4.com/constants/uninitialization_reasons"
    reason_url5 = "https://www.mql5.com/en/docs/constants/namedconstants/uninitreason"
    for name, desc in reasons.items():
        add_variable(m4, name, f"ENUM_UNINIT_REASON constant: {desc}", "shared", reason_url4)
        add_variable(m5, name, f"ENUM_UNINIT_REASON constant: {desc}", "shared", reason_url5)

    add_variable(m4, "TERMINAL_SCREEN_DPI",
                 "Terminal property: screen DPI (ENUM_TERMINAL_INFO_INTEGER)",
                 "shared", "https://docs.mql4.com/constants/terminalconstants")
    add_variable(m5, "TERMINAL_SCREEN_DPI",
                 "Terminal property: screen DPI (ENUM_TERMINAL_INFO_INTEGER)",
                 "shared", "https://www.mql5.com/en/docs/constants/environment_state/terminalstatus")

    file_consts = {
        "FILE_BIN": "File open flag: binary mode (no CR/LF translation)",
        "FILE_SHARE_READ": "File open flag: shared read access",
        "FILE_SHARE_WRITE": "File open flag: shared write access",
    }
    for name, desc in file_consts.items():
        add_variable(m4, name, f"File constant: {desc}",
                     "shared", "https://docs.mql4.com/constants/fileconstants")
        add_variable(m5, name, f"File constant: {desc}",
                     "shared", "https://www.mql5.com/en/docs/constants/io_constants/fileopenflags")

    save(m4)
    save(m5)
    print("issue-136 supplement applied")


# --- Step 2: migrate the issue #34 MQL4-only API admissions -----------------

ENTRY_RE = re.compile(
    r'\{\s*"(?P<name>[^"]+)",\s*new\(\s*"(?P=name)"\s*,\s*'
    r'Mql4OnlyApiKind\.(?P<kind>Function|Variable)\s*,\s*'
    r'"(?P<reason>[^"]*)"\s*,\s*'
    r'"(?P<replacement>[^"]*)"'
    r'(?:\s*,\s*SemanticsChanged:\s*true)?\s*\)',
)


def apply_migration_admissions() -> None:
    src = MQL4ONLY_REGISTRY.read_text(encoding="utf-8")
    doc = load("mql4")
    added = updated = 0
    for m in ENTRY_RE.finditer(src):
        name = m.group("name")
        kind = "function" if m.group("kind") == "Function" else "variable"
        reason = m.group("reason")
        replacement = m.group("replacement")
        semantics_changed = "SemanticsChanged: true" in m.group(0)
        mapping = {
            "replacement": replacement,
            "kind": "semantic" if semantics_changed else ("rename" if replacement else "manual"),
            "semanticsChanged": semantics_changed,
            "note": reason,
        }
        bucket = doc["functions"] if kind == "function" else doc["variables"]
        if name in bucket:
            entry = bucket[name]
            updated += 1
        else:
            # Admitted by REQ-MA-02 review (issue #34) but absent from the
            # hand-curated builtin registry: documented MQL4 API that was
            # never registered. Add with honest provenance.
            entry = {
                "signature" if kind == "function" else "description":
                    "(documented MQL4 API; signature not yet captured)",
                "apiTier": "legacy",
                "docUrl": None,
                "provenance": "issue-34-migration-registry",
            }
            bucket[name] = entry
            added += 1
        entry["mql4OnlyApi"] = True
        entry["mql5Mapping"] = mapping
    save(doc)
    print(f"migrated {added + updated} admissions ({added} new entries, {updated} updated)")


if __name__ == "__main__":
    apply_issue136_supplement()
    apply_migration_admissions()
