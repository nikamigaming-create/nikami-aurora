# Architecture

Nikami Aurora is one runtime with explicit game profiles. Similar ancestry is
not treated as binary or behavioral identity.

The first product proof is governed by
[`DUAL-PROFILE-HELLO-WORLD.md`](DUAL-PROFILE-HELLO-WORLD.md). Shared
architecture is accepted only when the KOTOR and DAO routes both exercise it;
the prior OpenDAO tree is migrated through
[`OPENDAO-HARVEST.md`](OPENDAO-HARVEST.md), not merged wholesale.

## Dependency direction

```text
Nikami.Aurora.Core
        ^
        |
Profiles.Kotor        Profiles.DragonAgeOrigins
        ^                         ^
        +------------+------------+
                     |
               Runtime.Godot
```

- `Core` depends only on the .NET base class library.
- A profile may depend on `Core`; profiles never depend on each other.
- Runtime and tooling discover profiles through `IGameProfile`.
- Importers consume owned installations and write ignored local bundles.
- Accepted release importers are Aurora-owned public source and never invoke an
  external game-specific RE/conversion tool or private importer.
- Godot-facing nodes remain adapters; simulation and compatibility rules stay
  in ordinary C# services.
- Desktop and OpenXR frontends emit the same KOTOR profile intent. KOTOR
  locomotion outcomes are computed by `Profiles.Kotor`; tracked HMD/controller
  pose remains presentation state and never mutates gameplay state directly.

## Profile-owned boundaries

Every profile owns its implementations of:

1. Install detection and resource precedence.
2. Archive and structured-data formats.
3. Models, materials, animation, area assembly, and navigation sources.
4. Script dialect, action table, event model, and object bindings.
5. Rules, combat arithmetic, progression, inventory, and party semantics.
6. Dialogue, cinematics, UI, localization, audio, and save compatibility.

The shared runtime may expose neutral contracts for those capabilities, but it
must not encode KOTOR or Dragon Age defaults in `Core`.

## Initial profiles

- `kotor`: Odyssey-family profile. The first vertical slice is `end_m01aa`.
- `dragon-age-origins`: Eclipse-family profile migrated selectively from
  OpenDAO. Its interim City Elf slice now runs through the neutral dispatcher;
  the external compatibility-cache boundary remains a migration blocker, not
  an accepted release dependency.
