# KOTOR room-lighting contract

## Source contract

Room MDL meshes may provide two texture identities and two UV channels:

- texture 1 / UV1: diffuse color;
- texture 2 / UV2: authored static lightmap.

Nikami Aurora preserves both channels in the generated GLB as `TEXCOORD_0`
and `TEXCOORD_1`. The Godot room shader samples each installed texture through
its matching UV set and modulates diffuse color by the lightmap.

The module ARE supplies dynamic ambient color. Room MDL light nodes supply
position, color, radius, multiplier, ambient-only, dynamic-type, shadow, and
priority values through their time-zero controllers. Those records remain in
native coordinates until the Godot adapter converts them with the same basis as
room and actor placement.

## Current implementation

```text
output.rgb = diffuse.rgb * min(1.0, lightmap.rgb)
output.a   = diffuse.a
```

The former guessed `2.0` gain was removed after it visibly clipped the Endar
Spire whites and disagreed with the independent renderer contract. Dynamic
objects currently use the ARE ambient value plus Godot point lights populated
from the exact source node values. Godot's point-light attenuation remains a
renderer mapping under test; it is not yet claimed as pixel parity.

## Acceptance

- Every mesh with a second texture and full UV2 array exports `TEXCOORD_1`.
- The glTF material carries the installed lightmap separately from diffuse.
- Godot binds the second texture to UV2, never UV1.
- Meshes without lightmaps retain the diffuse-only path.
- The manifest records the ARE source hash and every authored room light.
- The verified opening imports 134 source light records with no fabricated key
  light.
- Missing lightmaps fail locally without introducing fabricated textures.
- Generated textures and proof captures remain ignored.
