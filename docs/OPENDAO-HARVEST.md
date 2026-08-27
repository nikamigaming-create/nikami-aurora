# OpenDAO harvest ledger

Status: **canonical migration inventory**

Source repository: `nikamigaming-create/OpenDAO`
Audited source baseline: `dde9186` (`main`, 2026-08-26 audit)

This ledger controls how the prior public OpenDAO work enters Nikami Aurora.
It prevents two equally damaging outcomes: rebuilding useful first-party work,
or copying an entire standalone runtime and its historical assumptions into a
multi-profile architecture.

All migrations must also satisfy
[`DUAL-PROFILE-HELLO-WORLD.md`](DUAL-PROFILE-HELLO-WORLD.md) and
[`CLEAN_ROOM.md`](CLEAN_ROOM.md).

## Baseline truth

Confirmed at the audited baseline:

- OpenDAO is a public MIT-licensed source tree owned by the same account.
- `dotnet build godot/OpenDAO.csproj --configuration Release` succeeds with no
  warnings or errors.
- GitHub source-validation, Windows-runtime, and CodeQL jobs have passed.
- The tree contains 120 C# files across domain, application, infrastructure,
  launcher, main-menu, intro, presentation, diagnostics, and rendering areas.
- It contains first-party ERF and classic GFF readers, profile/cache adapters,
  world-loading services, DAO materials and shaders, menus, character creation,
  cinematics, FaceFX presentation, player movement, HUD, and persistence
  foundations.
- The public runtime still expects a compatibility cache created by an
  explicitly supplied private importer. That is incompatible with the Aurora
  release contract and must be replaced.
- The prior New Game route enters a Redcliffe slice; it does not materialize the
  selected retail origin prologue. It is not the accepted DAO Hello World route.
- Historical documentation describes broader story/NCS work than the clean
  baseline currently contains as source. Documentation and telemetry are
  evidence, not migratable implementation.

Inside Nikami Aurora, `Profiles.DragonAgeOrigins` currently provides install
detection only. No OpenDAO subsystem is considered harvested until it is
ported, tested, and recorded below.

## Status vocabulary

- `candidate` — public source exists and ownership/provenance looks suitable;
- `porting` — an Aurora-owned migration branch is active;
- `synthetic-tested` — source-free tests pass in the Aurora solution;
- `owned-data-proven` — a fresh first-party import exercises it;
- `parity-accepted` — its matched retail rows pass; and
- `rejected` — it violates architecture, provenance, scope, or dependency
  policy and will not migrate.

Every row begins as `candidate` or `rejected`. File presence is not completion.

## Harvest candidates

| OpenDAO source | Value already present | Aurora destination/decision | Initial state |
| --- | --- | --- | --- |
| `Domain/Characters`, `Abilities`, `Inventory`, `Party`, `Quests`, `Combat`, `Story`, `Sessions`, `World` | Plain state/value objects and invariants | Port only DAO semantics into `Profiles.DragonAgeOrigins`; promote a contract to Core only after KOTOR proves the same neutral abstraction | `candidate` |
| `Application/Abstractions` and character initializers | Narrow ports for profiles, persistence, content, lighting, navigation, models, time, and environment | Use as design evidence; reconcile with Aurora interfaces instead of creating a second application framework | `candidate` |
| `Infrastructure/Archives/ErfArchive.cs` | Bounded ERF V2.0 and ERF/RIM V2.1 reader with synthetic coverage | Port into a first-party DAO format library, preserve fail-closed range/compression checks, expand with owned-source precedence tests | `candidate` |
| `Infrastructure/Catalogs/ClassicGff32RootReader.cs` | First-party classic GFF root reader | Port into the DAO format library and expand to every field/list/layout required by the selected route | `candidate` |
| GDA/area/character catalog providers | Data-backed ability, character, and area adapters | Rebind to the new first-party import manifest; remove old private-cache schema assumptions | `candidate` |
| `Infrastructure/Persistence` | JSON store and player-session repository | Adapt to Aurora's versioned profile snapshots and atomic save/cache policy | `candidate` |
| World content, arrival, scheduling, collision, navigation, lighting, terrain, water, and material services | Substantial DAO-specific Godot presentation knowledge | Keep DAO-specific; split source interpretation into the profile and rendering into the Godot adapter | `candidate` |
| DAO shaders | Terrain, water, sky, character, hair, eyelash, tint, static/cutout, cloud, and fallback-effect paths | Port only after source material identities and parameters are imported and hash-bound; no hand-authored visual substitution may close parity | `candidate` |
| `MainMenu` GFX/atlas/font/canvas classes | Source-driven menu, Scaleform-like canvas, fonts, character creation, preview, video options, and world-map foundations | Adapt under a DAO UI presenter selected by the neutral runtime dispatcher | `candidate` |
| `Intro` sequence plan/controller | Startup sequence orchestration | Convert to neutral startup requests plus DAO-authored sequence data; no DAO timing in Core | `candidate` |
| `Presentation/Cinematics` | Dialogue, layered animation, player appearance, opening cutscene, and FaceFX presentation foundations | Adapt to DAO profile records and matched route telemetry | `candidate` |
| `Presentation/Player` and `Presentation/World` | Player controller, interaction/highlight, world composition, HUD, scene bounds | Reuse behavior where evidence-backed; replace standalone composition with Aurora profile dispatch | `candidate` |
| `Diagnostics` and smoke gates | Architecture, character flow, abilities, locomotion, and runtime-file checks | Port useful assertions into dependency-free Aurora acceptance projects | `candidate` |
| Native launcher and `Launcher` UI | Installation/session/display concepts | Do not ship a DAO-specific launcher; harvest requirements into one Aurora launcher | `candidate` |

