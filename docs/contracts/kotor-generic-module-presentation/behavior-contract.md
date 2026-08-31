# KOTOR generic module presentation contract

## Boundary

Every imported Odyssey module selects one explicit content mode:

- `end_m01aa` requires `endar-opening` and its source-specific dialogue,
  cameras, encounter, and automation contracts;
- every other supported module requires `generic-world`, has no
  `firstEncounter`, and cannot request Endar automation.

The module identifier is normalized to lowercase ASCII letters, digits, and
underscore, with a 16-character maximum. The launcher selects
`local/kotor/<module>/module-manifest.json` from `-Module` and rejects a
manifest whose module or content-mode identity disagrees.

Physical RIM filename case is not manifest identity. Import enumerates the
owned installation's `Modules` directory, requires exactly one
case-insensitive match for both `<module>.rim` and `<module>_s.rim`, and passes
the physical filenames to the resource index. A missing pair or a case-folded
collision fails closed. The normalized manifest identity remains lowercase,
while target hashes are taken from the resolved physical files.

## Generic visual import

The importer resolves the module IFO/GIT/ARE/LYT and exports every listed room,
walkmesh, authored light, supported room emitter, material surface, diffuse
texture, UV2 lightmap, TXI-declared bump map, and referenced environment map.
Each resolved source resource retains its resref, type, byte count, and SHA-256
identity in the ignored local manifest.

`bumpmaptexture` selects the normal map and `bumpmapscaling` carries its exact
authored strength. The owned alias `bumpmapscale` is recovered from the raw TPC
footer and canonicalized only at the directive-name boundary. The value is
retained in the material contract, glTF `normalTexture.scale`, and a
deterministic material marker consumed by custom lightmap/environment shaders
and enhanced dynamic PBR. `bumpyshinytexture` remains an environment-map
directive and is never silently reinterpreted as a normal map. Conflicting
environment-map or bump-scale directives, scale without a declared bump map,
unsupported emitter semantics, missing diffuse textures, and missing declared
bump maps fail the corresponding coverage boundary.

Exact `decal 0`/`decal 1` is also source-rendered without a texture or module
allowlist. Enabled decals retain alpha blending and depth testing, never write
depth, and render at the profile-owned decal priority after ordinary priority-0
transparent room surfaces. The manifest records the decal surface count and
the runtime requires an exact marker/count join. Malformed, contradictory, or
otherwise valued decal directives fail closed.

Raw `channelscale` and `channeltranslate` records carry one declared mode/count
followed by exactly four numeric channel coefficients. The parser joins those
four bounded continuation rows only when all are finite scalars; incomplete or
malformed blocks remain visible as unsupported directives. Parsing the block
does not authorize its water/arturo/distortion procedure at runtime.

The emitter predicate is module-neutral. Fountain and finite-lifetime `Single`
updates preserve source birth/lifetime, motion, curves, blend, and atlas phase
through `GpuParticles3D`; persistent `Single` records require negative-one
lifetime, unit birth rate, zero base velocity/gravity, constant positive size,
normal blend, and a bounded one-based atlas interval. Normal persistent sprites
face the camera, while `Billboard_to_Local_Z` sprites use the emitter's imported
right/up basis. A zero authored atlas dimension is retained in provenance and
has an effective dimension of one, matching the bounded Odyssey reader rule.

`Billboard_to_World_Z` is a fixed Odyssey XY / Godot XZ plane and never falls
back to a camera billboard. `Billboard_to_Local_Z` uses imported emitter basis.
`Aligned_to_Particle_Dir` enables velocity alignment. Normal, Lighten,
two-sided, render-order, depth-test/no-depth-write, and sprite-grid behavior
remain distinct. Source depth-texture, trail-spawn, linked, lightning,
punch-through, and event-driven `Explosion` combinations still fail closed;
the room importer has no proven detonation-event join and does not fabricate a
continuous explosion card. Emitter-only room records receive a room anchor even
when the MDL contains no mesh, so their effects are not dropped.

