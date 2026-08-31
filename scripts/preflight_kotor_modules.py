#!/usr/bin/env python3
"""Read-only static and private-evidence preflight for owned KotOR modules.

The command never exports game resources or creates a game cache.  It may write
the generated JSON report to an explicitly selected path.  Manifest and runtime
artifacts are evidence inputs only and never imply retail-parity acceptance.
"""

from __future__ import annotations

import argparse
from collections import Counter
import json
import math
import sys
from pathlib import Path
from typing import Any, Iterable

sys.dont_write_bytecode = True

import import_kotor_module as importer


PREFLIGHT_SCHEMA = "nikami-aurora-kotor-owned-install-preflight-v2"
STATIC_CLAIM = "static-preflight-only-no-runtime-parity"
BLOCKED_CORE = "blocked-core"
BLOCKED_SEMANTICS = "blocked-current-import-or-runtime"
PASS_WITH_GAPS = "pass-with-structural-or-explicit-gaps"
PASS_NO_GAPS = "preflight-pass-no-static-gaps"

TXI_RENDERED_DIRECTIVES = importer.TXI_RENDERED_DIRECTIVES
TXI_UNSUPPORTED_PRESENTATION_DIRECTIVES = (
    importer.TXI_UNSUPPORTED_PRESENTATION_DIRECTIVES)
TXI_SAMPLING_OR_METADATA_DIRECTIVES = importer.TXI_SAMPLING_OR_METADATA_DIRECTIVES


def sorted_counter(counter: Counter[str]) -> dict[str, int]:
    return {key: int(counter[key]) for key in sorted(counter)}


def coverage_class(
    core_errors: Counter[str], blockers: Counter[str], gaps: Counter[str]
) -> str:
    if any(core_errors.values()):
        return BLOCKED_CORE
    if any(blockers.values()):
        return BLOCKED_SEMANTICS
    if any(gaps.values()):
        return PASS_WITH_GAPS
    return PASS_NO_GAPS


emitter_unsupported_reasons = importer.room_emitter_unsupported_reasons


def discover_module_pairs(module_root: Path) -> tuple[dict[str, tuple[str, str]], list[str]]:
    """Discover case-preserving base/story RIM pairs and reject collisions."""
    if not module_root.is_dir():
        raise RuntimeError(f"KOTOR module directory was not found: {module_root}")
    base: dict[str, list[str]] = {}
    story: dict[str, list[str]] = {}
    for path in module_root.iterdir():
        if not path.is_file() or path.suffix.casefold() != ".rim":
            continue
        stem = path.stem
        is_story = stem.casefold().endswith("_s")
        identity_source = stem[:-2] if is_story else stem
        try:
            identity = importer.normalize_module_id(identity_source)
        except RuntimeError:
            continue
        target = story if is_story else base
        target.setdefault(identity, []).append(path.name)
    collisions = {
        identity: sorted(names)
        for source in (base, story)
        for identity, names in source.items()
        if len(names) > 1
    }
    if collisions:
        raise RuntimeError(f"Ambiguous case-insensitive module RIM identities: {collisions}")
    paired = {
        identity: (base[identity][0], story[identity][0])
        for identity in sorted(set(base) & set(story))
    }
    unpaired = sorted(set(base) ^ set(story))
    return paired, unpaired


def fresh_evidence() -> dict[str, Any]:
    return {
        "core": Counter(),
        "blockers": Counter(),
        "gaps": Counter(),
        "counts": Counter(),
        "emitterReasons": Counter(),
        "emitterCombinations": Counter(),
        "emitterBounceCoefficients": Counter(),
        "emitterVisualSafety": Counter(),
        "emitterRenderBlockers": Counter(),
        "txiDirectives": Counter(),
        "txiDirectiveValues": Counter(),
        "bumpMapScaleValues": Counter(),
        "unsupportedTxiDirectives": Counter(),
        "unsupportedTxiValues": Counter(),
        "unclassifiedTxiDirectives": Counter(),
        "unclassifiedTxiValues": Counter(),
        "invalidRoomModelResrefs": Counter(),
        "roomModelParseErrors": Counter(),
        "roomModelParseIdentities": Counter(),
        "mixedMaterialSemantics": Counter(),
        "mixedMaterialIdentities": Counter(),
        "creatureTemplates": Counter(),
        "diffuse": set(),
        "lightmaps": set(),
        "bumpmaps": set(),
        "environmentMaps": set(),
        "textures": set(),
        "roomModels": set(),
    }


def merge_evidence(target: dict[str, Any], source: dict[str, Any]) -> None:
    for key in (
        "core", "blockers", "gaps", "counts", "emitterReasons",
        "emitterCombinations", "emitterBounceCoefficients",
        "emitterVisualSafety", "emitterRenderBlockers", "txiDirectives",
        "unsupportedTxiDirectives", "unclassifiedTxiDirectives",
        "bumpMapScaleValues", "txiDirectiveValues", "unsupportedTxiValues",
        "unclassifiedTxiValues", "invalidRoomModelResrefs",
        "roomModelParseErrors", "roomModelParseIdentities",
        "mixedMaterialSemantics",
        "mixedMaterialIdentities",
        "creatureTemplates",
    ):
        target[key].update(source[key])
    for key in (
        "diffuse", "lightmaps", "bumpmaps", "environmentMaps", "textures",
        "roomModels",
    ):
        target[key].update(source[key])


