# Dual-profile desktop Hello World contract

Status: **canonical delivery contract**
Adopted: 2026-08-26

This document is the stable scope for Nikami Aurora's first public proof. If a
roadmap, issue, pull request, or later conversation conflicts with it, this
document controls until it is changed in a reviewed commit. A scope change must
update this file and explain which acceptance gate changed and why.

The active checkpoint status and repair order are maintained in
[`DUAL-PROFILE-PARITY-CLOSURE.md`](DUAL-PROFILE-PARITY-CLOSURE.md). A proof
capture cannot override a red row in that ledger.

## Product decision

Nikami Aurora will first prove one complete, retail-matched desktop opening
route for each of these legally installed games:

1. *Star Wars: Knights of the Old Republic* through the first real Endar Spire
   action at level 1; and
2. *Dragon Age: Origins* through one selected origin's first real action at
   level 1.

Both routes must run through one public Nikami Aurora executable, launcher,
profile dispatcher, cache manager, and release package. KOTOR and DAO retain
separate profile-owned formats, precedence, coordinates, rules, scripts, and
presentation adapters. One game must never be forced through the other game's
loader.

Desktop is the product gate. Existing OpenXR work remains preserved but parked;
no additional VR feature work may displace either desktop route before this
contract passes.

## Honest public claim

The accepted Hello World claim will be:

> Nikami Aurora imports legally owned KOTOR and Dragon Age: Origins
> installations and plays a validated, retail-matched opening desktop slice of
> each game from startup through controllable level-1 action.

This proves the dual-profile architecture and the systems exercised by the two
routes. It does not claim that either full campaign, every origin, every class,
every item, or every script is already supported. "Perfect slice" means that
the selected route has no known unaccepted functional or presentation delta at
its declared checkpoints; it does not turn slice coverage into a whole-game
claim.

## End-user flow

```text
download Nikami Aurora
        |
        v
choose KOTOR or Dragon Age: Origins
        |
        v
select a legally owned installation folder
        |
        v
validate supported build -> import to a private local cache -> Play
        |
        v
game-native startup -> menus -> New Game -> opening -> level-1 action
```

The release must not require a repository checkout, shell, Python installation,
editor, private importer, Haven Tools, PyKotor, MDLOps, or any separately
downloaded game-specific reverse-engineering or conversion utility.

## Common route requirements

Each accepted route begins when the Aurora process opens and ends only after
the player has completed the first real action and regained normal control.
Each route must prove all applicable stages:

1. profile selection, installation validation, and fresh local import;
2. source startup logos, movies, legal screens, and music in authored order;
3. game-native main menu and New Game navigation;
4. the selected character or character-creation path;
5. opening movie, crawl, cinematic, loading screen, or equivalent transition;
6. source-authored area, actors, equipment, cameras, lighting, materials,
   animation, dialogue, voices, music, sound effects, and visual effects;
7. player-controlled dialogue and interaction without showcase automation;
8. native HUD plus the inventory/equipment screens exercised by the route;
9. real gameplay state, combat or equivalent action, level-1 state, and return
   to unrestricted control;
10. save, exit, Continue/load, and restoration of the accepted checkpoint; and
11. deterministic telemetry and matched retail evidence for every checkpoint.

Unsupported behavior must fail closed with an actionable diagnostic. A test
route may drive profile intents directly, but a production acceptance run must
not depend on injected input, focus changes, UI automation, test environment
switches, scripted movement, or automatically selected dialogue.

## KOTOR route: `kotor-endar-level1-v1`

### Identity

- Profile ID: `kotor`
- Engine family: Odyssey
- Current verified executable SHA-256:
  `34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`
- Opening module: `end_m01aa`

The accepted character preset, menu choices, and route state must be recorded
in a versioned route manifest before parity capture. A later character change
creates a new evidence row; it must not silently replace the baseline.

### Route

1. Aurora selects and validates the KOTOR installation.
2. The first-party KOTOR importer builds a hash-bound local cache.
3. The authored startup presentation and KOTOR main menu appear.
4. The user follows the accepted New Game/character path.
5. The authored opening crawl/movie and Endar Spire loading presentation play.
6. The opening cabin sequence and Trask dialogue run with user-selected replies.
7. The player regains control, moves on source navigation, uses the footlocker,
   and verifies inventory/equipment through the native UI.
8. The player crosses the real corridor trigger and completes the Carth/Trask
   transmission.
9. Normal area progression—not `ShowcaseRoute` or a test flag—starts the first
   Sith encounter.
10. The player completes a real combat loop with targeting, actions, vitality,
    damage, death, audio/effects, Trask participation, and combat HUD feedback.
11. The level-1 state is visible, control is restored, and save/Continue
    restores the accepted result.

The current KOTOR opening proof is source-bound and functional through a staged
first encounter, but retail visual parity and this complete production route
are not yet proven.

## DAO route: `dao-city-elf-level1-v1`

### Identity

