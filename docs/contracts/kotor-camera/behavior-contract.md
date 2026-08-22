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
framing. Source-style shot randomization and line-of-sight correction remain
pending isolated experiments.

## Verified opening evidence

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- 40 GIT camera records are preserved.
- DLG control entry 54 selects static camera 17.
- Camera 17 resolves to 55-degree FOV and its derived forward vector points
  toward Trask with cosine similarity above 0.96.
- The following visible Trask entry selects dialogue framing, replacing the
  transient static control shot.
- Gameplay camera FOV is 72 degrees; it is not used to judge dialogue-body
  proportions.

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
through the 72-degree player camera.