class StaticScanner:
    def __init__(self, installation: Any):
        self.installation = installation
        self.texture_cache: dict[str, dict[str, Any]] = {}
        self.room_cache: dict[str, dict[str, Any]] = {}

    def texture(self, resref: str) -> dict[str, Any]:
        key = resref.strip().lower()
        if not key or key == "null":
            return {"exists": False, "parsed": False, "directives": {}, "cube": False}
        if key in self.texture_cache:
            return self.texture_cache[key]
        source = None
        txi = ""
        texture = None
        parse_error = None
        try:
            source, txi = self.installation.texture_resource_result(resref)
            if source is not None:
                texture = self.installation.texture(resref)
        except Exception as exc:  # evidence is classified rather than trusted
            parse_error = type(exc).__name__
        record = {
            "exists": source is not None,
            "parsed": texture is not None,
            "parseError": parse_error,
            "directives": importer.parse_txi_directives(
                importer.raw_tpc_txi(importer.resource_data(source))
                if source is not None and importer.resource_type_name(source) == "TPC"
                else str(txi or (texture.txi if texture is not None else ""))),
            "alphaTest": float(texture.alpha_test) if texture is not None else 1.0,
            "cube": bool(
                texture is not None and texture.is_cube_map and
                len(texture.layers) == 6),
        }
        self.texture_cache[key] = record
        return record

    def count_txi(
        self, evidence: dict[str, Any], resref: str, texture: dict[str, Any]
    ) -> None:
        key = resref.strip().lower()
        if not key or key == "null":
            return
        evidence["textures"].add(key)
        for directive, values in texture["directives"].items():
            occurrence_count = max(1, len(values))
            evidence["txiDirectives"][directive] += occurrence_count
            # TXI keywords and resrefs are case-insensitive. Case-folding
            # keeps the JSON histogram consumable by case-insensitive object
            # readers while retaining exact numeric/procedure values.
            normalized_values = [
                " ".join(value.split()).casefold() or "<empty>"
                for value in values
            ]
            if not normalized_values:
                normalized_values = ["<empty>"]
            evidence["txiDirectiveValues"].update(
                f"{directive}={value}" for value in normalized_values)
            if directive == "bumpmapscaling":
                evidence["bumpMapScaleValues"].update(values)
            classification = importer.txi_directive_class(directive, values)
            if classification == "unsupported":
                evidence["unsupportedTxiDirectives"][directive] += occurrence_count
                evidence["unsupportedTxiValues"].update(
                    f"{directive}={value}" for value in normalized_values)
                evidence["blockers"]["unsupported_txi_semantic"] += occurrence_count
            elif classification == "unclassified":
                evidence["unclassifiedTxiDirectives"][directive] += occurrence_count
                evidence["unclassifiedTxiValues"].update(
                    f"{directive}={value}" for value in normalized_values)
                evidence["blockers"]["unclassified_txi_directive"] += occurrence_count

    def audit_material(self, evidence: dict[str, Any], mesh: Any) -> None:
        evidence["counts"]["materialSurfaces"] += 1
        diffuse = str(mesh.texture_1 or "").strip()
        lightmap = str(mesh.texture_2 or "").strip()
        diffuse_texture = self.texture(diffuse)
        lightmap_texture = self.texture(lightmap)
        if diffuse and diffuse.lower() != "null":
            evidence["diffuse"].add(diffuse.lower())
            self.count_txi(evidence, diffuse, diffuse_texture)
            if not diffuse_texture["exists"]:
                evidence["blockers"]["missing_diffuse_texture"] += 1
            elif not diffuse_texture["parsed"]:
                evidence["blockers"]["diffuse_texture_parse"] += 1
        if lightmap and lightmap.lower() != "null":
            evidence["lightmaps"].add(lightmap.lower())
            self.count_txi(evidence, lightmap, lightmap_texture)
            if not lightmap_texture["exists"]:
                evidence["gaps"]["missing_lightmap_texture"] += 1
            elif not lightmap_texture["parsed"]:
                evidence["gaps"]["lightmap_texture_parse"] += 1

        directives = diffuse_texture["directives"]
        additive = bool({
            value.lower()
            for value in directives.get("blending", [])
        } & {"1", "additive"})
        if additive:
            evidence["counts"]["sourceUnshadedSurfaces"] += 1
        bump_values = directives.get("bumpmaptexture", [])
        bump = bump_values[-1].split()[0] if bump_values and bump_values[-1].split() else ""
        bump_scale_values = directives.get("bumpmapscaling", [])
        if bump_scale_values:
            try:
                parsed_scales = [float(value.strip()) for value in bump_scale_values]
                valid_scales = (
                    all(math.isfinite(value) for value in parsed_scales) and
                    all(value == parsed_scales[0] for value in parsed_scales[1:]))
            except ValueError:
                valid_scales = False
            if not valid_scales or not bump or bump.lower() == "null":
                evidence["blockers"]["invalid_bump_map_scale_semantic"] += 1
        if bump and bump.lower() != "null":
            evidence["bumpmaps"].add(bump.lower())
            bump_texture = self.texture(bump)
            self.count_txi(evidence, bump, bump_texture)
            if not bump_texture["exists"] or not bump_texture["parsed"]:
                evidence["blockers"]["missing_or_invalid_bump_texture"] += 1

        environment_candidates: list[str] = []
        for directive in ("envmaptexture", "bumpyshinytexture"):
            values = directives.get(directive, [])
            if values and values[-1].split():
                candidate = values[-1].split()[0]
                if candidate.lower() != "null":
                    environment_candidates.append(candidate)
        identities = {candidate.lower() for candidate in environment_candidates}
        if len(identities) > 1:
            evidence["blockers"]["conflicting_environment_map"] += 1
        elif environment_candidates:
            environment = environment_candidates[-1]
            evidence["environmentMaps"].add(environment.lower())
            environment_texture = self.texture(environment)
            self.count_txi(evidence, environment, environment_texture)
            if not environment_texture["exists"] or not environment_texture["cube"]:
                evidence["blockers"]["missing_or_invalid_environment_map"] += 1
            if additive:
                evidence["counts"]["additiveEnvironmentSurfaces"] += 1
                directive_kinds = "+".join(
                    directive for directive in ("envmaptexture", "bumpyshinytexture")
                    if directives.get(directive))
                evidence["mixedMaterialSemantics"][
                    f"additive+environment:{directive_kinds}"] += 1
                evidence["mixedMaterialIdentities"][
                    f"{diffuse.lower()}|{environment.lower()}"] += 1
        if additive and lightmap and lightmap.lower() != "null":
            evidence["counts"]["additiveLightmappedSurfaces"] += 1
            evidence["mixedMaterialSemantics"]["additive+lightmap"] += 1
            evidence["mixedMaterialIdentities"][
                f"{diffuse.lower()}|{lightmap.lower()}"] += 1

    def audit_emitter(self, evidence: dict[str, Any], node: Any) -> None:
        source = node.emitter
        texture_name = str(source.texture or "").strip()
        texture = self.texture(texture_name)
        if texture_name and texture_name.lower() != "null":
            self.count_txi(evidence, texture_name, texture)
        controller_values = importer.room_emitter_controller_values(node)
        point_to_point_target = importer.room_emitter_point_to_point_target(node)
        emitter = {
            "update": str(source.update),
            "render": str(source.render),
            "blend": str(source.blend),
            "flags": int(source.flags),
            "spawnType": int(source.spawn_type),
            "renderOrder": int(source.render_order),
            "frameBlender": int(source.frame_blender),
            "depthTexture": str(source.depth_texture or ""),
            "xGrid": int(source.x_grid),
            "yGrid": int(source.y_grid),
            "pointToPointTargetPosition": point_to_point_target,
            **controller_values,
        }
        evidence["counts"]["emitters"] += 1
        combination = "|".join(
            str(emitter[key]).lower() for key in ("update", "render", "blend"))
        evidence["emitterCombinations"][combination] += 1
        if int(source.flags) & importer.ROOM_EMITTER_COLLISION_BOUNCE_FLAG:
            coefficient = float(emitter["bounceCoefficient"])
            evidence["emitterBounceCoefficients"][
                format(coefficient, ".9g")] += 1
        visual_reasons = importer.room_emitter_visual_safety_reasons(emitter)
        if visual_reasons:
            evidence["emitterVisualSafety"].update(visual_reasons)
        else:
            evidence["emitterVisualSafety"]["validated"] += 1
        reasons = emitter_unsupported_reasons(
            emitter, bool(texture["exists"] and texture["parsed"]))
        if reasons:
            evidence["blockers"]["unsupported_emitter"] += 1
            evidence["emitterReasons"].update(reasons)
            if "render" in reasons:
                if str(source.render).lower() not in importer.SUPPORTED_ROOM_EMITTER_RENDERS:
                    evidence["emitterRenderBlockers"]["render_mode"] += 1
                flag_names = (
                    (0x0004, "wind"),
                    (0x0010, "collision_bounce"),
                    (0x0080, "inherit_parent_velocity"),
                    (0x0200, "collision_splat"),
                    (0x0400, "inherit_particle"),
                    (0x0800, "depth_texture_flag"),
                    (0x1000, "unknown_flag_13"),
                )
                if (int(source.flags) &
                        importer.ROOM_EMITTER_POINT_TO_POINT_FLAG and
                        point_to_point_target is None):
                    evidence["emitterRenderBlockers"][
                        "point_to_point_target"] += 1
                for flag, name in flag_names:
                    if int(source.flags) & flag:
                        evidence["emitterRenderBlockers"][name] += 1
                if int(source.spawn_type) != 0:
                    evidence["emitterRenderBlockers"]["spawn_type"] += 1
                if int(source.frame_blender) != 0:
                    evidence["emitterRenderBlockers"]["frame_blending"] += 1
                depth_texture = str(source.depth_texture or "").strip().lower()
                if depth_texture not in {"", "null"}:
                    evidence["emitterRenderBlockers"]["depth_texture_name"] += 1
                if not (
                    importer.GODOT_RENDER_PRIORITY_MIN <= int(source.render_order) <=
                    importer.GODOT_RENDER_PRIORITY_MAX
                ):
                    evidence["emitterRenderBlockers"]["render_priority"] += 1
        else:
            evidence["counts"]["supportedEmitters"] += 1
            if int(source.flags) & importer.ROOM_EMITTER_POINT_TO_POINT_FLAG:
                evidence["counts"]["pointToPointEmitters"] += 1

    def scan_room(self, model_name: str) -> dict[str, Any]:
        key = model_name.lower()
        if key in self.room_cache:
            return self.room_cache[key]
        evidence = fresh_evidence()
        evidence["roomModels"].add(key)
        if importer.is_source_room_placeholder(model_name):
            evidence["counts"]["sourceRoomPlaceholders"] += 1
            self.room_cache[key] = evidence
            return evidence
        try:
            mdl_resource = self.installation.resource(
                model_name, importer.ResourceType.MDL)
            mdx_resource = self.installation.resource(
                model_name, importer.ResourceType.MDX)
        except Exception:
            evidence["blockers"]["invalid_room_model_resref"] += 1
            evidence["invalidRoomModelResrefs"][repr(model_name)] += 1
            self.room_cache[key] = evidence
            return evidence
        if mdl_resource is None or mdx_resource is None:
            evidence["blockers"]["missing_room_model_pair"] += 1
            self.room_cache[key] = evidence
            return evidence
        try:
            model = importer.read_owned_mdl(
                importer.resource_data(mdl_resource),
                importer.resource_data(mdx_resource))
        except Exception as exc:
            evidence["blockers"]["room_model_parse"] += 1
            evidence["roomModelParseErrors"][
                f"{type(exc).__name__}:{str(exc).strip() or '<empty>'}"] += 1
            evidence["roomModelParseIdentities"][model_name.lower()] += 1
            self.room_cache[key] = evidence
            return evidence

        def visit(node: Any) -> None:
            if node.emitter is not None:
                try:
                    self.audit_emitter(evidence, node)
                except Exception:
                    evidence["blockers"]["emitter_metadata_parse"] += 1
                    evidence["counts"]["emitters"] += 1
                    evidence["emitterReasons"]["lifetime"] += 1
            if node.light is not None:
                evidence["counts"]["lights"] += 1
            mesh = node.mesh
            node_name = str(node.name or "").lower()
            collision_only = node.aabb is not None or node_name.startswith("walkmesh")
            if mesh is not None and collision_only:
                vertices = mesh.vertex_positions
                faces = mesh.faces
                valid = bool(vertices and faces) and all(
                    0 <= index < len(vertices)
                    for face in faces
                    for index in (face.v1, face.v2, face.v3)
                )
                if not valid:
                    evidence["blockers"]["invalid_walkmesh_geometry"] += 1
                else:
                    evidence["counts"]["walkmeshTriangles"] += len(faces)
            if mesh is not None and bool(mesh.render) and not collision_only:
                evidence["counts"]["renderNodes"] += 1
                vertices = mesh.vertex_positions
                faces = mesh.faces
                if not vertices or not faces:
                    evidence["gaps"]["empty_render_node"] += 1
                else:
                    valid = all(
                        0 <= index < len(vertices)
                        for face in faces
                        for index in (face.v1, face.v2, face.v3)
                    )
                    if not valid:
                        evidence["blockers"]["invalid_mesh_indices"] += 1
                    else:
                        evidence["counts"]["validMeshes"] += 1
                        evidence["counts"]["triangles"] += len(faces)
                        if len(mesh.vertex_uv1) != len(vertices):
                            evidence["gaps"]["incomplete_diffuse_uv"] += 1
                        lightmap_name = str(mesh.texture_2 or "").strip()
                        if (lightmap_name and lightmap_name.lower() != "null" and
                                len(mesh.vertex_uv2) != len(vertices)):
                            evidence["gaps"]["incomplete_lightmap_uv"] += 1
                        if int(mesh.transparency_hint) != 0:
                            evidence["gaps"]["unconsumed_mesh_transparency_hint"] += 1
                        if bool(mesh.animate_uv):
                            evidence["gaps"]["unconsumed_animated_uv"] += 1
                        self.audit_material(evidence, mesh)
            for child in node.children:
                visit(child)

        visit(model.root)
        self.room_cache[key] = evidence
        return evidence

    def scan_module(self, module: str) -> dict[str, Any]:
        evidence = fresh_evidence()
        evidence["counts"]["module"] = 1
        try:
            importer.resolve_module_rim_filenames(self.installation, module)
            ifo_resource = importer.find_module_resource(
                self.installation, module, "IFO")
            git_resource = importer.find_module_resource(
                self.installation, module, "GIT")
            are_resource = importer.find_module_resource(
                self.installation, module, "ARE")
            git = importer.read_git(importer.resource_data(git_resource))
            area = importer.read_are(importer.resource_data(are_resource))
            ifo = importer.read_ifo(importer.resource_data(ifo_resource))
            entry = importer.vector3(ifo.entry_position)
            if len(entry) != 3 or any(not math.isfinite(value) for value in entry):
                evidence["blockers"]["invalid_entry_spawn"] += 1
            else:
                evidence["counts"]["entrySpawns"] += 1
            evidence["counts"]["cameras"] += len(git.cameras)
            evidence["counts"]["creatures"] += len(git.creatures)
            evidence["counts"]["doors"] += len(git.doors)
            evidence["counts"]["placeables"] += len(git.placeables)
            evidence["creatureTemplates"].update(
                importer.canonical_resref(creature.resref).casefold()
                for creature in git.creatures)
            area_resref = importer.canonical_resref(ifo.area_name)
            layout_resource = self.installation.resource(
                area_resref, importer.ResourceType.LYT)
            if layout_resource is None:
                raise RuntimeError(f"Layout {area_resref}.lyt could not be resolved")
            layout = importer.read_lyt(importer.resource_data(layout_resource))
            loading_resref, loading_selection, _ = (
                importer.source_loading_background(
                    self.installation, module, area))
        except Exception as exc:
            evidence["core"][type(exc).__name__] += 1
            return evidence
        loading_texture = self.texture(loading_resref)
        if not loading_texture["exists"] or not loading_texture["parsed"]:
            evidence["blockers"]["missing_or_invalid_loading_background"] += 1
        elif loading_selection.startswith("area-loadscreens-row-"):
            evidence["counts"]["areaLoadscreenFallbacks"] += 1
        else:
            evidence["counts"]["moduleLoadingBackgrounds"] += 1
        minimap_texture = self.texture(f"lbl_map{area_resref}")
        if not minimap_texture["exists"]:
            evidence["gaps"]["missing_source_minimap"] += 1
        elif not minimap_texture["parsed"]:
            evidence["blockers"]["source_minimap_parse"] += 1
        else:
            evidence["counts"]["sourceMinimaps"] += 1
        evidence["counts"]["roomPlacements"] += len(layout.rooms)
        for room in layout.rooms:
            merge_evidence(evidence, self.scan_room(str(room.model)))
        return evidence