- Profile ID: `dragon-age-origins`
- Engine family: Eclipse
- Current verified executable SHA-256:
  `400C2C9E97BB1A534121553BE66F202429EF6F7320C93C86F9B7A491864647BD`
- Locked development-proof identity: female City Elf rogue `Kallian`, appearance
  `preset-4`.
- Opening area/arrival: `bec110ar_players_house` / `bec110wp_start`.
- Opening cinematic/dialogue: `start_wake` / `bec110cr_shianni`.

Before implementation acceptance begins, the supported DAO edition/executable
hash and exact sex, class, appearance preset, difficulty, and dialogue route
must be locked in the versioned route manifest. Once locked, changing one of
those fields creates a new evidence row.

### Route

1. Aurora selects and validates the DAO installation.
2. The first-party DAO importer builds a hash-bound local cache without Haven
   Tools or the prior private importer.
3. The authored startup presentation and DAO main menu appear.
4. New Game and the accepted character-creation path work through the native
   source-driven interface.
5. The City Elf origin starts in the player's house at its real beginning,
   including the owned Alienage loading presentation and wake-up sequence; the
   old shortcut directly into Redcliffe is not an accepted substitute.
6. The opening cinematic/dialogue uses source actors, equipment, animation,
   FaceFX, cameras, lighting, voices, music, sound effects, and timing.
7. The player regains control with the native HUD, movement, interaction,
   inventory, abilities, party state, and journal state needed by the route.
8. The player completes the first real level-1 action/combat and returns to
   unrestricted control.
9. Save, exit, and Continue restore the accepted state.

Aurora now boots an automated development version of this exact route through
the shared dispatcher. Public OpenDAO code supplies the DAO menus, GFx reader,
world presentation, actors, cinematic/dialogue, FaceFX, audio, HUD, and player
adapter. The gate proves complete preset-4 bodies, the area-specific retail
loading artwork, source cameras/voices, dialogue completion, HUD, and
locomotion. It does **not** yet satisfy this production contract: its imported
world/generated presentation comes from external ignored OpenDAO cache roots,
the route is automation-driven, the first combat/save/Continue stages are
absent, and matched retail parity is not accepted. The controlled migration and
remaining importer boundary are specified in
[`OPENDAO-HARVEST.md`](OPENDAO-HARVEST.md).

## Architecture boundary

| Layer | Owns | Must not own |
| --- | --- | --- |
| `Nikami.Aurora.Core` | Engine-neutral profile discovery, import lifecycle, typed requests/events, cache and launch contracts | KOTOR/DAO resource IDs, rules, coordinates, scripts, UI, or presentation defaults |
| `Profiles.Kotor` | Odyssey formats, precedence, native state, rules, script behavior, route declarations | DAO assumptions or Godot nodes |
| `Profiles.DragonAgeOrigins` | Eclipse formats, precedence, native state, rules, script behavior, route declarations | KOTOR assumptions or Godot nodes |
| First-party import libraries | Read owned data, validate source identity, create deterministic manifests and converted payloads | Runtime UI, proprietary payloads in Git, external game-specific tools |
| Godot runtime | Profile dispatch, rendering, input, audio, windowing, UI surfaces, capture adapters | Game rules or content-specific route orchestration in the generic entrypoint |
| Public configuration | User/profile policy, supported-build declarations, presentation tuning, engineering budgets | Extracted content, protocol definitions, or a second programming language hidden in JSON |
| Ignored local cache | Imported assets, localized text, audio, models, manifests, and source/payload hashes | Anything committed or distributed as game content |

The current KOTOR-specific Godot boot class is a vertical-slice adapter, not the
permanent multi-game entrypoint. New work must move toward a neutral runtime
dispatcher and small profile-owned services rather than expanding a monolithic
game-specific coordinator.

## First-party tooling and dependency policy

### Default dependency envelope

The supported product path may depend on:

- Godot Engine and its normal, audited rendering/asset plugin ecosystem;
- the .NET runtime, SDK, and base class libraries used by Godot C#;
- operating-system and graphics APIs; and
- Nikami-authored source that is buildable from the public repository.

An external library exception is allowed only when it is broadly established,
open source, license-compatible, source-pinned, narrowly wrapped, independently
replaceable, and materially safer or more correct than a first-party
implementation. The pull request must document why the exception is necessary.
An opaque binary or game-specific RE/conversion executable can never receive
this exception.

### Reverse-engineering boundary

Ghidrust and other Nikami-owned tools may collect private static or runtime
evidence. Their output crosses into the public repository only as target hashes,
neutral observations, behavior contracts, and synthetic tests. Decompiler
pseudocode, raw dumps, retail binaries, extracted content, and private captures
remain outside the repository.

The production importer and runtime must not clone, install, link, load, or
execute Haven Tools, a private importer, PyKotor, MDLOps, xoreos, or another
party's game-specific reverse-engineering/conversion tool.

### Current replacement ledger

