# Release UX contract

Nikami Aurora releases are an engine and launcher, never a game-data bundle.

## User flow

```text
download Aurora -> select game -> choose installed game folder -> validate -> play
                                      |
                                      +-> private local import/cache on first run
```

The launcher remembers each approved install path, reports an actionable error
when required files are absent, and binds every cache to source hashes. A game
update or mod change invalidates only affected cache entries.

## Packaging gate

A public release is not ready until it provides all of the following without a
developer shell:

- self-contained Windows executable and Godot runtime;
- game/profile picker and native folder chooser;
- install validation before import;
- public Aurora-owned import libraries built into the release;
- visible import progress, cancellation, and recoverable errors;
- per-game private cache with deterministic invalidation;
- Play, cache rebuild, and diagnostic-log actions;
- no proprietary assets in the package, repository, or telemetry.

The import path must not download, clone, or execute Haven Tools, a private
importer, PyKotor, MDLOps, xoreos, or another external game-specific RE or
conversion tool. Godot/.NET and ordinary audited rendering or asset libraries
remain permitted under the dependency policy in
[`DUAL-PROFILE-HELLO-WORLD.md`](DUAL-PROFILE-HELLO-WORLD.md). Existing proof
scripts are migration scaffolding, not components to bundle into the product.

The checked-in PowerShell and Python entry points are development harnesses for
the current proof. They are not the intended release interface and cannot close
the first-party fresh-import gate.

## Distribution gates

`.gitignore` is not a release boundary. Every desktop archive and future XR
package must be inspected independently after export and must reject game
archives, generated assets, local captures, signing material, and private paths.
The user-owned conversion result must be deterministic, versioned, fully
SHA-256 indexed, validated in a staging directory, and atomically installed so
a failed update leaves the previous cache usable.

This gate follows the supplied
[asset-free Godot XR release model](https://github.com/Brobert-in-aus/guides/blob/main/vr/shipping-an-asset-free-godot-xr-port.md),
adapted to Aurora's owned-install importer and desktop/OpenXR targets.
