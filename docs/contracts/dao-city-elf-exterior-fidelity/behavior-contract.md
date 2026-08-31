# Dragon Age: Origins City Elf exterior fidelity contract

## Boundary and source identity

This contract covers the transition from `bec110ar_players_house` into
`bec100ar_elven_alienage`, plus the opening `start_wake` camera that hands off
to the City Elf dialogue. It does not claim whole-game or KOTOR parity.

The owned-area evidence is
`modules/single player/data/al_bec01al_alienage.rim::bec100ar_elven_alienage.are`:

- archive SHA-256
  `bbb4862daba76e4036d09a3eacefe8b88aaa3f490853b8270e7465b9cde67318`;
- entry SHA-256
  `642fe8794424eaed2e77607ab72991877e9f23d75162b2aec530a9b200262b17`;
- 180 definitions, 5,267 instances, 211 GLBs, 898 external images, 29 exact
  environment-contract fields plus the two water-fog extensions, and 34 active
  actors in the local import validation.

Those identities and counts are metadata evidence only. The repository does
not contain the archive, extracted retail assets, private captures, or generated
cache.

## Source-first presentation contract

The earliest owner of each exterior presentation fact remains the installed
area data and its locally generated manifest. Runtime presentation must:

1. validate the exact 29-field ATMO contract and the two water-fog extensions
   for presence, type, vector length, finite values, count, and aggregate
   SHA-256 before presentation consumes them;
2. use a sky background instead of exposing the renderer clear color through
   gaps above or between exterior geometry;
3. preserve source transforms, instance colors, material bindings, and terrain
   blend masks;
4. reject a visible mesh surface without a bound material;
5. suppress meshes whose actual source mesh identity is a collision proxy,
   even when its definition-level semantic was incorrectly classified as
   visual; collision geometry remains available to physics;
6. restore mipmapped alpha-scissor foliage coverage instead of treating leaf
   and grass cards as sorted translucent particles; and
7. validate imported material texture payloads against their content-addressed
   SHA-256 names and retain installed glTF, terrain, and water slot identities;
   unauthorized water semantics remain explicitly partial; and
8. materialize the City Elf route's six installed MMH graphs from exact MMH,
   MAO, and high-resolution DDS identities. Every authored instance must own at
   least one source emitter. Unsupported distortion sub-emitters remain absent
   and named; generated `OpenDAO fallback FX` GLBs are never loaded.

`NIKAMI_AURORA_PRESENTATION_TIER=source` keeps linear tonemapping and disables
AgX, SSAO, SSIL, glow, volumetric fog, and the heuristic foliage coverage
repair. The default Forward+ `enhanced` tier may enable those features after
source composition. Volumetric density, sharpness, depth, and both range
multipliers are source-driven; no installed wind field exists, so the volume is
static and telemetry reports wind unsupported. Enhanced mode must require the
exact `forward_plus` rendering method. It must not move source geometry, change
actor identity, invent environment colors, or be cited as retail parity.
The source cloud layer samples static, seamless 3D value noise on normalized
view direction. The installed ATMO preserves cloud coefficients but not the
retail sky-dome UV transform, so this direction-space projection is an explicit
probable adaptation rather than a one-to-one renderer claim. A prior planar
2D noise domain produced large triangular facets. Diagnostic captures proved
that the facets persisted with the fog volume and every source effect disabled,
but disappeared when the sky-cloud branch was disabled. Enhanced mode may use
the world-shaped cloud fog volume only after the same upward capture passes.

## Objective cinematic framing contract

Camera 5 of `start_wake` is an authored point-of-view composition. Actor 3 is
the near camera-source actor and actor 4 is the intended subject. At the stable
sample after the cut:

- actor 3 is hidden for the duration of the shot;
- actor 4's head sphere is in front of the camera near plane;
- the complete head sphere stays inside a four-percent viewport margin;
- its projected height is between 0.06 and 0.65 of the viewport height; and
- a world ray from camera to head reports clear line of sight.

Failure of any item is a hard cinematic failure, including the historical case
where a wall shadow dominated the frame while the intended actor was not
visible. The renderer-neutral projection gate lives in
`Nikami.Aurora.Core` so other profiles can test the same facts without importing
DAO rules.

Camera 5 currently uses an explicit head-geometry POV adaptation because no
installed actor-relative camera attachment has been recovered. Runtime logs
the original CUT transform, adapted transform, positional delta, and angular
delta. Passing visibility proves a usable composition, not one-to-one retail
camera parity; matched camera telemetry is still required.

## Acceptance telemetry

A passing source-bound exterior emits:

