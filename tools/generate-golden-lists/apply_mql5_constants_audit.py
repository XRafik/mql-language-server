#!/usr/bin/env python3
"""Apply the MQL5 constants audit to the golden list (issue #173, round 2).

Consumes ``data/mql5-constants-audit.json`` produced by ``audit_mql5_constants.py`` and edits
``data/builtins/mql5.json`` in exactly two evidence-backed ways:

* remove the ``remove`` entries — truncated fragments whose documented parent is already a
  dataset entry, so no real name loses its coverage (audit rule S3);
* add the ``add`` entries — identifiers documented in the HTML authority that the dataset is
  missing, each carrying the description captured from its evidence row (audit rule S4).

Nothing else changes: the ``adjudicate`` and ``undocumented_unknown`` sets stay untouched, on
the conservative rule that undocumented is not proven fabricated (issue #176's
``WFA_PerformCalculations`` is a real undocumented intrinsic).

The script is idempotent — re-running it after a successful apply is a no-op — and refuses to
write when the audit report is missing or predates a dataset change, so the golden list can
never drift from its evidence.

Usage::

    python3 tools/generate-golden-lists/apply_mql5_constants_audit.py            # dry run
    python3 tools/generate-golden-lists/apply_mql5_constants_audit.py --write    # apply
"""

from __future__ import annotations

import argparse
import html
import json
import re
import sys
from pathlib import Path

TOOL_ROOT = Path(__file__).resolve().parent
REPO_ROOT = TOOL_ROOT.parent.parent
DATASET = REPO_ROOT / "data" / "builtins" / "mql5.json"
REPORT = TOOL_ROOT / "data" / "mql5-constants-audit.json"

PROVENANCE = "mql5-constants-audit-2026-10-26"
ADDED_PROVENANCE = "mql5-constants-audit-add-2026-10-26"

# One evidence row: <span class="f_EnumID">NAME</span> ... <span class="f_EnumDesc">text</span>
ROW_RE = re.compile(
    r"<span class=\"f_EnumID\">([^<]+)</span>.*?<span class=\"f_EnumDesc\">([^<]+)</span>",
    re.S,
)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Apply the MQL5 constants audit to the golden list.")
    parser.add_argument("--write", action="store_true", help="write the dataset (default: dry run)")
    parser.add_argument("--report", default=str(REPORT))
    parser.add_argument("--dataset", default=str(DATASET))
    return parser.parse_args(argv)


def load_description_from_evidence(name: str, page_cache: Path) -> str | None:
    """Return the documented description of `name`, from its own evidence table row."""
    for page in sorted(page_cache.glob("*.html")):
        text = page.read_text(encoding="utf-8", errors="replace")
        if name not in text:
            continue
        for match in ROW_RE.finditer(text):
            if match.group(1).strip() == name:
                # Evidence cells carry HTML entities and non-breaking spaces; the golden
                # list feeds hover text directly, so store the decoded prose.
                text = html.unescape(match.group(2))
                text = re.sub(r"[\s\u00a0]+", " ", text).strip()
                return text or None
    return None


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    report_path = Path(args.report)
    dataset_path = Path(args.dataset)

    if not report_path.is_file():
        print(f"❌ audit report not found: {report_path}\n   run audit_mql5_constants.py first.", file=sys.stderr)
        return 2

    report = json.loads(report_path.read_text(encoding="utf-8"))
    dataset = json.loads(dataset_path.read_text(encoding="utf-8"))
    variables = dataset.get("variables") or {}
    original_order = list(variables)

    removals = [entry["name"] for entry in report.get("remove") or []]
    additions = report.get("add") or []
    present_removals = [name for name in removals if name in variables]
    needed_additions = [entry for entry in additions if entry["name"] not in variables]
    addition_names = [entry["name"] for entry in needed_additions]

    print(f"report: {report_path.relative_to(REPO_ROOT)}")
    print(f"removals: {len(removals)} ({len(present_removals)} still present)")
    print(f"additions: {len(additions)} ({len(addition_names)} still missing)")

    already_applied = not present_removals and not addition_names
    if already_applied:
        print("dataset already matches the audit; nothing to write (idempotent no-op).")
        return 0

    if not args.write:
        for name in present_removals:
            print(f"  - remove  {name}")
        for entry in needed_additions:
            print(f"  + add     {entry['name']}  <- fragment {entry.get('from_fragment')}")
        print("dry run: pass --write to apply")
        return 0

    # The file is ordered by extraction history, not alphabetically: a repaired name takes
    # the slot of the fragment it came from, so the edit stays minimal and reviewable.
    for entry in needed_additions:
        description = load_description_from_evidence(entry["name"], TOOL_ROOT / "data" / "page-cache")
        entry["description"] = description
        if description is None:
            print(f"  ! no evidence row for {entry['name']}; placeholder description", file=sys.stderr)

    for name in present_removals:
        variables.pop(name)

    rebuilt: list[tuple[str, object]] = []
    inserted: set[str] = set()
    for name in original_order:
        if name in variables:
            for entry in needed_additions:
                if entry.get("from_fragment") == name and entry["name"] not in inserted:
                    rebuilt.append((entry["name"], {
                        "description": entry["description"] or "(description not captured)",
                        "provenance": ADDED_PROVENANCE,
                    }))
                    inserted.add(entry["name"])
            rebuilt.append((name, variables[name]))
    for entry in needed_additions:
        if entry["name"] not in inserted:
            rebuilt.append((entry["name"], {
                "description": entry["description"] or "(description not captured)",
                "provenance": ADDED_PROVENANCE,
            }))
            inserted.add(entry["name"])

    dataset["variables"] = dict(rebuilt)
    dataset_path.write_text(json.dumps(dataset, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"applied: -{len(present_removals)} +{len(addition_names)} -> {len(dataset['variables'])} variables")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
