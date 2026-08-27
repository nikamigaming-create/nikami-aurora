# Roadmap

Progress is measured by evidence gates, not by file-count or launch-only
claims.

The canonical delivery target and claim boundary are defined in
[`DUAL-PROFILE-HELLO-WORLD.md`](DUAL-PROFILE-HELLO-WORLD.md). The OpenDAO
migration inventory is maintained in
[`OPENDAO-HARVEST.md`](OPENDAO-HARVEST.md). If an older gate description
conflicts with those contracts, the canonical contracts control.

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

## Gate 5 — KOTOR flat opening completion

- Replace PyKotor, MDLOps, and the Python conversion stack with public
  Aurora-owned Odyssey readers and deterministic payload generation.
- Run the game-native startup/menu/New Game path into the Endar Spire.
- Replace showcase/test progression with a human-driven route.
- Complete real first combat, level-1 state, save/Continue, desktop keyboard/
  mouse and controller behavior, and matched retail acceptance.

## Gate 6 — DAO flat opening completion

- Harvest provenance-clean OpenDAO components according to the migration ledger.
- Replace the private/Haven importer boundary with a public Aurora-owned Eclipse
  importer and deterministic payload generation.
- Run the DAO-native startup/menu/New Game/character-creation path into the
  selected female City Elf origin rather than the old Redcliffe shortcut.
- Complete its opening cinematic/dialogue, real level-1 action, save/Continue,
  desktop input, and matched retail acceptance.

## Gate 7 — dual-profile desktop Hello World release

- One self-contained launcher selects KOTOR or DAO, validates a legally owned
  installation, performs a staged atomic local import, and plays the accepted
  route.
- A clean machine needs no shell, repository, Python, private importer, Haven
  Tools, PyKotor, MDLOps, xoreos, or external game-specific converter.
- Package-content, dependency/license, performance, fresh-import, save/Continue,
  and matched-retail gates pass for both profiles from merged `main`.
- Publish only the accurately bounded opening-slice claim.

## Gate 8 — post-desktop expansion

- Resume physical OpenXR acceptance only after both flat routes pass.
- Expand KOTOR and DAO slice by slice toward complete campaign coverage while
  preserving independent profile gates.

KOTOR II, Jade Empire, Dragon Age II, Neverwinter Nights, Neverwinter Nights 2,
and The Witcher remain expansion profiles, not Hello World promises.