Emitter `xSize` and `ySize` controllers are preserved as raw hundredths-of-a-
metre provenance and as validated metre footprints. Fountain particles spawn
across that imported local XY rectangle after the Odyssey Z-up / Godot Y-up
basis conversion; they do not stack at the emitter origin. The `TINTED` flag
multiplies lifecycle color by the owned ARE dynamic ambient. Neither transfer
depends on module ID or presentation tier.

There is contradictory clean-room evidence for persistent `Single` records
whose base velocity is zero but random-velocity controller is nonzero. One
implementation applies random velocity; another activates random modulation
only when base velocity is nonzero. The selected neutral contract treats the
modulation as inactive at zero base velocity, which preserves the owned static
bird-sprite class. Confidence is medium and `parity_claim=none`; matched retail
motion remains required to close the contradiction.

An absent referenced lightmap is contradictory source evidence rather than a
license to fabricate. It is carried under
`source-absence-report-no-fabrication-v1`; manifest count and records must
match at launch and runtime. The affected surface retains its exact diffuse
source and reports the omission.

## Runtime tiers

Both tiers use the same imported source identities.

- Source keeps diffuse-only room materials unshaded with dielectric specular
  `0`, roughness `1`, no normal-map response, exact bounded lightmap transfer,
  and no renderer-owned SSR/SSAO/SSIL/SDFGI/volumetrics.
- Enhanced uses shaded dielectric room materials with fallback specular `0.5`
  and roughness `0.68`, exact imported normal maps where TXI authorizes them,
  bounded lightmap/dynamic-light transfer, SSAO/SSIL, and SSR only where source
  reflective material/cubemap coverage authorizes reflections. SDFGI remains
  disabled over Odyssey baked lightmaps and volumetrics remain disabled without
  atmosphere evidence. Enhanced output carries `parity_claim=none`.

Opaque, transparent, additive, decal, lightmapped, environment-mapped, and
transparent environment-mapped surfaces retain separate blend/depth paths.
Every exported material surface, emitter, and environment map must be
materialized/bound exactly once in the aggregate inventory; unsupported source
semantics fail closed.

Source room lights with `affectDynamic=false` are classified as `baked_only`:
their static contribution is already carried by UV2 room lightmaps and they
must not be instantiated as Godot lights over the same surfaces. Eligible
dynamic lights, baked-only lights, ambient-only lights, and disabled records
must sum to the authored inventory. The ARE dynamic ambient remains the source
illumination for dynamic objects when no light opts into dynamic influence.

Enhanced presentation also normalizes every imported player, actor, equipment,
door, and placeable material without module IDs: non-additive surfaces use the
profile dielectric specular/roughness and exact normal texture when present;
source transparency, blend, culling, texture, and environment-map identity are
preserved. Source presentation is not modified by this postprocess.

## Non-Endar acceptance fixture

The owned `tar_m02aa` proof imports 17 rooms, 41,216 triangles, 574 material
surfaces, 72 resolved lightmaps, three exact TXI bump maps, and three cubemaps.
It reports three absent source lightmaps without fabrication. Runtime must boot
as `generic-world`, bind all three cubemaps (including transparent
`LTS_glass01` / `CM_M02int`), and report:

```text
NIKAMI_AURORA_ROOM_PBR status=ready module=tar_m02aa tier=enhanced pbr_surfaces=574 normal_mapped_surfaces=281 resolved_bump_maps=3
NIKAMI_AURORA_SOURCE_ABSENCE status=reported policy=source-absence-report-no-fabrication-v1 missing_assets=3 fabricated=0
NIKAMI_AURORA_KOTOR_BOOT status=pass module=tar_m02aa mode=generic-world rooms=17 authoredRooms=17
```

Generated manifests, textures, models, and captures remain local ignored data.

## Owned-install static preflight

