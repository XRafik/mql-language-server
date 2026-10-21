#!/usr/bin/env python3
"""Enrich data/builtins/mql4.json with real descriptions (issue #166, MQL4).

Baseline (main @ 15458f0): all 350 functions carry no description field and
the 4 predefined series variables (Open/High/Low/Close) carry the
placeholder "(documented MQL4 API; signature not yet captured)".

This tool fills the `description` field ONLY — never names or signatures
(#136 contract: the golden list is the source of truth; registries are
projections enforced by RegistryGoldenListParityTests).

Source: the official MQL4 Reference CHM (~/Descargas/mql4.chm), decompiled
with 7z into a cache directory (default
 tools/generate-golden-lists/data/raw/mql4-chm/ — under the gitignored
data/raw/ intermediates directory; the decompiled HTML is never
committed). 690 HTML files, cp1252 encoding.

Extraction (measured 2026-10-19 page audit):

- Function pages (246 of 350 dataset functions): uniform page structure —
  breadcrumb "MQL4 Reference / <section>" → H1 (function name) → one or
  more `p_Function` description paragraphs → signature box with
  per-parameter comments → "Parameters" → "Returned value" → "Example".
  The description is the consecutive p_Function prose directly under the
  H1 (same convention as apply_mql5_descriptions.py for MQL5 functions:
  prose between the page heading and the captured signature).
- Grouped constant tables (60 further function-section entries): constant
  families live in identifier→description table rows:
    MODE_*            marketinfoconstants.htm (id, value, description)
    TERMINAL_*        terminalstatus.htm      (id, description, type)
    ACCOUNT_*         accountinformation.htm  (id, description, type)
    CRYPT_*/ENUM_CRYPT_METHOD otherconstants.htm (id, description) +
                      the "Crypt Methods" prose paragraph
    MB_ICON*          messbconstants.htm      (ids(csv), value, description)
  A first cell may list several constants ("MB_ICONINFORMATION,
  MB_ICONASTERISK"); all listed names share the row description.
- Predefined variables: predefined.htm Variable/Value table rows
  (p_fortable): Open, High, Low, Close (+ the other predefined names).

44 dataset functions have no source in this CHM build (MQL5-only names
that leaked into the mql4 dataset such as OrderGetDouble/CopyBuffer/OnTick,
undocumented legacy aliases such as rand/round/FileRead, and constants
newer than the CHM such as CRYPT_HASH_SHA3_*). They stay description-less
and are REPORTED with page-audit evidence — text is never fabricated or
copied from the MQL5 docs.

The run is idempotent: entries whose description is already real are never
touched, so re-running makes no further changes. The full extraction is
written to tools/generate-golden-lists/data/mql4-descriptions.json for
review, including every unrecoverable entry with its page-audit evidence.

Usage:
    python3 apply_mql4_descriptions.py --from-chm <mql4.chm> [--cache-dir DIR]
                                       [--evidence PATH] [--dry-run]
"""

from __future__ import annotations

import html
import json
import re
import subprocess
import sys
from datetime import date, datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_PATH = REPO_ROOT / "data" / "builtins" / "mql4.json"
DEFAULT_CACHE = Path(__file__).resolve().parent / "data" / "raw" / "mql4-chm"
DEFAULT_EVIDENCE = Path(__file__).resolve().parent / "data" / "mql4-descriptions.json"

DESC_MARKER = "descriptions-official-mql4-reference-chm-{today}"
PLACEHOLDERS = {
    "(description not yet captured)",
    "(documented MQL4 API; signature not yet captured)",
}

# Grouped constant-table sources (see module docstring).
CONSTANT_TABLE_PAGES = {
    "marketinfoconstants.htm",   # MODE_* (MarketInfo constants)
    "terminalstatus.htm",        # TERMINAL_* (terminal information)
    "accountinformation.htm",    # ACCOUNT_* (account information)
    "otherconstants.htm",        # CRYPT_* (other constants)
    "messbconstants.htm",        # MB_ICON* (MessageBox dialog constants)
    "predefined.htm",            # Open/High/Low/Close (+ other predefined)
}

# Pure value cell: decimal/hex number (the value column of a constants
# table), used to tell [id, value, description] from [id, description, type].
VALUE_RE = re.compile(r"^(0x[0-9A-Fa-f]+|-?\d+)$")

