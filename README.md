# Nikami Aurora

Nikami Aurora is an experimental, open compatibility runtime for legally
installed Aurora-family games and their Odyssey and Eclipse descendants.
The first two profiles are *Star Wars: Knights of the Old Republic* and
*Dragon Age: Origins*.

The canonical first-release target is the
[dual-profile desktop Hello World](docs/DUAL-PROFILE-HELLO-WORLD.md): one
retail-matched opening route for KOTOR and one for Dragon Age: Origins, both
running through the same asset-free launcher and public first-party import
pipeline. Flat desktop completion is the product gate; additional VR work is
parked until both routes pass.

The project is at early vertical-slice stage. It can identify a target
installation, validate profile-specific source markers, produce a SHA-256-bound
target manifest, import the real KOTOR Endar Spire room geometry and textures,
load that owned-data bundle into Godot, assemble Trask from his installed body,
head, texture, weapon, skin weights, and inherited animations, and advance
through the first authored dialogue choices. Authored lightmaps, area ambient
color, room light nodes, static cameras, and dialogue framing drive the opening
presentation. Installed dialogue voice and LIP tracks drive source-timed body
talk and facial animation. Movement is constrained to the installed room
walkmeshes and the first authored lockdown door is materialized and interactive.
Validated NCS contracts now execute the opening dialogue-door and plot-XP
branches. Full combat and general script execution remain active milestones.

The same Godot project and neutral boot dispatcher now also run a bounded DAO
development slice: female City Elf rogue Kallian, preset 4, from the native DAO
main menu and character-creation screens through the owned Alienage loading
art, `start_wake`, the Shianni dialogue, the native HUD, and controllable room
movement. That flow uses public code harvested from OpenDAO commit `404bbaa`
and reads all retail presentation from the selected installation or an ignored
machine-local cache. It is a renderable automated integration proof, not yet a
release path: Aurora still needs its own fresh DAO importer, human-driven route,
combat/save completion, and matched-retail parity acceptance.

The current player slice materializes a deterministic chargen player from the
installed appearance/portrait tables, drives source idle/walk/run clips, and
shares one player/cinematic camera authority between desktop SpringArm and
OpenXR origin presentations. Active first-person XR gameplay masks only the
local player's eight head meshes, preventing inside-face artifacts while the
source torso, arms, hand rig, and equipped weapon remain attached and visible.
Dialogue/cinematic cameras and every desktop path restore the complete head.

Desktop keys and OpenXR controller axes now map into one immutable movement
intent. Native-coordinate speed, facing, walkmesh, and closed-door outcomes are
owned and synthetically tested by `Profiles.Kotor`, not duplicated in Godot.

Opening gameplay state is now profile-owned as well. Locker use, per-placement
door state, validated script outcomes, and XP transitions produce deterministic
before/after snapshots and typed events consumed by either desktop or OpenXR
presentation. Duplicate Odyssey object tags remain independent through stable
placement IDs.

The opening footlocker now resolves its installed UTP/UTI contents and transfers
the exact two Medpacs, Clothing, and Short Sword into that profile snapshot once.
A compact world-space loot readout is shared by desktop and OpenXR; an active XR
controller receives the same event through the versioned haptic action.

The opening Clothing and Short Sword can now be equipped through desktop `Q` or
OpenXR `B/Y`. The profile owns slot legality and inventory removal; Godot swaps
to a source-derived `PMBBM01` skinned player with the sword attached beneath the
animated right-hand hook while preserving idle/walk/run state.

The flat path now imports the source loading screen, native 800x600 HUD, and
native-centered 640x480 Inventory and Equipment menus. The opening player can
navigate between Inventory and Equipment, select all nine authored paper-doll
slots, equip or remove Clothing and the Short Sword through profile state, and
render the base, single-item, or combined source-derived player variant. Owned
GUI, font, icon, portrait, audio, model, and capture payloads stay local.
The inventory's source Quest Items/All Items toggle now reads each installed
UTI `Plot` bit, and its native arrow/thumb scrollbar clips and navigates lists
that exceed the authored viewport.
The opening Inventory party controls now select the player or Trask through
profile-owned state, refresh the source portrait and imported vitality/Defense,
and target Medpac use at the selected member.

Opening-room furniture now distinguishes baked room meshes from GIT placeables.
The installed `PLC_Chair2` model is instanced at all three authored placements;
because the UTP is not usable, those chairs never steal the footlocker prompt.

