#!/usr/bin/env python3
"""Issue #174: golden-list placement normalization (data-only change).

The dataset-hygiene audits (audit_mql4_dataset.py / audit_mql5_dataset.py)
found two placement defects shared by both dialect golden lists:

1. Constants registered under `functions`: the ACCOUNT_*/MODE_*-marketinfo/
   CRYPT_*/ENUM_CRYPT_METHOD/MB_ICON*/TERMINAL_* families sit in the
   `functions` section of mql4.json (67 entries) and TERMINAL_DATA_PATH/
   TERMINAL_DLLS_ALLOWED sit in the `functions` section of mql5.json (2
   entries). The MQL5 list places its constants under `variables`, so the
   same kind of builtin lands in different sections per dialect. The registry
   projection (BuiltinGoldenData) reads a `signature` for every functions
   entry, so a constant there renders a meaningless pseudo-signature hover.

2. Legacy MQL4 account functions registered under `variables`: the
   AccountBalance..AccountStopoutMode family (16 entries) is callable API,
   not data. Their real MQL4 signatures are taken verbatim from the official
   MQL4 Reference CHM (Account Information pages, p_CodeExample blocks in
   tools/generate-golden-lists/data/raw/mql4-chm/account*.htm).

Normalization rules (never fabricating or dropping entries):
- Constants only in `functions` move wholesale to `variables`; the
  pseudo-`signature` field (e.g. "Account property: balance") is dropped —
  the description field already carries the documented prose.
- Constants already in both sections: the `functions` twin is removed; the
  surviving `variables` entry gains the normalization marker.
- Legacy Account* functions move to `functions` with their real CHM
  signatures; their description is preserved.
- Every touched entry records `placement-normalized-2026-10-27` in its
  per-entry provenance.
- Registry code untouched (the #136 contract): Mql4Builtins/Mql5Builtins
  remain projections of these files; the parity tests gate the result.
"""

import json
import re
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
MARKER = "placement-normalized-2026-10-27"

# The constant-like entries to normalize, discovered by
# audit_mql4_dataset.py / audit_mql5_dataset.py (kept-verified/placement
# notes) and pinned by the issue #174 gate tests. Every entry is ALL-CAPS
# (the MQL4/MQL5 convention for constants) and none is callable API.
CONSTANT_RE = re.compile(r"[A-Z0-9_]+")

# Real MQL4 signatures from the official reference CHM (Account Information
# pages). AccountFreeMarginCheck's documented parameter list carries inline
# comments; normalized to the same one-line shape the other entries use.
ACCOUNT_FUNCTION_SIGNATURES = {
    "AccountBalance": "double AccountBalance()",
    "AccountCredit": "double AccountCredit()",
    "AccountCompany": "string AccountCompany()",
    "AccountCurrency": "string AccountCurrency()",
    "AccountEquity": "double AccountEquity()",
    "AccountFreeMargin": "double AccountFreeMargin()",
    "AccountFreeMarginCheck": "double AccountFreeMarginCheck(string symbol, int cmd, double volume)",
    "AccountFreeMarginMode": "double AccountFreeMarginMode()",
    "AccountLeverage": "int AccountLeverage()",
    "AccountMargin": "double AccountMargin()",
    "AccountName": "string AccountName()",
    "AccountNumber": "int AccountNumber()",
    "AccountProfit": "double AccountProfit()",
    "AccountServer": "string AccountServer()",
    "AccountStopoutLevel": "int AccountStopoutLevel()",
    "AccountStopoutMode": "int AccountStopoutMode()",
}


def add_marker(entry: dict) -> None:
    provenance = entry.get("provenance", "")
    if MARKER not in provenance:
        entry["provenance"] = f"{provenance}; {MARKER}" if provenance else MARKER


def normalize_constant_placement(golden: dict, dialect: str) -> dict:
    """Move ALL-CAPS constants out of `functions` into `variables`."""
    moved_new, deduped = [], []
    functions = golden["functions"]
    for name in list(functions):
        if not CONSTANT_RE.fullmatch(name):
            continue
        entry = functions.pop(name)
        if name in golden["variables"]:
            # Misplaced twin of an existing variables entry: drop it and mark
            # the survivor — no description is overwritten, no data is lost.
            add_marker(golden["variables"][name])
            deduped.append(name)
        else:
            # Constant only registered as a function: move wholesale. The
            # pseudo-signature ("Account property: balance" style) is schema
            # noise in the variables section — when the entry carries no CHM
            # description, that one-line label becomes the description so the
            # variables projection never registers an undocumented name.
            moved_new.append(name)
            golden["variables"][name] = {
                "description": entry.get("description") or entry.get("signature") or "",
                "provenance": entry.get("provenance", ""),
            }
            add_marker(golden["variables"][name])
    print(f"{dialect}: {len(moved_new)} constants moved functions->variables, "
          f"{len(deduped)} misplaced twins removed")
    return {"moved": moved_new, "deduped": deduped}


def normalize_legacy_account_functions(golden: dict) -> dict:
    """Move the legacy MQL4 Account* functions from `variables` to `functions`."""
    moved = []
    variables = golden["variables"]
    for name, signature in ACCOUNT_FUNCTION_SIGNATURES.items():
        if name not in variables:
            raise SystemExit(f"{name}: expected in mql4.json variables, not found")
        entry = variables.pop(name)
        if name in golden["functions"]:
            raise SystemExit(f"{name}: already in mql4.json functions — dataset drifted")
        golden["functions"][name] = {
            "signature": signature,
            "provenance": entry.get("provenance", ""),
            "description": entry.get("description", ""),
        }
        add_marker(golden["functions"][name])
        moved.append(name)
    print(f"mql4: {len(moved)} legacy account functions moved variables->functions")
    return moved


def main() -> None:
    for dialect in ("mql4", "mql5"):
        path = REPO / "data" / "builtins" / f"{dialect}.json"
        golden = json.loads(path.read_text(encoding="utf-8"))
        before_f, before_v = len(golden["functions"]), len(golden["variables"])

        normalize_constant_placement(golden, dialect)
        if dialect == "mql4":
            normalize_legacy_account_functions(golden)

        after_f, after_v = len(golden["functions"]), len(golden["variables"])
        print(f"{dialect}: functions {before_f}->{after_f}, variables {before_v}->{after_v}")

        path.write_text(
            json.dumps(golden, indent=2, ensure_ascii=False) + "\n",
            encoding="utf-8",
        )


if __name__ == "__main__":
    main()