H1_RE = re.compile(r'<p class="p_H1"><span class="f_H1">[^<]+</span></p>', re.S)
PROSE_STOP_RE = re.compile(
    r'<div style="[^"]*CodeExample|<p class="p_BoldTitles">|<p class="p_H2"',
    re.S)
PARA_RE = re.compile(r'<p class="([^"]+)">(.*?)</p>', re.S)
TD_RE = re.compile(r"<td[^>]*>(.*?)</td>", re.S)
TR_RE = re.compile(r'<tr class="EnumTable">(.*?)</tr>', re.S)
TAG_RE = re.compile(r"<[^>]+>")


def clean_text(raw: str) -> str:
    """Strip HTML, decode entities, collapse whitespace."""
    text = re.sub(r"<br\s*/?>", " ", raw)
    text = TAG_RE.sub("", text)
    text = html.unescape(text).replace("\u00a0", " ")
    return re.sub(r"\s+", " ", text).strip()


def decompile_chm(chm_path: Path, cache_dir: Path) -> None:
    """Decompile the CHM into cache_dir with 7z (idempotent)."""
    if any(cache_dir.glob("*.htm")):
        return
    cache_dir.mkdir(parents=True, exist_ok=True)
    subprocess.run(["7z", "x", "-y", f"-o{cache_dir}", str(chm_path)],
                   check=True, stdout=subprocess.DEVNULL)


def table_rows(page_html: str) -> list[list[str]]:
    """Cleaned cells of every constants/predefined table row."""
    rows: list[list[str]] = []
    for tr in TR_RE.findall(page_html):
        cells = [clean_text(td) for td in TD_RE.findall(tr)]
        cells = [c for c in cells if c]
        if cells:
            rows.append(cells)
    return rows


def row_entries(cells: list[str]) -> list[tuple[str, str]]:
    """(identifier, description) pairs of one table row.

    A first cell may list several constants ("MB_ICONINFORMATION,
    MB_ICONASTERISK"); all share the row description. A 3-cell row is
    either [id, value, description] (numeric middle cell) or
    [id, description, type]; a 2-cell row is [id, description].
    """
    if len(cells) < 2:
        return []
    ident_cell, description = cells[0], cells[1]
    if len(cells) >= 3 and VALUE_RE.fullmatch(cells[1]):
        description = cells[2]
    elif len(cells) >= 3 and VALUE_RE.fullmatch(cells[0]):
        ident_cell, description = cells[1], cells[2]
    idents = [i.strip() for i in ident_cell.split(",") if i.strip()]
    return [(i, description) for i in idents]


def extract_function_page(page_path: Path) -> str | None:
    """Prose under the H1 of a uniform function page (p_Function run)."""
    page = page_path.read_bytes().decode("cp1252")
    if not (h1 := H1_RE.search(page)):
        return None
    tail = page[h1.end():]
    stop = PROSE_STOP_RE.search(tail)
    paras = PARA_RE.findall(tail[:stop.start()] if stop else tail)
    sentences = [clean_text(body) for cls, body in paras
                 if cls == "p_Function" and clean_text(body)]
    if not sentences:  # prose must start directly under the H1
        return None
    return " ".join(sentences)


def extract_constants(pages: dict[str, Path]) -> tuple[dict[str, tuple[str, str]],
                                                       list[tuple[str, str]]]:
    """(identifier -> (page, description)) for every grouped constant row."""
    found: dict[str, tuple[str, str]] = {}
    for page_name in sorted(CONSTANT_TABLE_PAGES):
        page_path = pages[Path(page_name).stem.lower()]
        for cells in table_rows(page_path.read_bytes().decode("cp1252")):
            for ident, description in row_entries(cells):
                found.setdefault(ident, (page_name, description))
    # ENUM_CRYPT_METHOD: prose under the "Crypt Methods" section heading.
    other = pages["otherconstants"].read_bytes().decode("cp1252")
    if m := re.search(
            r'Crypt Methods</span></p>\s*<p class="p_Text">'
            r'<span class="f_fortable">(.*?)</span>', other, re.S):
        found.setdefault(
            "ENUM_CRYPT_METHOD",
            ("otherconstants.htm", clean_text(m.group(1))))
    return found, []


def build_evidence(entries: dict[str, dict], unrecoverable: list[dict],
                   counts: dict) -> dict:
    return {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "source": ("official MQL4 Reference CHM (mql4.chm, decompiled with "
                   "7z; page audit 2026-10-19)"),
        "counts": counts,
        "entries": entries,
        "unrecoverable": unrecoverable,
    }


