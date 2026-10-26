#!/usr/bin/env python3
"""MQL5 golden-list constants audit (issue #173, round 2).

Why this tool exists
--------------------
``data/builtins/mql5.json`` carries 2,023 variable (constant) entries, and the previous
verification authority could not re-check them: the PDF dumps of the MQL5 Reference
appear to lack the enum *member* tables. That premise was measured and is wrong —
the cached HTML of ``mql5.com/en/docs/constants`` does contain the member tables
(``DEAL_TYPE_BALANCE`` with its "Balance" description is present), while a large part
of the dataset does not match it. The hole is in the dataset, not in the evidence.

Evidence base (S0: no crawling)
------------------------------
Two local artifacts, cross-checked:

* ``data/page-cache/*.html`` — 93 pages covering all nine sections of
  ``/en/docs/constants``. Authority for membership (87.4% of the dataset matches exactly).
* ``data/pdf/mql5-reference-layout.txt`` — ``pdftotext -layout`` of the official
  reference. Cross-check only (49.5% alone): its clipped table columns and
  kerning-split identifiers ("(S TAT_BALANCE_DD).") are the *origin* of the
  truncated fragments this audit reports, so it cannot also be the judge of them.

Classification (S2)
-------------------
Every variable lands in exactly one class, with the evidence that decided it:

``documented``
    Exact identifier present in the evidence base.
``truncated-fragment``
    Not documented itself, but a strict prefix or suffix of a documented identifier.
    These are extraction artifacts (``ALANCE_DD`` from ``STAT_BALANCE_DD``,
    ``BJ_BITMAP_LABEL`` from ``OBJ_BITMAP_LABEL``), never valid MQL identifiers.
``undocumented-unknown``
    Neither documented nor explainable as a fragment. Kept: absence of documentation is
    not proof of fabrication (undocumented intrinsics exist — ``WFA_PerformCalculations``,
    issue #176), and the conservative rule recorded in the task document retires an entry
    only on positive evidence of a different documented name.

Actionable sets (S3/S4)
-----------------------
``remove``
    A fragment whose documented parent is *already* a dataset entry, so dropping the
    fragment cannot uncover a real name (no false ``5070``).
``add``
    A documented identifier missing from the dataset, reachable only through an
    unambiguous fragment relationship: the parent is documented, is not itself a
    fragment, is at least ``MIN_PARENT_LENGTH`` characters long, and the dataset holds a
    fragment of it. Short collisions (``J_C`` -> ``OBJ_C``) stay as adjudication
    candidates instead.
``adjudicate``
    Fragments whose parent is documented but absent from the dataset, or matched only by
    an ambiguous parent. Reported for a human pass, not applied.

Read-only with respect to the golden lists. Output is
``data/mql5-constants-audit.json`` (local evidence artifact, like the MQL4/MQL5
dataset-audit siblings), deterministic and re-runnable (S6).

Usage::

    python3 tools/generate-golden-lists/audit_mql5_constants.py
    python3 tools/generate-golden-lists/audit_mql5_constants.py --format table
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

# The identifier pattern already requires at least four characters, so a fragment
# shorter than that cannot reach the classifier.
MIN_PARENT_LENGTH = 10
# An identifier that ends in "_" is a clipped table cell, never a valid MQL name; and a
# parent that the authority itself extends with a letter is clipped too. Both would
# smuggle new fragments into the dataset through the "addition" path.
CLIPPED_SUFFIX = "_"
IDENTIFIER_RE = re.compile(r"\b([A-Z][A-Z0-9_]{3,})\b")

TOOL_ROOT = Path(__file__).resolve().parent
REPO_ROOT = TOOL_ROOT.parent.parent


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Audit MQL5 golden-list constants against local evidence.")
    parser.add_argument("--dataset", default=str(REPO_ROOT / "data" / "builtins" / "mql5.json"))
    parser.add_argument("--page-cache", default=str(TOOL_ROOT / "data" / "page-cache"))
    parser.add_argument("--pdf-layout", default=str(TOOL_ROOT / "data" / "pdf" / "mql5-reference-layout.txt"))
    parser.add_argument("--out", default=str(TOOL_ROOT / "data" / "mql5-constants-audit.json"))
    parser.add_argument("--format", choices=("json", "table"), default="json")
    return parser.parse_args(argv)


def identifiers_from(text: str) -> set[str]:
    return set(IDENTIFIER_RE.findall(text))


def build_evidence(page_cache: Path, pdf_layout: Path) -> tuple[dict[str, list[str]], set[str], dict[str, str]]:
    """Return (identifier -> sources, authority set, coverage stats).

    The authority set is the cached HTML only: it is the artifact whose table rows are
    intact, and it is what a removal or an addition has to be proven against. The PDF
    dump is inventory and origin evidence — its clipped columns and kerning-split
    identifiers produced the very fragments under audit, so it cannot certify a name.
    """
    evidence: dict[str, set[str]] = {}
    authority: set[str] = set()
    pages = sorted(page_cache.glob("*.html")) if page_cache.is_dir() else []

    for page in pages:
        found = identifiers_from(page.read_text(encoding="utf-8", errors="replace"))
        authority |= found
        for name in found:
            evidence.setdefault(name, set()).add(f"page-cache/{page.name}")

    web_only = set(authority)

    pdf_names: set[str] = set()
    if pdf_layout.is_file():
        pdf_names = identifiers_from(pdf_layout.read_text(encoding="utf-8", errors="replace"))
    for name in pdf_names:
        evidence.setdefault(name, set()).add("pdf/mql5-reference-layout.txt")

    stats = {
        "distinct_identifiers": len(evidence),
        "html_pages": len(pages),
        "html_authority_identifiers": len(authority),
        "pdf_identifiers": len(pdf_names),
        "pdf_only_identifiers": len(pdf_names - web_only),
    }
    return {name: sorted(srcs)[:4] for name, srcs in evidence.items()}, authority, stats


def parent_candidates(fragment: str, evidence_names: list[str]) -> list[str]:
    """Authority-documented identifiers of which `fragment` is a strict prefix or suffix."""
    parents = [
        name
        for name in evidence_names
        if name != fragment
        and len(name) > len(fragment)
        and (name.startswith(fragment) or name.endswith(fragment))
    ]
    # Shortest parent first: the tightest explanation of the truncation.
    return sorted(parents, key=lambda name: (len(name), name))


def classify(dataset: dict, evidence: dict[str, list[str]], authority: set[str]) -> dict:
    variables = dataset.get("variables") or {}
    names = sorted(variables)
    dataset_names = set(names)
    # Only the HTML authority may explain a fragment or justify an addition (S3/S4).
    evidence_names = sorted(authority)

    documented: list[dict] = []
    fragments: list[dict] = []
    unknown: list[dict] = []

    for name in names:
        if name in evidence:
            documented.append({
                "name": name,
                "evidence": evidence[name],
                "in_authority": name in authority,
            })
            continue

        parents = parent_candidates(name, evidence_names)
        if parents:
            fragments.append(
                {
                    "name": name,
                    "parents": parents[:4],
                    "parent_in_dataset": [p for p in parents if p in dataset_names][:4],
                    "provenance": (variables[name] or {}).get("provenance")
                    if isinstance(variables[name], dict)
                    else None,
                }
            )
        else:
            unknown.append({"name": name, "provenance": (variables[name] or {}).get("provenance")
                            if isinstance(variables[name], dict) else None})

    # S3: removal loses no coverage only when the parent is already in the dataset.
    remove = [f for f in fragments if f["parent_in_dataset"]]
    adjudicate_fragments = [f for f in fragments if not f["parent_in_dataset"]]

    # S4: documented parents of dataset fragments that the dataset is missing.
    def is_clipped(name: str) -> bool:
        if name.endswith(CLIPPED_SUFFIX):
            return True
        return any(
            other != name and other.startswith(name) and not other[len(name):].startswith("_")
            for other in evidence_names
        )

    gap_parents = {
        parent
        for fragment in fragments
        for parent in fragment["parents"]
        if len(parent) >= MIN_PARENT_LENGTH
        and parent not in dataset_names
        and not is_clipped(parent)
    }
    additions = [
        {"name": parent, "evidence": evidence[parent], "from_fragment": next(
            f["name"] for f in fragments if parent in f["parents"])}
        for parent in sorted(gap_parents)
    ]
    ambiguous = [f for f in adjudicate_fragments if not any(len(p) >= MIN_PARENT_LENGTH for p in f["parents"])]

    total = len(names)
    matched = len(documented)
    return {
        "schema": "mql5-constants-audit-v1",
        "dataset": "data/builtins/mql5.json",
        "totals": {
            "variables": total,
            "documented": matched,
            "documented_pct": round(100.0 * matched / total, 1) if total else 0.0,
            "truncated_fragments": len(fragments),
            "undocumented_unknown": len(unknown),
            "removable_fragments": len(remove),
            "additions_supported": len(additions),
            "adjudication_needed": len(adjudicate_fragments),
            "ambiguous_adjudications": len(ambiguous),
        },
        "documented_in_authority": sum(1 for d in documented if d["in_authority"]),
        "documented_pdf_only": sum(1 for d in documented if not d["in_authority"]),
        "documented_sample": [d["name"] for d in documented[:12]],
        "remove": remove,
        "add": additions,
        "adjudicate": adjudicate_fragments,
        "undocumented_unknown": unknown,
        "rules": {
            "fragment_min_length": "enforced by IDENTIFIER_RE (4+ characters)",
            "min_parent_length_for_addition": MIN_PARENT_LENGTH,
            "removal_policy": "fragment whose documented parent is already a dataset entry (S3)",
            "addition_policy": "documented parent of a dataset fragment, unambiguous and long enough (S4)",
            "unknown_policy": "kept: undocumented is not proven fabricated (S2, conservative rule L2)",
        },
    }


def print_table(report: dict) -> None:
    totals = report["totals"]
    print("MQL5 constants audit — data/builtins/mql5.json variables")
    print(f"  documented            {totals['documented']:5} ({totals['documented_pct']}%)")
    print(f"  truncated fragments   {totals['truncated_fragments']:5}")
    print(f"  undocumented unknown  {totals['undocumented_unknown']:5}")
    print(f"  removable (S3)        {totals['removable_fragments']:5}")
    print(f"  additions (S4)        {totals['additions_supported']:5}")
    print(f"  to adjudicate         {totals['adjudication_needed']:5}")
    print("\n  remove -> parent already documented in the dataset:")
    for entry in report["remove"][:12]:
        print(f"    {entry['name']:28} -> {', '.join(entry['parent_in_dataset'][:2])}")
    print("\n  add -> documented but missing:")
    for entry in report["add"][:12]:
        print(f"    {entry['name']:28} <- fragment {entry['from_fragment']}")


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    dataset_path = Path(args.dataset)
    if not dataset_path.is_file():
        print(f"❌ dataset not found: {dataset_path}", file=sys.stderr)
        return 2

    dataset = json.loads(dataset_path.read_text(encoding="utf-8"))
    evidence, authority, evidence_stats = build_evidence(Path(args.page_cache), Path(args.pdf_layout))
    if not evidence:
        print(
            "❌ empty evidence base: no cached pages and no readable PDF layout dump.\n"
            "   This audit never fetches the network by design (issue #173, S0): restore\n"
            "   tools/generate-golden-lists/data/page-cache/ first.",
            file=sys.stderr,
        )
        return 2

    report = classify(dataset, evidence, authority)
    report["evidence"] = evidence_stats

    out_path = Path(args.out)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    out_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    if args.format == "table":
        print_table(report)
        print(f"\nreport: {out_path.relative_to(REPO_ROOT)}")
    else:
        json.dump({"totals": report["totals"], "evidence": report["evidence"], "report": str(out_path)},
                  sys.stdout, indent=2)
        print()
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
