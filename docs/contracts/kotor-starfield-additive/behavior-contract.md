# KOTOR Endar Spire starfield/additive-window contract

## Reported defect

Endar Spire viewport panels rendered as opaque black glass. The exterior
`LSP_stars02` texture was present in the owned bundle, but no stars were visible.

## Source evidence

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Opening room `M01aa_01a` MDL SHA-256:
  `6F8994A2049BD6D279C0F4BF3E33C876E4875BFB2ABAE49DE3F39E51B355B161`.
- Exterior room `M01aa_07b` MDL SHA-256:
  `094970F795437D24B4F6A9657CF4955B65B7422A900ACC0C5BDAFDDEB0AF9164`.
- Window overlay `LHR_dust01` TPC SHA-256:
  `1DDCFC95EA9BB34D94C83175133B0C184E9F109B79A049E6E06C1E0BDD8D6BAB`.
- Star sphere `LSP_stars02` TPC SHA-256:
  `0E6250336D46E05F5FD50EF66284B8ACFD0F8EB61171C6A0AD36BD7109F4F2F1`.
- Nebula overlay `LSP_nebula01` TPC SHA-256:
  `1BE6C3637AA29E7D1CE994E8F4F372A77A5CB70C5410BC0EA8E94F6A13217E48`.
- Reflection cubemap `CM_Endar` TPC SHA-256:
  `BE7AE29E41D85FE753C802887FEA0E5416F3080613CFE2447717717DB689C8C5`.
- Metallic equipment cubemap `CM_Baremetal` TPC SHA-256:
  `B63A103E1E10DCB333016EA2F5AEBFE2B1FCE63105B97D861AC63F1C7EA43871`.

`M01aa_01a` node `win_side` uses `LHR_dust01`, mesh transparency hint 3,
and source TXI directive `blending 1`. Its decoded alpha is fully opaque; alpha
coverage alone therefore cannot identify its blend behavior.

`M01aa_07b` contains a 512×512 `LSP_stars02` texture on an inward-facing sphere
that surrounds the playable module. The texture has 4.43% pixels above RGB 32
and is not missing or empty.

## Root cause and port behavior

glTF PBR has no standard additive blend mode. The importer previously discarded
the TXI directive, leaving `LHR_dust01` in the opaque path. Its black texels
wrote over the star sphere.

The importer now:

- records TPC TXI text in its texture cache;
- treats exact `blending 1`/`blending additive` directives as source additive;
- emits a blended, double-sided glTF material with an internal additive name
  marker; and
- retains the decoded texture unchanged.

Runtime recognizes only that importer marker, selects Godot additive blending,
and disables depth writes for the overlay. Black contributes zero while dust/
scratch texels add over the exterior star sphere. Ordinary alpha-blended blast
decals remain mix-blended, and opaque furniture/floor materials are unchanged.

`LHR_flr01` separately declares `envmaptexture CM_Endar`. The importer now
parses that TXI identity independently from additive blending, verifies that
the owned texture is a six-layer 64x64 cubemap, and exports all six faces as
hash-bound machine-local PNG payloads under `environment-maps/cm_endar/`.
Owned Sith armor, blasters, swords, and the broken-droid placeable also resolve
`CM_Baremetal`, exported under the corresponding machine-local directory.
Materials retain an internal environment-map identity marker. This closes the
missing-data/import-conversion boundary without treating the cubemap as a
visible sky or fabricating a replacement reflection source.

PyKotor's TPC reader normalizes the packed Odyssey cubemap with its documented
per-face rotations and positive/negative-X swap. The importer records the
resulting Godot/DDS layer order explicitly as X+, X-, Y+, Y-, Z+, Z-, records
the exported PNG row transform, and declares the Godot-to-Odyssey sample basis
`(x,-z,y)`. Runtime hashes and decodes all six square faces, regenerates matched
mip chains, binds the resulting `samplerCube`, and fails before gameplay if the
schema, order, transform, dimensions, payload, basis, material identity, or
source-map coverage drifts.

The exact retail reflection transfer weight remains unproven. The declared
source-tier adapter policy therefore uses the diffuse texture alpha directly
as a bounded lerp mask: no second guessed intensity is applied and the cubemap
replaces rather than additively blows out the surface response. Enhanced adds
`alpha * (1 - alpha) * 0.35` only to partially reflective texels and caps the
total reflection weight at `0.90`, guaranteeing at least 10% authored albedo.
The former `alpha * 1.35` enhancement zeroed the albedo contribution on 78.5%
of `LHR_flr01` pixels and visibly erased the floor pattern. Lightmapped
materials retain their UV2/baked-light pass. When TXI explicitly combines
additive blending with an environment map, runtime samples the same source-
alpha-bounded cubemap mix and then publishes it through `blend_add` with depth
writes disabled. When the mesh also has a UV2 lightmap, the authored lightmap
transfer occurs before the additive blend. Alpha-mixed environment materials
retain their distinct transparent environment variants. Imported actor, weapon,
placeable, room, and later player-equipment variant materials all pass through
the same binding path.

The owned corpus contains 17 additive/environment surfaces and two
additive/lightmapped surfaces; the latter also carry an environment map. Exact
manifest counts are checked against runtime bindings. This preserves every
declared source input without claiming exact retail blend-order parity.

Boot fails if the owned module produces no source-additive materials. Current
telemetry reports five:

```text
NIKAMI_AURORA_OPACITY ... sourceTransparentBase=22 sourceAdditive=5 ...
NIKAMI_AURORA_ENVIRONMENT_MAPS ... maps=2 faces=12 tier=source boost=0.00 maxWeight=1.00 basis=godot-to-odyssey:x,-z,y bindings=CM_Baremetal:N,CM_Endar:N
```

## Verification and limits

The equipped-player cabin capture now shows dense owned stars through every
visible viewport while preserving the player's head, hands, sword, room
lighting, furniture depth, and window dust lines.

This contract transfers explicit additive and environment-map TXI identities
through a working runtime cubemap response. Exact retail reflection transfer,
decal order, animated UV, ship animation, planet presentation, and complete
Odyssey TXI semantics remain separate parity work.
