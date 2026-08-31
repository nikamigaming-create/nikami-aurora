# Dragon Age: Origins all-area render-fidelity contract

## Boundary

This contract applies the DAO renderer to every locally imported area. Layout
identity may appear in diagnostics, but it cannot select a presentation tier,
enable a shader, or authorize a fallback. Route runners may add stricter
inventory and capture assertions for a particular story slice.

The evidence is the generated catalog from a legally owned installation. It is
disposable and is not part of the repository. The audited catalog contains 352
ready profiles and 47,561 unique GLBs. Its 81,278 glTF materials all satisfy the
validated PBR factor, alpha-mode, alpha-cutoff, culling, and texture-identity
contract. Of the profiles, 349 carry the exact 29-field atmosphere contract and
three carry the exact eight-field base-lighting contract. The base-only areas
must still receive the application-wide enhanced renderer; only source-bound
sky, fog, probe, and volumetric transfers are unavailable.

## Application and material policy

`Nikami.Aurora.Core.RenderingQualityPolicy` is the single parser and selector
for backend and source/enhanced tier. The DAO policy supplies source evidence
facts, while the layout is telemetry only. Enhanced presentation requires
Forward+ and enables the common AgX, SSAO, SSIL, glow, shadow-map, MSAA,
debanding, and anisotropic sampling baseline. SSR and SDFGI stay fail-closed:
draw geometry does not prove reflection semantics, and a source light list does
not prove an indirect-lighting policy. Volumetrics require the exact atmosphere
contract. None of these enhancements is a retail-parity claim.

Every imported visible surface must retain its exact glTF material index and
content-addressed source-model and texture identity through Godot import,
PackedScene caching, and static batching. Runtime consumes source
`baseColorFactor`, metallic and roughness factors, normal scale, `alphaMode`,
`alphaCutoff`, and `doubleSided`. Enhanced materials use anisotropic mip
filtering for color, normal, and PBR textures; tint, expression, and other mask
samplers keep their established non-anisotropic semantics. Name-based foliage
or material repair is forbidden.

Terrain descriptors are accepted only with contained, existing palette and
blend-mask resources. No audited water descriptor currently authorizes source
shader parity. Source tier therefore stays on the imported glTF material.
Enhanced tier may use the finite installed water parameter vectors and exact
normal-map identity in a bounded Godot PBR shader so missing color semantics do
not become opaque-white geometry; telemetry labels the result enhanced,
source-semantic unsupported, and non-parity. DAO MAO semantics on ordinary
imported glTF materials likewise remain unsupported; an identity-ready glTF
material is not promoted to MAO parity. The effect path below has a separate,
narrowly decoded MAO semantic subset and does not change that world-material
limitation.

## Per-area readiness and evidence matrix

The catalog gate can write `opendao-all-level-render-matrix-v1` JSON with one
row for every ready source key. A row includes the area ID and layout only as
identity, plus source-entry/archive, profile, world-manifest, model, and actor
hashes. It separately reports geometry, exact PBR material count, lighting and
ATMO, effects, navigation, camera spawn/visibility, transition playability,
fresh environment evidence, and creature-gallery evidence. A successful
import never upgrades the latter runtime/visual fields.

Navigation is decoded from the installed PC ARL source using the exact
pathfinding/grid labels 3020/3110, dimensions 3086/3087, cell size 3088, base
position 3090, and byte accessibility list 3092. Dimensions, finite positive
cell size, exact list length, and at least one byte equal to one are required.
This establishes a navigation prerequisite only, not arrival, camera LOS, or
transition proof. The installed inventory has 149/153 unique layouts ready;
profile-weighted, 343/352 rows are ready, eight reference absent core ARLs, and
one has an unsupported ARL structure. Every absent/unsupported row remains a
matrix blocker.

The same matrix inventories 6,871 active authored actor placements. Each
placement retains template identity, active-manifest ordinal, source transform,
model hash, import validation, and per-model PBR readiness. An in-world
creature row cannot pass unless every expected active actor has a fresh visible
crop at its authored transform. Preview viewports, NPC substitutions,
teleported beauty shots, nonblack-only crops, and a partial contact sheet are
not accepted. Environment and creature frames carry their own file hashes;
when no validated runtime evidence manifest is joined, both statuses remain
`unverified` and their paths/hashes remain null.

`Run-DaoAllAreaRuntimeCensus.ps1` consumes that exact matrix and boots each
profile through the generic headless Forward+ path. It stores ignored logs and
a resumable `opendao-all-area-runtime-census-v1` report after every row. Exit,
world-ready/smoke markers, strict all-surface PBR, lighting, effects, spawn
warnings, and log hash are recorded independently. A headless load without a
spawn warning is only `prerequisite-pass`; it cannot become visual camera or
playability proof.

