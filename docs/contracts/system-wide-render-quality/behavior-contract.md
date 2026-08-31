# System-wide render-quality contract

Status: **active integration gate**

This contract selects presentation once for the application. A profile, area,
module, layout, room, camera beat, or source resource may provide authored facts,
but none of those identities may select a lower or different renderer policy.
The game-neutral executable policy is
`Nikami.Aurora.Core.RenderingQualityPolicy`.

## Tiers and evidence

`source` is the comparison tier. Profile code remains responsible for source
camera, geometry, material, lighting, atmosphere, emitter, timing, and transfer
semantics. A Forward+ source run retains the application's non-semantic sampling
budgets (4x MSAA, debanding, 16x anisotropic ceiling, and high-quality shadow
maps), but enables no enhanced lighting capability and makes no parity claim.
`source-comparison-candidate` means only that the frame is eligible for a matched
retail comparison; it is not evidence that the comparison passed.

`enhanced` retains those source owners and applies the application-wide quality
policy. It is deliberately classified as `enhanced-non-parity`. A beautiful
enhanced frame cannot close a source-parity row.

Both tiers report `parity_claim=none` until matched build, state, camera, event
time, and source identity evidence is reviewed separately.

## Full-blast Forward+ target

The enhanced tier requires exact `forward_plus`. Missing mandatory capabilities
or an explicit enhanced request under Mobile or Compatibility fails closed; it
does not silently downgrade. An explicit source request remains available for a
lower-capability backend.

The project-level values below are application-wide quality budgets. They do not enable SSAO,
SSIL, SSR, SDFGI, reflection probes, or volumetric fog in an Environment by
themselves.

| Facility | Godot 4.7.1 value | Contract |
| --- | --- | --- |
| antialiasing | 4x MSAA; TAA off | 4x coverage is mandatory. TAA stays off because temporal accumulation can ghost moving particles and skinned characters. |
| debanding | on | Reduce visible gradients in skies, fog, and HDR output. |
| texture sampling | 16x anisotropic ceiling; trilinear mipmaps | Enhanced materials must request an anisotropic mipmapped sampler for this budget to take effect. |
| directional shadows | 8192, 32-bit depth, soft-ultra `5` | Higher sampling quality only; source light identity and shadow ownership remain profile-owned. |
| positional shadows | 8192 atlas, 32-bit depth, soft-ultra `5` | No invented lights or shadow casters. |
| SSAO | ultra `4`, full resolution, adaptive target `1.0` | Mandatory enhanced capability; disabled by the source tier Environment. |
| SSIL | ultra `4`, full resolution, adaptive target `1.0` | Mandatory enhanced capability; disabled by the source tier Environment. |
| SSR budget | full resolution | Optional source-evidence gate; the Godot 4.7.1 project API has no global SSR roughness-quality setting. |
| SDFGI budget | full resolution, ray count `5`, convergence `5`, lights updated every frame `0` | Optional source-evidence gate, never a blanket replacement for authored lightmaps or probes. |
| volumetric fog | trilinear filter `2` | Optional source-evidence gate; density, bounds, color, and participation require a profile contract. |

AgX tone mapping is mandatory in each enhanced Environment and absent from the
source Environment. The Core decision exposes the immutable values through
`RenderingQualityPolicy.FullBlastValues` so runtimes and telemetry consume the
same target as `project.godot`.

## Reflection, SDFGI, and volumetric gates

Reflection maps and probes are never synthesized to fill a missing source slot.
An enhanced scene begins with source-bound probes and environment maps only.
Screen-space reflections require both runtime capability and explicit source
reflection authorization. Without either, the gate returns
`CapabilityUnavailable` or `SourceEvidenceRequired` and stays off.

SDFGI separately requires runtime capability and an indirect-lighting
authorization for the imported scene. It must not double-light baked lightmaps,
replace an authored irradiance probe, or erase the source tier's transfer
function. Volumetrics use the same two-part gate and must preserve camera
visibility and material identity.

## Scope and scalability

`RenderingSelectionScope.Application` with an empty key is the only valid
selector. `Profile`, `Area`, `Module`, and `Layout` scopes, plus a scene key
smuggled into application scope, throw `InvalidDataException`.

The mandatory enhanced capability set is Forward+, AgX, high-resolution
shadows, anisotropic filtering, SSAO, SSIL, 4x MSAA, and debanding. These fail as
one unit. SSR, SDFGI, and volumetrics are optional local gates because enabling
them without source coverage can be less faithful than leaving them disabled.
This is the scalability boundary: change tier explicitly, or fail; never select
quality from a scene name.

## Required telemetry

Every adopting runtime emits the decision's stable marker:

```text
NIKAMI_AURORA_RENDER_QUALITY status=ready scope=application tier=enhanced backend=forward_plus agx=1 shadows=1 shadow_size=8192 anisotropy=1 anisotropy_samples=16 ssao=1 ssil=1 msaa=4x taa=0 debanding=1 reflections=<0|1> reflections_gate=<status> reflection_policy=<status> sdfgi=<0|1> sdfgi_gate=<status> volumetrics=<0|1> volumetrics_gate=<status> parity_claim=none
```

Profile telemetry additionally records the source identities and authorization
that opened an optional gate. A global `1` without that profile evidence is not
an acceptance pass.

## Area-name audit

The Core request contains no scene identity that can affect a valid decision.
Acceptance rejects profile-, area-, module-, and layout-keyed selections. The
DAO synthetic policy is also evaluated with unrelated arbitrary layout strings;
layout is carried only for telemetry and the decisions remain identical.

Named-area branches still exist for authored arrivals, waypoints, effect
inventory checks, camera rules, and source routing. Those are source-behavior
owners, not renderer-tier selectors. The current KOTOR and DAO tier selectors
use only the application environment request and active backend, but must adopt
the Core decision and marker before either profile can claim this system-wide
contract is fully integrated.

## Validation

The 23 project setting paths and values in this contract were enumerated through
Godot 4.7.1's built-in `ProjectSettings` property list and parsed successfully
by the 4.7.1 Mono console. Core acceptance covers the concrete values, full and
gated enhanced decisions, source/enhanced separation, unknown capability
failure, scene-key rejection, and arbitrary DAO layout neutrality.
