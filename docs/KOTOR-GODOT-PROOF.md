# KOTOR Godot opening proof

Nikami Aurora now imports the owned Steam KOTOR `end_m01aa` module and boots it
through the new Godot runtime. This path does not launch or inject into the
retail executable.

## Verified target

- Executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`
- Module: `end_m01aa`
- Area: `m01aa`
- Layout: 15 authored room records, including one walkmesh-only connector.
- Godot geometry: 14 visual room models, 78,346 triangles.
- Gameplay placements: 26 creatures, 15 doors, and 58 waypoints.
- Cameras: 40 installed GIT camera records plus the area camera-style row.
- Lighting: installed area ambient color and 134 room-model light records.
- Navigation: 540 walkable source triangles with an accepted grounded movement
  proof from the authored entry point.
- Player proof: portrait 18 / appearance 137, 11 meshes, 2,603 triangles, five
  skins, and source `pause1`, `walk`, and `run` clips.

The importer resolves Trask from the area-local `end_trask.utc`, then applies
the installed rules tables to assemble:

- body model `N_RepSold`;
- body texture `N_RepSold01`;
- unique head `N_traskH`;
- right-hand model `w_BlstrPstl_001`;
- five source-derived glTF skins across 13 rendered meshes;
- inherited `pause1`, `tlknorm`, and `walk` clips from `S_Male02`;
- the 16-shape `talk` facial clip from `S_Male02`;
- dialogue graph `end_trask01`.

`end_trask01` contains zero explicit DLG gesture records, so the authored
opening performance is the default `tlknorm` body loop with voice-bound `talk`
facial overlay. No additional gesture is fabricated.

The Godot dialogue view starts at the installed opening branch and displays the
locally resolved TLK line and player responses. A deterministic test selection
of the first response advances to Trask's next authored entry.

## Run

```powershell
./scripts/Import-KotorModule.ps1 -GameRoot '<owned KOTOR install>'
./scripts/Start-KotorGodot.ps1
```

Request the OpenXR presentation path (with automatic desktop fallback when no
HMD is available):

```powershell
./scripts/Start-KotorGodot.ps1 -OpenXR
```

Generate an ignored local proof capture and automatically choose the first
response:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -CapturePath 'artifacts/kotor-opening.png' `
  -DialogueChoice 0 `
  -OpenFirstDoor `
  -CaptureAndExit
```

For a local visual-QA frame without the proof overlay:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -CapturePath 'artifacts/kotor-trask-qa.png' `
  -DialogueChoice 0 `
  -OpenFirstDoor `
  -CleanCapture `
  -CaptureAndExit
```

## Honest boundary

Confirmed in the new runtime:

- source-bound module import;
- real room geometry and diffuse textures;
- authored UV2 lightmaps through the Odyssey room shader;
- source area ambient color and room-model point-light records;
- exact authored room and object placement data;
- assembled, skinned Trask model with separate hook-bound head skin;
- inherited idle, talk, and walk animation tracks;
- weapon attachment following the animated right hand;
- GFF WXYZ camera orientation, height, pitch, and FOV import;
- per-node DLG camera directives and source-style dialogue framing;
- 50 installed dialogue voice/LIP pairs resolved into the ignored bundle;
- MP3 voice playback, 16-shape LIP interpolation on seven facial bones, and
  return to `pause1` when a line ends;
- bytecode-validated contracts for `k_pend_traskdl40`, `k_pend_door1xp`, and
  the opening locker XP slice in `k_pend_chest02`;
- automatic `end_door01` opening from the DLG control script;
- exact plot-XP gating: locker `0→50`, then door `50→150` using the
  `end_tutorial` row from `plot.2da`;
- exact `end_locker01` UTP placement and `PLC_FootLker` model with bounded `E`
  interaction and `OnInventory=k_pend_chest02` execution;
- textured `P_MAL_A_MED_01` player assembled from `PMBAM`, `PMBAMA01`, and
  `PMHA01` without a private save;
- third-person source-style camera and source-distance-derived player movement
  animation speeds;
- opt-in OpenXR origin/camera path with desktop fallback and HMD-relative
  cinematic camera authority;
- profile-owned native-coordinate movement simulation shared by desktop and XR
  intent, with synthetic speed/facing/dead-zone/door-blocker acceptance;
- OpenXR action map and tracked grip nodes for movement, sprint, interaction,
  recenter, and haptic output;
- installed dialogue graph, local TLK text, and selectable replies;
- deterministic dialogue advancement.
- walkmesh-constrained player movement;
- the exact `end_door01` placement and `DOR_LHR01` model with bounded `E`
  interaction.

Not yet implemented:

- final renderer transfer-function and light-attenuation parity;
- dialogue-camera obstruction correction and nondeterministic shot variants;
- general DLG gesture-ID execution outside this zero-gesture conversation;
- area, effects, and music playback;
- full character-creation UI and save-selected appearance/equipment;
- OpenXR controller render models, gameplay haptic events, and physical-headset
  stereo/input acceptance;
- general NCS VM execution and complete plot/party state;
- retail door animation and the installed `k_pend_door1xp` NCS behavior;
- combat, inventory, saves, and area transitions.

The current door-opening presentation remains a temporary Godot tween. Script
targeting and XP effects are source-backed; the door animation and the script's
move/pause/resume scheduling are not yet claimed as retail parity.

Generated GLBs, localized dialogue text, screenshots, and all other game data
remain under ignored local directories and are never published.