`scripts/preflight_kotor_modules.py` performs a read-only room/material/emitter
inventory over paired RIMs. It emits JSON and a
`NIKAMI_AURORA_KOTOR_PREFLIGHT` marker to stdout, accepts no output path, sets
`writesProprietaryOutputs=false`, and carries the claim
`static-preflight-only-no-runtime-parity`. The default report completes even
when blockers are found so one unsupported module cannot hide evidence from
the rest of the installation; `--require-importable` returns nonzero when a
selected module has a hard blocker. Setup failures, ambiguous physical RIM
identity, and unclassifiable core resources return failure.

Coverage classes have deliberately narrow meanings:

- `blocked-core` cannot resolve or parse the module IFO/GIT/ARE/LYT boundary;
- `blocked-current-import-or-runtime` reaches that boundary but contains a
  material, emitter, model, texture, or TXI semantic the current lane rejects;
- `pass-with-structural-or-explicit-gaps` has no hard blocker, but retains
  reported source absence or an explicitly unconsumed structural hint;
- `preflight-pass-no-static-gaps` has no issue in the scanned subset only.

None of these classes establishes runtime playability, visual acceptance, or
retail parity. Actor/object assembly, scripts, dialogue, music, navigation,
and gameplay remain outside the scan. The preflight shares the importer's pure
room-emitter acceptance predicate and reports unsupported reasons as
`update`, `render`, `blend`, `grid`, `lifetime`, and `texture`; the predicate is
not copied into the audit.

On 2026-08-30, the owned target with executable SHA-256
`34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`
contained 117 paired module resources and no unpaired module IDs. Twenty-one
pairs used physical filename case that differs from normalized identity; all
117 were addressable after physical-case resolution. The static corpus covered
1,472 room placements / 1,160 unique room identities, 52,361 valid rendered
mesh placements, 4,650,848 triangles, 5,828 authored light placements, and 6,368
unique texture references probed.

The measured classification after the room-boundary and mixed-material fixes
was 54 modules blocked by current import/runtime semantics, 51 without a hard
blocker but with explicit structural/source gaps, and 12 with no issue in the
scanned subset. This is an observed support boundary, not a claim that 63
modules are playable.

All 179 formerly invalid room-resref occurrences are the literal Odyssey
`****` sentinel, confined to 15 `stunt_*` cinematic layouts. Import preserves
each placement as `sourcePlaceholder=true` with no MDL, MDX, GLB, light,
emitter, walkmesh, or material payload; runtime materializes an empty authored
placement node and reports `fabricated=0 skipped=0`. It is not treated as a
missing room or replaced with guessed geometry.

The same cinematic layouts have no `load_<module>` bitmap and no area minimap
texture. Import follows the ARE `loadscreen_id` through the installed
`loadscreens.2da` and binds that row's exact bitmap (`LOAD_DEFAULT` for
`stunt_00`); it records the table hash and selection path. The absent minimap
remains `null`, runtime reports `source-absent fabricated=0`, and no guessed map
image is generated.
The final scan resolved 91 module-named loading bitmaps and 26 exact ARE/table
fallbacks; 95 minimaps resolved and 22 remained explicit source absences.

All 31 former room-model parse failures across 26 unique MDLs reduced to one
boundary defect: the third-party reader rebased offsets after the 12-byte MDL
resource wrapper but retained the wrapper-inclusive accessible length. The
importer and preflight now read the exact source interval `[12, byteCount)`;
they do not append padding or substitute bytes. A previously failing model and
a known-good Endar model were checked through the bounded reader; the latter
retained the same 78 nodes, 62 meshes, 13 lights, and zero emitters as the old
path. Recovering this class exposed 1,164 additional valid mesh placements,
62,112 triangles, 92 lights, 214 emitters, and additional source TXI evidence.

The 17 additive-plus-environment surfaces (16 `envmaptexture`, one
`bumpyshinytexture`) and two additive-plus-lightmap surfaces now retain both
source semantics. Dedicated runtime variants bind the cubemap and/or UV2
lightmap before `blend_add`, disable depth writes, and remain unshaded rather
than silently entering opaque PBR. Manifest counts must match runtime bindings.
`tar_m09aa` provides runtime evidence for one additive/environment surface;
the two lightmapped combinations remain statically covered in `tar_m10ac`,
whose independent collision-bounce emitters still fail closed before boot.
No exact retail blend-order parity is claimed.

