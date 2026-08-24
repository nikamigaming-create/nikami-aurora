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
  skins, and source `pause1`, `walk`, `run`, and facial `talk` clips.

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

Run the active Meta XR Simulator through an isolated Vulkan spectator path
without changing the system OpenXR runtime:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -OpenXRSimulator `
  -TestFirstEncounter `
  -CapturePath 'artifacts/kotor-first-encounter-openxr-spectator.png' `
  -CaptureDialogueNode 'encounter:combat-ready' `
  -CaptureFrame 1 `
  -CleanCapture `
  -CaptureAndExit
```

Capture the local-avatar first-person look-down gate after equipping the owned
opening Clothing and Short Sword:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -OpenXRSimulator `
  -SkipOpeningDialogue `
  -OpenFirstLocker `
  -EquipOpeningGear `
  -XrBodyLookDown `
  -CapturePath 'artifacts/kotor-xr-local-body-hands.png' `
  -CaptureDialogueNode 'xr:body-lookdown' `
  -CaptureFrame 1 `
  -CleanCapture `
  -CaptureAndExit
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

Run the source-bound first Sith encounter and stop on its asserted
combat-ready frame:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -TestFirstEncounter `
  -CapturePath 'artifacts/kotor-first-encounter-combat-ready.png' `
  -CaptureDialogueNode 'encounter:combat-ready' `
  -CaptureFrame 1 `
  -CleanCapture `
  -CaptureAndExit
```

Run the complete startup-to-action route and capture only its final asserted
gameplay frame:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -ShowcaseRoute `
  -CapturePath 'artifacts/kotor-showcase-desktop-complete.png' `
  -CaptureDialogueNode 'showcase:complete' `
  -CaptureFrame 1 `
  -CleanCapture `
  -CaptureAndExit
```

After every final gate passes, create the one validated local VR-path MP4:

```powershell
./scripts/Export-KotorShowcaseVideo.ps1 `
  -OutputPath 'artifacts/nikami-aurora-kotor-showcase.mp4'