```text
OPENDAO_AUTHORED_ATMOSPHERE status=ready background=source-atmo-sky preserved=31 exact_contract=29 mapped=27
OPENDAO_WORLD_MATERIAL_CENSUS status=partial binding_status=ready identity_status=ready layout=Den201d ... unresolved_identity=0
OPENDAO_WORLD_MATERIAL_CENSUS status=partial binding_status=ready identity_status=ready layout=den200d ... unresolved_identity=0
OPENDAO_WORLD_EFFECT_CENSUS status=ready materialized=ready parity=partial layout=den200d definitions=4 instances=32 rendered=32
OPENDAO_CITY_ELF_SKY_CAPTURE status=pass nonblank_ratio=... luminance_stddev=... luminance_range=... facet_edge_ratio=... facet_gate=pass
OPENDAO_CINEMATIC_VISIBILITY status=pass camera=5 actor=4
OPENDAO_CINEMATIC_POV_ADAPTATION status=adapted parity=probable camera=5
```

The four preserved-but-unmapped atmosphere fields are
`fog_water_intensity`, `fog_water_cap`, `moon_rotation`, and `skydome`.
Water-fog values are preserved in the installed water contract, but the
current water rendering path is not authorized as parity. `mapped=27` is a
transfer count, not a matched-capture claim.

A passing optional renderer layer under Forward+ also emits:

```text
OPENDAO_RENDER_PIPELINE status=ready method=forward_plus tier=enhanced tonemap=agx ssao=1 ssil=1 glow=1 volumetric_clouds=1
OPENDAO_RENDER_ENHANCEMENT status=ready renderer=forward_plus tier=enhanced tonemapper=agx ssao=1 ssil=1 volumetric_clouds=1 parity_claim=none
OPENDAO_VOLUMETRIC_CLOUDS status=ready source=are-atmo ... wind=unsupported-static enhancement=2026-quality parity_claim=none
```

Source-tier acceptance instead requires `tier=source`, `tonemap=linear`, and
zero for SSAO, SSIL, glow, and volumetric clouds. The playable smoke preserves
the player, head, and camera transforms, points the desktop camera toward the
Alienage sky for sixteen reprojection frames, records nonblank and spatial
variation metrics, captures `alienage-sky.png`, then restores gameplay state.
It also rejects the high-contrast edge density produced by the known planar
cloud-facet failure. The capture proves a rendered, non-faceted sky view, not
retail-matched cloud shape.

The City Elf route acceptance additionally requires the opening cutscene,
dialogue, transition, exterior gameplay, and player-control markers already
owned by the route runner. Pure synthetic acceptance proves projection and
wall-occlusion rejection independently of Godot.

## Delta classification

Confirmed source or implementation defects corrected here:

- the source sky shader and cloud-volume shader existed but were not bound to
  the loaded exterior environment;
- planar 2D procedural cloud sampling exposed value-noise cell boundaries as
  large wedges; effect-root, sky-cloud-off, constant-cloud, and one-octave
  isolation located the defect in that noise domain, which is now replaced by
  seamless static 3D direction-space sampling and an objective facet gate;
- compatibility rendering could not execute the fog shader used for
  volumetric clouds;
- `fne_dstbdgfloor_0` exposed a `UCX_*` collision mesh as visible gray geometry;
- imported foliage used unstable blended-alpha coverage; and
- the cinematic acceptance had no objective subject visibility, size, or
  obstruction check; and
- Godot could discard runtime material names and custom material metadata while
  caching or batching. Identity now follows the exact glTF material index,
  survives on per-surface MeshInstance metadata, is republished onto the active
  material Godot actually renders, and is required on every visible surface;
  and
- all route effect placements were dropped while tiny generated fallback GLBs
  existed in the cache. The runtime now blocks those GLBs and instantiates 3/3
  interior plus 32/32 exterior placements from installed effect resources.

Probable improvements, still requiring matched retail capture:

- source-coefficient atmospheric scattering, cloud placement, fog depth, AgX,
  AO/indirect contact, and foliage filtering reduce the observed flat, pale,
  and noisy exterior presentation;
- the point-of-view adaptation plus gate removes the near-side/wall-dominant
  failure mode, but its logged transform delta is probable until a matched
  retail camera trace proves the actor-relative attachment; and
- source-coefficient effect emitters restore texture, blend, depth, billboard
  or plane orientation, local basis, lifetime, emission range, and contact-sheet
  timing for supported nodes, but skipped distortion prevents full graph parity.

Unknown or explicitly unsupported:

- DAO distortion-mask semantics in three route graphs; these are skipped rather
  than replaced and keep effect parity `partial`;
- retail-exact water shading where the import does not authorize a source
  visual;
- exact roughness, metallic, and occlusion interpretation for material slots
  whose source semantics are not yet evidenced;
- exact atmosphere exposure and cloud shape without a matched-motion retail
  capture; and
- retail gameplay camera calibration: current FOV/pitch/spring values remain
  `pending-retail-match` because no matched player/camera telemetry is present;
- whole-area visual parity, all dialogue shots, and final character skin and
  cloth grading.

Unsupported effect nodes stay absent and are reported as such. They must not be
replaced by cache-owned fallback cards merely to make a capture look busier.
