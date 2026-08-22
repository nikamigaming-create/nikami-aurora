# KOTOR opening movement and door contract

## Target

- Game: Steam KOTOR 1.0.3.0
- Executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`
- Module: `end_m01aa`
- Area: `m01aa`

## Grounded observations

- The module IFO entry is `(15.4248, 20.1215, -1.2750)` in KOTOR coordinates.
- The 15 layout room models contain 540 faces whose source surface material is
  marked walkable.
- The first placed door is template `sw_door_test001`, tag `end_door01`, at
  `(21.1225, 20.7130, -1.31576)` with bearing `1.57` radians.
- Its UTD selects generic-door appearance 48 and model `DOR_LHR01`.
- Its installed conversation is `end_door01`; its OnOpen script is
  `k_pend_door1xp`.

Confidence: confirmed for source identity, geometry, placement, and resource
resolution. Runtime movement projection and door model loading are confirmed
by the new Godot proof. Native door action semantics remain unknown.

## Port behavior

1. Convert source `(x, y, z)` to Godot `(x, z, -y)`.
2. Construct navigation triangles only from faces whose installed surface
   material is walkable.
3. Place the player at the IFO entry after barycentric projection to the nearest
   valid face.
4. Accept horizontal movement only when the candidate point projects to a
   walkable face; preserve the interpolated authored elevation.
5. Reject movement into the temporary closed-door obstruction radius.
6. Expose interaction only within 2.6 metres of the door.
7. Until NCS and MDL animation execution exist, label the Godot door tween as a
   temporary presentation and retain the native conversation/script identities.

## Port-side tests

- Entry projection must succeed at `-1.2750` metres.
- A 1.5 metre forward request from the opening camera must be accepted and stay
  at the same authored elevation.
- A candidate outside every walkable face must be rejected.
- The opening door must resolve to `DOR_LHR01` and emit its source tag,
  conversation, and OnOpen script in telemetry.
- Generated meshes, walkmesh triangles, dialogue text, and captures remain
  ignored local artifacts.
