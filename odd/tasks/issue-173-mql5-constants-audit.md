# Issue #173 (round 2) — MQL5 constants audit with the evidence we already have

Second-round audit of the MQL5 golden list's constant entries against a documentation evidence base,
part 1 of #173. Part 2 (the five MQL4 unknown-bucket suspects) is already merged and closed:
`ArraySearch`, `PositionSelectByIndex`, `IsTradeAllowedWebRequest`, `SymbolInfoSessionOrder` were removed
in the round-2/3 adjudications and `HistoryTotal` was kept as a documented obsolete alias.

## Specs

**S0 — No crawling.** `mql5.com` bans easily, and the issue's premise that no usable evidence source
exists is wrong. The evidence base is the two artifacts already on disk: `data/page-cache/` (93 HTML
pages covering all 9 sections of `/en/docs/constants`) cross-checked with
`data/pdf/mql5-reference-layout.txt`. Nothing in this feature fetches the network.

**S1 — Measured coverage, reported honestly.** With those two sources, 1,802 of 2,023 variable entries
(89.1%) are confirmed documented by exact identifier match. The PDF alone covers 1,002 (49.5%) and is
therefore not an authority; it is used as a cross-check and to explain defect origin. The remaining 221
entries are not silently treated as fabricated.

**S2 — Classification, one rule per bucket.** Every variable lands in exactly one class, with the
evidence that decided it:
- `documented`: exact identifier present in the evidence base.
- `truncated-fragment`: the entry is a strict prefix or suffix of a documented identifier and is not
  itself documented (PDF column clipping plus kerning artifacts, e.g. `ALANCE_DD` from
  `(S TAT_BALANCE_DD)`).
- `undocumented-unknown`: neither documented nor explainable as a fragment. Kept, no removal (this is
  the conservative rule the user chose, mirroring the MQL4 rounds).

**S3 — Removal only when it loses no coverage.** A `truncated-fragment` entry is removed from
`data/builtins/mql5.json` only when its documented parent is **already** a dataset entry, so the
removal cannot create a false `5070`. Fragments whose parent is documented but absent from the dataset
are reported as gaps, not removed blindly.

**S4 — Additions only with page-level evidence.** A documented identifier missing from the dataset is
added only when its parent relationship is unambiguous: the parent is documented, at least 10 characters
long, is not itself a fragment, and the fragment maps to it by strict prefix or suffix. Ambiguous
matches (`J_C` → `OBJ_C`) stay in the report as adjudication candidates.

