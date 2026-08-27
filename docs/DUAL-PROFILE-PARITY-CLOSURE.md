# Dual-profile opening parity closure

Status: **active acceptance ledger**

Scope owner: [`DUAL-PROFILE-HELLO-WORLD.md`](DUAL-PROFILE-HELLO-WORLD.md)

Adopted: 2026-08-27

This ledger turns the dual-profile Hello World contract into reviewable work.
It is deliberately stricter than the existing proof videos. A capture that
boots, moves, or contains source assets is useful evidence, but it is not a
parity pass.

## Current verdict

Neither opening route is parity-accepted.

- KOTOR is `renderable` through a scripted Endar Spire encounter. Its proof
  does not contain the same player-controlled combat state as the retail panel,
  and it does not prove the native HUD, inventory, equipment, damage loop, or
  save/Continue in one normal route.
- DAO is `renderable` through an automated City Elf house-to-Alienage proof.
  The proof leaves the Shianni conversation earlier than the matched retail
  sequence, returns to the HUD, and drives scripted locomotion into a wall.
  The later origin cells, inventory-family screens, real combat, and
  save/Continue are not implemented as one normal route.
- The 2026-08-27 side-by-side is classified
  `matched-moving-non-parity`. It must not be reused as a parity claim.

The next public video is blocked until both route ledgers below are green. We
will not hide missing gameplay with tighter edits, paused panels, automation,
or a semantically different retail scene.

## Meaning of “100%”

For this milestone, 100% means **no known unaccepted delta inside the two
declared opening routes**. It does not mean both complete campaigns.

Every source-owned cell reached by the accepted route, and every actor,
placeable, item, animation, effect, sound, light, camera, trigger, script, and
UI state exercised there, must be accounted for. Adjacent cells that the
accepted branch does not enter are not silently added to the claim. Expansion
continues by adding another route/cell ledger after these two pass.

## Comparison invariant

A comparison row is valid only when both sides bind the same:

- supported executable and source-container hashes;
- area/module, story state, dialogue node, equipment, party, and difficulty;
- semantic event and control state;
- camera mode/ID, field of view, and effect- or animation-relative time; and
- resolution, renderer policy, and capture provenance.

Both panels must be live. A current cutscene cannot be compared with retail
player-controlled combat. A retail conversation cannot be compared with an
Aurora locomotion smoke. Structural telemetry may explain a result, but it
cannot close a visual or interaction row by itself.

## Severity and repair order

| Severity | Meaning | Examples in the present proof |
| --- | --- | --- |
| P0 | Wrong state, missing owner, or unusable route | staged KOTOR fight used as gameplay; DAO conversation ends before retail; absent combat/save; actor or camera outside the intended scene |
| P1 | Required system or presentation channel missing | HUD/inventory not exercised; missing sky/atmosphere; absent projectile, sound, voice, or VFX event |
| P2 | Present but visibly or temporally wrong | light energy, alpha/depth, facial mouth motion, animation blend, camera framing, effect scale/timing |
| P3 | Final polish within an otherwise matched row | grading, tiny spacing, sub-frame phase, particle random seed |

Fix the earliest owner first: state/sequence, then completeness and transforms,
then material/lighting/camera/animation/effects/audio, then grading. Downstream
polish against the wrong state is discarded work.

## KOTOR route ledger — `kotor-endar-level1-v1`

Locked target: Steam KOTOR 1.0.3.0, executable SHA-256
`34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`,
module `end_m01aa`.

