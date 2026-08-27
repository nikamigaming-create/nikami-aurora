# DAO gameplay camera contract

## Failure being removed

The proof runtime selected third-person camera distance from whether the loaded
area contained authored lights. Every real City Elf cell has lights, so this
forced a 1.35 m near-camera presentation. During the automated post-dialogue
movement it collapsed against house geometry and produced wall/shadow-only
frames. Lighting inventory is not a camera input in the source contract.

## Runtime ownership

`res://config/dao/presentation.json` owns the Godot gameplay-camera mapping:

- field of view;
- near and far planes;
- pitch;
- third-person spring length; and
- collision margin.

The loader rejects missing files, unknown fields, unsupported schemas, invalid
ranges, and unknown calibration states. World lighting configures only lights.
It cannot alter the camera distance.

The current profile uses the proof's pre-existing wide distance and is marked
`pending-retail-match`. This removes the lighting coupling and near-first-person
shortcut; it is not a visual-parity claim.

## Acceptance

The City Elf route cannot promote the profile to `retail-accepted` until a
matched human-controlled retail/Aurora row binds the same area, waypoint,
player orientation, zoom state, and resolution and proves:

- player and intended interaction target remain in frame;
- no wall-only frame occurs after the Shianni handoff;
- spring collision does not expose inside surfaces or hide the avatar;
- camera FOV, pitch, distance, orbit direction, and zoom limits match; and
- the same result holds in the house and Alienage, without branching on light
  count.

Automated dialogue choices and locomotion smoke may diagnose the camera, but
they are not acceptance evidence.
