# KOTOR room-lighting contract

## Source contract

Room MDL meshes may provide two texture identities and two UV channels:

- texture 1 / UV1: diffuse color;
- texture 2 / UV2: authored static lightmap.

Nikami Aurora preserves both channels in the generated GLB as `TEXCOORD_0`
and `TEXCOORD_1`. The Godot room shader samples each installed texture through
its matching UV set and modulates diffuse color by the lightmap.

## Current implementation

```text
output.rgb = diffuse.rgb * lightmap.rgb * 2.0
output.a   = diffuse.a
```

The texture identities, UV sets, and use of multiplicative static lighting are
confirmed from installed model data and runtime output. The `2.0` gain is
probable: it matches the conventional Odyssey lightmap range and produces a
material improvement over diffuse-only rendering, but final gamma/gain parity
requires a hash-bound retail comparison from the same camera.

## Acceptance

- Every mesh with a second texture and full UV2 array exports `TEXCOORD_1`.
- The glTF material carries the installed lightmap separately from diffuse.
- Godot binds the second texture to UV2, never UV1.
- Meshes without lightmaps retain the diffuse-only path.
- Missing lightmaps fail locally without introducing fabricated textures.
- Generated textures and proof captures remain ignored.
