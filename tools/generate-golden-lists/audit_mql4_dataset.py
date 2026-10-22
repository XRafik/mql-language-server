#!/usr/bin/env python3
"""Audit data/builtins/mql4.json against the official MQL4 Reference CHM.

Deterministically regenerates the MQL4 dataset-hygiene classification report
(odd/tasks/mql4-dataset-hygiene.md): for every golden-list name (functions +
variables) decide whether the MQL4 Reference CHM documents it, and derive the
set of MQL5-only names that leaked into the MQL4 golden list.

Authority: the decompiled official MQL4 Reference CHM at
 tools/generate-golden-lists/data/raw/mql4-chm/ (690 HTML pages, cp1252) —
 the MQL4 documentation authority per the #136 contract ("documentation-driven
 golden lists").

Classification (per name, evaluated in order):

1. dedicated page       — a non-`mql5_*` CHM page whose filename stem equals
                          the name (case-insensitive): MQL4-legitimate.
2. event handler        — an `On*` handler listed with its own H3 heading in
                          events.htm (OnStart, OnInit, OnDeinit, OnTick,
                          OnTimer, OnTester, OnChartEvent, OnCalculate):
                          MQL4-legitimate. OnTrade / OnTradeTransaction are
                          NOT listed there (MQL5-only, migration prose only).
3. identifier evidence  — the name appears in a non-`mql5_*` page in a
                          normative position: a table cell (code-example
                          spans stripped), an H1-H4 heading, or an anchor
                          (compared case-insensitively): MQL4-legitimate.
4. weak word evidence   — the name occurs (\\b\\w+\\b, case-sensitive) in a
                          non-`mql5_*` page only outside the positions above:
                          recorded, but NOT decisive on its own.
5. mql5_*-pages only    — word evidence only in the `mql5_*` migration pages.
6. zero evidence        — no word evidence in any non-`mql5_*` page.

Removal rule (the #136 / dataset-hygiene rule): a golden-list name is
MQL5-only and must be removed when it is present in data/builtins/mql5.json
AND has no dedicated page, is not an events.htm handler, and has no
identifier evidence in any non-`mql5_*` page.

Two small, explicitly documented judgment lists (recorded in the report JSON)
reproduce the human page-audit decisions from the task document:

- REMOVE_OVERRIDES: names whose only "identifier evidence" was judged
  non-normative (IndicatorRelease: the indicators.htm overview table only —
  the page's own normative function table and function_indices.htm exclude
  it).
- KEEP_EXCEPTIONS: zero-identifier-evidence names that are nevertheless kept
  because they are not provably MQL5-only (_AppliedTo is corpus-admitted per
  #136; the CRYPT_* constants are newer than this CHM build; ENUM_APPLIED_
  PRICE / OBJPROP_DIRECTION / TIME_SECONDS have weak prose evidence only and
  are kept ambiguous).

Output: data/mql4-dataset-audit.json (the evidence artifact for the removal
set and the regression gate in
tests/Analysis/Issue136BuiltinRegistryCoverageTests.cs). The script is
read-only with respect to the golden lists.

Usage:
    python3 audit_mql4_dataset.py [--chm-dir DIR] [--output PATH]
"""

from __future__ import annotations

import html
import json
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
MQL4_GOLDEN = REPO_ROOT / "data" / "builtins" / "mql4.json"
MQL5_GOLDEN = REPO_ROOT / "data" / "builtins" / "mql5.json"
DEFAULT_CHM_DIR = Path(__file__).resolve().parent / "data" / "raw" / "mql4-chm"
DEFAULT_OUTPUT = Path(__file__).resolve().parent / "data" / "mql4-dataset-audit.json"

TAG_RE = re.compile(r"<[^>]+>")
CODE_SPAN_RE = re.compile(r'<span class="f_CodeExample".*?</span>', re.S)
WORD_RE = re.compile(r"\b\w+\b")
TD_RE = re.compile(r"<td[^>]*>(.*?)</td>", re.S)
HEADING_RE = re.compile(r'<span class="f_H[1-4]">(\w+)</span>')
ANCHOR_RE = re.compile(r'<a name="(\w+)"')
EVENT_H3_RE = re.compile(r'<span class="f_H3">(\w+)</span>')

