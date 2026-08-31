#!/usr/bin/env python3
"""Sequentially import every owned KotOR module into a private evidence cache.

This runner deliberately starts only one importer process at a time.  It writes
an atomic, resumable result ledger containing exit states and hashes, but never
copies generated game assets into the repository history.
"""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys
from typing import Any

sys.dont_write_bytecode = True

import import_kotor_module as importer
import preflight_kotor_modules as preflight


SCHEMA = "nikami-aurora-kotor-private-corpus-import-v1"
MAX_LOG_CHARACTERS = 262_144


def utc_now() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


def bounded_log(text: str) -> str:
    if len(text) <= MAX_LOG_CHARACTERS:
        return text
    half = MAX_LOG_CHARACTERS // 2
    removed = len(text) - MAX_LOG_CHARACTERS
    return (
        text[:half] +
        f"\nNIKAMI_AURORA_LOG_TRUNCATED removed_characters={removed}\n" +
        text[-half:]
    )


def write_json_atomic(path: Path, value: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(
        json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def current_manifest_record(path: Path, output_root: Path) -> dict[str, Any]:
    manifest = json.loads(path.read_text(encoding="utf-8"))
    emitters = [
        emitter
        for room in manifest.get("rooms", [])
        for emitter in room.get("emitters", [])
    ]
    current = (
        manifest.get("schema") == "nikami-aurora-kotor-module-v1" and
        all(emitter.get("schema") == "nikami-aurora-kotor-room-emitter-v2"
            for emitter in emitters) and
        all(
            creature.get("renderImportSchema") ==
            "nikami-aurora-kotor-source-creature-v1" and
            creature.get("renderStatus") in {"ready", "unsupported"} and
            bool(creature.get("glb")) ==
            (creature.get("renderStatus") == "ready") and
            (creature.get("renderStatus") != "ready" or
             len(creature.get("animation", {}).get("boundsMinimum", [])) == 3 and
             len(creature.get("animation", {}).get("extent", [])) == 3)
            for creature in manifest.get("creatures", [])) and
        int(manifest.get("counts", {}).get("renderReadyCreatures", -1)) ==
        sum(creature.get("renderStatus") == "ready"
            for creature in manifest.get("creatures", [])) and
        int(manifest.get("counts", {}).get("unsupportedCreatures", -1)) ==
        sum(creature.get("renderStatus") != "ready"
            for creature in manifest.get("creatures", []))
    )
    encounter = manifest.get("firstEncounter")
    if encounter is not None:
        current = current and (
            encounter.get("effects", {}).get("schema") ==
            "nikami-aurora-kotor-first-encounter-effects-v2")
    return {
        "path": path.relative_to(output_root).as_posix(),
        "sha256": importer.sha256_file(path),
        "schema": manifest.get("schema", ""),
        "module": manifest.get("module", ""),
        "contentMode": manifest.get("contentMode", ""),
        "currentImporterContract": current,
        "counts": manifest.get("counts", {}),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument("--output-root", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    parser.add_argument("--mdlops", type=Path, required=True)
    parser.add_argument("--runtime-config", type=Path, required=True)
    parser.add_argument("--module", action="append", default=[])
    parser.add_argument("--timeout-seconds", type=int, default=1800)
    parser.add_argument("--resume", action="store_true")
    parser.add_argument("--stop-on-failure", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    game_root = args.game_root.resolve()
    output_root = args.output_root.resolve()
    report_path = args.report.resolve()
    mdlops = args.mdlops.resolve()
    runtime_config = args.runtime_config.resolve()
    executable = game_root / "swkotor.exe"
    if not executable.is_file():
        raise RuntimeError(f"KotOR executable was not found: {executable}")
    if not mdlops.is_file():
        raise RuntimeError(f"MDLOps was not found: {mdlops}")
    if not runtime_config.is_file():
        raise RuntimeError(f"Runtime configuration was not found: {runtime_config}")
    if args.timeout_seconds <= 0:
        raise RuntimeError("--timeout-seconds must be positive")

    installation = importer.Installation(game_root)
    pairs, unpaired = preflight.discover_module_pairs(installation.module_path())
    selected = [importer.normalize_module_id(value) for value in args.module]
    modules = sorted(set(selected)) if selected else sorted(pairs)
    unknown = sorted(set(modules) - set(pairs))
    if unknown:
        raise RuntimeError(f"Selected module pairs were not found: {unknown}")

    existing: dict[str, Any] = {}
    if args.resume and report_path.is_file():
        prior = json.loads(report_path.read_text(encoding="utf-8"))
        if prior.get("schema") != SCHEMA:
            raise RuntimeError("Resume report schema does not match this runner")
        if prior.get("target", {}).get("executableSha256") != importer.sha256_file(
                executable):
            raise RuntimeError("Resume executable identity does not match")
        existing = {entry["module"]: entry for entry in prior.get("results", [])}

    output_root.mkdir(parents=True, exist_ok=True)
    log_root = report_path.parent / "import-logs"
    result_by_module: dict[str, dict[str, Any]] = dict(existing)
    report: dict[str, Any] = {
        "schema": SCHEMA,
        "claim": "private-import-evidence-only-no-runtime-or-parity",
        "status": "running",
        "startedUtc": utc_now(),
        "target": {
            "executableSha256": importer.sha256_file(executable),
            "discoveredModulePairs": len(pairs),
            "selectedModulePairs": len(modules),
            "unpairedModuleIds": unpaired,
        },
        "sequential": True,
        "maximumConcurrentImports": 1,
        "outputRoot": str(output_root),
        "results": [],
    }

    def checkpoint(status: str) -> None:
        ordered = [result_by_module[module] for module in modules
                   if module in result_by_module]
        report["status"] = status
        report["results"] = ordered
        report["summary"] = {
            "attempted": len(ordered),
            "imported": sum(entry["status"] == "imported" for entry in ordered),
            "failed": sum(entry["status"] == "failed" for entry in ordered),
            "timedOut": sum(entry["status"] == "timed-out" for entry in ordered),
            "remaining": len(modules) - len(ordered),
        }
        write_json_atomic(report_path, report)

    checkpoint("running")
    importer_path = Path(__file__).resolve().with_name("import_kotor_module.py")
    try:
        for index, module in enumerate(modules, start=1):
            prior_entry = result_by_module.get(module)
            manifest_path = output_root / module / "module-manifest.json"
            if (args.resume and prior_entry and
                    prior_entry.get("status") == "imported" and
                    manifest_path.is_file() and
                    prior_entry.get("manifest", {}).get("sha256") ==
                    importer.sha256_file(manifest_path)):
                print(
                    f"NIKAMI_AURORA_CORPUS_IMPORT status=resume-skip "
                    f"module={module} index={index}/{len(modules)}")
                continue

            print(
                f"NIKAMI_AURORA_CORPUS_IMPORT status=started "
                f"module={module} index={index}/{len(modules)}")
            command = [
                sys.executable,
                str(importer_path),
                "--game-root", str(game_root),
                "--module", module,
                "--output", str(output_root / module),
                "--mdlops", str(mdlops),
                "--runtime-config", str(runtime_config),
            ]
            started = utc_now()
            try:
                completed = subprocess.run(
                    command,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    timeout=args.timeout_seconds,
                    check=False,
                )
                combined = bounded_log(
                    completed.stdout +
                    ("\n--- STDERR ---\n" if completed.stderr else "") +
                    completed.stderr)
                log_path = log_root / f"{module}.log"
                log_path.parent.mkdir(parents=True, exist_ok=True)
                log_path.write_text(combined, encoding="utf-8")
                imported = completed.returncode == 0 and manifest_path.is_file()
                entry: dict[str, Any] = {
                    "module": module,
                    "status": "imported" if imported else "failed",
                    "startedUtc": started,
                    "finishedUtc": utc_now(),
                    "exitCode": completed.returncode,
                    "log": {
                        "path": log_path.relative_to(report_path.parent).as_posix(),
                        "sha256": importer.sha256_file(log_path),
                        "truncated": "NIKAMI_AURORA_LOG_TRUNCATED" in combined,
                    },
                }
                if imported:
                    entry["manifest"] = current_manifest_record(
                        manifest_path, output_root)
                    if entry["manifest"]["module"].casefold() != module:
                        entry["status"] = "failed"
                        entry["failure"] = "manifest-module-identity-mismatch"
                    elif not entry["manifest"]["currentImporterContract"]:
                        entry["status"] = "failed"
                        entry["failure"] = "stale-manifest-contract"
                else:
                    failure_lines = [
                        line.strip() for line in completed.stderr.splitlines()
                        if line.strip().startswith("KOTOR_IMPORT_FAIL:")]
                    entry["failure"] = (
                        failure_lines[-1] if failure_lines
                        else "importer-nonzero-or-manifest-missing")
            except subprocess.TimeoutExpired as exc:
                combined = bounded_log(
                    (exc.stdout or "") + "\n--- STDERR ---\n" + (exc.stderr or ""))
                log_path = log_root / f"{module}.log"
                log_path.parent.mkdir(parents=True, exist_ok=True)
                log_path.write_text(combined, encoding="utf-8")
                entry = {
                    "module": module,
                    "status": "timed-out",
                    "startedUtc": started,
                    "finishedUtc": utc_now(),
                    "exitCode": None,
                    "failure": f"timeout-after-{args.timeout_seconds}-seconds",
                    "log": {
                        "path": log_path.relative_to(report_path.parent).as_posix(),
                        "sha256": importer.sha256_file(log_path),
                        "truncated": "NIKAMI_AURORA_LOG_TRUNCATED" in combined,
                    },
                }
            result_by_module[module] = entry
            checkpoint("running")
            print(
                f"NIKAMI_AURORA_CORPUS_IMPORT status={entry['status']} "
                f"module={module} index={index}/{len(modules)}")
            if entry["status"] != "imported" and args.stop_on_failure:
                checkpoint("stopped-on-failure")
                return 1
    except KeyboardInterrupt:
        checkpoint("interrupted")
        return 130

    failures = [entry for entry in result_by_module.values()
                if entry.get("status") != "imported"]
    checkpoint("complete-with-blockers" if failures else "complete")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
