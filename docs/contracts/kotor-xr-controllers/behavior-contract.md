# KOTOR OpenXR controller-presentation contract

## Ownership

Tracked head and hand poses are presentation/input state. They produce immutable
KOTOR profile intent and never directly mutate walkmesh, collision, combat,
inventory, plot, or NCS state. Desktop and XR use the same movement and
interaction actions.

## Action map

The versioned Godot action map exposes:

- aim and grip poses for both hands;
- `primary` thumbstick vectors;
- `primary_click`, `ax_button`, and `by_button` booleans;
- analog `trigger` and `grip` values;
- explicit `interact` semantics through `ax_button`;
- `recenter`; and
- per-hand `haptic` output.

Oculus Touch bindings cover every listed action. The Khronos simple-controller
profile supplies pose, interaction, recenter, and haptic fallbacks where analog
controls do not exist. Left-stick movement passes through the same radial-dead-
zone mapper and profile simulation as desktop input.

## Model provider order

Each tracked `XRController3D` owns a grip-local model container with:

1. `OpenXRRenderModelManager` using `XR_EXT_interaction_render_model`;
2. dynamically instantiated `OpenXRFbRenderModel` when the Meta Vendors plugin
   exposes it; and
3. a colored procedural fallback.

The procedural model remains visible until a runtime provider has produced a
child model, and only one representation is visible per controller. No guessed
rotation or translation is applied between the runtime model and grip pose.
Both portable and Meta render-model extensions are requested; Meta support
remains optional because the fallback is always present.

The grip containers and procedural fallbacks are safe to construct during a
desktop boot. Portable and vendor runtime-model nodes are added only after the
OpenXR interface successfully initializes; a disabled or unavailable runtime
therefore cannot enter the render-model extension lifecycle.

## Evidence and remaining gates

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Godot 4.6.3 loads the expanded action map without parse or binding errors.
- OpenXR creates an instance against Oculus runtime 1.207.0.
- With no connected HMD, initialization fails with
  `XR_ERROR_FORM_FACTOR_UNAVAILABLE` and desktop play remains functional.
- Grip/fallback hierarchy construction, runtime-provider deferral, clean
  desktop boot, and no-HMD fallback are `confirmed`.

Physical six-degree tracking, runtime model selection, trigger/grip/button/stick
animation calibration, focus-loss recovery, controller/hand switching, haptic
delivery (including the inventory-transfer pulse), and final Quest manifest
permissions require a connected-device gate and are not claimed complete.

Methodology reference:
[Brobert OpenXR controller-model guide](https://github.com/Brobert-in-aus/guides/blob/main/vr/openxr-runtime-controller-models.md).
