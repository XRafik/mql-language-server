# Builtin golden lists (issue #136)

Documentation-driven single source of truth for the builtin registries and
the MQL4→MQL5 migration layer. Each file is embedded into the server
assembly (`LogicalName: MqlLanguageServer.Builtins.builtins.<dialect>.json`)
and consumed by:

| Consumer | Use |
|---|---|
| `Mql4Builtins` / `Mql5Builtins` | Builtin lookup suppressing 1070/5070 unresolved-symbol FPs |
| `Mql4OnlyApiRegistry` (rule 5060) | Migration warnings projected from explicit `mql4OnlyApi` admissions (REQ-MA-02, issue #34) |
| `RegistryGoldenListParityTests` | CI gate: registry ↔ this file must be in exact parity |

## Entry schema

```jsonc
// functions: name -> { "signature": "...", "apiTier": "shared", "docUrl": "...", "mql5Mapping": {...}, "mql4OnlyApi": true }
// variables: name -> { "description": "...", ... same optional metadata }
{
  "signature": "string TimeToStr(datetime value, int mode=TIME_DATE|TIME_MINUTES)",
  "apiTier": "legacy",              // legacy | build600 | shared (informational; drives nothing by itself)
  "docUrl": "https://docs.mql4.com/convert/timetostr",   // provenance; section-level URLs allowed
  "provenance": "issue-136-documented",                  // which step introduced the entry
  "mql4OnlyApi": true,              // REQ-MA-02 admission → projected into rule 5060
  "mql5Mapping": {
    "replacement": "TimeToString",
    "kind": "rename",               // rename | semantic | manual
    "semanticsChanged": false,      // true ⇒ 5060 only flags bare MQL4-style reads
    "note": "reason / migration hint (shown by rule 5060)"
  }
}
```

## Maintenance rules

- Adding a documented name: edit the JSON (never the C# classes), run the
  parity tests.
- A name must carry `docUrl` (official reference) or an explicit
  `provenance` explaining why it is present without a URL.
- `mql4OnlyApi: true` is a deliberate REQ-MA-02 admission decision, never
  auto-derived from `apiTier`: shared names with unchanged semantics must
  NOT be flagged by rule 5060.
- Regenerate the seed from the pre-#136 C# registries only with
  `tools/generate-golden-lists/extract_registry.py` against
  `git show HEAD:src/...` — the current tree no longer contains the
  hand-curated dictionaries.

## Provenance (2026-10, v2.5.1-rc.1 → rc.2)

- Seeded from the hand-curated C# registries by `extract_registry.py`
  (`provenance: hand-curated-registry-v2.5.1-rc.1`).
- Enriched by `apply_issue136.py`:
  documented names from issue #136 (`issue-136-documented`) and the 47
  REQ-MA-02 admissions migrated from the former
  `Mql4OnlyApiRegistry` (issue #34) with their migration mappings
  (`issue-34-migration-registry` for entries the old registry had missed).