Source-opaque room furniture now stays in Godot's opaque depth-writing path.
The lightmap shader no longer turns generic diffuse alpha into transparency, so
the opening sofas and bunks correctly occlude the floor behind them.

Explicit source TXI additive overlays now use a separate non-depth-writing
material path. This makes black `LHR_dust01` window texels non-occluding and
restores the owned `LSP_stars02` exterior sphere without weakening furniture
opacity or treating arbitrary texture alpha as transparency.

Crossing the authored first-corridor trigger now sets profile global
`END_TRASK_DLG=10`, signals Trask event 50, and starts the installed Carth radio
transmission at DLG starter 8. Carth is source-assembled and standing under the
retail-matched static camera; desktop cinematics use an independent camera that
the gameplay SpringArm cannot overwrite.

That transmission now continues without fake prompts through the three empty
DLG reply records, plays both Trask responses and the journal line, advances the
two authored globals, reveals the map in profile state, and stops at the two
real player responses. The tight Trask shot is calibrated against the same
recorded retail sequence, and facial LIP deltas preserve each unique head's
authored mouth rest instead of replacing it with the generic supermodel pose.

The next room-3 slice now opens the second lockdown door and stages the first
Sith firefight through cameras 26, 19, and 20. It materializes the two Sith,
the Republic target, existing corpse/debris/power-conductor props, source
cutscene attack/death clips, both Trask voice/LIP lines, blaster shot/impact
audio, battle music, and source-sized additive projectile/muzzle textures. The
runtime now also transfers all 12 installed room emitters: nine `fx_Smoke`
systems and three `fx_Spark` systems, including the dense authored smoke at the
damaged room-3 corridor end. The deterministic combat-ready gate verifies every
launch, impact, room effect, voice, script global, environment placement, and
music transition; general combat AI remains a later milestone.

Those slices now run as one deterministic startup-to-action route. It begins at
the installed opening dialogue, waits through every selected voice, takes the
shortest validated authored branch, opens the locker and equips its Clothing
and Short Sword, traverses the real corridor trigger, completes the Carth/Trask
transmission, and reaches then releases the first Sith fight. Completion asserts
15 voices, seven choices, XP/equipment/map/global state, both doors, music, and
all first-encounter effects before exposing a capture/movie stage key.

The final development recording path is equally fail-closed: one wrapper runs
Godot Movie Maker against the active OpenXR spectator, validates route telemetry
and 1280×720 audio/video streams, converts a checked temporary OGV intermediate to
H.264/AAC MP4, deletes the temporary directory, and refuses to overwrite an
existing output. Generated movies remain ignored local artifacts.

XR controller presentation follows a three-level provider chain: portable
OpenXR runtime models, dynamically available Meta FB models, then tracked
procedural fallbacks. The same primary-stick and face-button actions feed
gameplay and eventual live model animation. Runtime-model managers are created
only after OpenXR initializes, so normal desktop and no-HMD fallback boots do
not depend on an XR extension lifecycle. Procedural controller meshes also stay
hidden until XR is active, so they cannot leak into desktop character shots.

For deterministic VR-path QA, the Meta XR Simulator can now be selected only
for the launched process. A Vulkan XR subviewport renders the real tracked
session while a shared-world spectator camera mirrors the HMD to the normal
root viewport for non-black still and eventual Movie Maker capture. The harness
does not change the system OpenXR registry, and it fails if XR silently falls
back to desktop.

## Why a new runtime