def module_record(module: str, evidence: dict[str, Any]) -> dict[str, Any]:
    return {
        "module": module,
        "coverageClass": coverage_class(
            evidence["core"], evidence["blockers"], evidence["gaps"]),
        "coreErrors": sorted_counter(evidence["core"]),
        "blockers": sorted_counter(evidence["blockers"]),
        "explicitGaps": sorted_counter(evidence["gaps"]),
        "counts": sorted_counter(evidence["counts"]),
        "emitterUnsupportedReasons": sorted_counter(evidence["emitterReasons"]),
        "emitterSemanticCombinations": sorted_counter(
            evidence["emitterCombinations"]),
        "emitterBounceCoefficients": sorted_counter(
            evidence["emitterBounceCoefficients"]),
        "emitterVisualSafety": sorted_counter(
            evidence["emitterVisualSafety"]),
        "emitterRenderBlockers": sorted_counter(
            evidence["emitterRenderBlockers"]),
        "unsupportedTxiDirectives": sorted_counter(
            evidence["unsupportedTxiDirectives"]),
        "unsupportedTxiValues": sorted_counter(evidence["unsupportedTxiValues"]),
        "unclassifiedTxiDirectives": sorted_counter(
            evidence["unclassifiedTxiDirectives"]),
        "unclassifiedTxiValues": sorted_counter(evidence["unclassifiedTxiValues"]),
        "invalidRoomModelResrefs": sorted_counter(
            evidence["invalidRoomModelResrefs"]),
        "roomModelParseErrors": sorted_counter(evidence["roomModelParseErrors"]),
        "roomModelParseIdentities": sorted_counter(
            evidence["roomModelParseIdentities"]),
        "mixedMaterialSemantics": sorted_counter(
            evidence["mixedMaterialSemantics"]),
        "mixedMaterialIdentities": sorted_counter(
            evidence["mixedMaterialIdentities"]),
        "creatureTemplates": sorted_counter(evidence["creatureTemplates"]),
    }


