#!/usr/bin/env python3
"""Enrich data/builtins/mql5.json with documented MQL5 stdlib functions.

Follow-up to apply_mql5_enum_constants.py: the 2026-10-18 audit against the
official MQL5 Reference showed only 155 of ~760 documented functions were
registered, so UnresolvedSymbolRule emitted 5070 false positives for calls
to documented functions and completion/hover missed them.

The script scans the function-reference pages of a pdftotext -raw dump of
the official "MQL5 Reference" PDF. A function page renders as:

    <printed page number>
    <Name>                       <- page heading, bare identifier line
    <description prose>
    <ret-type> Name(             <- signature start (may span lines)
    <param> // comment           <- parameter comments are stripped
    );

Extraction rules:
- The heading must equal a TOC name (the reference's own table of contents
  is the documentation universe; parsed from the -layout dump).
- Signatures must be free-function shaped: `type Name(`. Receiver-carrying
  signatures (matrix::SVD, matrix.MatMul) never match, which structurally
  excludes all matrix/vector methods and OpenBLAS internals.
- Lowercase-first names (MetaTrader Python API, e.g. `initialize`) are
  excluded: they are not MQL5.
- Code samples never match: a signature start requires the `Name(` at the
  end of a line preceded by a bare return-type token, and terminates at
  `);` within a bounded window without a `{`.

Merge adds only; every new entry carries per-page provenance. Overloads on
one page are joined with " | ".

Usage:
    python3 apply_mql5_functions.py --from-pdf <raw.txt> <layout.txt> [--dry-run]
"""

from __future__ import annotations

import json
import re
import sys
from datetime import date, datetime, timezone
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
GOLDEN_PATH = REPO_ROOT / "data" / "builtins" / "mql5.json"

SIG_WINDOW_CHARS = 600

# Free-function return types (lowercase primitives + struct/handle-ish
# lowercase words). Receiver-carrying pages (`matrix::Name(`) never match
# the start pattern anyway.
RETURN_TYPE_RE = re.compile(
    r"\b(bool|int|long|ulong|uint|double|string|void|datetime|color|char|"
    r"uchar|short|ushort|float|matrix|vector)[ \t]+")

NAME_LINE_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)$")
PAGE_NUMBER_RE = re.compile(r"^\d{1,5}$")
TOC_NAME_RE = re.compile(r"^([A-Za-z_][A-Za-z0-9_]{1,60})(?:\s*\.{3,}.*)?$")
COMMENT_RE = re.compile(r"//[^\n]*")
WS_RE = re.compile(r"\s+")


def toc_names(layout_text: str) -> set[str]:
    """Documented names from the reference's own table of contents."""
    names: set[str] = set()
    for page in layout_text.split("\f"):
        for line in page.splitlines():
            m = TOC_NAME_RE.match(line.strip())
            if m:
                names.add(m.group(1))
    return names


def extract_functions(raw_text: str, universe: set[str]) -> tuple[dict[str, dict], list[dict]]:
    """name -> {signature, pages} for every documented free function."""
    functions: dict[str, set[int]] = {}
    signatures: dict[str, list[str]] = {}
    stats = {"pagesScanned": 0, "pagesMatched": 0}

    for pdf_page, page in enumerate(raw_text.split("\f"), start=1):
        lines = [l.strip() for l in page.splitlines() if l.strip()]
        if not lines:
            continue
        stats["pagesScanned"] += 1

        # Find the bare-identifier heading line(s) on this page. The page's
        # first non-footer line is its section title: the Matrix and Vector
        # Methods chapter (methods + OpenBLAS internals, scope-excluded) is
        # skipped wholesale, while DX/OpenCL functions that merely take or
        # return matrices stay in.
        section = next((l for l in lines if "©" not in l
                        and not PAGE_NUMBER_RE.match(l)), "")
        if "Matrix and Vector" in section:
            continue
        headings = [l for l in lines if NAME_LINE_RE.match(l)
                    and not PAGE_NUMBER_RE.match(l)
                    and l in universe]
        if not headings:
            continue

        matched = False
        for name in headings:
            sigs = collect_signatures(page, name)
            if not sigs:
                continue
            matched = True
            functions.setdefault(name, set()).add(pdf_page)
            signatures.setdefault(name, []).extend(sigs)
        if matched:
            stats["pagesMatched"] += 1

    result = {}
    for name, pages in functions.items():
        seen: list[str] = []
        for sig in signatures[name]:
            if sig not in seen:
                seen.append(sig)
        result[name] = {
            "signature": " | ".join(seen),
            "pages": sorted(pages),
        }
    return result, [stats]