| Checkpoint | Current state | Highest blocker | Closure evidence |
| --- | --- | --- | --- |
| Installation and fresh import | `importable` only through the proof import stack | Replace PyKotor/MDLOps/Python conversion with public Aurora readers | clean packaged build imports a fresh owned install; source and payload hashes bind |
| Startup, legal, movies, music | `renderable` | normal human route and matched order/timing not accepted | uninterrupted startup capture and event timeline |
| Main menu and New Game | `renderable` | accepted character path not yet locked end-to-end | human keyboard/mouse and controller traversal; exact source UI |
| Opening crawl, movie, loading | `renderable` | full matched temporal row absent | matched live row with source audio and no blank interval |
| Cabin and Trask dialogue | `renderable` | current proof automation; no accepted human choice route | identical dialogue branch, camera sequence, voice/LIP/gesture timing, and control handoff |
| Cabin HUD and controls | `renderable` in isolated contracts | not accepted in the normal route | HUD visible at the same state; move, target, interact, pause |
| Footlocker | `renderable` in a synthetic/owned gate | production trigger/script transaction not accepted | open, transfer every route item once, close, persist |
| Inventory and equipment | `renderable` in isolated contracts | never proved in the matched gameplay route | source layout, party selection, scroll/filter, inspect/use/equip/unequip, resulting actor appearance and stats |
| Corridor progression and transmission | `renderable` in proof orchestration | showcase/test progression still owns control | source trigger and script graph drive the event under human control |
| Pre-combat room event | `renderable` | camera/placement/timing not retail-accepted; muzzle presentation overexposed | matched cameras 26/19/20, participants, room props, voices, projectiles, impacts, smoke/sparks, music, and handoff |
| First player combat | **absent** | staged encounter is not combat gameplay | target selection, queued action, attack roll, damage, vitality, death, XP, combat HUD, SFX/VFX, Trask participation, cancel/retarget, control return |
| Level-1 result | **absent** | no completed combat transaction | state and UI agree on level, vitality, inventory, party, XP, and script globals |
| Save, exit, Continue | **absent** | no source-equivalent checkpoint persistence | save from accepted state, process exit, Continue, deterministic restoration |

### KOTOR scene completeness gate

For every `end_m01aa` room visible or traversed before the route endpoint, the
importer emits a source census and the runtime emits a materialization census.
The join must close with zero unexplained differences for rooms, meshes,
walkmeshes, placeables, creatures, doors, triggers, waypoints, static cameras,
lights, ambient/audio emitters, model emitters, textures, TXI directives, and
scripts. The damaged corridor must include its actual source geometry and
emitters; neither a clear-color void nor a fabricated sky patch is acceptable.

## DAO route ledger — `dao-city-elf-level1-v1`

Locked development identity: female City Elf rogue `Kallian`, `preset-4`,
opening area `bec110ar_players_house`, waypoint `bec110wp_start`, cinematic
`start_wake`, dialogue `bec110cr_shianni`.

The accepted dialogue baseline follows the source graph through player nodes
`153`, `157`, and `159`; these are resource identities, not embedded localized
text. Human choice timing is unconstrained. Automation may exercise the branch
in development, but cannot appear in acceptance evidence.

| Checkpoint | Current state | Highest blocker | Closure evidence |
| --- | --- | --- | --- |
| Installation and fresh import | private compatibility cache only | first-party public Eclipse importer is incomplete | clean packaged build imports a fresh owned install with no Haven/private converter |
| Startup and main menu | `renderable` | automation and matched order/timing not accepted | uninterrupted human traversal with source layout, music, animation, and transitions |
| Character creation | `renderable` for the locked identity | normal interaction and full state commit not accepted | sex/race/origin/class/appearance/name choices persist into world, UI, body, voice, stats, inventory |
| Alienage loading presentation | `renderable` | matched timing/artwork animation row absent | correct owned artwork, labels, animation, progress, music, and transition |
| House wake cinematic | `renderable` | camera/actor/facial/lighting rows not accepted | every source camera switch frames its intended actor(s); exact voice, FaceFX, animation, bed/body/equipment, lights, and timing |
| Shianni dialogue | `renderable` | current proof exits earlier than matched retail and then shows gameplay walls/shadows | complete locked branch under human choice; retail and Aurora remain on the same node and camera event until the same handoff |
| House gameplay camera | **P0 repaired, pending evidence** | proof-era camera distance was selected from light count; new profile remains retail-uncalibrated | matched player/camera telemetry plus live screen framing; no wall-only or near-first-person handoff |
| HUD and interaction | `renderable` | only narrow proof coverage | source HUD, portraits, minimap, quickbar, selection/highlight, pause, and feedback under human control |
| Inventory, equipment, abilities, party, journal | **incomplete** | inventory-family route screens are not present/accepted as one-to-one systems | every screen opens from normal input, matches source state/layout, performs its route transactions, and restores gameplay |
| `bec110ar_players_house` completeness | `renderable` from private cache | no exhaustive runtime/source join or visual acceptance | zero-delta census plus matched room coverage |
| `bec100ar_elven_alienage` completeness | `renderable` from private cache | sky/atmosphere, exposure, outdoor lighting, actors, scripts, effects, and route timing not accepted | zero-delta census; sky dome/cloud/moon/fog/sun/probe/water/terrain/props/actors/audio/VFX matched over a route camera path |
| Post-kidnapping branch | inventoried, not route-locked | source script graph must prove the exact female-origin transition before implementation is claimed | versioned transition graph binds destination area/waypoint and state |
| Estate action/combat | **absent** | estate cell runtime, scripts, combat, UI, effects, and audio are not integrated | accepted female route reaches its source destination and completes first real combat normally |
| Level-1 result | **absent** | no complete origin action transaction | character, party, inventory, journal, plot, XP/level, and world agree |
| Save, exit, Continue | **absent** | current session snapshot is not route acceptance | save from accepted state, process exit, Continue, deterministic restoration |