def fresh_manifest_evidence(manifest_root: Path | None) -> tuple[
    dict[str, list[dict[str, Any]]], int
]:
    by_module: dict[str, list[dict[str, Any]]] = {}
    if manifest_root is None or not manifest_root.is_dir():
        return by_module, 0
    artifacts = 0
    for path in sorted(manifest_root.rglob("module-manifest.json")):
        artifacts += 1
        try:
            manifest = json.loads(path.read_text(encoding="utf-8"))
            module = importer.normalize_module_id(str(manifest["module"]))
            emitters = [
                emitter
                for room in manifest.get("rooms", [])
                for emitter in room.get("emitters", [])
            ]
            creatures = manifest.get("creatures", [])
            creature_models = [
                model
                for creature in creatures
                for model in creature.get("models", [])
            ]
            weapon_models = [
                model for model in creature_models
                if model.get("role") in {"rightWeapon", "leftWeapon"}
            ]
            creature_emitters = [
                emitter for creature in creatures
                for emitter in creature.get("effects", {}).get("emitters", [])
            ]
            creature_lights = [
                light for creature in creatures
                for light in creature.get("effects", {}).get("lights", [])
            ]
            creature_effect_animations = [
                animation for creature in creatures
                for animation in creature.get("effects", {}).get(
                    "animations", [])
            ]
            current_contract = (
                manifest.get("schema") == "nikami-aurora-kotor-module-v1" and
                all(emitter.get("schema") ==
                    "nikami-aurora-kotor-room-emitter-v2"
                    for emitter in emitters) and
                all(
                    creature.get("renderImportSchema") ==
                    "nikami-aurora-kotor-source-creature-v1" and
                    creature.get("renderStatus") in {"ready", "unsupported"} and
                    bool(creature.get("glb")) ==
                    (creature.get("renderStatus") == "ready") and
                    (creature.get("renderStatus") != "ready" or
                     len(creature.get("animation", {}).get(
                         "boundsMinimum", [])) == 3 and
                     len(creature.get("animation", {}).get("extent", [])) == 3)
                    for creature in creatures) and
                all(
                    len(creature.get("models", [])) > 0 and
                    sum(model.get("role") == "body"
                        for model in creature.get("models", [])) == 1
                    for creature in creatures
                    if creature.get("renderStatus") == "ready") and
                all(
                    model.get("renderSurfaces", 0) > 0 and
                    0 <= model.get("additiveSurfaces", -1) <=
                    model.get("renderSurfaces", 0)
                    for model in creature_models) and
                all(
                    creature.get("effects", {}).get("schema") ==
                    "nikami-aurora-kotor-actor-effects-v1" and
                    len(creature.get("effects", {}).get("emitters", [])) ==
                    sum(int(model.get("emitterNodes", 0))
                        for model in creature.get("models", [])) and
                    len(creature.get("effects", {}).get("lights", [])) ==
                    sum(int(model.get("lightNodes", 0))
                        for model in creature.get("models", []))
                    for creature in creatures) and
                all(emitter.get("schema") ==
                    "nikami-aurora-kotor-actor-emitter-v1"
                    for emitter in creature_emitters) and
                all(light.get("schema") ==
                    "nikami-aurora-kotor-actor-light-v1"
                    for light in creature_lights) and
                int(manifest.get("counts", {}).get("renderReadyCreatures", -1)) ==
                sum(creature.get("renderStatus") == "ready"
                    for creature in creatures) and
                int(manifest.get("counts", {}).get("unsupportedCreatures", -1)) ==
                sum(creature.get("renderStatus") != "ready"
                    for creature in creatures) and
                int(manifest.get("counts", {}).get(
                    "authoredCreatureModels", -1)) == len(creature_models) and
                int(manifest.get("counts", {}).get(
                    "equippedWeaponModels", -1)) == len(weapon_models) and
                int(manifest.get("counts", {}).get(
                    "equippedWeaponAdditiveSurfaces", -1)) == sum(
                        int(model["additiveSurfaces"])
                        for model in weapon_models) and
                int(manifest.get("counts", {}).get(
                    "authoredCreatureEmitters", -1)) ==
                    len(creature_emitters) and
                int(manifest.get("counts", {}).get(
                    "authoredCreatureLights", -1)) == len(creature_lights) and
                int(manifest.get("counts", {}).get(
                    "authoredCreatureEffectAnimations", -1)) ==
                    len(creature_effect_animations)
            )
            encounter = manifest.get("firstEncounter")
            if encounter is not None:
                current_contract = current_contract and (
                    encounter.get("effects", {}).get("schema") ==
                    "nikami-aurora-kotor-first-encounter-effects-v2")
            record = {
                "path": path.relative_to(manifest_root).as_posix(),
                "sha256": importer.sha256_file(path),
                "schema": manifest.get("schema", ""),
                "currentImporterContract": current_contract,
                "creaturePresentation": {
                    "expected": int(manifest.get("counts", {}).get(
                        "creatures", len(manifest.get("creatures", [])))),
                    "renderReady": int(manifest.get("counts", {}).get(
                        "renderReadyCreatures", 0)),
                    "unsupported": int(manifest.get("counts", {}).get(
                        "unsupportedCreatures", 0)),
                    "uniqueTemplates": int(manifest.get("counts", {}).get(
                        "uniqueCreatureTemplates", 0)),
                    "modelParts": len(creature_models),
                    "equippedWeapons": len(weapon_models),
                    "weaponAdditiveSurfaces": sum(
                        int(model.get("additiveSurfaces", 0))
                        for model in weapon_models),
                    "effectEmitters": len(creature_emitters),
                    "effectLights": len(creature_lights),
                    "effectAnimations": len(creature_effect_animations),
                },
            }
        except Exception as exc:
            module = f"<invalid:{path.relative_to(manifest_root).as_posix()}>"
            record = {
                "path": path.relative_to(manifest_root).as_posix(),
                "sha256": importer.sha256_file(path),
                "error": f"{type(exc).__name__}:{exc}",
                "currentImporterContract": False,
            }
        by_module.setdefault(module, []).append(record)
    return by_module, artifacts


