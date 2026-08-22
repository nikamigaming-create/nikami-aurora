# KOTOR OpenXR simulator and spectator contract

## Scope

This contract provides a deterministic development capture path for the active
KOTOR OpenXR presentation. It does not replace the normal physical-headset
runtime path and does not authorize a showcase video by itself.

Verified development stack:

- Godot 4.6.3 .NET;
- Meta XR Simulator 205.0;
- OpenXR loader 1.1.54 reported by Godot;
- Windows Vulkan 1.4.329, Godot Forward Mobile; and
- NVIDIA GeForce RTX 4070 SUPER for the owned-runtime observation.

## Runtime selection and system isolation

`Start-KotorGodot.ps1 -OpenXRSimulator` locates the newest installed
`MetaXRSimulator/v*/meta_openxr_simulator.json` unless an explicit
`-OpenXRRuntimeJson` is supplied. It exports `XR_RUNTIME_JSON` only to the
launched process tree and restores the caller's prior environment value.

The harness never runs the simulator's elevation script and never modifies:

```text
HKLM\SOFTWARE\Khronos\OpenXR\1\ActiveRuntime
```

The installed system runtime therefore remains Oculus after every test. No UI
automation, window focus, injected input, or registry mutation is used.

Meta XR Simulator does not expose `XR_KHR_opengl_enable`. The normal Godot
compatibility renderer correctly fails that binding, so the harness selects
Godot's Vulkan `mobile` rendering method. `NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE`
makes runtime fallback a test failure instead of allowing a desktop run to be
misreported as VR.

## Viewport topology

Normal OpenXR keeps the root viewport in XR mode. Simulator spectator mode uses
two viewports sharing the same `World3D`:

```text
OpenXRRenderViewport (Vulkan, UseXR=true)
└── XROrigin3D
    ├── tracked XRCamera3D
    └── tracked controller nodes/models

root viewport (UseXR=false, deterministic capture/movie output)
└── OpenXRSpectatorCamera (copies the tracked XRCamera3D world transform)
```

The XR viewport still owns the real OpenXR swapchain and tracked pose. The root
camera is a mono spectator mirror of that tracked HMD view; it does not replay a
separate desktop camera path. Because the root remains a conventional viewport,
Godot still capture and Movie Maker output can read it rather than receiving a
black XR swapchain texture.

## Camera base and height contract

Gameplay calibrates the current tracked head pose `H0` to the avatar's authored
local eye transform `E`, then carries that origin offset with the player body:

```text
GameplayOriginOffset = E × inverse(H0)
XROriginWorld = PlayerWorld × GameplayOriginOffset
XRCameraWorld = XROriginWorld × H(current)
```

At recenter time, the tracked eye is exactly at the authored avatar eye. Later
head translation and rotation remain relative to that calibration. This uses
the source eye height once; it does not add the source height on top of the HMD
height.

For an authored cinematic camera with desired world transform `D` and the
current tracked head-local transform `H`, the runtime sets:

```text
XROriginWorld = D × inverse(H)
XRCameraWorld = XROriginWorld × H = D
```

The cinematic formula exactly recenters the current head pose on the authored source camera while
preserving subsequent head-relative motion. The active encounter observation
reported 0.000000 m positional error and forward-dot 1.000000 for gameplay,
cameras 26, 19, 20, and both dynamic Trask dialogue bases. The previous
implementation added approximately 1.6 m of tracked head height on top of the
source camera and placed the mirror in the ceiling.

## Capture assertions

When a capture is bound to a dialogue/stage key, spectator mode waits one extra
matching render frame so the shared-world camera texture reflects the new pose.
It samples an 8×8 grid across the output and fails if every sample is near
black. A successful log must contain all of:

```text
NIKAMI_AURORA_XR_SPECTATOR status=ready source=hmd world=shared output=root
NIKAMI_AURORA_OPENXR status=ready ... spectator=True
NIKAMI_AURORA_XR_CAMERA_BASE status=recentered ... error=0.000000
NIKAMI_AURORA_FIRST_ENCOUNTER status=pass ...
NIKAMI_AURORA_CAPTURE status=Ok source=xr-spectator ...
```

The verified encounter still is 1280×720. Its visible-pixel fraction is 91.86%,
compared with 92.06% for the desktop combat-ready capture. Backend tone and
lighting differ: Vulkan's mean luma was 110.85 versus 88.68 for OpenGL. This is
an explicit renderer delta, not visual-parity evidence.

## Limits

- The spectator harness is intended for clean automated capture. Root Canvas UI
  is not duplicated into the XR subviewport in this mode.
- Physical-HMD pose, controller, stereo comfort, and delivered haptics still
  require hardware acceptance.
- The Vulkan/OpenGL tone difference remains uncalibrated against retail.
- Godot Movie Maker has not been invoked yet. No draft or showcase video was
  generated while developing this contract.
- Godot 4.6.3 emits two known engine-exit diagnostics after the post-draw
  shutdown request (interaction-profile RIDs and a spatial-entity disconnect).
  The final wrapper requires exactly those two post-route signatures and rejects
  every changed, additional, or pre-shutdown error.
- A final MP4 remains blocked on the complete startup-to-action route and all
  facial, opacity, audio, desktop, fallback, and active-XR gates.