**S5 — Contract preserved.** The audit is a tool that writes a report
(`tools/generate-golden-lists/audit_mql5_constants.py` → `data/mql5-constants-audit.json`); the golden
list changes only through evidence-backed edits with per-entry provenance; registry code is untouched
(the #136 contract). Gate tests pin both the removals and the additions, and the kept-unknown count.

**S6 — Reversible and re-runnable.** Re-running the audit reproduces the same report deterministically
(no timestamps in compared fields), so a future pass with more evidence can reclassify without guessing.

## Tasks

- T1 — Audit tool + report (S1, S2, S6), with the classification counts as the acceptance number.
- T2 — Dataset edit: remove `truncated-fragment` entries whose parent is already present (S3), add the
  unambiguous documented gaps (S4), each with provenance (S5).
- T3 — Gate tests RED-first, pinning removals, additions and the unknown bucket (S5).
- T4 — Report the coverage limit and the deferred 192 in `CHANGELOG.md` + issue comment (S1).

## Log

L1 (user, 2026-10-26): asked whether the already-downloaded PDF could serve as the evidence source
instead of crawling, warning that mql5.com bans easily. Measured answer: the PDF covers 49.5% of the
dataset alone (51.4% with a kerning-normalisation attempt), the cached HTML covers 87.4%, the union
89.1% — so the PDF is a cross-check, not an authority, and no crawl is needed to run this pass.

L2 (user, 2026-10-26): conservative removal rule — retire an entry only with positive evidence of a
different documented name covering the same enum; names that are merely undocumented stay in the
unknown bucket. Rationale: undocumented intrinsics exist (`WFA_PerformCalculations`, #176), so absence
of documentation must not become a false positive for code that compiles.

L3 (parent, 2026-10-26): the issue's canonical proof of the evidence hole is itself wrong. `DEAL_BUY`
is not a documented MQL5 constant — it is a `hand-curated-registry-v2.5.1-rc.1` entry, and the
documented name in the same table is `DEAL_TYPE_BUY` (`DEAL_TYPE_BALANCE`, "Balance", is present in the
cached evidence). So the hole was never in the evidence source; it is in the dataset.

L4 (parent, 2026-10-26): origin of the fragments, found in `mql5-reference-layout.txt`: column-clipped
rows plus kerning-split identifiers ("(S TAT_BALANCE_DD).", "ACCOUNT_M ARGIN_MODE_RETAIL_HEDGING"). A
kerning-normalising re-extraction recovers only 37 more identifiers (+1.9%), which is why the PDF is
not promoted to authority.

L5 (parent, 2026-10-26): **first rule draft leaked new fragments into the additions.** When both
artifacts were treated as evidence, `add` proposed `TRADE_TRANSACTION_DEAL_`, `TAT_BALANCE_DD`,
`ACCOUNT_MA` — clipped cells from the PDF itself. Fix: removals and additions must be proven against
the HTML authority only (`html_authority_identifiers` = 1,973; the PDF contributes 2,986 identifiers
that exist nowhere in the HTML). A parent is additionally rejected when it ends in `_` or the
authority itself extends it with a letter, i.e. when the parent is itself a clipped cell.

L6 (parent, 2026-10-26): the audit and the apply must be run to a **fixed point**. Pass 1 removed 18
fragments and added 3 names; that made 4 more fragments' parents present in the dataset, so pass 2
removed them. Converged state: 2,004 variables, 1,805 documented (90.1%), 0 fragments, 0
adjudications, 199 kept unknowns. A single pass leaves 4 removable fragments behind.

L7 (parent, 2026-10-26): verification evidence.
- Deterministic: two audit runs produced byte-identical reports. Idempotent: a third apply reports a
  no-op against the converged dataset.
- Gate tests bite: with one fragment re-inserted and one addition deleted, both gates fail; after
  restoring the dataset both pass.
- Functional (real stdio session, pull diagnostics): the 3 added names produce no `5070`;
  `ORDER_LIMIT`, `ALANCE_DD`, `TRADE_TRANSACTION_DEAL` are now correctly flagged with no collateral
  diagnostics; `DEAL_TYPE_BUY` control unchanged.
- MQL4 symmetry: none of the 22 names exists in `mql4.json`, and the fragment rule applied to the
  official MQL4 CHM (690 HTML pages, 412/601 variables documented) finds zero fragments.
- Suite: 1,750 passing (1,745 with the PR-template filter) while the branch sat on `main` before
  #184 landed; after merging the main that contains #184 it is **1,760 (1,755 filtered)** against
  `main`'s **1,758**, both measured in throw-away worktrees, so the delta is exactly the two new
  gates. Registry code untouched (the #136 contract); parity tests included.
- A suite count belongs to the tree being delivered, measured after every merge. The first "1,760"
  was written from an adjacent branch's run; the figures above are this tree's, measured after the
  merge that brought #184 in.

L8 (independent verifier, 2026-10-26): **APPROVE**, all seven claims replicated independently
(`mvpnxckd-6-to0k`). Confirmed: the 22 removed names are all absent from the HTML authority and each
has a documented parent present after the change; the fixed-point arithmetic (18+3, then 4) reproduced
exactly; the 3 additions byte-match their `f_EnumID`/`f_EnumDesc` row with entities decoded; 199/199
unknowns kept; no `src/` or `tests/fixtures/` diff; MQL4 re-checked by the verifier itself (690 CHM
pages, 412/601 documented, 0 fragments); audit deterministic (report sha256 unchanged across runs) and
apply a verified no-op by hash.

Two corrections taken from its flags:
1. The suite figure claimed 1,760; measured on the pre-merge branch it was 1,750, and 1,760 after
   merging the main that carries #184 (main itself: 1,758). L7 records both.
2. "22 clipped table cells promoted by the pdftotext extraction" is precise for 21 of them:
   `CHART_SCRIPT` carried `hand-curated-registry-v2.5.1-rc.1` provenance, not PDF provenance. Its
   removal evidence is the same shape — it only ever occurs inside the documented
   `CHART_SCRIPT_NAME` — so the removal stands; the wording was fixed in the commit message and is
   recorded here.

Also flagged and accepted as by-design: the evidence cache lives in untracked
`tools/generate-golden-lists/data/`, so a fresh clone cannot re-run the audit until it is restored
(the tool fails loudly rather than inventing evidence, per S0). The dated decision snapshot
`data/mql5-constants-audit-2026-10-26.json` is committed with this pass so the 22+3 evidence is
reviewable in the PR even though the live report is regenerable.