def runtime_level_evidence(
    runtime_root: Path | None, module: str
) -> dict[str, Any] | None:
    if runtime_root is None:
        return None
    path = runtime_root / module / "runtime.log"
    if not path.is_file():
        return None
    text = path.read_text(encoding="utf-8", errors="replace")
    markers = {
        "boot": f"NIKAMI_AURORA_KOTOR_BOOT status=pass module={module}" in text,
        "roomPbr": f"NIKAMI_AURORA_ROOM_PBR status=ready module={module}" in text,
        "dynamicPbr": f"NIKAMI_AURORA_DYNAMIC_PBR status=ready module={module}" in text,
        "lights": f"NIKAMI_AURORA_LIGHTING status=ready module={module}" in text,
        "emitters": f"NIKAMI_AURORA_ROOM_EMITTERS status=ready module={module}" in text,
        "creatures": f"NIKAMI_AURORA_CREATURES status=ready module={module}" in text,
        "creatureGround": (
            f"NIKAMI_AURORA_CREATURE_GROUND_COVERAGE status=ready module={module}"
            in text),
        "genericPlayability": (
            f"NIKAMI_AURORA_GENERIC_SHOWCASE status=pass module={module}" in text),
        "transition": (
            f"NIKAMI_AURORA_GENERIC_TRANSITION status=pass module={module}" in text),
    }
    return {
        "path": path.relative_to(runtime_root).as_posix(),
        "sha256": importer.sha256_file(path),
        "errorFree": "ERROR:" not in text and "status=fail" not in text,
        "markers": markers,
    }