`bec120ar_alariths_store`, `bec200ar_estate_ext`, and
`bec210ar_estate_int` are already visible in the private structural inventory.
They are candidates, not automatically part of the female baseline. The source
plot/transition graph decides which are mandatory. This prevents both skipping
a real cell and inflating coverage with an adjacent cell that the locked branch
never enters.

### DAO cell completeness gate

Each mandatory cell must close a generated census for terrain, room/prop/tree
instances, water, static and character lights, sun, fog, atmosphere, clouds,
moon, sky dome, reflection probe, placeables, doors, triggers, waypoints,
actors, equipment, animation banks, cutscenes/dialogues, FaceFX, scripts,
music, ambient/audio emitters, and VFX. Outdoor acceptance fails on a black or
empty background even when foreground geometry is present.

## Machine gate and complexity budget

The cell census is a pair of hash maps keyed by stable source identity:

```text
source inventory O(N) -> runtime inventory O(M) -> keyed join O(N + M)
```

No per-frame path may rescan the whole cell or inventory. Deterministic report
sorting may be `O(N log N)`. Navigation and story traversal declare `V`/`E`
bounds. Increasing synthetic corpora record work units and reject unexplained
superlinear growth.

Every checkpoint report carries one of: `missing`, `wrong-state`,
`renderable-unmatched`, `matched-failed`, or `accepted`. “Pass” without the
comparison identity and closing assertion is invalid. Unknown source records,
unsupported render semantics, absent assets, and ambiguous precedence fail
closed.

## Implementation slices

1. **Truthful route ownership:** exclude showcase automation, scripted
   locomotion, and auto-choice captures from parity eligibility.
2. **Normal UI route:** expose and exercise HUD, inventory, equipment, party,
   abilities, and journal through ordinary input in both profiles.
3. **Real gameplay state:** implement KOTOR combat and the DAO origin action
   from source rules/scripts, including effects/audio and return to control.
4. **Cell completeness:** generate and close per-cell source/runtime censuses;
   repair geometry, alpha/depth, sky/atmosphere, materials, lights, actors, and
   emitters by earliest owner.
5. **Cinematic parity:** match every camera/actor/animation/FaceFX/audio event,
   including temporal handoffs—not selected hero frames.
6. **Persistence:** save, exit, Continue, and compare restored state.
7. **Fresh first-party import and package:** repeat both routes from a clean
   machine and legally owned folders using only the public Aurora pipeline.
8. **Final evidence:** capture one uninterrupted live retail-versus-Aurora row
   per checkpoint, then produce the single dual-profile video.

No later slice can turn an earlier red row green by declaration. The ledger is
updated in the same pull request as each closing implementation and evidence
summary.