| Current proof dependency | Current use | Required first-party replacement before release |
| --- | --- | --- |
| PyKotor | KOTOR archives, structured resources, tables, textures, dialogue, scripts | Aurora-owned Odyssey KEY/BIF, RIM/ERF, GFF, LYT, TLK, 2DA, TPC/TXI, LIP, DLG, and NCS readers |
| MDLOps | KOTOR MDL/MDX conversion | Aurora-owned MDL/MDX hierarchy, mesh, skin, controller, supermodel, and animation reader plus deterministic Godot/glTF output |
| NumPy, Pillow, trimesh | KOTOR proof conversion/math/image helpers | `System.Numerics`, Godot/BCL image facilities, and focused Nikami-authored conversion code |
| OpenDAO private importer/Haven boundary | Creation of the old DAO compatibility cache | Aurora-owned ERF/RIM, GFF/GDA, talktable, MMH/MSH/MAO/MAT, ANI, terrain/area, FaceFX, audio, movie, and UI import pipeline |
| Microsoft DI in OpenDAO | Old runtime composition | Do not migrate by default; use Aurora's explicit composition unless a reviewed common-library exception is justified |
| FFmpeg proof scripts | Optional development movie conversion/inspection | Never an end-user import/runtime dependency; final capture tooling is outside the gameplay acceptance path |

Existing ignored caches may remain local evidence while replacements are built.
No route can close its fresh-import or release gate until the accepted cache was
created entirely by the public first-party pipeline.

## Design and complexity rules

- **SOLID:** profile rules, import formats, state, and presentation have explicit
  ownership and narrow interfaces.
- **DRY:** one semantic operation has one implementation and one testable owner;
  similar-looking KOTOR and DAO behavior is not deduplicated until both profiles
  prove the abstraction.
- **YAGNI:** build only what the two accepted routes or their release pipeline
  require. Future campaigns and games do not justify speculative frameworks.
- **No magic values:** content IDs, UI geometry, cameras, timing, transforms,
  and tuning come from source-bound manifests or public configuration. Format
  tags, schema IDs, opcodes, masks, and mathematical identities remain named,
  evidence-backed, tested invariants in profile code.
- **Fail closed:** unknown formats, ambiguous precedence, source drift,
  unsupported scripts, and incomplete assets produce explicit failures rather
  than guessed behavior or placeholders.
- **Bounded growth:** large coordinator classes must be reduced at demonstrated
  subsystem seams while implementing the route; no speculative rewrite is
  authorized.

Complexity is gated per operation, not by claiming that every algorithm is
linear. Catalog and manifest scans, resource indexing, entity lookup setup,
inventory projection, and event dispatch should be O(N) where their problem
permits it. Deterministic sorting may be O(N log N); navigation and graph work
must declare the appropriate `V`/`E` bound. Public acceptance uses operation
counters over increasing synthetic sample sizes and rejects unexplained
superlinear growth or repeated full-corpus scans in frame-time paths.

## Parity and evidence gate

Every accepted checkpoint has a matched comparison row containing:

- executable build/hash and source-container hashes;
- area/module and exact profile snapshot;
- character identity, appearance, equipment, dialogue node, and script state;
- camera ID/mode/FOV and animation/effect-relative timestamp;
- renderer settings, resolution, and capture source; and
- measured deltas, owner, confidence, severity, and closing assertion.

Review covers scene completeness, transforms, opaque/alpha/additive/depth
behavior, materials, lights, cameras, skeletons, faces, animation, effects,
voices, music, timing, UI, controller/keyboard interaction, and gameplay
release. One favorable frame, a source-bound layout, or a successful simulator
run is not visual parity.

Private retail evidence remains private. The public tree receives only hashes,
neutral contracts, synthetic fixtures, and acceptance summaries.

## Delivery order

1. Lock this contract, route manifests, supported target builds, and public
   dependency rules.
2. Add the game-neutral launcher/profile dispatcher and atomic cache lifecycle.
3. Replace KOTOR's external proof import stack with first-party public readers
   while keeping the existing Endar proof as an oracle.
4. Complete and retail-match the human-driven KOTOR route through real combat,
   level-1 state, and save/Continue.
5. Harvest provenance-clean OpenDAO components into the DAO profile, build the
   first-party DAO importer, and complete the selected origin route.
6. Run clean-machine, fresh-import, package-content, performance, and matched
   retail acceptance for both profiles.
7. Publish one asset-free dual-profile desktop Hello World release.
8. Resume VR only after both flat routes pass.

## Gate state vocabulary

Use only these states when reporting progress:

- `detected` — install markers and build identity are validated;
- `inventoried` — required owned resources and precedence are cataloged;
- `importable` — the first-party public importer creates a valid local bundle;
- `renderable` — the bundle creates the claimed presentation without errors;
- `interactive` — a human can complete the declared route normally;
- `parity-accepted` — every matched row and temporal gate passes; and
- `release-ready` — a clean user can complete the supported flow from the
  packaged artifact without development tools.

Do not collapse those states into a generic claim that a profile "works."