Focused enhanced runtime markers are:

```text
NIKAMI_AURORA_ROOM_PLACEHOLDERS status=preserved source=15 fabricated=0 skipped=0
NIKAMI_AURORA_MINIMAP status=source-absent fabricated=0
NIKAMI_AURORA_KOTOR_BOOT status=pass module=stunt_00 mode=generic-world rooms=17 authoredRooms=17
NIKAMI_AURORA_MIXED_MATERIALS status=ready additive_environment=1 additive_lightmap=0 depth_write=disabled fabricated=0 parity_claim=none
NIKAMI_AURORA_KOTOR_BOOT status=pass module=tar_m09aa mode=generic-world rooms=18 authoredRooms=18
```

All 1,036 exact `decal=1` surface occurrences now take the generic decal path.
The previous 216 numeric-only directives were the four bounded continuation
rows of 27 `channelscale` and 27 `channeltranslate` blocks; the corrected parser
reports zero unclassified TXI directive. Three modules consequently moved from
blocked to explicit-gap coverage without any module or texture allowlist.
Missing source lightmaps (45 occurrences), source minimaps (22), incomplete
diffuse UVs (98), nonzero mesh transparency hints (486), and empty render nodes
(16) remain explicit gaps without fabricated
replacement data.

The expanded TXI blocker histogram is 443 surface occurrences:
`proceduretype` 67 (`cycle` 40, `water` 21, `arturo` 6); `wateralpha` 67;
`fps`, `numx`, and `numy` 40 each; and `channelscale`, `channeltranslate`,
`defaultheight`, `defaultwidth`, `distort`, `distortionamplitude`, and `speed`
27 each. Every cycle record is `8x8` at `30` fps. The six arturo records carry
channel mode `1`, four unit coefficients, distortion `1`/amplitude `2`, speed
`6`, and a `32x32` default size. The 21 water records carry channel mode `4`,
scales `0.2 0.2 0.2 30.2`, translations `0.5 0.7 0.6 0.5`, distortion
`2`/amplitude `4`, speed `10`, and a `32x32` default size. Authored water-alpha
values remain distinct: `0.20` 18; `1` 14; `0.5` 11; `0.40` 7; `0.75` 6;
`0.7` five; and `0.3`, `0.4`, and `0.50` two each. Parsed procedure blocks
remain unsupported until their exact runtime UV/channel/distortion behavior is
implemented; the preflight does not discard or promote them.

Raw-footer recovery found an authored bump scale for all 4,764 bump-mapped
surface placements, with no invalid, conflicting, or orphan scale. Exact value
occurrences were: `1` 3,571; `2` 678; `1.3` 240; `0.125` 86; `0.5` 70;
`5` 45; `1.2` 30; `.3` 10; `.2` 8; `10` 8; `0.25` 7; `1.5` and `.1` four
each; and `.5`,
`0.3`, and `0.15` once each. No value is selected by texture or module ID.

The strict pre-parser-expansion comparison started at 1,928 of 2,296 authored
emitter placements (83.97%). Generic straight point-to-point transfer admits 68
more of that exact set, producing 1,996 of 2,296 (86.93%) without changing its
denominator. The expanded payload-bounded parser exposes 214 additional
emitters; the final shared import/preflight result is 2,200 of 2,510 (87.65%),
including all 71 straight point-to-point occurrences in that larger corpus.
This separates the motion gain from the parser lane instead of presenting all
newly visible records as particle support.

