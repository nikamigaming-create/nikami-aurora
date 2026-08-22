# KOTOR player and desktop/XR camera contract

## Source appearance

- Target executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.
- Deterministic proof portrait: `portraits.2da` row 18, `po_pmha1`.
- Portrait appearance: row 137, `P_MAL_A_MED_01`.
- Body: `PMBAM`; texture prefix `PMBAMA` resolves installed variation
  `PMBAMA01`.
- Head: `heads.2da` row 41, `PMHA01`.
- The proof does not consume a personal save file. Future character creation
  replaces only this selection input, not the assembly contract.

The generated player contains 11 meshes, 1,911 vertices, 2,603 triangles, five
skins, and two hook-bound head skins. `S_Male02` supplies `pause1`, `walk`, and
`run`.

## Movement presentation

`appearance.2da` records walk distance 1.813 and run distance 3.96. Dividing by
the installed clip durations yields 1.700 m/s walk and 5.400 m/s run. The
runtime owns no alternate guessed speed constants. `Profiles.Kotor` now owns
native-coordinate facing conversion, radial input dead zone, fixed-delta speed,
walkmesh acceptance, and closed-door rejection. Godot submits intent and applies
the returned position/locomotion mode.

## Desktop camera

`camerastyle.2da` row 0 supplies distance 3.2 m, height 0.45 m, pitch 83 degrees,
and view angle 55 degrees. The camera target is the player's installed
`camerahook`. A Godot SpringArm presents the derived behind-and-above view while
the same player transform remains authoritative.

The prior retail VR bridge independently measured a 3.50 m forward calibration
for cancelling the effective Endar Spire spring arm. This is compatible evidence
but not substituted for the 3.2 m authored style value.

## OpenXR authority

The XR path is opt-in and uses one `XROrigin3D` with one `XRCamera3D`. Player
movement changes the shared player origin. Gameplay, dialogue, and cinematic
camera cuts change only the base XR origin; the runtime continues applying live
HMD orientation and position relative to that base. Head tracking never writes
back into movement, NCS, collision, RNG, or plot state.

Desktop and XR are presentation modes over one player state:

```text
player intent -> shared player/simulation state
                         |
             +-----------+-----------+
             |                       |
      desktop SpringArm       OpenXR tracked camera
```

The action map defines aim/grip poses, left-stick movement, sprint, interaction,
recenter, and haptic output for Oculus Touch plus pose/interaction/recenter
fallbacks for the Khronos simple controller. Both tracked grip nodes are direct
children of the XR origin. Recenter uses `ResetButKeepTilt`; controller motion
maps through the same profile intent as desktop keys.

The startup probe created OpenXR 1.1.54 against Oculus runtime 1.207.0. With no
connected HMD it returned `XR_ERROR_FORM_FACTOR_UNAVAILABLE` and the game
continued through the desktop camera. Runtime discovery, action-map parsing,
simulation separation, and fallback are `confirmed`; physical-headset stereo
and controller sampling, render models, gameplay haptic events, and comfort
behavior remain unverified gates.

## Asset-free release boundary

Following the supplied Brobert release guidance, source, executable output,
user-owned game data, and signing identity are separate payloads. Final packages
must be inspected rather than trusting ignored paths. Local asset conversion is
hash-indexed, staged, and atomic; OpenXR packages never contain installed KOTOR
assets.

References: [Brobert asset-free Godot XR guide](https://github.com/Brobert-in-aus/guides/blob/main/vr/shipping-an-asset-free-godot-xr-port.md)
and [Godot XR setup](https://docs.godotengine.org/en/stable/tutorials/xr/setting_up_xr.html).