def main() -> int:
    dry_run = "--dry-run" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]

    def opt(flag: str, default: str) -> str:
        return sys.argv[sys.argv.index(flag) + 1] if flag in sys.argv else default

    cache_dir = Path(opt("--cache-dir", str(DEFAULT_CACHE)))
    evidence_path = Path(opt("--evidence", str(DEFAULT_EVIDENCE)))
    if "--from-chm" not in sys.argv and not any(cache_dir.glob("*.htm")):
        print("usage: apply_mql4_descriptions.py --from-chm <mql4.chm> "
              "[--cache-dir DIR] [--evidence PATH] [--dry-run]", file=sys.stderr)
        return 2
    if "--from-chm" in sys.argv:
        decompile_chm(Path(args[0]), cache_dir)

    pages = {p.stem.lower(): p for p in cache_dir.glob("*.htm")}
    constants, _ = extract_constants(pages)

    golden = json.loads(GOLDEN_PATH.read_text(encoding="utf-8"))

    def needs_description(entry: dict) -> bool:
        return (not entry.get("description")
                or entry["description"] in PLACEHOLDERS)

    today = date.today().isoformat()
    entries: dict[str, dict] = {}
    unrecoverable: list[dict] = []
    functions_updated = variables_updated = 0

    for section, kind in (("functions", "function"), ("variables", "variable")):
        for name, entry in golden[section].items():
            page = pages.get(name.lower())
            source = None
            if page and (description := extract_function_page(page)):
                source = (page.name, description)
            elif name in constants:
                source = constants[name]
            if source is None:
                # Page-audit proof: no own page and no grouped-table row.
                if needs_description(entry):
                    if page is None:
                        reason = (f"no page {name.lower()}.htm in the MQL4 "
                                  f"Reference CHM (MQL5-only name or "
                                  f"undocumented MQL4 API)")
                    else:
                        reason = (f"no description source for '{name}' in the "
                                  f"MQL4 Reference CHM (constant absent from "
                                  f"this CHM build)")
                    unrecoverable.append({"name": name, "kind": kind,
                                          "reason": reason, "pages": []})
                continue
            source_page, description = source
            # Every recoverable entry is reported, updated or not, so the
            # evidence stays complete on idempotent re-runs. A few names
            # exist in both golden sections; record every kind.
            if name in entries:
                entries[name]["kind"] += f",{kind}"
            else:
                entries[name] = {"kind": kind, "page": source_page,
                                 "description": description}
            if not needs_description(entry):
                continue
            entry["description"] = description
            entry["provenance"] = (entry.get("provenance", "")
                                   + f"; {DESC_MARKER.format(today=today)}")
            if kind == "function":
                functions_updated += 1
            else:
                variables_updated += 1

    counts = {
        "functionsTotal": len(golden["functions"]),
        "functionsUpdated": functions_updated,
        "functionsWithoutDescription": sum(
            1 for e in golden["functions"].values() if needs_description(e)),
        "variablesTotal": len(golden["variables"]),
        "variablesUpdated": variables_updated,
        "variablePlaceholdersRemaining": sum(
            1 for e in golden["variables"].values() if needs_description(e)),
    }
    print(f"Extraction: {json.dumps(counts)}; "
          f"{len(unrecoverable)} unrecoverable entries.")

    if dry_run:
        print("Dry run: golden list not modified.")
        return 0

    if functions_updated or variables_updated:
        golden["provenance"] = (
            golden.get("provenance", "")
            + f" | Description enrichment {today} from the official MQL4 "
              f"Reference CHM by tools/generate-golden-lists/"
              f"apply_mql4_descriptions.py "
              f"(+{functions_updated} function descriptions, "
              f"+{variables_updated} variable descriptions).")
        GOLDEN_PATH.write_text(
            json.dumps(golden, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8")
        print(f"Golden list updated: +{functions_updated} function and "
              f"+{variables_updated} variable descriptions.")
    else:
        print("Nothing to update; golden list not modified.")

    evidence_path.parent.mkdir(parents=True, exist_ok=True)
    evidence_path.write_text(
        json.dumps(build_evidence(entries, unrecoverable, counts),
                   indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8")
    print(f"Evidence written to {evidence_path}.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
