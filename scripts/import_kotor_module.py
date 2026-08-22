#!/usr/bin/env python3
"""Import an owned KotOR module into a local Nikami Aurora runtime bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import sys
from pathlib import Path
from typing import Any

try:
    import numpy as np
    import trimesh
    from PIL import Image
    from pykotor.extract.installation import Installation, SearchLocation
    from pykotor.resource.formats.lyt import read_lyt
    from pykotor.resource.formats.mdl import read_mdl
    from pykotor.resource.formats.tpc import TPCTextureFormat
    from pykotor.resource.formats.twoda import read_2da
    from pykotor.resource.generics.dlg import DLGEntry, read_dlg
    from pykotor.resource.generics.git import read_git
    from pykotor.resource.generics.ifo import read_ifo
    from pykotor.resource.generics.utc import read_utc
    from pykotor.resource.generics.utd import read_utd
    from pykotor.resource.type import ResourceType
    from pykotor.tools import creature as creature_tools
    from pykotor.tools import door as door_tools
    from utility.common.geometry import SurfaceMaterial
except ImportError as exc:
    raise SystemExit(
        "Missing importer dependency. Install requirements-import.txt with Python 3.12."
    ) from exc


SCHEMA = "nikami-aurora-kotor-module-v1"
KOTOR_TO_GODOT = trimesh.transformations.rotation_matrix(-math.pi / 2.0, [1.0, 0.0, 0.0])


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def canonical_resref(value: Any) -> str:
    return str(value).strip()


def resource_data(resource: Any) -> bytes:
    value = resource.data
    if callable(value):
        value = value()
    return bytes(value)


def resource_name(resource: Any) -> str:
    value = resource.resname
    if callable(value):
        value = value()
    return str(value)


def resource_type_name(resource: Any) -> str:
    value = resource.restype
    if callable(value):
        value = value()
    return str(value).upper()


def find_module_resource(installation: Installation, module: str, restype: str) -> Any:
    for filename in (f"{module}.rim", f"{module}_s.rim"):
        for resource in installation.module_resources(filename):
            if resource_type_name(resource) == restype:
                return resource
    raise RuntimeError(f"{restype} resource was not found for module {module}")


def vector3(value: Any) -> list[float]:
    return [float(value.x), float(value.y), float(value.z)]


def quaternion_matrix(node: Any) -> np.ndarray:
    orientation = node.orientation
    quaternion = np.asarray(
        [float(orientation.w), float(orientation.x), float(orientation.y), float(orientation.z)],
        dtype=np.float64,
    )
    magnitude = np.linalg.norm(quaternion)
    if magnitude <= 1e-12:
        quaternion = np.asarray([1.0, 0.0, 0.0, 0.0], dtype=np.float64)
    else:
        quaternion /= magnitude
    transform = trimesh.transformations.quaternion_matrix(quaternion)
    transform[:3, 3] = np.asarray(vector3(node.position), dtype=np.float64)
    return transform


class TextureCache:
    def __init__(self, installation: Installation):
        self.installation = installation
        self.images: dict[str, Image.Image | None] = {}

    def image(self, name: str) -> Image.Image | None:
        key = name.strip().lower()
        if not key or key == "null":
            return None
        if key in self.images:
            return self.images[key]

        texture = self.installation.texture(name)
        if texture is None:
            self.images[key] = None
            return None
        texture.convert(TPCTextureFormat.RGBA)
        mipmap = texture.get()
        image = Image.frombytes("RGBA", (mipmap.width, mipmap.height), bytes(mipmap.data))
        image = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        self.images[key] = image
        return image


def material_for(mesh: Any, textures: TextureCache, override_texture: str | None = None) -> Any:
    texture_name = str(override_texture or mesh.texture_1 or "").strip()
    image = textures.image(texture_name)
    diffuse = mesh.diffuse
    color = [
        max(0, min(255, round(float(diffuse.r) * 255))),
        max(0, min(255, round(float(diffuse.g) * 255))),
        max(0, min(255, round(float(diffuse.b) * 255))),
        255,
    ]
    if image is not None:
        # Static room materials expect the retail lightmap pass. For this
        # diffuse-only proof, preserve the authored texture without multiplying
        # it by a dark pre-lighting material factor.
        color = [255, 255, 255, 255]
    return trimesh.visual.material.SimpleMaterial(
        image=image,
        diffuse=color,
        name=texture_name or "untextured",
    )


def export_room(
    installation: Installation,
    model_name: str,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    mdl_resource = installation.resource(model_name, ResourceType.MDL)
    mdx_resource = installation.resource(model_name, ResourceType.MDX)
    if mdl_resource is None or mdx_resource is None:
        raise RuntimeError(f"Missing MDL/MDX pair for room model {model_name}")

    mdl_bytes = resource_data(mdl_resource)
    mdx_bytes = resource_data(mdx_resource)
    model = read_mdl(mdl_bytes, source_ext=mdx_bytes)
    scene = trimesh.Scene(base_frame="kotor_model")
    mesh_count = 0
    vertex_count = 0
    triangle_count = 0
    diffuse_textures: set[str] = set()
    lightmaps: set[str] = set()
    walkmesh_triangles: list[list[list[float]]] = []

    def visit(node: Any, parent_transform: np.ndarray) -> None:
        nonlocal mesh_count, vertex_count, triangle_count
        world_transform = parent_transform @ quaternion_matrix(node)
        mesh = node.mesh
        node_name = str(node.name or "").lower()
        collision_only = node.aabb is not None or node_name.startswith("walkmesh")
        if collision_only and mesh is not None and mesh.vertex_positions and mesh.faces:
            local_vertices = [
                world_transform @ np.asarray(
                    [float(vertex.x), float(vertex.y), float(vertex.z), 1.0], dtype=np.float64)
                for vertex in mesh.vertex_positions
            ]
            for face in mesh.faces:
                material_id = int(face.material) & 0x1F
                try:
                    walkable = SurfaceMaterial(material_id).walkable()
                except ValueError:
                    walkable = False
                indices = (face.v1, face.v2, face.v3)
                if walkable and all(0 <= index < len(local_vertices) for index in indices):
                    walkmesh_triangles.append([
                        [float(local_vertices[index][0]), float(local_vertices[index][1]),
                         float(local_vertices[index][2])]
                        for index in indices
                    ])
        if (mesh is not None and bool(mesh.render) and not collision_only and
                mesh.vertex_positions and mesh.faces):
            vertices = np.asarray(
                [[float(vertex.x), float(vertex.y), float(vertex.z)] for vertex in mesh.vertex_positions],
                dtype=np.float32,
            )
            faces = np.asarray([[face.v1, face.v2, face.v3] for face in mesh.faces], dtype=np.int64)
            valid = bool(
                len(vertices)
                and len(faces)
                and faces.min(initial=0) >= 0
                and faces.max(initial=0) < len(vertices)
            )
            if valid:
                normals = None
                if len(mesh.vertex_normals) == len(vertices):
                    normals = np.asarray(
                        [[float(normal.x), float(normal.y), float(normal.z)] for normal in mesh.vertex_normals],
                        dtype=np.float32,
                    )
                uv = None
                if len(mesh.vertex_uv1) == len(vertices):
                    uv = np.asarray([[float(item.x), float(item.y)] for item in mesh.vertex_uv1], dtype=np.float32)
                visual = trimesh.visual.texture.TextureVisuals(
                    uv=uv,
                    material=material_for(mesh, textures),
                )
                geometry = trimesh.Trimesh(
                    vertices=vertices,
                    faces=faces,
                    vertex_normals=normals,
                    visual=visual,
                    process=False,
                    maintain_order=True,
                )
                mesh_count += 1
                vertex_count += len(vertices)
                triangle_count += len(faces)
                texture_name = str(mesh.texture_1 or "").strip()
                lightmap_name = str(mesh.texture_2 or "").strip()
                if texture_name and texture_name.lower() != "null":
                    diffuse_textures.add(texture_name)
                if lightmap_name and lightmap_name.lower() != "null":
                    lightmaps.add(lightmap_name)
                scene.add_geometry(
                    geometry,
                    node_name=f"{node.name}_{mesh_count}",
                    geom_name=f"{model_name}_{mesh_count}",
                    transform=KOTOR_TO_GODOT @ world_transform,
                )
        for child in node.children:
            visit(child, world_transform)

    visit(model.root, np.identity(4, dtype=np.float64))
    record = {
        "model": model_name,
        "glb": None,
        "mdlSha256": sha256_bytes(mdl_bytes),
        "mdxSha256": sha256_bytes(mdx_bytes),
        "meshCount": mesh_count,
        "vertexCount": vertex_count,
        "triangleCount": triangle_count,
        "diffuseTextures": sorted(diffuse_textures, key=str.lower),
        "lightmaps": sorted(lightmaps, key=str.lower),
        "walkmeshTriangles": walkmesh_triangles,
    }
    if mesh_count > 0:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_bytes(scene.export(file_type="glb"))
        record["glb"] = output_path.as_posix()
    return record


def find_node_transform(model: Any, target_name: str) -> np.ndarray | None:
    def visit(node: Any, parent_transform: np.ndarray) -> np.ndarray | None:
        world_transform = parent_transform @ quaternion_matrix(node)
        if str(node.name).lower() == target_name.lower():
            return world_transform
        for child in node.children:
            found = visit(child, world_transform)
            if found is not None:
                return found
        return None

    return visit(model.root, np.identity(4, dtype=np.float64))


def add_actor_model(
    scene: trimesh.Scene,
    installation: Installation,
    model_name: str,
    textures: TextureCache,
    base_transform: np.ndarray,
    override_texture: str | None = None,
) -> tuple[Any, dict[str, Any]]:
    mdl_resource = installation.resource(model_name, ResourceType.MDL)
    mdx_resource = installation.resource(model_name, ResourceType.MDX)
    if mdl_resource is None or mdx_resource is None:
        raise RuntimeError(f"Missing actor MDL/MDX pair for {model_name}")
    mdl_bytes = resource_data(mdl_resource)
    mdx_bytes = resource_data(mdx_resource)
    model = read_mdl(mdl_bytes, source_ext=mdx_bytes)
    mesh_count = 0
    vertex_count = 0
    triangle_count = 0

    def visit(node: Any, parent_transform: np.ndarray) -> None:
        nonlocal mesh_count, vertex_count, triangle_count
        world_transform = parent_transform @ quaternion_matrix(node)
        mesh = node.mesh
        node_name = str(node.name or "").lower()
        collision_only = node.aabb is not None or node_name.startswith("walkmesh")
        if (mesh is not None and bool(mesh.render) and not collision_only and
                mesh.vertex_positions and mesh.faces):
            vertices = np.asarray(
                [[float(vertex.x), float(vertex.y), float(vertex.z)] for vertex in mesh.vertex_positions],
                dtype=np.float32,
            )
            faces = np.asarray([[face.v1, face.v2, face.v3] for face in mesh.faces], dtype=np.int64)
            if len(vertices) and len(faces) and faces.min(initial=0) >= 0 and faces.max(initial=0) < len(vertices):
                normals = None
                if len(mesh.vertex_normals) == len(vertices):
                    normals = np.asarray(
                        [[float(item.x), float(item.y), float(item.z)] for item in mesh.vertex_normals],
                        dtype=np.float32,
                    )
                uv = None
                if len(mesh.vertex_uv1) == len(vertices):
                    uv = np.asarray([[float(item.x), float(item.y)] for item in mesh.vertex_uv1], dtype=np.float32)
                visual = trimesh.visual.texture.TextureVisuals(
                    uv=uv,
                    material=material_for(mesh, textures, override_texture),
                )
                geometry = trimesh.Trimesh(
                    vertices=vertices,
                    faces=faces,
                    vertex_normals=normals,
                    visual=visual,
                    process=False,
                    maintain_order=True,
                )
                mesh_count += 1
                vertex_count += len(vertices)
                triangle_count += len(faces)
                scene.add_geometry(
                    geometry,
                    node_name=f"{model_name}_{node.name}_{mesh_count}",
                    geom_name=f"{model_name}_{mesh_count}",
                    transform=KOTOR_TO_GODOT @ base_transform @ world_transform,
                )
        for child in node.children:
            visit(child, world_transform)

    visit(model.root, np.identity(4, dtype=np.float64))
    return model, {
        "model": model_name,
        "overrideTexture": override_texture,
        "mdlSha256": sha256_bytes(mdl_bytes),
        "mdxSha256": sha256_bytes(mdx_bytes),
        "meshCount": mesh_count,
        "vertexCount": vertex_count,
        "triangleCount": triangle_count,
    }


def export_trask_actor(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    utc_resource = installation.resource("end_trask", ResourceType.UTC)
    if utc_resource is None:
        raise RuntimeError("end_trask.utc could not be resolved")
    utc_bytes = resource_data(utc_resource)
    utc = read_utc(utc_bytes)
    order = [SearchLocation.OVERRIDE, SearchLocation.CHITIN]

    def table(name: str) -> Any:
        resource = installation.resource(name, ResourceType.TwoDA, order)
        if resource is None:
            raise RuntimeError(f"{name}.2da could not be resolved")
        return read_2da(resource_data(resource))

    appearance = table("appearance")
    heads = table("heads")
    baseitems = table("baseitems")
    body_model, body_texture = creature_tools.get_body_model(
        utc, installation, appearance=appearance, baseitems=baseitems)
    head_model, head_texture = creature_tools.get_head_model(
        utc, installation, appearance=appearance, heads=heads)
    right_model, left_model = creature_tools.get_weapon_models(
        utc, installation, appearance=appearance, baseitems=baseitems)
    if not body_model:
        raise RuntimeError("Trask body model could not be resolved")

    scene = trimesh.Scene(base_frame="end_trask")
    body, body_record = add_actor_model(
        scene, installation, body_model, textures, np.identity(4), body_texture)
    model_records = [body_record]
    head_hook = find_node_transform(body, "headhook")
    if head_model and head_hook is not None:
        _, record = add_actor_model(scene, installation, head_model, textures, head_hook, head_texture)
        model_records.append(record)
    right_hook = find_node_transform(body, "rhand")
    if right_model and right_hook is not None:
        _, record = add_actor_model(scene, installation, right_model, textures, right_hook)
        model_records.append(record)
    left_hook = find_node_transform(body, "lhand")
    if left_model and left_hook is not None:
        _, record = add_actor_model(scene, installation, left_model, textures, left_hook)
        model_records.append(record)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(scene.export(file_type="glb"))
    return {
        "glb": f"actors/{output_path.name}",
        "conversation": canonical_resref(utc.conversation),
        "utcSha256": sha256_bytes(utc_bytes),
        "models": model_records,
    }


def export_dialogue(
    installation: Installation,
    dialogue_name: str,
    output_path: Path,
) -> dict[str, Any]:
    resource = installation.resource(dialogue_name, ResourceType.DLG)
    if resource is None:
        raise RuntimeError(f"{dialogue_name}.dlg could not be resolved")
    data = resource_data(resource)
    dialogue = read_dlg(data)
    talktable = installation.talktable()

    def node_key(node: Any) -> str:
        kind = "entry" if isinstance(node, DLGEntry) else "reply"
        return f"{kind}:{int(node.list_index)}"

    def text_ref(node: Any) -> int:
        return int(node.text.stringref)

    def local_text(node: Any) -> str:
        stringref = text_ref(node)
        return talktable.string(stringref) if stringref >= 0 else ""

    def link_record(link: Any) -> dict[str, Any]:
        return {
            "target": node_key(link.node),
            "condition1": canonical_resref(link.active1),
            "condition1Not": bool(link.active1_not),
            "condition2": canonical_resref(link.active2),
            "condition2Not": bool(link.active2_not),
            "logic": int(link.logic),
        }

    nodes: dict[str, dict[str, Any]] = {}
    for node in [*dialogue.all_entries(), *dialogue.all_replies()]:
        key = node_key(node)
        nodes[key] = {
            "kind": "entry" if isinstance(node, DLGEntry) else "reply",
            "listIndex": int(node.list_index),
            "textRef": text_ref(node),
            "text": local_text(node),
            "speaker": str(getattr(node, "speaker", "")),
            "listener": str(getattr(node, "listener", "")),
            "voice": canonical_resref(getattr(node, "vo_resref", "")),
            "sound": canonical_resref(getattr(node, "sound", "")),
            "script1": canonical_resref(getattr(node, "script1", "")),
            "script2": canonical_resref(getattr(node, "script2", "")),
            "links": [link_record(link) for link in node.links],
        }

    starters = []
    for index, link in enumerate(dialogue.starters):
        record = link_record(link)
        record["index"] = index
        starters.append(record)
    graph = {
        "schema": "nikami-aurora-kotor-dialogue-v1",
        "resref": dialogue_name,
        "sourceSha256": sha256_bytes(data),
        "openingStarter": 0 if starters else -1,
        "starters": starters,
        "nodes": nodes,
    }
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_text(json.dumps(graph, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return {
        "path": f"dialogues/{output_path.name}",
        "sourceSha256": graph["sourceSha256"],
        "starterCount": len(starters),
        "nodeCount": len(nodes),
        "openingStarter": graph["openingStarter"],
    }


def export_opening_door(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    utd_resource = installation.resource("sw_door_test001", ResourceType.UTD)
    if utd_resource is None:
        raise RuntimeError("sw_door_test001.utd could not be resolved")
    utd_bytes = resource_data(utd_resource)
    utd = read_utd(utd_bytes)
    order = [SearchLocation.OVERRIDE, SearchLocation.CHITIN]
    genericdoors_resource = installation.resource("genericdoors", ResourceType.TwoDA, order)
    if genericdoors_resource is None:
        raise RuntimeError("genericdoors.2da could not be resolved")
    genericdoors = read_2da(resource_data(genericdoors_resource))
    model_name = door_tools.get_model(utd, installation, genericdoors=genericdoors)
    if not model_name:
        raise RuntimeError("Opening door model could not be resolved")
    scene = trimesh.Scene(base_frame="end_door01")
    _, model_record = add_actor_model(
        scene, installation, model_name, textures, np.identity(4))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(scene.export(file_type="glb"))
    return {
        "glb": f"doors/{output_path.name}",
        "model": model_name,
        "conversation": canonical_resref(utd.conversation),
        "onOpen": canonical_resref(utd.on_open),
        "locked": bool(utd.locked),
        "keyRequired": bool(utd.key_required),
        "utdSha256": sha256_bytes(utd_bytes),
        "modelSource": model_record,
    }


def import_module(game_root: Path, module: str, output_root: Path) -> Path:
    executable = game_root / "swkotor.exe"
    if not executable.is_file():
        raise RuntimeError(f"KOTOR executable not found: {executable}")
    base_rim = game_root / "modules" / f"{module}.rim"
    story_rim = game_root / "modules" / f"{module}_s.rim"
    if not base_rim.is_file() or not story_rim.is_file():
        raise RuntimeError(f"Module RIM pair not found for {module}")

    installation = Installation(game_root)
    ifo_resource = find_module_resource(installation, module, "IFO")
    git_resource = find_module_resource(installation, module, "GIT")
    ifo = read_ifo(resource_data(ifo_resource))
    git = read_git(resource_data(git_resource))
    area_resref = canonical_resref(ifo.area_name)
    lyt_resource = installation.resource(area_resref, ResourceType.LYT)
    if lyt_resource is None:
        raise RuntimeError(f"Layout {area_resref}.lyt could not be resolved")
    lyt_bytes = resource_data(lyt_resource)
    layout = read_lyt(lyt_bytes)

    rooms_root = output_root / "rooms"
    textures = TextureCache(installation)
    room_records: list[dict[str, Any]] = []
    for index, room in enumerate(layout.rooms, start=1):
        model_name = str(room.model)
        filename = f"{model_name.lower()}.glb"
        print(f"[{index:02d}/{len(layout.rooms):02d}] exporting {model_name}")
        record = export_room(installation, model_name, rooms_root / filename, textures)
        if record["glb"] is not None:
            record["glb"] = f"rooms/{filename}"
        record["position"] = vector3(room.position)
        room_records.append(record)

    trask_actor = export_trask_actor(installation, output_root / "actors" / "end_trask.glb", textures)
    trask_actor["dialogue"] = export_dialogue(
        installation,
        trask_actor["conversation"],
        output_root / "dialogues" / f"{trask_actor['conversation']}.json",
    )
    opening_door = export_opening_door(
        installation, output_root / "doors" / "end_door01.glb", textures)
    creatures = []
    for creature in git.creatures:
        record = {
            "template": canonical_resref(creature.resref),
            "position": vector3(creature.position),
            "bearing": float(creature.bearing),
        }
        if record["template"].lower() == "end_trask":
            record.update(trask_actor)
        creatures.append(record)
    doors = []
    for door in git.doors:
        record = {
            "template": canonical_resref(door.resref),
            "tag": str(door.tag),
            "position": vector3(door.position),
            "bearing": float(door.bearing),
            "linkedToModule": canonical_resref(door.linked_to_module),
        }
        if record["tag"].lower() == "end_door01":
            record.update(opening_door)
        doors.append(record)
    waypoints = [
        {
            "template": canonical_resref(waypoint.resref),
            "tag": str(waypoint.tag),
            "position": vector3(waypoint.position),
            "bearing": float(waypoint.bearing),
        }
        for waypoint in git.waypoints
    ]
    manifest = {
        "schema": SCHEMA,
        "profileId": "kotor",
        "engineFamily": "Odyssey",
        "module": module,
        "areaResRef": area_resref,
        "target": {
            "executableSha256": sha256_file(executable),
            "moduleRimSha256": sha256_file(base_rim),
            "storyRimSha256": sha256_file(story_rim),
            "layoutSha256": sha256_bytes(lyt_bytes),
            "gitSha256": sha256_bytes(resource_data(git_resource)),
            "ifoSha256": sha256_bytes(resource_data(ifo_resource)),
        },
        "entry": {
            "position": vector3(ifo.entry_position),
            "directionRadians": float(ifo.entry_direction),
        },
        "rooms": room_records,
        "creatures": creatures,
        "doors": doors,
        "waypoints": waypoints,
        "counts": {
            "rooms": len(room_records),
            "creatures": len(creatures),
            "doors": len(doors),
            "waypoints": len(waypoints),
            "cameras": len(git.cameras),
            "placeables": len(git.placeables),
            "triggers": len(git.triggers),
            "walkmeshTriangles": sum(len(room["walkmeshTriangles"]) for room in room_records),
        },
        "limitations": [
            "Diffuse textures are embedded; authored lightmaps are inventoried but not yet applied.",
            "Creature markers represent exact authored placements; creature models are not yet materialized.",
            "Dialogue, NCS execution, doors, collision, audio, and cinematics are not yet executed.",
        ],
    }
    output_root.mkdir(parents=True, exist_ok=True)
    manifest_path = output_root / "module-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(
        f"Imported {module}: rooms={len(room_records)} creatures={len(creatures)} "
        f"triangles={sum(room['triangleCount'] for room in room_records)}"
    )
    print(f"Manifest: {manifest_path}")
    return manifest_path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument("--module", default="end_m01aa")
    parser.add_argument("--output", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        import_module(args.game_root.resolve(), args.module.lower(), args.output.resolve())
    except Exception as exc:
        print(f"KOTOR_IMPORT_FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
