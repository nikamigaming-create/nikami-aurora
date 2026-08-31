# KOTOR Endar Spire room-emitter contract

## Scope

Odyssey room MDLs contain persistent emitter nodes in addition to meshes,
lights, and walkmesh. The original room importer visited those nodes but
exported only mesh and light payloads. Encounter camera 20 therefore exposed a
flat clear-color region at the damaged corridor end even though the installed
room owns smoke and spark effects there.

Retail observation shows smoke and later sparks in this shot. `LSP_stars02` is
the separate exterior sphere used by other Endar Spire views; placing it behind
this corridor damage would be a fabricated fix.

## Source inventory

The installed `end_m01aa` room models contain exactly 12 emitter nodes:

```text
M01aa_08b  2 smoke
M01aa_03a  3 smoke + 2 spark
M01aa_02a  2 smoke + 1 spark
M01aa_05a  2 smoke
total      9 smoke + 3 spark
```

Source MDL/MDX SHA-256 pairs are:

```text
M01aa_08b 2AE810680D1358046A858B742B6A95E8BD029947C6AEF945EE136243A4FBE60D
           A927523805FEFE42FB3A150EB553FDD9ACBBE4F6E98138A8866361D55F817A79
M01aa_03a A7F693675CAACC7130CE40DFCC898871045A3AAD27D770F20548978EEDEDEB68
           EF707411E927944694692EDE9C46371320054CCB3D74B7832A7247EF0A386FA6
M01aa_02a B3F3F145433B7ACCD381C783756A64933E53767135198473833BF8D7E0E80FF3
           FCF67FE2B6E70D877E4557EB201ABD08C55ABCE7F629EFEF3278E90212A205A7
M01aa_05a B2283DB989A0A89872C8F78F8D19DBAC225FFF35A5F484520D636FB4A67842DC
           37AA4DC59D71C88F1B4DA8D1694C0E0373C2F6A7EFB03E23DF471D9CD32F73E3
```

The two installed emitter textures are independently hash-bound:

```text
fx_Smoke TPC  78769AC6CAF27DE056D08186FB0C6269929D7FC2AB821DA84CBCD4CD31B826B3
PNG payload   B7D7DBAB6D7408963A8ADE30D31F2670F4DA6F2D58B3D8345E944B4013EAD146
fx_Spark TPC  F956DC290162599F5D117B0329BF2FBF80DDDA3E4808DC1B38E3A9EA8F049091
PNG payload   996509E51E12F9197CC12B5A1710B80D56045D40DD61F7950E5CBF97CAA75A7D
```

Generated PNGs and room data remain ignored owned-install artifacts.

## Damaged-end contract

`M01aa_03a/Object107/smoke044` resolves through its parent transform to local
room position `(-0.193330, -26.347099, 1.649840)`. Its source controller values
are:

```text
texture=fx_Smoke update=Fountain blend=Normal grid=4x4
birthRate=40 lifeExpectancy=6 velocity=1 randomVelocity=0.3
spread=1.0472 radians mass=-0.022
gravity=0
alpha=0 -> 0.1 -> 0 at percentages 0 -> 0.4 -> 1
size=2 -> 4 -> 5
```

The importer fails if that topology, position, birth rate, lifetime, or size
curve changes. This is the source smoke that covers the damaged corridor end;
it is not a generic fog volume or star backdrop.

## Runtime transfer

Each emitter becomes one `GpuParticles3D` system beneath its authored room.
Aurora transfers resolved position and fountain direction, birth and random
birth rates, velocity and random velocity, gravity, mass, rotation, spread, lifetime,
color/alpha/size curves, sprite-grid bounds, frame rate, and blur length.
Authored emitter `xSize`/`ySize` remain raw provenance and convert at the
source-defined `0.01` metres per unit into a local spawn rectangle. Tinted
emitters multiply their lifecycle color by the source ARE ambient instead of
becoming self-lit white cards.

Gravity and mass are separate Odyssey controller slots (`grav=116`,
`mass=124`). The owned Endar Spire emitters all author `grav=0`; mass ranges
from `-0.022` for smoke to `1.0` for sparks. Mass remains provenance, while the
Godot acceleration now uses only the authored gravity value. The former
`Gravity = -mass` mapping made sparks fall and smoke drift despite a zero-gravity
source contract and was the owner of the floating/curved particle mismatch.

- Normal `fx_Smoke` uses alpha blending, no depth writes, source 4×4 frames,
  random static frame selection when source FPS is zero, and lifetime prewarm.
