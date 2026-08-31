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

## Presentation-tier transfer

The source comparison tier performs one transfer only. `surface` is the
installed diffuse sample, or the diffuse/environment-map mix selected by the
installed diffuse-alpha reflection mask:

```text
source.rgb = surface.rgb * clamp(lightmap.rgb, 0.0, 1.0)
```

Godot publishes this result through the emission channel with zero diffuse
weight. That is an adapter detail which prevents authored point lights or ARE
dynamic ambient from lighting an already-baked room texel a second time; it
does not add emissive energy. Source-transparent surfaces preserve diffuse
alpha and their no-depth-write policy.

The enhanced tier retains the bounded modern response used by the 2026
Forward+ presentation:

```text
albedo.rgb   = surface.rgb * 0.12
baked.rgb    = clamp(lightmap.rgb, 0.0, 1.0)
emission.rgb = surface.rgb * max(baked.rgb, dynamic_ambient.rgb * 0.15)
```

This enhanced mapping preserves the complete baked signal and may respond to
source-authored point lights through a restrained diffuse term. The low ambient
ceiling avoids flattening source sign/window highlights and room shadows into
uniform grey. It is an explicit presentation enhancement and is not retail
parity evidence. The former guessed `2.0` lightmap gain remains removed.
Dynamic objects use the ARE ambient value plus Godot point lights populated
from the exact source node values. Godot's point-light attenuation remains a
renderer mapping under test; it is not yet claimed as pixel parity.

## Acceptance

- Every mesh with a second texture and full UV2 array exports `TEXCOORD_1`.
- The glTF material carries the installed lightmap separately from diffuse.
- Godot binds the second texture to UV2, never UV1.
- The source tier reports `formula=surface-times-clamped-lightmap`, zero dynamic
  weights, and `double_light=0`; the enhanced tier reports its three bounded
  coefficients and `parity_claim=none`.
- The environment-map lightmap variant computes its diffuse/environment mix
  before applying the same tier transfer.
- Meshes without lightmaps retain the diffuse-only path.
- The manifest records the ARE source hash and every authored room light.
- The verified opening imports 134 source light records with no fabricated key
  light.
- Missing source lightmaps are inventoried by room/material identity under the
  `source-absence-report-no-fabrication-v1` policy. The manifest count must
  exactly equal the reported records, and runtime emits the same count without
  introducing a fabricated texture. Missing diffuse or TXI-declared bump-map
  resources remain fatal because no safe surface interpretation exists.
- Generated textures and proof captures remain ignored.

Required runtime evidence is:

```text
NIKAMI_AURORA_LIGHTMAP_TRANSFER status=ready tier=source formula=surface-times-clamped-lightmap diffuse_weight=0.00 baked_weight=1.00 dynamic_ambient_weight=0.00 dynamic_lights=0 double_light=0
```

or, for the enhanced showcase:

```text
NIKAMI_AURORA_LIGHTMAP_TRANSFER status=ready tier=enhanced formula=baked-preserving-bounded-dynamic diffuse_weight=0.12 baked_weight=1.00 dynamic_ambient_weight=0.15 dynamic_lights=1 double_light=bounded
```

The verified retail install contains one contradictory source reference:
`M01aa_04a/Object5044` binds diffuse `LHR_wall07` and declares lightmap
`M01aa_04a_a0002t`, but no TPC/TGA/TXI resource with that identity resolves
from the installation. The importer now records this as an unresolved
lightmap/material contract and fails if the known inventory changes. It keeps
the diffuse surface and does not synthesize or substitute a lightmap. This is
source-bound and functional, but that surface cannot claim retail lightmap
parity until contradictory evidence resolves the absent source identity.