```

The wrapper refuses to overwrite a movie, records through Godot Movie Maker in
a checked temporary directory, validates active XR plus audio/video streams,
then removes the intermediate and leaves only the requested MP4.

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
- per-process Meta XR Simulator selection with a Vulkan XR subviewport and
  non-black HMD-following spectator capture, without registry or UI mutation;
- tracked-head-relative cinematic recentering with asserted zero positional
  and forward error, plus calibrated gameplay eye height without double-counting;
- first-person XR masking of exactly the eight local `PMHA01` head meshes while
  retaining the three Clothing body meshes, both hand bones, and the attached
  Short Sword; desktop and cinematic cameras retain the complete head;
- profile-owned native-coordinate movement simulation shared by desktop and XR
  intent, with synthetic speed/facing/dead-zone/door-blocker acceptance;
- profile-owned transactional experience, door, placeable, and validated-script
  state shared by desktop and XR interaction, including duplicate-tag placement
  isolation and synthetic `0→50→150` replay;
- source-bound `footlker001.utp` contents and UTI metadata, with exact one-time
  transfer of two Medpacs, Clothing, and a Short Sword into profile inventory;
- desktop/OpenXR-shared world-space loot presentation and an XR haptic request
  driven by the same item-transfer event;
- source-bound flat loading, native 800x600 HUD, and opening inventory layouts,
  including the Windows bitmap-font alias, loading music, owned minimap,
  snapshot-backed stats, and tested basic Medpac consumption;
- profile-owned atomic Armor/RightHand equipment and source-derived
  `PMBBM01`/`PMHA01`/Short Sword player presentation, including exact right-hand
  hierarchy attachment and preserved idle/walk/run clips;
- source-bound native 640x480 equipment menu centered at 800x600, including all
  nine human paper-doll slot icons, owned item/None icons, Equipment/Inventory
  navigation, atomic unequip, and base/Clothing/left-Sword/right-Sword variants;
- source-bound Quest Items/All Items inventory views driven by installed UTI
  `Plot` bits, plus clipped overflow rows and the owned arrow/thumb scrollbar;
- source-positioned opening party controls that select player or Trask through
  profile state, refresh the portrait/vitality/Defense fields, and direct
  Medpac use to the selected member;
- all three source `plc_chair2` opening-room placements materialized from one
  `PLC_Chair2` model while remaining excluded from interaction targeting;
- source-opaque lightmapped and base room materials audited into opaque
  depth-writing paths, eliminating floor bleed through opening-room furniture;
- source-low-alpha blast decals isolated in blended, non-depth-writing
  materials without weakening opaque floor/furniture coverage;
- explicit TXI additive window/nebula overlays transferred separately from
  alpha mixing, revealing the owned `LSP_stars02` exterior sphere;
- native polygon crossing for `end_trig02`, profile global
  `END_TRASK_DLG 0→10`, Trask event 50, and `end_trask01` starter 8 selection;
- source-assembled standing Carth with independent head skin, inherited
  `tlknorm`, installed voice/LIP, and retail-matched static camera 1 framing;
- desktop cinematic-camera ownership separated from the gameplay SpringArm;
- voice-finished automatic traversal of blank DLG reply controls from Carth
  through Trask's journal line, ending at the two authored player responses;
- module-scoped NCS effects for `END_CARTH_DLG=1`, `END_TRASK_DLG=11`, and
  full-map reveal, without borrowing the next module's similarly named script;
- retail-calibrated participant facing and tight `CameraAngle=1` Trask framing;
- unique-head-relative LIP translation/rotation deltas, preventing generic
  supermodel mouth collapse while preserving attached Trask, Carth, and player
  head/hand hierarchies;
- OpenXR action map and tracked grip nodes for movement, sprint, interaction,
  recenter, and haptic output;
- portable and Meta runtime-controller model providers with per-hand procedural
  fallbacks kept local to the tracked grip pose;
- desktop-hidden controller fallbacks and opt-in debug creature markers, keeping
  proof/release frames free of placeholder geometry;
- source-bound door 02, Sith pair, Republic combatant, three existing corpse
  props, broken-door assembly, and two power-conductor placements for the first
  room-3 fight;
- cameras 26→19→20, source cutscene attack/damage/death clips, both voiced Trask
  lines with LIP, `END_TRASK_DLG=1`, and the combat-ready transition;
- ammunition-table blaster shot/impact audio, ARE standard/battle music, and
  hash-validated decoding of IMA ADPCM and KOTOR-wrapped MP3 payloads;
- source-sized `w_laserfire_r` and `v_muzflash_01` projectile/muzzle textures,
  four launch/muzzle assertions, and at least three completed impact assertions;
- all 12 source room emitters from `M01aa_08b`, `M01aa_03a`, `M01aa_02a`, and
  `M01aa_05a`, including nine `fx_Smoke` systems and three additive
  motion-blurred `fx_Spark` systems; the room-3 damaged-end smoke replaces the
  incorrect clear-color void seen from encounter camera 20;
- one asserted boot→opening dialogue→gear→corridor transmission→first encounter
  →gameplay route with 15 completed voices and seven authored selections;
- separate proof and dialogue Canvas layers, allowing clean capture to retain
  subtitles/choices while hiding developer status and control text;
- installed dialogue graph, local TLK text, and selectable replies;
- deterministic dialogue advancement.
- walkmesh-constrained player movement;
- the exact `end_door01` placement and `DOR_LHR01` model with bounded `E`
  interaction.

Not yet implemented:

- final renderer transfer-function and light-attenuation parity;
- dialogue-camera obstruction correction and nondeterministic shot variants;
- general DLG gesture-ID execution outside this zero-gesture conversation;
- general area audio, ambient loops, and effects beyond the first encounter;
- full character-creation UI and saved arbitrary appearance/equipment;
- physical-headset runtime-model selection, haptic-delivery calibration, and
  stereo/input acceptance;
- tracked-controller-to-avatar hand/arm IK and physical-headset look-down comfort
  acceptance (the current source `pause1` pose keeps its authored hand placement);
- general NCS VM execution and complete plot/party state;
- retail door animation and complete `k_pend_door1xp` behavior beyond the
  validated XP branch;
- combat; the full campaign inventory and party corpus, party join/leave and
  controlled-character flows, arbitrary item effects, companion equipment,
  general equipment models/rules beyond the opening
  player items, derived equipment combat stats, saves, and area transitions.

The current door-opening presentation remains a temporary Godot tween. Script
targeting and XP effects are source-backed; the door animation and the script's
move/pause/resume scheduling are not yet claimed as retail parity.

Generated GLBs, localized dialogue text, screenshots, and all other game data
remain under ignored local directories and are never published.