def level_matrix_record(
    module: str,
    evidence: dict[str, Any],
    manifest_artifacts: list[dict[str, Any]],
    runtime: dict[str, Any] | None,
) -> dict[str, Any]:
    counts = evidence["counts"]
    static_blockers = sorted(
        key for key, count in evidence["blockers"].items() if count)
    static_core = sorted(key for key, count in evidence["core"].items() if count)
    current_manifests = [
        record for record in manifest_artifacts
        if record.get("currentImporterContract") is True
    ]
    runtime_markers = runtime["markers"] if runtime else {}
    blockers = [
        *(f"static-core:{key}" for key in static_core),
        *(f"static:{key}" for key in static_blockers),
    ]
    if not manifest_artifacts:
        blockers.append("missing-fresh-manifest-evidence")
    elif not current_manifests:
        blockers.append("stale-fresh-manifest-contract")
    if runtime is None:
        blockers.append("missing-runtime-evidence")
    else:
        if not runtime["errorFree"]:
            blockers.append("runtime-errors")
        for marker in (
            "boot", "roomPbr", "dynamicPbr", "lights", "emitters",
            "creatures", "creatureGround", "genericPlayability", "transition"):
            if not runtime_markers.get(marker, False):
                blockers.append(f"missing-runtime-marker:{marker}")

    emitter_count = int(counts.get("emitters", 0))
    supported_emitters = int(counts.get("supportedEmitters", 0))
    pbr_eligible = int(counts.get("materialSurfaces", 0)) - int(
        counts.get("sourceUnshadedSurfaces", 0))
    creature_presentation = (
        current_manifests[-1].get("creaturePresentation", {})
        if current_manifests else {})
    unsupported_creatures = int(creature_presentation.get("unsupported", 0))
    if unsupported_creatures:
        blockers.append("manifest:unsupported-creature-render")
    return {
        "module": module,
        "status": "ready" if not blockers else "blocked",
        "blockers": blockers,
        "roomsGeometry": {
            "status": "blocked" if static_core or static_blockers else "static-ready",
            "roomPlacements": int(counts.get("roomPlacements", 0)),
            "roomModels": len(evidence["roomModels"]),
            "validMeshes": int(counts.get("validMeshes", 0)),
            "triangles": int(counts.get("triangles", 0)),
        },
        "strictPbr": {
            "status": "runtime-ready" if (
                runtime_markers.get("roomPbr", False) and
                runtime_markers.get("dynamicPbr", False)) else "runtime-required",
            "renderableSurfaces": int(counts.get("materialSurfaces", 0)),
            "sourceUnshadedSurfaces": int(counts.get("sourceUnshadedSurfaces", 0)),
            "pbrEligibleSurfaces": pbr_eligible,
        },
        "lights": {
            "status": "runtime-ready" if runtime_markers.get("lights", False)
            else "runtime-required",
            "authored": int(counts.get("lights", 0)),
        },
        "emittersFx": {
            "status": "runtime-ready" if runtime_markers.get("emitters", False)
            else ("static-ready" if emitter_count == supported_emitters else "blocked"),
            "authored": emitter_count,
            "supported": supported_emitters,
            "visualSafety": sorted_counter(evidence["emitterVisualSafety"]),
        },
        "walkmeshCameraSpawn": {
            "status": "runtime-ready" if runtime_markers.get("boot", False)
            else "runtime-required",
            "walkmeshTriangles": int(counts.get("walkmeshTriangles", 0)),
            "cameras": int(counts.get("cameras", 0)),
            "entrySpawns": int(counts.get("entrySpawns", 0)),
        },
        "genericPlayabilityTransition": {
            "status": "runtime-ready" if (
                runtime_markers.get("genericPlayability", False) and
                runtime_markers.get("transition", False)) else "blocked",
            "playability": bool(runtime_markers.get("genericPlayability", False)),
            "transition": bool(runtime_markers.get("transition", False)),
        },
        "freshEvidence": {
            "artifacts": manifest_artifacts,
            "currentManifestCount": len(current_manifests),
            "runtime": runtime,
        },
        "creatureGallery": {
            "status": "runtime-gallery-required",
            "expectedPlacements": int(counts.get("creatures", 0)),
            "expectedTemplates": sorted_counter(evidence["creatureTemplates"]),
            "rendered": 0,
            "missing": int(counts.get("creatures", 0)),
            "importReady": int(creature_presentation.get("renderReady", 0)),
            "unsupported": unsupported_creatures,
            "worldFrame": None,
            "contactSheet": None,
            "strictPbr": False,
        },
        "claim": "runtime-and-retail-parity-unproven",
    }