# Documented weak-evidence removal: identifier-shaped evidence judged
# non-normative by the page audit (see module docstring).
REMOVE_OVERRIDES = {
    "IndicatorRelease":
        "Only in the indicators.htm overview table; the page's own normative "
        "function table and function_indices.htm exclude it (task-doc "
        "weak-evidence note).",
}

# Zero-identifier-evidence names kept (not provably MQL5-only).
KEEP_EXCEPTIONS = {
    "_AppliedTo": "Corpus-admitted in #136; absent from predefined.htm but "
                  "not provably absent from the MQL4 compiler.",
    "CRYPT_AES192": "Constant newer than this CHM build (otherconstants.htm "
                    " predates it).",
    "CRYPT_HASH_SHA512": "Constant newer than this CHM build.",
    "CRYPT_ARCH_GZIP": "Constant newer than this CHM build.",
    "CRYPT_ARCH_ZLIB": "Constant newer than this CHM build.",
    "ENUM_APPLIED_PRICE": "Weak prose evidence only; kept ambiguous.",
    "OBJPROP_DIRECTION": "Weak prose evidence only; kept ambiguous.",
    "TIME_SECONDS": "Weak prose evidence only; kept ambiguous.",
    # --- Issue #173 round-3 unknown-bucket adjudication (2026-10-22) ---------
    "CRYPT_ARCH_RLE": "Constant newer than this CHM build (CRYPT_* family, "
                      "same rationale as CRYPT_AES192).",
    "CRYPT_HASH_SHA3_256": "Constant newer than this CHM build (CRYPT_* "
                           "family).",
    "CRYPT_HASH_SHA3_512": "Constant newer than this CHM build (CRYPT_* "
                           "family).",
    "DeepSkyBlue": "Old-MQL4 bare web-color name; exact keyword index entry "
                   "in MQL4.hhk -> webcolors.htm (the table documents only "
                   "clr* forms).",
    "HotPink": "Old-MQL4 bare web-color name; exact keyword index entry in "
               "MQL4.hhk -> webcolors.htm.",
    "clrSeaShell": "Case-variant of the documented clrSeashell (webcolors.htm "
                   "normative table cell, constant_indices.htm, MQL4.hhk); "
                   "identifiers compare case-insensitively.",
    "clrAzure": "Color in the webcolors.htm 140-color keyword list and "
                "MQL4.hhk; the page's normative 132-cell table predates it.",
    "clrCyan": "Color in the webcolors.htm 140-color keyword list and "
               "MQL4.hhk; the page's normative 132-cell table predates it.",
    "clrDarkCyan": "Color in the webcolors.htm 140-color keyword list and "
                   "MQL4.hhk; the page's normative 132-cell table predates it.",
    "clrDarkMagenta": "Color in the webcolors.htm 140-color keyword list and "
                      "MQL4.hhk; the page's normative 132-cell table predates "
                      "it.",
    "clrDarkRed": "Color in the webcolors.htm 140-color keyword list and "
                  "MQL4.hhk; the page's normative 132-cell table predates it.",
    "clrFloralWhite": "Color in the webcolors.htm 140-color keyword list and "
                      "MQL4.hhk; the page's normative 132-cell table predates "
                      "it.",
    "clrFuchsia": "Color in the webcolors.htm 140-color keyword list and "
                  "MQL4.hhk; the page's normative 132-cell table predates it.",
    "clrGhostWhite": "Color in the webcolors.htm 140-color keyword list and "
                     "MQL4.hhk; the page's normative 132-cell table predates "
                     "it.",
    "MODE_DESCEND": "Documented in the ArraySort/ArrayBsearch parameter "
                    "descriptions ('MODE_DESCEND sort in descend direction') "
                    "and their code examples; prose positions, not table "
                    "cells.",
}


