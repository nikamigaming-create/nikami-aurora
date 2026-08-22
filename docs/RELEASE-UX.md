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
- bundled or natively replaced importer dependencies;
- visible import progress, cancellation, and recoverable errors;
- per-game private cache with deterministic invalidation;
- Play, cache rebuild, and diagnostic-log actions;
- no proprietary assets in the package, repository, or telemetry.

The checked-in PowerShell and Python entry points are development harnesses for
the same pipeline. They are not the intended release interface.