The generated light records do not expose the retail per-point-light shadow
bit. Point-light shadows therefore remain disabled and report that source field
as absent; a name such as `Fire` or `Candle` is not evidence. The source sun may
cast directional shadows, and enhanced shadow-map quality is application-wide.
Source-tier armour/skin, FaceFX, hair, and eyelash shaders remain unshaded and
manually apply the harvested DAO SH and point-light contract. Enhanced tier
selects separate shaded variants: it preserves the installed palettes, tint and
expression masks, alpha state, and glTF PBR inputs, but lets Godot evaluate
normal, roughness, specular, metallic, shadows, and environment lighting. The
runtime reports the active variant and shaded surface count; this enhanced path
is not a claim about the retail character-light shader.

## Effect policy and coverage

Effect materialization is definition-local and layout-neutral. The runtime
first validates the six curated contracts, then decodes the same neutral subset
from any exact installed PC `MMH V0.1` graph. Each decoded emitter retains the
MMH, MAO, and diffuse DDS SHA-256 identities; transform hierarchy, named target
direction, point/sphere/box spawn volume, birth and lifetime ranges, source or
world acceleration, full age-map color/scale keys, rotation ranges, flipbook
timing, orientation, and the accepted MAO blend semantic are transferred
without consulting an area or layout ID.

Age-map scale is not collapsed to one uniform `max(X,Y)` value. Both source
axes remain in the neutral age keys. Constant-aspect emitters size the quad by
that aspect and use the largest axis as the particle-process curve. Variable
aspect emitters use the same largest-axis process curve plus independently
sampled X/max and Y/max curve textures in the draw shader. This preserves
source X:Y evolution without changing birth, motion, color, atlas, or placement
policy. Malformed, empty, axis-empty, and zero-crossing scale curves fail closed
at emitter scope. Runtime telemetry distinguishes `constant-aspect` from
`source-independent-x-y` and reports the exact independent-emitter count.

Contact-sheet dimensions are likewise source-bound. The exact installed
combination zero columns, zero rows, and zero frames per second disables atlas
animation and is transferred as one static full-texture cell. This closes the
static foam emitter shared by `fxe_water_fall_03_p` through `_09_p`; each
retains its MMH, `fx_snowcloud.mao`, and `smoke04.dds` hashes. Mixed zero and
nonzero dimensions, non-finite/negative frame rates, and a positive frame rate
with zero cells remain `flipbook-contract-invalid` rather than receiving a
fabricated atlas layout.

Fail-closed behavior applies at two scopes. An absent/malformed graph, or a
graph with no emitter whose complete behavior is supported, suppresses the
whole definition. A graph with at least one complete emitter may materialize
only those emitters; every distortion or otherwise unsupported sibling emitter
is absent and independently counted. Linked particles, mesh particles,
movement-spread controllers, target attraction, normal-directed/cylinder
spawn, unsupported orientation, malformed volume data, and unknown MAO
semantics never inherit a generic card. Placement transforms are still
validated and every omitted definition/emitter remains explicit in telemetry.
Consequently a partially materialized definition is not full visual coverage.

The installed semantic audit also inventories 8,163 emitter placements with a
nonzero movement-spread controller and 4,526 with volume-normal-directed
velocity; those populations may overlap and are not an additive skip count.
The available source labels establish degrees and update-delay seconds for
movement spread, but not the stochastic update/integration law. The normal flag
is source-readable, but primitive sampling, inside-volume, and inverted-normal
behavior are not yet independently joined to the decoded fields. Both classes
therefore remain fail-closed instead of approximating them with Godot spread.

Enhanced tier adds size-derived proximity fade and per-atlas-cell edge
feathering to supported emitters. It does not use distance fade. Decoded
non-fire color and alpha remain at source unity exposure instead of receiving
a blanket dimming multiplier. Fire definitions receive bounded per-emitter
scale, exposure, and warm-core shaping, but the currently captured Ostagar
torch/bonfire remains too dark to establish acceptable flame fidelity. This is
still a legacy atlas path, not volumetric fire, and no fire parity claim follows
from materialization. Source tier retains decoded scale/color without these
adjustments. Godot's standard proximity fade is unavailable to the
independent-axis draw shader; telemetry therefore reports
`enhanced-standard-material-only` instead of fabricating equivalent depth
semantics for that class.

Before either tier creates a particle node, a profile-owned readability/safety
gate validates the complete age-map scale contract, the constant-versus-
independent aspect invariant, final card dimensions, DDS dimensions against the
authored atlas grid, frame rate and cycles per lifetime, visibility bounds, and
whether proximity fade is actually supported by the selected material path.
The hard renderer limits are 128 m per card axis, 16,384 m visibility extent,
4,096 atlas cells, 1,000 frames per second, and 4,096 cycles per lifetime.
These are corrupt-transfer limits, not evidence that a large source-authored
card looks good. The installed 69-definition audit reaches maxima of 100 m
(`fxe_sunbeam_orz`), 103.31 m visibility extent, 64 atlas cells, and 11 cycles
per lifetime. Large beams and cards still require their own temporal visual
review before any fidelity claim.

