# KOTOR dialogue-camera contract

## Source records

The module GIT camera list supplies an ID, position, height, field of view,
orientation, and pitch. GFF orientation values are stored in `w,x,y,z` order.
The camera transform adds height on native Z, applies the stored orientation,
then applies pitch in degrees around local X. The KOTOR-to-Godot basis is
applied only after that native transform is assembled.

DLG nodes independently supply camera angle, optional static-camera ID, FOV,
height, and animated-camera data. Blank control nodes can carry camera and
script directives even when they have no localized text.

## Dialogue framing

When a visible entry has no static-camera ID, the dialogue camera uses the
speaker and listener talk positions. The installed area row in
`camerastyle.2da` supplies the view angle; `end_m01aa` resolves to `55` degrees.
The speaker position follows the animated `talkdummy` node. Until a player
character model is assembled, the listener talk point is a documented 1.55 m
offset from the authored player entry.

The current proof chooses deterministic framing. The corridor transmission's
source `CameraAngle=1` uses a retail-calibrated tight-speaker shot after the
participants face one another; an angle-0 continuation by the same speaker
preserves that camera. Other dynamic nodes retain deterministic speaker-close
framing, including the first visible automatic (`CameraAngle=0`) Trask beat;
automatic framing is not treated as a gameplay-camera handoff. Desktop runtime
then fails closed unless the speaker bounds are fully contained, occupy the
expected projected-height band, and have a clear physics ray through dedicated
source-room trimesh visibility collision. The same isolated collision layer
drives the desktop SpringArm but not player/world movement. Collision meshes are
rebuilt from proven source-opaque surfaces only; transparent surfaces in a mixed
mesh are excluded, and an unknown active-material opacity fails closed. A
synthetic profile test fixes the mixed-surface selection at opaque indices
`0,2` for an opaque/transparent/opaque/transparent fixture. Source-style shot
randomization remains a pending isolated experiment.

## Verified opening evidence

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- 40 GIT camera records are preserved.
- DLG control entry 54 selects static camera 17.
- Camera 17 resolves to 55-degree FOV and its derived forward vector points
  toward Trask with cosine similarity above 0.96.
- The following visible Trask entry selects dialogue framing, replacing the
  transient static control shot.
- `camerastyle.2da` row 0 and owned-runtime `NIKAMI_AURORA_PLAYER` telemetry
  both resolve the gameplay view angle to 55 degrees; it is not used to judge
  dialogue-body proportions.

The first Sith encounter additionally binds camera 26 to the entering player
and cameras 19/20 to the scripted Republic-soldier attack target. Runtime
proves containment, projected size, depth, and source-room line of sight for
all three cuts. At the final handoff it synchronously publishes the SpringArm
child offset, restores player/Trask source-waypoint facing after dialogue
ownership, and requires a camera-behind-rendered-player cosine of at most
`-0.92` before declaring gameplay ready.

The GIT/DLG/2DA records and Godot telemetry are confirmed evidence. Quaternion
file order and transform sequencing are independently cross-checked against
the open reone implementation at commit
`9417f636f2d13c7b2f359308525009f286d765c0`; no implementation code is copied.

Confidence is `confirmed` for source values, WXYZ decoding, the static-camera
forward-vector test, and runtime camera selection. It is `probable` for the
speaker-close framing contract. Exact retail shot randomization is `unknown`
until a hash-bound frame sequence is compared. The port-side regression is a
capture in which camera 17 activates transiently, the visible Trask entry
selects speaker framing at 55 degrees, and the character is not rendered
through a separate guessed player FOV.
