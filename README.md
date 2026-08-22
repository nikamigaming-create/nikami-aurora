# Nikami Aurora

Nikami Aurora is an experimental, open compatibility runtime for legally
installed Aurora-family games and their Odyssey and Eclipse descendants.
The first two profiles are *Star Wars: Knights of the Old Republic* and
*Dragon Age: Origins*.

The project is at early vertical-slice stage. It can identify a target
installation, validate profile-specific source markers, produce a SHA-256-bound
target manifest, import the real KOTOR Endar Spire room geometry and textures,
load that owned-data bundle into Godot, assemble Trask from his installed body,
head, texture, and weapon resources, and advance through the first authored
dialogue choices. Movement is constrained to the installed room walkmeshes and
the first authored lockdown door is materialized and interactive. Full combat
and script execution remain active milestones.

## Why a new runtime

The existing [OpenKOTOR](https://github.com/nikamigaming-create/OpenKOTOR)
retail bridge and [OpenDAO](https://github.com/nikamigaming-create/OpenDAO)
compatibility proof contain valuable research, automation, and runtime work.
Nikami Aurora gives that work a clean multi-game architecture:

- `Core` owns engine-independent contracts and deterministic state.
- `Profiles` own each game's formats, resource precedence, script ABI, rules,
  world assembly, and presentation adapters.
- A future Godot runtime will own rendering, input, audio, UI, and OpenXR.
- Retail hooks remain evidence oracles; implementation crosses the boundary
  only through documented behavioral contracts and synthetic tests.

No proprietary game assets, extracted resources, or executables belong in
this repository.

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

Install the pinned importer dependencies into Python 3.12, then generate the
ignored local bundle from a legally installed KOTOR copy:

```powershell
py -3.12 -m pip install -r requirements-import.txt
./scripts/Import-KotorModule.ps1 `
  -GameRoot 'D:\SteamLibrary\steamapps\common\swkotor'
```

Launch the new Godot runtime:

```powershell
./scripts/Start-KotorGodot.ps1
```

The importer writes only to `local/kotor/end_m01aa`, which is excluded from
Git. The runtime loads 15 authored Endar Spire room records, materializes Trask,
and exposes the remaining exact creature placements as identified debug markers
while broader creature model assembly is implemented.

## Repository map

- `src/Nikami.Aurora.Core` — profile and installation contracts.
- `src/Nikami.Aurora.Profiles.Kotor` — Odyssey/KOTOR profile.
- `src/Nikami.Aurora.Profiles.DragonAgeOrigins` — Eclipse/DAO profile.
- `src/Nikami.Aurora.Cli` — deterministic profile and target tooling.
- `godot` — shared Godot runtime and the active KOTOR vertical slice.
- `scripts` — owned-install import and launch commands.
- `tests/Nikami.Aurora.Acceptance` — dependency-free synthetic acceptance.
- `docs/ARCHITECTURE.md` — dependency and profile boundaries.
- `docs/CLEAN_ROOM.md` — evidence and implementation separation.
- `docs/ROADMAP.md` — KOTOR-first delivery gates and DAO migration.
- `docs/KOTOR-GODOT-PROOF.md` — exact Endar Spire proof and limitations.

## Legal

Nikami Aurora is independent and unaffiliated. Users must provide legally
obtained game installations. See [NOTICE.md](NOTICE.md),
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and [LICENSE](LICENSE).