A supported point-to-point record must have the source straight selector, one
static leaf target at its exact transformed position, and finite positive
gravity. Runtime disables ordinary world-down gravity and applies that source
value as a constant-magnitude target pull on a per-emitter isolated attraction
layer. Source random velocity is added over `[0, randVel]`; it is not
symmetrically subtracted from the base magnitude. Bezier, missing, animated,
ambiguous, and non-finite target contracts remain fail-closed. The clean
`kas_m23aa` runtime reports 138/138 materialized emitters including nine
point-to-point systems. Gameplay-distance frames 180 through 182 use the source
55 degree field of view and a 3.231 metre camera distance, visibly establishing
target-directed temporal motion across the reported 0.651 metre segment.

The same runtime marker reports an exact `quad_max_m=0.040`: this owned effect
authors 0.03/0.04/0.01 metre size phases and a separate 2x2 metre spawn
rectangle. Size-phase values already use world units; only emitter `xSize` and
`ySize` use the source hundredths conversion. Enhanced presentation preserves
those dimensions while independently upsampling each low-resolution atlas cell
with bounded Lanczos filtering, generating mipmaps, removing the duplicate 2x
Lighten exposure, and extending depth-backed soft intersection to non-oriented
Lighten cards. Final gameplay-distance frames show filtered source flares rather
than the initial magnified square texels. This is generic presentation evidence,
not a retail RNG or blend-kernel parity claim. Card construction is still
visible where several authored flares overlap, so full particle visual parity
remains an explicit gap.

The remaining 310 blocked emitters report 18 lifetime, 268 render/motion, 24
update-mode, and ten texture reasons; reasons can overlap. Active render/motion
occurrences are 136 collision-bounce, 127 wind, five inherited-parent-velocity,
and five inherited-particle flags, with the five inheritance records overlapping
one another. Those classes remain fail-closed because their collision,
area-wind, or moving-parent/particle joins are not source-proven. Across all 117
module pairs, final presentation coverage is 54 blocked, 51 explicit-gap, and
12 no-static-gap. Emitter percentage does not erase the independent TXI or
other explicitly reported presentation gaps above.

This preflight uses the local PyKotor evidence toolchain. It does not make that
third-party implementation a first-party public-release import gate and does
not replace the repository's clean-room, packaging, or runtime acceptance
requirements.

## Second non-Endar exterior evidence boundary

The owned `kas_m22aa` static scan reaches six visual rooms, 21,582 triangles,
455 material surfaces, 37 resolved lightmaps, one exact TXI bump map, two
cubemaps, and 186 emitters. Its inventory includes 171 supported persistent
4x4 bird sprites, while 15 fountains carry active render/motion metadata that
the current runtime does not reproduce. The module therefore remains blocked;
an older 186/186 screenshot is not acceptance under this contract. All 64
authored room lights set `affectDynamic=false` and remain classified as
baked-only rather than silently dropped or applied a second time.

The clean runtime orientation fixture is instead `danm15`: its static scan has
no blocker or explicit gap, and both neighboring runtime captures report 35/35
materialized `Billboard_to_World_Z` emitters with `oriented=35`,
`oriented_alpha=35`, `distributed=35`, `tinted=35`, and `soft_fade=0`. Each
record authors a 2,000 by 2,000 emitter rectangle, validated as 20 by 20 metres,
and an ambient-tinted source color. Restoring those omitted transfers removes
the former origin-stacked white pools while keeping a subtle XZ-ground haze in
both source-linear and enhanced-AGX captures. This is runtime smoke for the
generic orientation and source-transfer contracts, not retail parity.

## Known incomplete semantics

Generic import retains arbitrary creature, door, placeable, trigger, waypoint,
and camera records and materializes available player, creature, door, and
placeable models without module IDs. Behavioral actor/object assembly, module
script execution, dialogue traversal, area music policy, pathfinding, and
retail camera-state parity require separate source-bound contracts. Their
records must not inherit Endar behavior or be presented as complete.

The former `tat_m18aa` room-parser boundary is closed by the exact payload-
bounded MDL reader. That module remains blocked only where independently
reported TXI or emitter semantics are unsupported; recovered geometry is not
used to erase those separate blockers.
