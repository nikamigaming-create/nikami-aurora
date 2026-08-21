# Repository instructions

- Never commit proprietary game assets, executables, extracted localized text,
  retail saves, generated caches, or raw reverse-engineering dumps.
- Keep `Nikami.Aurora.Core` game-neutral and dependent only on the .NET base
  class library. Game formats, rules, script actions, and presentation behavior
  belong in profile projects.
- Treat disassembly and decompiler output as evidence, not implementation
  source. Cross the research boundary through neutral behavioral contracts and
  synthetic tests as described in `docs/CLEAN_ROOM.md`.
- Fail closed when source identity, format interpretation, or behavioral
  semantics are unknown. Report unsupported behavior explicitly.
- Preserve target SHA-256, build identity, provenance, confidence, and
  contradictory evidence in compatibility claims.
- Before committing source changes, run:
  `dotnet build Nikami.Aurora.sln --configuration Release` and
  `dotnet run --project tests/Nikami.Aurora.Acceptance --configuration Release --no-build`.
