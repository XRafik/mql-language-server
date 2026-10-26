# System-Include Synthetic Fixture (issue #179, S5)

Committed synthetic stand-in for a terminal `Include` tree. It exists so the
Tier-1 regression tests (`tests/Lsp/Handlers/SystemIncludeFixtureTests.cs`)
can prove that undeclared-symbol diagnostics for symbols reached only through
an angle-bracket include closure are suppressed — without requiring a real
MetaTrader installation and without any MetaQuotes/MetaEditor content
(project-authored code only, per `tests/fixtures/README.md` licensing rules).

## Layout

`Include/` plays the role of the terminal's `MQL5/Include` / `MQL4/Include`
root; the tests point `SystemIncludeRoots.Set(...)` at it.

| File | Nuance of the real Standard Library it imitates |
|---|---|
| `Include/Trade/TradeLib.mqh` | The `CTrade` pattern: one header declaring a base class (`CSynthObjectBase`) and a derived class (`CSynthTrade : public CSynthObjectBase`) that consumer documents instantiate. |
| `Include/Collections/ArrayLongLib.mqh` | The `CArrayLong` pattern: a collection-like type (`CArrayLongSynth`) with a count accessor. |
| `Include/Expert/ExpertLib.mqh` | An includer header one hop below the entry: pulls the collection header from the `Collections\` family, as real library headers depend on headers from another directory of the Include tree. This hop makes the closure transitive. |
| `Include/Expert/ExpertEntry.mqh` | The entry header the consumer document includes via `#include <Expert\ExpertEntry.mqh>`; everything else arrives through the transitive closure it opens. |

Include closure exercised by the tests (depth > 1):

```
consumer document
└── <Expert\ExpertEntry.mqh>            (angle-bracket, resolved via system include roots)
    ├── <Trade\TradeLib.mqh>           (angle hop: base + derived class, CTrade pattern)
    └── "ExpertLib.mqh"                (same-directory quoted hop)
        └── <Collections\ArrayLongLib.mqh>   (angle hop: collection type, CArrayLong pattern)
```

Cross-family hops use the angle form (Include-root addressing); the one
sibling hop uses the same-directory quoted form. Both are the include styles
the server's didOpen indexer resolves from inside a header (quoted hops are
containment-guarded to the includer's own directory, so sibling headers stay
colocated exactly like the real library's).

## Dialect coverage

Both dialects are covered: `.mqh` headers are indexed with the includer's
language, so this single committed tree serves the `.mq5` and `.mq4` consumer
documents the tests open (one include root, mirroring the established
`SystemIncludeResolutionTests` harness).
