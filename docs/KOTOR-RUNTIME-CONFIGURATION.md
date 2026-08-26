# KOTOR runtime configuration boundary

`config/kotor-runtime.json` is the public, versioned front door for KOTOR
profile policy. The owned-install importer validates it, records its SHA-256,
and embeds the resolved values in `module-manifest.json`. A runtime bundle is
therefore deterministic: changing the public configuration requires a local
re-import, and the Godot runtime fails closed when the schema or hash is absent.

The importer accepts an alternate public configuration explicitly:

```powershell
./scripts/Import-KotorModule.ps1 `
  -GameRoot '<owned KOTOR install>' `
  -RuntimeConfig './config/kotor-runtime.json'
```

## Literal classification

Not every numeric or string literal is configuration. The boundary is:

- Player baseline state, presentation adjustments, fallback colors and font
  sizes, test milestones, and engineering guardrails are profile policy and
  belong in `config/kotor-runtime.json`.
- GUI extents, localized strings, party statistics, resource identities,
  lighting, animation, effects, and timing extracted from the player's game
  belong in the ignored imported manifest. The runtime derives outer list and
  viewport dimensions from those source records.
- File-format tags, schema names, opcodes, equipment bit masks, hashes, and
  confirmed retail assertions are protocol or clean-room evidence. Moving them
  into a user-tunable file would weaken validation, so they remain named,
  fail-closed contracts in code and contract documents.
- Unit conversions, normalized bounds, zero/one identities, collection counts,
  and numerical safety checks are algorithmic invariants, not knobs.

This keeps the configuration cohesive instead of turning it into a global bag
of implementation details. KOTOR owns its policy; the game-neutral core does
not depend on it.

## Deterministic O(N) guard

Inventory filtering and overflow materialization use one shared
`KotorInventoryProjection` implementation. It reports definition visits,
dictionary lookups, filter evaluations, rows materialized, and total work
units. Runtime telemetry emits those counters.

The source-free acceptance suite evaluates the real projection over the sample
sizes and maximum exponent in `config/kotor-runtime.json`. It calculates the
adjacent log/log work-curve exponent and fails if the configured O(N) bound is
exceeded. This is deterministic operation accounting rather than wall-clock
timing, so CI load cannot create false regressions.
