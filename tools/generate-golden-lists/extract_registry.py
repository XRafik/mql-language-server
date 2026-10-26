#!/usr/bin/env python3
"""
Issue #136, Fase 1: one-off extractor that seeds the golden lists from the
hand-curated C# registries (migration baseline).

Provenance of every entry produced by this script is
  "migrated from hand-curated <file> (v2.5.1-rc.1)"
so the initial golden lists are a faithful, diffable snapshot of the registry
content the server ships today. Documentation enrichment (docUrl, apiTier,
mql5Mapping, new documented names) is applied by apply_issue136.py on top.

Usage:  python3 extract_registry.py   (run from repo root)
Writes: data/builtins/mql4.json, data/builtins/mql5.json
"""

import json
import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

SOURCES = {
    "mql4": REPO / "src/Mql4/Builtins/Mql4Builtins.cs",
    "mql5": REPO / "src/Mql5/Builtins/Mql5Builtins.cs",
}

# Matches { "Name", "Value" } initializer pairs. Values in both registries
# never contain escaped quotes (verified by grep), so a simple pattern is safe.
PAIR_RE = re.compile(r'\{\s*"((?:[^"\\]|\\.)+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}')


def decode_cs(value: str) -> str:
    r"""Decode C# string escapes (\uXXXX, quotes, backslashes) that the
    simple regex captures verbatim from the C# source."""
    return re.sub(
        r'\\u([0-9a-fA-F]{4})',
        lambda m: chr(int(m.group(1), 16)),
        value,
    ).replace('\\"', '"').replace('\\\\', '\\')

# Split the two lazy dictionaries: everything after the LazyBuiltInVariables
# declaration line belongs to the variables block.
VAR_DECL_RE = re.compile(
    r'Lazy<Dictionary<string,\s*string>>\s+LazyBuiltInVariables'
)


def extract(path: Path) -> tuple[dict, dict]:
    text = path.read_text(encoding="utf-8")
    m = VAR_DECL_RE.search(text)
    if not m:
        sys.exit(f"LazyBuiltInVariables declaration not found in {path}")
    funcs_text, vars_text = text[: m.start()], text[m.start():]
    functions = dict(PAIR_RE.findall(funcs_text))
    variables = dict(PAIR_RE.findall(vars_text))
    return functions, variables


def main() -> None:
    out_dir = REPO / "data/builtins"
    out_dir.mkdir(parents=True, exist_ok=True)
    for dialect, src in SOURCES.items():
        functions, variables = extract(src)
        dupes = len(functions) + len(variables)
        doc = {
            "$schema": "./golden-list.schema.md",
            "dialect": dialect,
            "provenance": (
                f"Seeded by tools/generate-golden-lists/extract_registry.py "
                f"from hand-curated {src.name} (v2.5.1-rc.1). Enriched with "
                f"documented names + metadata by apply_issue136.py."
            ),
            "functions": {
                name: {"signature": decode_cs(sig), "provenance": "hand-curated-registry-v2.5.1-rc.1"}
                for name, sig in functions.items()
            },
            "variables": {
                name: {"description": decode_cs(desc), "provenance": "hand-curated-registry-v2.5.1-rc.1"}
                for name, desc in variables.items()
            },
        }
        out = out_dir / f"{dialect}.json"
        out.write_text(json.dumps(doc, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"{out}: {len(functions)} functions, {len(variables)} variables/constants "
              f"({dupes} pairs total)")


if __name__ == "__main__":
    main()