## Rewrite rather than copy

These areas contain useful observations but cannot migrate unchanged:

- Any reader or resolver coupled to the old private compatibility-cache schema.
- Generated-GLB assumptions whose source model, skin, material, animation, and
  payload provenance cannot be reconstructed by the new public importer.
- OpenDAO composition built around `Microsoft.Extensions.DependencyInjection`.
  Aurora currently uses explicit references and should not add a container
  merely to preserve the old wiring.
- Menu or HUD code containing layout/timing constants that should come from
  installed GFX/GFF/GDA data or a public profile configuration.
- Simplified combat numbers, placeholder effects, fallback geometry, proxy
  characters, fabricated text, or deterministic staging that differs from the
  selected retail route.
- A direct Redcliffe New Game shortcut. It remains a useful world-loading test,
  not the accepted origin flow.

## Evidence only; do not represent as working Aurora code

The following remain useful research inputs but are not implementations:

- `docs/dao-story-runtime.md` and other historical capability narratives whose
  named runtime classes are absent from the audited clean baseline;
- source hashes, action-coverage counts, neutral ABI observations, camera or
  transform measurements, and accepted/rejected parity notes;
- source-only route manifests and synthetic fixtures;
- prior screenshots, movies, local logs, generated cache reports, and retail
  telemetry kept outside the public product; and
- OpenMW experiments and three-way comparison results.

Every behavioral claim recovered from those materials requires a neutral
contract, an Aurora implementation, a synthetic test, and owned-data replay.

## Rejected from the product/import path

- Haven Tools, its source, binaries, patches, plugins, output assumptions, or
  setup instructions.
- Any private importer or converter.
- PyKotor, MDLOps, xoreos, or another party's game-specific RE/engine code as an
  import/runtime dependency.
- `tools/dao-control` binaries or injected control mechanisms in the shipped
  runtime. Nikami-owned observation tooling may remain private evidence tooling,
  but no UI automation, focus change, or injected input is part of acceptance.
- Retail executables, archives, saves, localized text, audio, textures, models,
  movies, converted payloads, raw dumps, or captures.
- Generated cache directories, absolute machine paths, credentials, and
  signing material.
- Placeholder/proxy assets used to make an incomplete scene look finished.

## First-party DAO importer backlog

The accepted DAO route requires public Nikami-authored implementations for the
following, staged only as the route demands:

1. installation inventory, DLC/override precedence, stable resource keys, and
   source hashing;
2. ERF/RIM and required compression variants;
3. classic GFF plus GDA, talktable, UTC/UTI/ARE/ARL, DLG, CUT, and script
   metadata needed by the route;
4. MMH/MSH/MAO/MAT models and materials, skeletons, equipment sockets, and
   deterministic Godot/glTF payload output;
5. ANI/blend trees, FaceFX/lip data, cameras, cutscene timelines, voices,
   music, sound effects, and startup/movie references;
6. terrain, area layout, navigation, blockers, placeables, creatures, lights,
   atmosphere, water, VFX, and source UI/GFX/font records;
7. route-scoped script behavior, character creation, party/inventory/ability/
   combat state, save/Continue, and typed presentation events; and
8. staged atomic cache installation with source/payload hashes and dependency-
   free validation.

The importer should be C#/.NET source in the Nikami Aurora repository unless a
reviewed common-library exception under the canonical dependency policy is
approved. It must never shell out to a game-specific converter.

## Migration procedure for every row

1. Identify the exact OpenDAO files and public commit.
2. Verify authorship, license, architecture, and absence of generated/private
   material.
3. Write the neutral behavior/source contract and target profile ownership.
4. Port the smallest coherent service; do not preserve old namespaces or
   wiring solely to reduce diff size.
5. Add synthetic fixtures and complexity counters where corpus size matters.
6. Run a fresh import made by the public first-party DAO importer.
7. Exercise the real DAO profile through the shared Godot dispatcher.
8. Compare the matched retail row and record remaining deltas.
9. Update this ledger's state in the same pull request.
10. Audit the public tree and release staging directory before merge.

## Completion gate

The OpenDAO harvest is complete for the Hello World only when:

- every subsystem required by `dao-dalish-level1-v1` is at least
  `owned-data-proven`;
- every visible or behavioral route checkpoint is `parity-accepted`;
- the fresh cache is generated without Haven Tools, private importers, or
  external game-specific RE/conversion tools;
- no DAO assumption leaked into `Nikami.Aurora.Core`;
- the old Redcliffe shortcut is not standing in for the selected origin; and
- a clean packaged Aurora install completes select-folder -> import -> startup
  -> menu -> New Game -> origin -> level-1 action -> save/Continue.
