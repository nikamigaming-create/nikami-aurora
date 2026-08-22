# KOTOR profile gameplay-state contract

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- The three source-bound opening-script contracts and their hashes remain
  defined by the
  [opening-script contract](../kotor-opening-scripts/behavior-contract.md).
- `end_m01aa` contains duplicate door tags and many empty placeable tags. Tags
  are script lookup values, not unique world-object identities.

Decompiler output is not implementation input. Profile transitions consume the
validated behavioral contracts exported from the owned installation.

## Ownership boundary

`Profiles.Kotor` owns the opening slice's authoritative:

- player experience;
- per-placement door state;
- per-placement placeable-used state; and
- execution outcome for each validated script contract.

Godot sends interaction or script requests to the profile and consumes typed
presentation events. It does not independently award XP or decide whether an
object has already been used. Desktop keyboard and OpenXR controller
interaction therefore enter the same transaction path.

Each manifest placement receives a deterministic instance ID (`door:NNNN` or
`placeable:NNNN`) based on source order. NCS-style tag lookup remains
case-insensitive and resolves the first matching placement for this isolated
contract. Distinct placements with the same tag never share open/closed state.

## Transaction behavior

Every operation captures before/after snapshots and an ordered event list:

- using `end_locker01` for the first time marks only that placement used and
  executes `k_pend_chest02`;
- using it again is idempotent and awards no additional XP;
- opening a door executes its installed `OnOpen` script once for that opening;
- closing a door changes placement state without executing `OnOpen`;
- `k_pend_traskdl40` resolves `end_door01`, opens the exact placement, and
  emits the validated dialogue-script outcome; and
- unknown scripts produce an unsupported event without mutating the snapshot.

Contract construction rejects invalid SHA-256 values, duplicate instance IDs,
invalid XP values, mismatched plot-percentage math, and recursive
script-contract cycles.

## Verification

The dependency-free acceptance suite replays locker use, repeated locker use,
the dialogue-directed door open, its installed `OnOpen` XP branch, duplicate-
tag isolation, and a direct close. It asserts the complete snapshot transition
`0→50→150`.

An owned-install Godot replay against `end_m01aa` confirms:

```text
NIKAMI_AURORA_GAMEPLAY_STATE status=ready scripts=3 doors=15 placeables=60 xp=0
NIKAMI_AURORA_NCS_CHAIN status=pass xp=0->50->150
```

This is a validated action-table slice, not a claim of a general NCS VM.
Inventory contents, equipment, party propagation, global variables, saves,
action-queue timing, and complete script coverage remain future contracts.