def clean_text(raw: str) -> str:
    """Strip HTML, decode entities, collapse whitespace."""
    text = TAG_RE.sub(" ", raw)
    text = html.unescape(text).replace("\u00a0", " ")
    return re.sub(r"\s+", " ", text)


def load_chm(chm_dir: Path) -> dict[str, dict]:
    """Per-page word sets and normative identifier sets.

    Returns {page_name: {"words": set, "identifiers": set, "anchors": set}}.
    Identifiers are words in normative positions: table cells (code-example
    spans stripped first), H1-H4 headings. Anchors are lowercased (the CHM
    lowercases anchor ids relative to the identifier they mark).
    """
    pages: dict[str, dict] = {}
    for path in sorted(chm_dir.glob("*.htm")):
        raw = path.read_bytes().decode("cp1252", errors="replace")
        body = CODE_SPAN_RE.sub(" ", raw)
        identifiers: set[str] = set()
        for td in TD_RE.findall(body):
            identifiers |= set(WORD_RE.findall(clean_text(td)))
        identifiers |= set(HEADING_RE.findall(raw))
        pages[path.name] = {
            "words": set(WORD_RE.findall(clean_text(raw))),
            "identifiers": identifiers,
            "anchors": {a.lower() for a in ANCHOR_RE.findall(raw)},
        }
    return pages


def events_handlers(chm_dir: Path) -> list[str]:
    """MQL4 event handlers listed with their own H3 heading in events.htm."""
    raw = (chm_dir / "events.htm").read_bytes().decode("cp1252",
                                                       errors="replace")
    handlers = re.findall(EVENT_H3_RE, raw)
    return sorted(h for h in handlers if h.startswith("On"))


def audit(pages: dict[str, dict], handlers: set[str],
          mql4: dict, mql5_names: set[str]) -> dict:
    non_mql5 = {n: p for n, p in pages.items() if not n.startswith("mql5_")}
    mql5_pages = {n: p for n, p in pages.items() if n.startswith("mql5_")}
    page_stems = {n[:-len(".htm")].lower() for n in non_mql5}

    entries: dict[str, dict] = {}
    removal: dict[str, dict] = {}
    zero_evidence: list[str] = []
    unknown: dict[str, dict] = {}

    def classify(name: str, kind: str) -> dict:
        entry: dict = {"kind": kind}
        if name.lower() in page_stems:
            entry["verdict"] = "dedicated-page"
            entry["mql4_legitimate"] = True
            return entry
        if name in handlers:
            entry["verdict"] = "events-handler"
            entry["mql4_legitimate"] = True
            return entry
        identifier_pages = sorted(
            p for p, page in non_mql5.items()
            if name in page["identifiers"] or name.lower() in page["anchors"])
        if identifier_pages and name not in REMOVE_OVERRIDES:
            entry["verdict"] = "identifier-evidence"
            entry["pages"] = identifier_pages
            entry["mql4_legitimate"] = True
            return entry
        word_pages = sorted(p for p, page in non_mql5.items()
                            if name in page["words"])
        mql5_word_pages = sorted(p for p, page in mql5_pages.items()
                                 if name in page["words"])
        entry["mql5_pages"] = mql5_word_pages
        if word_pages:
            entry["weak_evidence_pages"] = word_pages
        if name in REMOVE_OVERRIDES:
            entry["verdict"] = "remove-override"
            entry["note"] = REMOVE_OVERRIDES[name]
            entry["mql4_legitimate"] = False
        elif name in KEEP_EXCEPTIONS:
            entry["verdict"] = "kept-ambiguous"
            entry["note"] = KEEP_EXCEPTIONS[name]
            entry["mql4_legitimate"] = None
        elif not word_pages:
            entry["verdict"] = "zero-evidence"
            entry["mql4_legitimate"] = None
        elif mql5_word_pages and not word_pages:
            entry["verdict"] = "mql5-pages-only"
            entry["mql4_legitimate"] = None
        else:
            entry["verdict"] = "weak-word-evidence"
            entry["mql4_legitimate"] = None
        if (name in mql5_names
                and not entry.get("mql4_legitimate")
                and name not in KEEP_EXCEPTIONS):
            entry["removed"] = True
            removal[name] = entry
        elif not word_pages:
            zero_evidence.append(name)
        if not entry.get("mql4_legitimate"):
            unknown[name] = entry
        return entry

    for name in mql4["functions"]:
        entries[name] = classify(name, "function")
    for name in mql4["variables"]:
        entries[name] = classify(name, "variable")

    return {
        "entries": entries,
        "removal": removal,
        "zero_evidence": sorted(zero_evidence),
        "unknown": unknown,
    }