def collect_signatures(page: str, name: str) -> list[str]:
    """Normalized `ret-type Name(params)` signatures for one page."""
    sigs: list[str] = []
    for m in re.finditer(rf"(?<![A-Za-z0-9_]){name}\s*\(", page):
        # Return type: the last bare word before the call, on the same line.
        line_start = page.rfind("\n", 0, m.start()) + 1
        prefix = page[line_start:m.start()].strip()
        if not RETURN_TYPE_RE.fullmatch(prefix + " "):
            continue
        # Bounded window to the closing ");".
        window = page[m.start():m.start() + SIG_WINDOW_CHARS]
        end = window.find(");")
        if end < 0:
            continue
        body = window[:end + 2]
        if "{" in body:  # crossed into a code sample body
            continue
        body = COMMENT_RE.sub("", body)
        sig = WS_RE.sub(" ", prefix + " " + body).strip()
        sig = re.sub(r"\s+\(", "(", sig)
        sig = re.sub(r"\(\s+", "(", sig)
        if sig and sig not in sigs:
            sigs.append(sig)
    return sigs


def main() -> int:
    dry_run = "--dry-run" in sys.argv
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if "--from-pdf" not in sys.argv or len(args) < 2:
        print("usage: apply_mql5_functions.py --from-pdf <raw.txt> <layout.txt> "
              "[--dry-run]", file=sys.stderr)
        return 2
    raw_text = Path(args[0]).read_text(encoding="utf-8", errors="replace")
    layout_text = Path(args[1]).read_text(encoding="utf-8", errors="replace")

    universe = toc_names(layout_text)
    functions, evidence = extract_functions(raw_text, universe)
    print(f"PDF extraction: {json.dumps(evidence[0])}; "
          f"{len(functions)} documented free functions.")

    golden = json.loads(GOLDEN_PATH.read_text())
    existing = set(golden["functions"])
    outside = sorted(set(functions) - universe)
    print(f"[validate] extracted names outside the TOC universe: {len(outside)}")
    if outside:
        print("  " + ", ".join(outside))
    already = sorted(set(functions) & existing)
    print(f"[validate] functions already registered: {len(already)}")

    if dry_run:
        print("Dry run: golden list not modified.")
        return 0

    today = date.today().isoformat()
    added = 0
    for name, info in sorted(functions.items()):
        if name in existing:
            continue
        golden["functions"][name] = {
            "signature": info["signature"],
            "provenance": (
                f"official-mql5-reference-pdf-{today} "
                f"pages {','.join(map(str, info['pages'][:5]))}"
                + (" et al" if len(info["pages"]) > 5 else "")
            ),
        }
        added += 1

    if added == 0:
        print("Nothing new to add; golden list not modified.")
        return 0

    golden["provenance"] = (
        golden.get("provenance", "")
        + f" | Function enrichment {today} from the official MQL5 Reference "
          f"PDF by tools/generate-golden-lists/apply_mql5_functions.py "
          f"(+{added} documented functions with signatures)."
    )
    GOLDEN_PATH.write_text(
        json.dumps(golden, indent=2, ensure_ascii=False) + "\n")
    print(f"Golden list updated: +{added} functions "
          f"(functions now {len(golden['functions'])}).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
