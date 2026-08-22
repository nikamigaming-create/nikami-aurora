# Roadmap

Progress is measured by evidence gates, not by file-count or launch-only
claims.

## Gate 0 — repository baseline

- Source-only build and synthetic acceptance pass on Windows and Linux.
- KOTOR and Dragon Age profiles are registered independently.
- Owned-install probes emit SHA-256-bound target manifests.
- Public-safe and clean-room policies are enforced.

## Gate 1 — KOTOR resource catalog

- Implement exact Override, module, patch, RIM/ERF, and KEY/BIF precedence.
- Catalog the installed KOTOR resource set deterministically.
- Parse TLK, 2DA, GFF3, RIM/ERF, KEY/BIF, and module metadata.
- Keep all extracted content in an ignored local cache.

## Gate 2 — Endar Spire world proof

- Load `end_m01aa` room layout, visibility, models, textures, and walkmesh.
- Render a deterministic Godot scene from the owned installation.
- Bind every proof to exact source hashes and compare with the retail oracle.

## Gate 3 — playable world slice

- Player movement and collision.
- One correctly assembled and animated creature.
- Doors, triggers, waypoints, audio, and area transitions.

## Gate 4 — story runtime

- KOTOR NCS decoding and execution with a profile-owned action table.
- TLK-backed dialogue graphs, plot state, party state, inventory, and saves.
- Complete a deterministic dialogue and scripted transition in `end_m01aa`.

## Gate 5 — opening completion

- Complete the Endar Spire opening with evidence-backed gameplay behavior.
- Add desktop, controller, and OpenXR acceptance paths.
- Ship a self-contained launcher whose only required user input is the legally
  installed game folder; importer tooling remains internal to the release.

## Gate 6 — dual-profile proof

- Migrate generic OpenDAO services without importing DAO assumptions into Core.
- Boot one KOTOR and one Dragon Age area through the same runtime executable.
- Keep format, script ABI, rules, and presentation parity independently gated.

KOTOR II, Jade Empire, Dragon Age II, Neverwinter Nights, Neverwinter Nights 2,
and The Witcher are expansion profiles, not Gate 0 promises.