- Lighten `fx_Spark` uses additive emission, source 2×2 animation, and a narrow
  quad elongated by the source motion-blur length.
- Both remain depth-tested against opaque room geometry and use source
  world-resolved directions rather than camera-facing guessed motion.
- The generic path accepts bounded finite `Single` particles and the persistent
  sprite signature used by `kas_m22aa`: `life=-1`, unit birth rate, zero base
  velocity/gravity, constant size, and a bounded one-based atlas interval.
  Persistent records become `AnimatedSprite3D`; finite records preserve their
  one-shot/loop lifetime and motion through a one-particle GPU system.
- Local-Z sprites use imported emitter basis. World-Z particles lie on Godot's
  XZ plane after Odyssey Z-up conversion, and aligned particles rotate local Y
  onto source velocity. Authored zero atlas dimensions remain provenance while
  the runtime uses their source-defined effective value of one.
- Random-playback flags map to a per-particle phase inside the authored atlas
  interval; they do not invent physical rotation. Static-room
  inheritance selectors are inert because the owned emitter parent does not
  move after publication.
- A straight point-to-point emitter is accepted only when flag `0x0001` is set,
  Bezier selector `0x0002` is clear, and the emitter has exactly one leaf child
  supplying a finite target at its static rest pose (either no position
  controller or one matching row at time zero). Import resolves that child
  through the authored emitter transform and converts the resulting target with
  the same Odyssey-to-Godot basis as the source. Runtime applies the exact
  positive source gravity as a constant-magnitude pull toward that position,
  disables the ordinary world-down gravity term, and isolates each target and
  particle system on its own per-room attraction layer. Visibility bounds
  include the source-to-target distance plus bounded lifetime travel.
- Bezier point-to-point paths and missing, animated, ambiguous, or non-finite
  targets remain fail-closed. Wind, collision bounce/splat, active
  parent-velocity or particle inheritance, depth-texture, unknown flag,
  nonzero spawn-type, and frame-blending records also remain fail-closed until
  their required engine joins are source-proven.
- Enhanced Forward+ emitters enable Godot's depth-backed proximity fade
  over a bounded `0.45` metre intersection distance. This softens the hard
  billboard edge where smoke crosses room geometry without changing the source
  texture, sprite phase, motion, or lifetime. The same fade covers non-oriented
  Lighten cards so flares do not draw a hard intersection against room
  geometry. Source presentation keeps the exact non-faded material path.
- Owned effect payloads receive deterministic mipmaps in both tiers. Enhanced
  presentation also upsamples each atlas cell independently with bounded
  Lanczos filtering (minimum 128 pixels per cell, maximum 4x) before rebuilding
  mipmaps. It does not change the source grid, frame interval, particle size,
  color, alpha, or timing and cannot bleed one source frame into a neighboring
  atlas cell. Lighten cards contribute the source once; the former duplicate 2x
  emission/exposure path is removed.

## Acceptance

Module boot must report:

```text
NIKAMI_AURORA_ROOM_EMITTERS status=ready module=end_m01aa authored=12 materialized=12 alpha=9 additive=3 single=0 point_to_point=0 smoke=9 sparks=3 soft_fade=12 soft_fade_distance=0.45
```

The first encounter cannot pass without the same 9/3 counts and damaged-end
binding, and the damaged-end gate also requires authored gravity zero. Desktop
camera 20 and the active Vulkan/OpenXR spectator both show the
source smoke over the former clear-color hole. Exact particle RNG phase and
Odyssey motion-blur kernel parity remain unclaimed.

The corresponding source-tier gate reports `soft_fade=0` and
`soft_fade_distance=0.00`.

The clean generic motion fixture `kas_m23aa` reports `authored=138`,
`materialized=138`, `point_to_point=9`, and enhanced `soft_fade=9`.
Gameplay-distance frames 180 through 182 use the source 55 degree field of view
and a 3.231 metre camera distance. They show the selected firefly cluster moving
between neighboring frames around the same finite 0.651 metre source-to-target
segment reported by `NIKAMI_AURORA_P2P_CAMERA`, whose `quad_max_m=0.040`
telemetry independently closes the false 3-to-4-metre card hypothesis. The
enhanced 64x64, 2x2 atlas is filtered to 256x256 as four independent 128x128
cells, replacing the initial magnified square texels with the bounded source
flare. This proves runtime publication, temporal target-directed motion, and
the generic filtering/soft-intersection improvement; retail RNG, blend-kernel,
and matched-camera parity remain unclaimed. The billboard construction remains
visible where several authored flares overlap, so final particle visual parity
also remains an explicit gap.
