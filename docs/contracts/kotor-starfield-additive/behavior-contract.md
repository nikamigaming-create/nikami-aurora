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

Boot fails if the owned module produces no source-additive materials. Current
telemetry reports five:

```text
NIKAMI_AURORA_OPACITY ... sourceTransparentBase=22 sourceAdditive=5 ...
```

## Verification and limits

The equipped-player cabin capture now shows dense owned stars through every
visible viewport while preserving the player's head, hands, sword, room
lighting, furniture depth, and window dust lines.

This contract transfers only explicit additive TXI directives. Environment-map,
decal-order, animated UV, ship animation, planet, and complete Odyssey TXI
semantics remain separate parity work.