def build_report(
    game_root: Path,
    installation: Any,
    module_ids: Iterable[str],
    manifest_root: Path | None = None,
    runtime_evidence_root: Path | None = None,
) -> dict[str, Any]:
    scanner = StaticScanner(installation)
    aggregate = fresh_evidence()
    records = []
    evidence_by_module: dict[str, dict[str, Any]] = {}
    for module in module_ids:
        evidence = scanner.scan_module(module)
        evidence_by_module[module] = evidence
        merge_evidence(aggregate, evidence)
        records.append(module_record(module, evidence))
    class_counts = Counter(record["coverageClass"] for record in records)
    unique_model_aggregate = fresh_evidence()
    for evidence in scanner.room_cache.values():
        merge_evidence(unique_model_aggregate, evidence)
    blockers = sum(
        1 for record in records
        if record["coverageClass"] in {BLOCKED_CORE, BLOCKED_SEMANTICS})
    status = "complete-with-blockers" if blockers else "complete"
    executable = game_root / "swkotor.exe"
    manifests_by_module, manifest_artifact_count = fresh_manifest_evidence(
        manifest_root)
    level_matrix = [
        level_matrix_record(
            module,
            evidence_by_module[module],
            manifests_by_module.get(module, []),
            runtime_level_evidence(runtime_evidence_root, module),
        )
        for module in sorted(evidence_by_module)
    ]
    matrix_status = Counter(record["status"] for record in level_matrix)
    unique_manifest_modules = sum(
        1 for module in evidence_by_module if manifests_by_module.get(module))
    return {
        "schema": PREFLIGHT_SCHEMA,
        "claim": STATIC_CLAIM,
        "status": status,
        "writesProprietaryOutputs": False,
        "target": {
            "executableSha256": importer.sha256_file(executable),
            "modulePairsAudited": len(records),
        },
        "coverageClasses": sorted_counter(class_counts),
        "levelMatrixSummary": {
            "rows": len(level_matrix),
            "status": sorted_counter(matrix_status),
            "freshManifestArtifacts": manifest_artifact_count,
            "uniqueModulesWithFreshManifest": unique_manifest_modules,
            "missingFreshManifestRows": len(level_matrix) - unique_manifest_modules,
        },
        "levelMatrix": level_matrix,
        "aggregate": {
            "moduleCounts": sorted_counter(aggregate["counts"]),
            "uniqueRoomModelCounts": sorted_counter(unique_model_aggregate["counts"]),
            "uniqueRoomModels": len(scanner.room_cache),
            "uniqueDiffuseTextures": len(aggregate["diffuse"]),
            "uniqueLightmaps": len(aggregate["lightmaps"]),
            "uniqueBumpmaps": len(aggregate["bumpmaps"]),
            "uniqueEnvironmentMaps": len(aggregate["environmentMaps"]),
            "uniqueTexturesProbed": len(scanner.texture_cache),
            "blockers": sorted_counter(aggregate["blockers"]),
            "explicitGaps": sorted_counter(aggregate["gaps"]),
            "emitterUnsupportedReasons": sorted_counter(
                aggregate["emitterReasons"]),
            "emitterSemanticCombinations": sorted_counter(
                aggregate["emitterCombinations"]),
            "emitterBounceCoefficients": sorted_counter(
                aggregate["emitterBounceCoefficients"]),
            "emitterVisualSafety": sorted_counter(
                aggregate["emitterVisualSafety"]),
            "emitterRenderBlockers": sorted_counter(
                aggregate["emitterRenderBlockers"]),
            "txiDirectiveOccurrences": sorted_counter(
                aggregate["txiDirectives"]),
            "txiDirectiveValues": sorted_counter(
                aggregate["txiDirectiveValues"]),
            "bumpMapScaleValues": sorted_counter(
                aggregate["bumpMapScaleValues"]),
            "unsupportedTxiDirectives": sorted_counter(
                aggregate["unsupportedTxiDirectives"]),
            "unsupportedTxiValues": sorted_counter(
                aggregate["unsupportedTxiValues"]),
            "unclassifiedTxiDirectives": sorted_counter(
                aggregate["unclassifiedTxiDirectives"]),
            "unclassifiedTxiValues": sorted_counter(
                aggregate["unclassifiedTxiValues"]),
            "invalidRoomModelResrefs": sorted_counter(
                aggregate["invalidRoomModelResrefs"]),
            "roomModelParseErrors": sorted_counter(
                aggregate["roomModelParseErrors"]),
            "roomModelParseIdentities": sorted_counter(
                aggregate["roomModelParseIdentities"]),
            "mixedMaterialSemantics": sorted_counter(
                aggregate["mixedMaterialSemantics"]),
            "mixedMaterialIdentities": sorted_counter(
                aggregate["mixedMaterialIdentities"]),
        },
        "modules": records,
        "limitations": [
            "Static evidence does not establish runtime acceptance or retail parity.",
            "Creature identities and placements are inventoried, but creature rendering, doors, placeables, dialogue, scripts, music, and runtime navigation require separate evidence.",
            "Unsupported TXI and emitter semantics are reported and are not blanked, replaced, or promoted to supported.",
            "A row remains blocked until its current manifest, error-free runtime boot, exact PBR/light/emitter markers, generic playability, transition, and environment creature gallery are all independently evidenced.",
        ],
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument(
        "--module", action="append", default=[],
        help="Audit one normalized module ID; repeat for multiple modules.")
    parser.add_argument(
        "--require-importable", action="store_true",
        help="Return a nonzero status if any selected module has a hard blocker.")
    parser.add_argument(
        "--manifest-root", type=Path,
        help="Read private module-manifest evidence recursively from this root.")
    parser.add_argument(
        "--runtime-evidence-root", type=Path,
        help="Read <module>/runtime.log evidence recursively from this root.")
    parser.add_argument(
        "--output", type=Path,
        help="Also write the machine-readable report to this explicit path.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        game_root = args.game_root.resolve()
        executable = game_root / "swkotor.exe"
        if not executable.is_file():
            raise RuntimeError(f"KOTOR executable was not found: {executable}")
        installation = importer.Installation(game_root)
        pairs, unpaired = discover_module_pairs(installation.module_path())
        selected = [importer.normalize_module_id(value) for value in args.module]
        if selected:
            missing = sorted(set(selected) - set(pairs))
            if missing:
                raise RuntimeError(f"Selected module RIM pairs were not found: {missing}")
            module_ids = sorted(set(selected))
        else:
            module_ids = sorted(pairs)
        report = build_report(
            game_root,
            installation,
            module_ids,
            args.manifest_root.resolve() if args.manifest_root else None,
            args.runtime_evidence_root.resolve()
            if args.runtime_evidence_root else None,
        )
        report["target"]["discoveredModulePairs"] = len(pairs)
        report["target"]["physicalCaseVariantPairs"] = sum(
            1 for identity, filenames in pairs.items()
            if filenames != (f"{identity}.rim", f"{identity}_s.rim"))
        report["target"]["unpairedModuleIds"] = unpaired
        serialized = json.dumps(report, sort_keys=True, separators=(",", ":"))
        if args.output:
            output_path = args.output.resolve()
            output_path.parent.mkdir(parents=True, exist_ok=True)
            output_path.write_text(serialized + "\n", encoding="utf-8")
            print(
                "NIKAMI_AURORA_KOTOR_LEVEL_MATRIX "
                f"status=written rows={len(report['levelMatrix'])} "
                f"path={output_path} sha256={importer.sha256_file(output_path)}")
        else:
            print(serialized)
        print(
            "NIKAMI_AURORA_KOTOR_PREFLIGHT "
            f"status={report['status']} claim={STATIC_CLAIM} "
            f"pairs={len(module_ids)} writes=0")
        if args.require_importable and report["status"] != "complete":
            return 1
        return 0
    except Exception as exc:
        print(f"KOTOR_PREFLIGHT_FAIL: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