The existing [OpenKOTOR](https://github.com/nikamigaming-create/OpenKOTOR)
retail bridge and [OpenDAO](https://github.com/nikamigaming-create/OpenDAO)
compatibility proof contain valuable research, automation, and runtime work.
Nikami Aurora gives that work a clean multi-game architecture:

- `Core` owns engine-independent contracts and deterministic state.
- `Profiles` own each game's formats, resource precedence, script ABI, rules,
  world assembly, and presentation adapters.
- The Godot runtime owns rendering, input, audio, UI, and future OpenXR paths.
- Retail hooks remain evidence oracles; implementation crosses the boundary
  only through documented behavioral contracts and synthetic tests.

No proprietary game assets, extracted resources, or executables belong in
this repository.

## Release experience

The end-user contract is deliberately small:

1. Download one Nikami Aurora release.
2. Choose a supported game and point Aurora at its legally installed folder.
3. Aurora validates that installation and creates a machine-local cache.
4. Press Play; later launches reuse or incrementally refresh that cache.

Python, PyKotor, MDLOps, NumPy, Pillow, trimesh, the Godot editor, repository
checkout, and command-line import steps are transitional proof dependencies.
They must be replaced by Aurora-owned public import code before the dual-profile
release and must never become end-user setup or downloaded tool requirements.
Game assets are never included in Nikami Aurora releases or uploaded from the
user's machine. See
[`docs/RELEASE-UX.md`](docs/RELEASE-UX.md) for the packaging gate.

## Quick start

Build and run the source-only acceptance suite:

```powershell
dotnet build Nikami.Aurora.sln --configuration Release
dotnet run --project tests/Nikami.Aurora.Acceptance --configuration Release
```

List available profiles:

```powershell
dotnet run --project src/Nikami.Aurora.Cli -- list-profiles
```

Probe a legally installed Steam KOTOR copy:

```powershell
dotnet run --project src/Nikami.Aurora.Cli -- probe `
  --profile kotor `
  --root 'D:\SteamLibrary\steamapps\common\swkotor'
```

The command exits successfully only when every required profile marker exists.
Its JSON output includes the executable SHA-256 and never copies game content.
`isValid` means that the required source layout is present; it does not claim
that optional overrides, proxy DLLs, or the complete installation are stock.

The first verified development target is the Steam KOTOR 1.0.3.0 executable:
`34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`.

## Import and run the Endar Spire Godot proof

The following is the current development-proof path, not the accepted release
dependency envelope. The replacement work is tracked in the
[dual-profile contract](docs/DUAL-PROFILE-HELLO-WORLD.md).

Install the pinned importer dependencies into Python 3.12, then generate the
ignored local bundle from a legally installed KOTOR copy:

```powershell
py -3.12 -m pip install -r requirements-import.txt
./scripts/Bootstrap-MDLOps.ps1
./scripts/Import-KotorModule.ps1 `
  -GameRoot 'D:\SteamLibrary\steamapps\common\swkotor'
```

Profile policy is exposed in `config/kotor-runtime.json`. The importer validates
and hash-binds that configuration into the ignored local module manifest; pass
`-RuntimeConfig` to import with another public configuration. See
`docs/KOTOR-RUNTIME-CONFIGURATION.md` for the configuration/evidence boundary
and deterministic O(N) inventory guard.

Launch the new Godot runtime:

```powershell
./scripts/Start-KotorGodot.ps1
```

The importer writes only to `local/kotor/end_m01aa`, which is excluded from
Git. The runtime loads 15 authored Endar Spire room records, materializes and
animates Trask, Carth, the deterministic player, and the first Sith encounter,
and keeps remaining creature debug markers opt-in while broader creature model
assembly is implemented.

## Repository map

- `src/Nikami.Aurora.Core` — profile and installation contracts.
- `src/Nikami.Aurora.Profiles.Kotor` — Odyssey/KOTOR profile.
- `src/Nikami.Aurora.Profiles.DragonAgeOrigins` — Eclipse/DAO profile.
- `src/Nikami.Aurora.Cli` — deterministic profile and target tooling.
- `godot` — shared Godot runtime and active KOTOR/DAO vertical slices.
- `godot/config/dao` — public DAO cinematic and loading-presentation policy;
  no retail payloads.
- `config/kotor-runtime.json` — public KOTOR profile policy and guardrails.
- `scripts` — owned-install import and launch commands.
- `scripts/Run-DaoOriginAcceptance.ps1` — shared six-origin DAO runtime launch,
  capture, and video path backed by a legally owned local installation
  integration/video gate using external ignored OpenDAO cache roots.
- `tests/Nikami.Aurora.Acceptance` — dependency-free synthetic acceptance.
- `docs/ARCHITECTURE.md` — dependency and profile boundaries.
- `docs/CLEAN_ROOM.md` — evidence and implementation separation.
- `docs/ROADMAP.md` — dual-profile desktop delivery and expansion gates.
- `docs/DUAL-PROFILE-HELLO-WORLD.md` — canonical two-game desktop product,
  architecture, dependency, parity, and release contract.
- `docs/OPENDAO-HARVEST.md` — controlled migration ledger for the prior OpenDAO
  implementation and evidence.
- `docs/KOTOR-GODOT-PROOF.md` — exact Endar Spire proof and limitations.
- `docs/KOTOR-RUNTIME-CONFIGURATION.md` — policy, source, evidence, and O(N)
  monitoring boundaries.

## Legal

Nikami Aurora is independent and unaffiliated. Users must provide legally
obtained game installations. See [NOTICE.md](NOTICE.md),
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and [LICENSE](LICENSE).
