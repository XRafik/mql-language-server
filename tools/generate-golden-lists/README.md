# generate-golden-lists (issue #136, Fase 1)

One-off migration + maintenance scripts for the documentation-driven builtin
golden lists (`data/builtins/*.json`).

## Scripts

| Script | Purpose |
|---|---|
| `extract_registry.py` | Seeds the golden lists from the pre-#136 hand-curated C# registries. Run against `git show HEAD:` copies of `Mql4Builtins.cs` / `Mql5Builtins.cs` (the live tree loads from JSON since #136). Decodes C# escapes (`\u0026` etc.). |
| `apply_issue136.py` | Enrichment: (1) adds the documented names from issues #126/#136 with docUrls and `apiTier`; (2) migrates the 47 REQ-MA-02 admissions from the pre-#136 `Mql4OnlyApiRegistry.cs` (issue #34) into `mql4OnlyApi` + `mql5Mapping` metadata. Both steps are idempotent-guarded (refuses to overwrite existing entries). |

## Documented-name sources

- MQL4: `docs.mql4.com` reference sections (functions, constants,
  enumerations, predefined variables).
- MQL5: `www.mql5.com/en/docs` equivalent surface.
- `docUrl` may be section-level where per-name anchors are uncertain;
  upgrading them to per-name URLs is ongoing data work, tracked by
  `provenance: issue-136-documented`.

## Future enrichment (not yet automated)

The end state per #136 is a full documentation scrape producing these lists.
When adding that collector, keep the contract: the collector writes the JSON,
the parity tests gate the registry, and REQ-MA-02 admissions stay explicit
metadata (never derived).