def build_report(chm_dir: Path, mql4: dict, mql5: dict) -> dict:
    pages = load_chm(chm_dir)
    handlers = events_handlers(chm_dir)
    mql5_names = set(mql5["functions"]) | set(mql5["variables"])
    result = audit(pages, set(handlers), mql4, mql5_names)

    removal_functions = sorted(n for n, e in result["removal"].items()
                               if e["kind"] == "function")
    removal_variables = sorted(n for n, e in result["removal"].items()
                               if e["kind"] == "variable")

    return {
        "rule": (
            "Remove a golden-list name iff it is present in mql5.json AND "
            "has no dedicated non-mql5_* CHM page (page stem == name, "
            "case-insensitive), is not an events.htm H3 handler, and has no "
            "identifier evidence (table cell with code spans stripped, H1-H4 "
            "heading, or anchor) in any non-mql5_* CHM page. Weak word "
            "evidence outside those positions is not decisive. Documented "
            "judgment overrides: REMOVE_OVERRIDES (IndicatorRelease) and "
            "KEEP_EXCEPTIONS (corpus-admitted / newer-than-CHM names). See "
            "odd/tasks/mql4-dataset-hygiene.md."
        ),
        "authority": (
            "official MQL4 Reference CHM decompiled at "
            "tools/generate-golden-lists/data/raw/mql4-chm/ "
            f"({len(pages)} HTML pages, cp1252); MQL5-only leakage decided "
            "against data/builtins/mql5.json"
        ),
        "generated_utc": datetime.now(timezone.utc)
            .strftime("%Y-%m-%dT%H:%M:%SZ"),
        "dataset_state": {
            "functions": len(mql4["functions"]),
            "variables": len(mql4["variables"]),
        },
        "chm": {
            "pages": len(pages),
            "mql5_migration_pages": sorted(n for n in pages
                                           if n.startswith("mql5_")),
        },
        "events_handlers": handlers,
        "remove_overrides": REMOVE_OVERRIDES,
        "keep_exceptions": KEEP_EXCEPTIONS,
        "removal_set": {
            "functions": removal_functions,
            "variables": removal_variables,
            "count": len(removal_functions) + len(removal_variables),
        },
        "removal_evidence": result["removal"],
        "zero_evidence": result["zero_evidence"],
        "unknown_bucket": result["unknown"],
    }


def main() -> int:
    def opt(flag: str, default: str) -> str:
        if flag in sys.argv:
            return sys.argv[sys.argv.index(flag) + 1]
        return default

    chm_dir = Path(opt("--chm-dir", str(DEFAULT_CHM_DIR)))
    output = Path(opt("--output", str(DEFAULT_OUTPUT)))

    if not chm_dir.is_dir():
        print(f"error: CHM directory not found: {chm_dir}", file=sys.stderr)
        return 1
    mql4 = json.loads(MQL4_GOLDEN.read_text(encoding="utf-8"))
    mql5 = json.loads(MQL5_GOLDEN.read_text(encoding="utf-8"))

    report = build_report(chm_dir, mql4, mql5)
    output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n",
                      encoding="utf-8")

    rs = report["removal_set"]
    print(f"Audit written to {output}")
    print(f"  removal set: {rs['count']} "
          f"({len(rs['functions'])} functions, {len(rs['variables'])} variables)")
    print(f"  zero-evidence names: {len(report['zero_evidence'])}")
    print(f"  unknown bucket: {len(report['unknown_bucket'])}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