The catalog has 316 effect-bearing profiles, 69 unique definitions, and 16,229
placements. At least one source emitter now materializes for 47 definitions and
13,465 placements (82.97%), up from 6 definitions and 9,187 placements
(56.61%). That placement metric is deliberately not called complete coverage:
only 24 definitions and 836 placements have every known source emitter
supported. Another 23 definitions and 12,629 placements are partial in 278
profiles. The remaining 22 definitions and 2,764 placements are wholly
unsupported in 151 profiles.

The independent source-emitter census finds 69,176 emitter placements in graphs
whose MMH inventory can be inspected. Of those, 42,535 render (61.49%) and
26,641 do not. Of the rendered total, 4,103 emitter placements use independent
X/Y scale curves. Within materialized graphs, 7,552 distortion and 9,389 other
semantic emitter placements are explicitly skipped; the balance belongs to
wholly unsupported graphs. Three source-MMH-absent resrefs account for 244
definition placements whose emitter denominator is unknown and excluded from
61.49%. This denominator boundary is reported rather than guessed.

Within the 9,389 skipped semantic-emitter placements in otherwise
materialized graphs, the source-audit reason inventory is exact: 4,908 are
curated emitters whose neutral contracts are not yet recovered, 1,886 use
movement-spread updates, 1,680 use normal-directed spawn, 551 use linked
particles, 219 use mesh particles, 114 use asymmetric spread, 24 have invalid
sphere radii, four have empty scale maps, and three have invalid spawn boxes.
The installed `DADistortionMask.mat` `Particle`/`Particle_CS` material objects
are counted as distortion instead of being mislabeled as generic unknown MAO
semantics. Static zero-cell contact sheets and time-varying X:Y scale are no
longer in the skipped inventory.

## Acceptance

The offline catalog gate emits:

```text
OPENDAO_CATALOG_RENDER_FIDELITY status=partial profiles=352 layout_neutral_policy=352 exact_atmo=349 base_lighting_only=3 navigation_ready=343 navigation_absent=8 navigation_unsupported=1 glbs=47561 pbr_materials=81278 pbr_contract_ready=81278 alpha_mask=4414 alpha_blend=1498 double_sided=54643 effect_profiles=316 effect_definitions=69 effect_instances=16229 supported_effect_definitions=47 supported_effect_instances=13465 supported_effect_coverage=82.97% fully_supported_effect_definitions=24 fully_supported_effect_instances=836 partial_effect_definitions=23 partial_effect_instances=12629 unsupported_effect_definitions=22 unsupported_effect_instances=2764 unsupported_effect_profiles=151 partial_effect_profiles=278 rendered_effect_emitter_placements=42535 readability_validated_emitter_placements=42535 maximum_effect_card_dimension=100 maximum_effect_visibility_extent=103.31 maximum_effect_atlas_frames=64 maximum_effect_animation_cycles=11 independent_scale_emitter_placements=4103 known_source_effect_emitter_placements=69176 known_unsupported_effect_emitter_placements=26641 known_effect_emitter_coverage=61.49% unknown_effect_emitter_inventory_placements=244 distortion_emitters_skipped=7552 semantic_emitters_skipped=9389 active_creature_placements=6871 runtime_verified_areas=0 creature_gallery_verified_areas=0 readability_policy=source-scale+atlas+timing+bounded-fade effect_mao_semantics=decoded-subset mao_semantics=unsupported parity_claim=none
```

Every runtime area additionally emits `NIKAMI_AURORA_RENDER_QUALITY`,
`OPENDAO_AREA_RENDER_POLICY`, `OPENDAO_IMPORTED_PBR`, the world material and
effect censuses, and `OPENDAO_AREA_CONTENT_FIDELITY`. A base-lighting-only area
must show the same application tier and required enhanced features while
reporting atmosphere/volumetrics unsupported. A full-atmosphere exterior must
show source ATMO validation and conditionally enabled volumetrics. Arbitrary
non-City-Elf interior and exterior captures are smoke evidence for this generic
path, not retail-matched parity evidence. The opt-in close-effect capture selects
only an exact requested source resref, records its MMH hash and contract kind,
and emits two neighboring frames plus bounds, visibility, luminance, and motion
telemetry. It may isolate one installed graph emitter on a diagnostic-only
render layer so world occlusion cannot masquerade as a renderer failure. The
current `fxe_fire_large_p` neighboring-frame pair isolates its five-key
`Emitter_FireBase` on a neutral slate background. It reports
`scale_axis_contract=source-independent-x-y`, clear 5/5 bounds visibility,
projected height `0.5010`, central peak luminance `0.3147`, and motion ratio
`0.0334`; visual review finds a readable animated flame without a rectangular
boundary, giant card, streak, or occlusion. This is scoped evidence for one
independent-scale emitter transfer and atlas feather only. The graph remains
partial, and the pair does not establish whole-graph aesthetics, world-space
or retail parity. Earlier torch, smoke, lyrium, lava-burble, rapids, sunbeam,
and dark-background captures remain rejected or superseded.
The newly recovered static waterfall-foam emitter has corpus and runtime
contract coverage but no visual-acceptance claim: the available waterfall
capture still contains the previously rejected slab/streak artifacts from the
whole graph, so it cannot validate this isolated transfer.
