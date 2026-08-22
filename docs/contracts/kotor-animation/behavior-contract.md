# KOTOR creature skin and animation contract

## Source contract

Creature appearance is assembled from the installed UTC and rules tables. For
Trask this resolves the body, body texture, unique head, right-hand weapon, and
the `S_Male02` inherited animation supermodel.

Skin slots are mapped through the inverse of each MDL skin `bonemap`. The
stored QBone quaternion uses binary `w,x,y,z` order and the paired TBone is its
translation. For a mesh and animated joint, the neutral skinning contract is:

```text
mesh-local joint matrix = inverse(mesh absolute) * joint absolute * inverse bind
```

The exported glTF expresses the equivalent joint absolute and inverse-bind
relationship. Weights are normalized per vertex and preserve up to four source
influences.

## Hook-bound models

Body, head, and weapon are not flattened into one pose. The head keeps its own
skin bind space beneath the body's authored `headhook`; the weapon remains
beneath `rhand`. Shared supermodel animation names target the body hierarchy,
while head-only neck and facial names target the hook-bound head hierarchy.
Merging the head bones into the torso without preserving that bind space puts
the head inside the collar and is explicitly rejected by visual QA.

## Current proof

- 13 rendered meshes and 2,250 vertices;
- five glTF skins;
- `pause1`, `tlknorm`, and `walk` clips;
- 88, 83, and 70 animation channels respectively;
- Trask's source bearing preserved through the KOTOR-to-Godot basis;
- pistol attachment follows the animated right hand;
- authored dialogue-camera capture verifies that the head is present and the
  talk pose is not the source rest/T-pose.

Rest-pose reconstruction errors are below `0.0000009` source units for the
torso and below `0.0000005` for each arm. This rules out hand scaling from the
skin-slot, weight, or inverse-bind mapping. Very close wide-angle QA cameras are
not valid proportion references.

Lip animation timing, per-dialogue gesture selection, animation events,
transitions, and dangly-mesh simulation remain separate parity gates.
