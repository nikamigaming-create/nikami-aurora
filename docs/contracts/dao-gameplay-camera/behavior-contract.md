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
- source and enhanced camera-pivot height;
- third-person spring length; and
- collision margin; and
- enhanced-only obstruction-probe radius and avatar-clearance thresholds.

The loader rejects missing files, unknown fields, unsupported schemas, invalid
ranges, and unknown calibration states. World lighting configures only lights.
It cannot alter the camera distance.

The current profile uses the proof's pre-existing wide distance and is marked
`pending-retail-match`. This removes the lighting coupling and near-first-person
shortcut; it is not a visual-parity claim.

Enhanced Forward+ uses a sphere-shaped spring-arm probe instead of the source
comparison ray so door frames and wall edges cannot place the near plane inside
the room shell. When an obstruction compresses the authored-yaw arm below the
validated safe-framing distance, the runtime sphere-casts seven bounded orbit
candidates at 0, +/-35, +/-70, and +/-90 degrees. It accepts only candidates
that preserve the configured minimum avatar clearance, then scores them with a
capped arm-length benefit and an authored-yaw penalty. This makes the smallest
adequately clear three-quarter view beat an unnecessary side-on orbit. Switch
and authored-yaw return thresholds use hysteresis so grazing geometry cannot
make the camera snap between candidates.

If every orbit remains obstructed, the pivot moves higher and laterally into a
close over-shoulder composition. Separate face, hair, and eyelash meshes are
suppressed only while the camera is inside their bounds because those custom
materials do not reliably honor geometry-instance transparency; clothing/body
surfaces retain a bounded partial fade and the body silhouette remains visible.
Opacity, visibility, and the baseline pivot return with hysteresis as soon as
clearance is restored. Whole-avatar hiding is forbidden, so collision cannot
silently turn third-person gameplay into first person. The behavior is selected
at application scope, never by area/layout/light count, and is explicitly an
enhanced non-parity adaptation. Source tier retains the configured source pivot,
ray probe, authored yaw, and visible avatar.

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

The current `ost102d` enhanced obstruction fixture proves only the bounded
recovery behavior: the authored yaw was limited to 0.362 m, the scored selector
chose +70 degrees with 1.896 m predicted clearance, and the captured frame kept
the player body plus the room and fireplace visible. The remaining left-wall
coverage is accepted as a fixture caveat, not as retail composition evidence;
telemetry therefore remains `parity_claim=none`.
