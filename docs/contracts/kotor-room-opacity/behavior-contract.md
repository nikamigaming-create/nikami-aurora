# KOTOR room opacity/depth contract

## Reported defect

Solid opening-room sofa, bunk, and divider surfaces allowed the floor pattern
to remain visible through them. Chair geometry and source texture alpha were
initially investigated, but the defect persisted in baked `M01aa_01a` room
furniture and was a render-state problem.

## Evidence baseline

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- `M01aa_01a` MDL SHA-256:
  `6F8994A2049BD6D279C0F4BF3E33C876E4875BFB2ABAE49DE3F39E51B355B161`.
- `M01aa_01a` MDX SHA-256:
  `443835BA65135A43AC3F18206E1AE2CDCC0CDC91662D91951BF9CB9E5CF11DF6`.
- The affected `LHR_wall01`, `LHR_wall07`, `LHR_wall08`, and `LMI_bed01`
  decoded textures all contain alpha 255 at every pixel.
- Generated glTF materials omit `alphaMode`, which means `OPAQUE`, and use a
  base-color factor alpha of 1.0.

The source data therefore does not authorize transparency for these surfaces.

## Root cause

The lightmap shader assigned `ALPHA = base.a`. In Godot, using the shader
`ALPHA` output selects the transparent material path even when sampled alpha is
1.0. The companion non-lightmapped path also inherited imported depth/
transparency state without asserting the source-opaque contract. Solid
furniture consequently lost reliable opaque depth behavior and blended with
the floor behind it.

## Port behavior

For current room GLBs whose source contract is opaque:

- the lightmap shader never writes `ALPHA` and requests opaque depth draw;
- non-lightmapped `BaseMaterial3D` copies force `Transparency=Disabled`,
  `DepthDrawMode=OpaqueOnly`, depth testing enabled, and albedo alpha 1.0; and
- runtime boot fails if no lightmapped or base opaque materials were audited.

Transparency must later be enabled only by a separate source-backed TXI/mesh
classification. A generic diffuse alpha channel is not sufficient evidence.

## Matched before/after gate

Both owned-runtime captures use:

```text
module=end_m01aa
target=34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88
camera_mode=chair-closeup
chair_count=3
fov=55.000
position=(15.420348, 0.9449998, -23.76386)
capture_frame=90
```

- Before PNG SHA-256:
  `F2BF4FBF85BF69AEA1DED2CEE52728A8A6A84AB6B7033C2FE26F4CE86488F837`.
- After PNG SHA-256:
  `964A4B5E721B16DEC7DADF7E8175510662ED63EC24E98FBF7B0A942229AC5D8E`.
- A second audited after run reproduced that PNG byte-for-byte.
- 136,158 of 921,600 pixels changed above a two-level RGB tolerance
  (`14.774%`); changes fill the previously translucent furniture surfaces.

Runtime audit:

```text
NIKAMI_AURORA_OPACITY status=pass policy=source-opaque lightmapped=338 base=164 alphaWrites=0 depthWrite=opaque
```

## Regression gates and limits

Confirmed after the fix:

- the floor no longer appears through the sofa/bunk shells;
- all three chair placements remain visible;
- player Clothing, Short Sword, head/hand skinning, and animation remain intact;
- opening dialogue camera, voice, and LIP playback still execute;
- locker inventory and the `0→50→150` XP chain pass; and
- Oculus OpenXR without an HMD still follows the clean desktop fallback.

Explicit TXI blend/additive/environment-map semantics and genuinely transparent
room assets remain a separate classifier/import contract. This fix does not
guess those rules or treat all future Aurora-family materials as opaque.
