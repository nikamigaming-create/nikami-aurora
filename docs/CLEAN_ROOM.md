# Clean-room and evidence policy

Nikami Aurora separates retail research from public implementation.

## Research/oracle lane

Retail bridges, debuggers, disassemblers, hook logs, address maps, and raw
binary observations belong in target-specific evidence dossiers or dedicated
oracle repositories. Each conclusion records the target SHA-256, game build,
module and RVA where applicable, static evidence, runtime evidence, confidence,
contradictions, and a proposed experiment.

## Public implementation lane

Only the following cross into the runtime repository:

- implementation-neutral behavioral contracts;
- public format specifications and properly licensed dependencies;
- target manifests containing hashes and non-copyrightable metadata;
- synthetic fixtures created for this project;
- independently written implementation and regression tests.

Do not commit proprietary binaries, extracted assets, decompiler pseudocode,
assembly transcriptions, copied retail data tables, or raw localized text.
Decompiler output is evidence, never source code.

## Verification

Every ported subsystem should have:

1. A target-bound oracle observation.
2. A neutral `behavior-contract.md`.
3. Synthetic unit or integration fixtures.
4. A runtime acceptance test.
5. A parity comparison that reports disagreement rather than hiding it.
