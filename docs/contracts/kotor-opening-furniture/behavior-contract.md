# KOTOR opening-room furniture contract

## Reported visual issue

The large bright L-shaped object visible near the opening-room furniture was
reported as malformed chair geometry. Source inspection separates two things:

- that large object is baked into room `M01aa_01a`; it is not a GIT chair
  placement; and
- the room's three actual `plc_chair2` GIT placeables were omitted by the
  previous one-placeable importer slice.

The correct fix is to materialize the missing placeables, not reshape or hide
source room geometry.

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `M01aa_01a` MDL SHA-256:
  `6F8994A2049BD6D279C0F4BF3E33C876E4875BFB2ABAE49DE3F39E51B355B161`.
- `M01aa_01a` MDX SHA-256:
  `443835BA65135A43AC3F18206E1AE2CDCC0CDC91662D91951BF9CB9E5CF11DF6`.
- `plc_chair2.utp` SHA-256:
  `2265CE488632F98866CC0B87989000C3EBB0E12CAAD00CE8B8454BA53FDD4AC0`.
- `PLC_Chair2` MDL SHA-256:
  `9EB4051F8E7AB51735CF296ADB81AFA056CDA4E4EA6F3B8E3937C1F59A2BF7DC`.
- `PLC_Chair2` MDX SHA-256:
  `42B69056F90F03A8C513416E72F33C98AE8254F7116DC5716A810D84C86099BB`.

The UTP resolves appearance row 20 to model `PLC_Chair2`, texture
`PLC_Chair1`, animation state 2, `Useable=false`, and no inventory or scripts.
MDLOps and PyKotor independently report one render mesh with 236 vertices and
156 triangles. Its sculpted parts are intentionally disconnected source mesh
components, not an exploded node hierarchy.

## Placement contract

The one ignored generated GLB is instanced at all three authored placements:

| Instance | Native XYZ | Bearing radians |
|---|---|---:|
| `placeable:0015` | `11.851921, 29.085163, -1.275000` | 2.552533 |
| `placeable:0016` | `18.961248, 29.168673, -1.275000` | 0.981719 |
| `placeable:0017` | `15.447873, 29.537743, -1.275001` | 1.374445 |

Godot materializes all source-backed placeable GLBs but only returns
`Useable=true` entries from interaction targeting. The three chairs therefore
render without acquiring the footlocker's prompt, inventory, or script path.
Each receives a deterministic placement ID even though the template tag is
shared.

## Verification and limits

Confirmed in the owned runtime:

- all three chair records load from one source-bound model;
- a room-scale inspection shows three distinct placed chairs with authored
  bearings;
- the footlocker remains the only usable opening-room placeable; and
- the complete locker/door XP replay still passes `0→50→150`.

The placeable PWK/collision path, animation-state playback for animated
placeables, and broad automatic export of all 60 area placeables remain future
contracts. Generated models and captures remain ignored local data.
