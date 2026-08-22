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
- Navigation: 540 walkable source triangles with an accepted grounded movement
  proof from the authored entry point.

The importer resolves Trask from the area-local `end_trask.utc`, then applies
the installed rules tables to assemble:

- body model `N_RepSold`;
- body texture `N_RepSold01`;
- unique head `N_traskH`;
- right-hand model `w_BlstrPstl_001`;
- dialogue graph `end_trask01`.

The Godot dialogue view starts at the installed opening branch and displays the
locally resolved TLK line and player responses. A deterministic test selection
of the first response advances to Trask's next authored entry.

## Run

```powershell
./scripts/Import-KotorModule.ps1 -GameRoot '<owned KOTOR install>'
./scripts/Start-KotorGodot.ps1
```

Generate an ignored local proof capture and automatically choose the first
response:

```powershell
./scripts/Start-KotorGodot.ps1 `
  -CapturePath 'artifacts/kotor-opening.png' `
  -DialogueChoice 0 `
  -CaptureAndExit
```

## Honest boundary

Confirmed in the new runtime:

- source-bound module import;
- real room geometry and diffuse textures;
- authored UV2 lightmaps through the Odyssey room shader;
- exact authored room and object placement data;
- assembled static Trask model;
- installed dialogue graph, local TLK text, and selectable replies;
- deterministic dialogue advancement.
- walkmesh-constrained player movement;
- the exact `end_door01` placement and `DOR_LHR01` model with bounded `E`
  interaction.

Not yet implemented:

- final retail lighting gamma/gain parity;
- skeletal animation and lipsync;
- voice and area audio playback;
- player character creation/model assembly;
- NCS condition/action execution and plot state;
- retail door animation and the installed `k_pend_door1xp` NCS behavior;
- combat, inventory, saves, and area transitions.

The current door-opening presentation is a temporary Godot tween. It is not
claimed as retail animation or native script parity.

Generated GLBs, localized dialogue text, screenshots, and all other game data
remain under ignored local directories and are never published.
