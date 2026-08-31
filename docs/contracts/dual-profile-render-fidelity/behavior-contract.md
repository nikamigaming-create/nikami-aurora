# Dual-profile 2026 render-fidelity contract

Status: **active implementation gate**

This contract covers presentation shared by the KOTOR Endar Spire and Dragon
Age: Origins City Elf slices. It does not turn a visually attractive frame into
a retail-parity claim. Camera, state, source identity, geometry, material, and
effect semantics close first; Forward+ enhancements are evaluated afterward
against the same source-bound scene.

Application-wide renderer selection and concrete Godot 4.7.1 quality values are
owned by the [system-wide render-quality contract](../system-wide-render-quality/behavior-contract.md).
No area, module, or layout may override that selection.

## Presentation tiers

- `source` preserves the authored camera, geometry, texture, light, emitter,
  blend/depth, timing, and transfer-function contract. It is the comparison
  tier for retail parity.
- `enhanced` keeps those owners unchanged and adds the 2026 renderer layer:
  AgX tone mapping, restrained HDR glow, SSAO, screen-space indirect light,
  and source-authorized volumetric/cloud integration where that evidence exists.
- Both tiers use the Forward+ renderer for normal desktop and OpenXR runs.
  Compatibility rendering is not acceptable evidence for volumetrics, fog
  materials, or the final enhanced presentation.
- KOTOR runs `enhanced` by default under Forward+.
  `Start-KotorGodot.ps1 -SourcePresentation` selects the source comparison tier
  without changing the
  imported content.

## Matched rows

| Route event | Required camera/state | Blocking acceptance |
| --- | --- | --- |
| Endar pre-combat and first combat | Retail third-person gameplay state for gameplay; exact authored IDs for cuts | No first-person substitute, cinematic/player authority leak, full-frame additive slab, or semantically different combat state |
| City Elf wake and Shianni dialogue | Source CUT/DLG camera event, actor placement, pose, equipment, and event-relative time | Intended subject is in front of the camera, occupies meaningful projected area, and is not replaced by a wall/bed/actor occluder |
| Alienage arrival | `bec100ar_elven_alienage`, `bec100wp_from_home`, player control | No clear-color hole; source sky/atmosphere, terrain, props, actors, water, vegetation, lights, and emitters join to the runtime census |

## Texture and PBR join

Aggregate texture counts do not pass. Every visible surface and effect records:

- stable source model/shape/material identity and source hash;
- every diffuse, normal, specular/environment, emissive, lightmap, opacity,
  tint/mask, terrain palette, cubemap face, and particle-sheet slot;
- decode color space, alpha policy, UV set, sampler/repeat mode, and runtime
  binding identity; and
- any documented conversion into roughness, metallic, normal, emission, or
  environment response.

Missing or ambiguous slots fail locally and remain explicit. The renderer does
not invent a normal map, replace a missing sky with a clear color, or guess an
environment map. Enhanced PBR may reinterpret a proven legacy channel only
through a deterministic, tested transfer function.

## Camera and visibility gate

Each cinematic switch evaluates the intended subject bounds against the active
camera before the shot is accepted. A passing shot has a finite transform, the
subject in front of the near plane, nonzero projected coverage, meaningful
viewport intersection, and no known source-geometry occluder covering the
subject. Camera IDs, FOV, switch order, and event-relative timing remain in the
telemetry. A fallback camera is reported as a failure, not silently substituted.

## Sky, atmosphere, and volumetrics

- Outdoor background coverage must come from source sky/atmosphere ownership;
  sampled background pixels cannot expose the project clear color.
- DAO cloud and fog-volume shaders execute only under Forward+. Cloud density,
  sharpness, depth, range, wind, sun, fog, moon, and probe inputs remain bound
  to authored fields where available.
- Volumetric enhancement is spatially bounded and must not obscure the camera's
  accepted subject or wash out material identity. KOTOR volumetrics remain
  disabled in both tiers until a source atmosphere or fog-volume contract is
  recovered; enhanced mode may not invent a global haze layer and present it as
  one-to-one lighting.
- Tone mapping and glow cannot turn a muzzle flash, impact, fire, or emissive
  texture into a screen-height white slab. Effect acceptance records projected
  coverage and finite HDR energy over its lifetime.

## Particle gate

Every materialized emitter preserves source attachment, orientation, blend and
depth policy, texture identity, sprite grid, birthrate, lifetime, size, velocity,
drag/gravity, update mode, loop/random policy, and event timing. Acceptance
reviews motion across neighboring frames. A particle that merely exists, but
floats in camera space or uses a guessed billboard/phase, remains failed.

## Evidence boundary

Tracked code and reports contain no retail assets, captures, localized text,
executables, caches, or raw reverse-engineering dumps. Private retail evidence
may establish the behavioral contract, while public acceptance uses hashes,
stable source identifiers, synthetic fixtures, telemetry, and user-owned local
imports.
