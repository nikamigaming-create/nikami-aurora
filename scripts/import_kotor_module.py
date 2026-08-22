#!/usr/bin/env python3
"""Import an owned KotOR module into a local Nikami Aurora runtime bundle."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import struct
import subprocess
import sys
from pathlib import Path
from typing import Any

from kotor_actor_gltf import export_actor

try:
    import numpy as np
    import trimesh
    from PIL import Image
    from pykotor.extract.capsule import Capsule
    from pykotor.extract.installation import Installation, SearchLocation
    from pykotor.resource.formats.lip import read_lip
    from pykotor.resource.formats.lyt import read_lyt
    from pykotor.resource.formats.mdl import read_mdl
    from pykotor.resource.formats.mdl.mdl_types import MDLControllerType
    from pykotor.resource.formats.ncs import read_ncs
    from pykotor.resource.formats.tpc import TPCTextureFormat
    from pykotor.resource.formats.twoda import read_2da
    from pykotor.resource.generics.are import read_are
    from pykotor.resource.generics.dlg import DLGEntry, read_dlg
    from pykotor.resource.generics.git import read_git
    from pykotor.resource.generics.ifo import read_ifo
    from pykotor.resource.generics.utc import read_utc
    from pykotor.resource.generics.utd import read_utd
    from pykotor.resource.generics.uti import read_uti
    from pykotor.resource.generics.utp import read_utp
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


def find_named_module_resource(
    installation: Installation, module: str, resname: str, restype: str
) -> Any:
    for filename in (f"{module}.rim", f"{module}_s.rim"):
        for resource in installation.module_resources(filename):
            if (resource_name(resource).lower() == resname.lower() and
                    resource_type_name(resource) == restype):
                return resource
    raise RuntimeError(f"{resname}.{restype.lower()} was not found in module {module}")


def vector3(value: Any) -> list[float]:
    return [float(value.x), float(value.y), float(value.z)]


def color3(value: Any) -> list[float]:
    return [float(value.r), float(value.g), float(value.b)]


def controller_value(node: Any, controller_type: MDLControllerType,
                     fallback: list[float]) -> list[float]:
    """Return the authored value at time zero for a scalar/vector controller."""
    for controller in node.controllers:
        if controller.controller_type == controller_type and controller.rows:
            data = [float(item) for item in controller.rows[0].data]
            if len(data) >= len(fallback):
                return data[:len(fallback)]
    return fallback


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


def camera_vectors(camera: Any) -> tuple[list[float], list[float]]:
    # GFF Orientation stores w,x,y,z. PyKotor's generic Vector4 exposes those
    # file-order values through x,y,z,w respectively.
    file_quaternion = np.asarray([
        float(camera.orientation.x),
        float(camera.orientation.y),
        float(camera.orientation.z),
        float(camera.orientation.w),
    ], dtype=np.float64)
    magnitude = np.linalg.norm(file_quaternion)
    if magnitude <= 1e-12:
        file_quaternion = np.asarray([1.0, 0.0, 0.0, 0.0], dtype=np.float64)
    else:
        file_quaternion /= magnitude
    rotation = (
        trimesh.transformations.quaternion_matrix(file_quaternion)
        @ trimesh.transformations.rotation_matrix(
            math.radians(float(camera.pitch)), [1.0, 0.0, 0.0])
    )
    forward = rotation @ np.asarray([0.0, 0.0, -1.0, 0.0])
    up = rotation @ np.asarray([0.0, 1.0, 0.0, 0.0])
    return (
        [float(item) for item in forward[:3]],
        [float(item) for item in up[:3]],
    )


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
    lightmap_name = str(mesh.texture_2 or "").strip()
    lightmap = textures.image(lightmap_name)
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
    return trimesh.visual.material.PBRMaterial(
        name=texture_name or "untextured",
        baseColorTexture=image,
        baseColorFactor=color,
        emissiveTexture=lightmap,
        emissiveFactor=[1.0, 1.0, 1.0] if lightmap is not None else None,
        metallicFactor=0.0,
        roughnessFactor=1.0,
    )


def patch_glb_texture_channels(data: bytes) -> bytes:
    """Promote trimesh custom UV2 attributes to standard glTF TEXCOORD_1."""
    if data[:4] != b"glTF":
        raise RuntimeError("Expected a binary glTF payload")
    json_length, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A:
        raise RuntimeError("GLB JSON chunk is missing")
    json_start = 20
    json_end = json_start + json_length
    document = json.loads(data[json_start:json_end].decode("utf-8"))
    changed = False
    for mesh in document.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            attributes = primitive.get("attributes", {})
            if "_TEXCOORD_1" in attributes:
                attributes["TEXCOORD_1"] = attributes.pop("_TEXCOORD_1")
                changed = True
    for material in document.get("materials", []):
        if "emissiveTexture" in material:
            material["emissiveTexture"]["texCoord"] = 1
            material["emissiveFactor"] = [1.0, 1.0, 1.0]
            changed = True
    if not changed:
        return data
    encoded = json.dumps(document, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    encoded += b" " * ((4 - len(encoded) % 4) % 4)
    remaining = data[json_end:]
    rebuilt = bytearray()
    rebuilt.extend(b"glTF")
    rebuilt.extend(struct.pack("<II", 2, 12 + 8 + len(encoded) + len(remaining)))
    rebuilt.extend(struct.pack("<II", len(encoded), 0x4E4F534A))
    rebuilt.extend(encoded)
    rebuilt.extend(remaining)
    return bytes(rebuilt)


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
    lights: list[dict[str, Any]] = []
    walkmesh_triangles: list[list[list[float]]] = []

    def visit(node: Any, parent_transform: np.ndarray) -> None:
        nonlocal mesh_count, vertex_count, triangle_count
        world_transform = parent_transform @ quaternion_matrix(node)
        if node.light is not None:
            color = controller_value(node, MDLControllerType.COLOR, color3(node.light.color))
            radius = controller_value(node, MDLControllerType.RADIUS, [float(node.light.radius)])[0]
            multiplier = controller_value(
                node, MDLControllerType.MULTIPLIER, [float(node.light.multiplier)])[0]
            lights.append({
                "name": str(node.name),
                "position": [float(item) for item in world_transform[:3, 3]],
                "color": color,
                "radius": radius,
                "multiplier": multiplier,
                "ambientOnly": bool(node.light.ambient_only),
                "dynamicType": int(node.light.dynamic_type),
                "affectDynamic": bool(node.light.affect_dynamic),
                "shadow": bool(node.light.shadow),
                "priority": int(node.light.light_priority),
            })
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
                vertex_attributes = {}
                if len(mesh.vertex_uv2) == len(vertices) and str(mesh.texture_2 or "").strip():
                    vertex_attributes["_TEXCOORD_1"] = np.asarray(
                        [[float(item.x), float(item.y)] for item in mesh.vertex_uv2], dtype=np.float32)
                geometry = trimesh.Trimesh(
                    vertices=vertices,
                    faces=faces,
                    vertex_normals=normals,
                    visual=visual,
                    process=False,
                    maintain_order=True,
                    vertex_attributes=vertex_attributes,
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
        "lights": lights,
        "walkmeshTriangles": walkmesh_triangles,
    }
    if mesh_count > 0:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        output_path.write_bytes(patch_glb_texture_channels(scene.export(file_type="glb")))
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


def load_model_pair(installation: Installation, model_name: str) -> tuple[Any, bytes, bytes]:
    mdl_resource = installation.resource(model_name, ResourceType.MDL)
    mdx_resource = installation.resource(model_name, ResourceType.MDX)
    if mdl_resource is None or mdx_resource is None:
        raise RuntimeError(f"Missing MDL/MDX pair for {model_name}")
    mdl_bytes = resource_data(mdl_resource)
    mdx_bytes = resource_data(mdx_resource)
    return read_mdl(mdl_bytes, source_ext=mdx_bytes), mdl_bytes, mdx_bytes


def load_animation_supermodel(
    installation: Installation,
    mdlops: Path,
    cache_root: Path,
    model_name: str = "S_Male02",
) -> tuple[Any, str]:
    if not mdlops.is_file():
        raise RuntimeError(
            f"MDLOps was not found: {mdlops}. Run scripts/Bootstrap-MDLOps.ps1 first.")
    mdl_resource = installation.resource(model_name, ResourceType.MDL)
    mdx_resource = installation.resource(model_name, ResourceType.MDX)
    if mdl_resource is None or mdx_resource is None:
        raise RuntimeError(f"Animation supermodel pair was not found: {model_name}")
    mdl_bytes = resource_data(mdl_resource)
    mdx_bytes = resource_data(mdx_resource)
    cache_root.mkdir(parents=True, exist_ok=True)
    stem = model_name.lower()
    mdl_path = cache_root / f"{stem}.mdl"
    mdx_path = cache_root / f"{stem}.mdx"
    ascii_path = cache_root / f"{stem}.mdl.ascii"
    source_hash = sha256_bytes(mdl_bytes + mdx_bytes)
    stamp_path = cache_root / f"{stem}.sha256"
    cached_hash = stamp_path.read_text(encoding="ascii").strip() if stamp_path.is_file() else ""
    if not ascii_path.is_file() or cached_hash != source_hash:
        mdl_path.write_bytes(mdl_bytes)
        mdx_path.write_bytes(mdx_bytes)
        completed = subprocess.run(
            [str(mdlops), "--use-ascii-extension", str(mdl_path)],
            cwd=cache_root,
            check=False,
            capture_output=True,
            text=True,
        )
        if completed.returncode != 0 or not ascii_path.is_file():
            raise RuntimeError(
                f"MDLOps failed for {model_name}: {completed.stdout}\n{completed.stderr}")
        stamp_path.write_text(source_hash + "\n", encoding="ascii")
    return read_mdl(ascii_path), source_hash


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
    mdlops: Path,
    animation_cache: Path,
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

    body, body_mdl, body_mdx = load_model_pair(installation, body_model)
    head = head_mdl = head_mdx = None
    if head_model:
        head, head_mdl, head_mdx = load_model_pair(installation, head_model)
    talk_offset = None
    if head is not None:
        head_hook = find_node_transform(body, "headhook")
        talk_dummy = find_node_transform(head, "talkdummy")
        if head_hook is not None and talk_dummy is not None:
            talk_transform = head_hook @ talk_dummy
            talk_offset = [float(item) for item in talk_transform[:3, 3]]
    right = right_mdl = right_mdx = None
    if right_model:
        right, right_mdl, right_mdx = load_model_pair(installation, right_model)
    animation_model, animation_source_hash = load_animation_supermodel(
        installation, mdlops, animation_cache)
    animation_report = export_actor(
        output_path,
        body_model=body,
        body_name=body_model,
        body_texture=body_texture,
        head_model=head,
        head_name=head_model,
        head_texture=head_texture,
        weapon_model=right,
        weapon_name=right_model,
        animation_model=animation_model,
        animation_names=("pause1", "tlknorm", "walk", "talk"),
        material_factory=lambda mesh, override: material_for(mesh, textures, override),
    )
    model_records = [{
        "model": body_model,
        "overrideTexture": body_texture,
        "mdlSha256": sha256_bytes(body_mdl),
        "mdxSha256": sha256_bytes(body_mdx),
    }]
    if head_model and head_mdl is not None and head_mdx is not None:
        model_records.append({
            "model": head_model,
            "overrideTexture": head_texture,
            "mdlSha256": sha256_bytes(head_mdl),
            "mdxSha256": sha256_bytes(head_mdx),
        })
    if right_model and right_mdl is not None and right_mdx is not None:
        model_records.append({
            "model": right_model,
            "overrideTexture": None,
            "mdlSha256": sha256_bytes(right_mdl),
            "mdxSha256": sha256_bytes(right_mdx),
        })
    return {
        "glb": f"actors/{output_path.name}",
        "conversation": canonical_resref(utc.conversation),
        "utcSha256": sha256_bytes(utc_bytes),
        "models": model_records,
        "animationSource": "S_Male02",
        "animationSourceSha256": animation_source_hash,
        "animation": animation_report,
        "talkOffset": talk_offset,
    }


def export_player_actor(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
    appearance_id: int = 137,
    portrait_id: int = 18,
) -> dict[str, Any]:
    appearance_resource = installation.resource("appearance", ResourceType.TwoDA)
    heads_resource = installation.resource("heads", ResourceType.TwoDA)
    portraits_resource = installation.resource("portraits", ResourceType.TwoDA)
    if appearance_resource is None or heads_resource is None or portraits_resource is None:
        raise RuntimeError("Player appearance tables could not be resolved")
    appearance_bytes = resource_data(appearance_resource)
    heads_bytes = resource_data(heads_resource)
    portraits_bytes = resource_data(portraits_resource)
    appearance = read_2da(appearance_bytes)
    heads = read_2da(heads_bytes)
    portraits = read_2da(portraits_bytes)
    portrait_appearance = int(portraits.get_cell(portrait_id, "appearancenumber"))
    if portrait_appearance != appearance_id:
        raise RuntimeError(
            f"Portrait {portrait_id} resolves appearance {portrait_appearance}, expected {appearance_id}")
    body_name = str(appearance.get_cell(appearance_id, "modela"))
    body_texture_prefix = str(appearance.get_cell(appearance_id, "texa"))
    body_texture = body_texture_prefix
    if installation.texture(body_texture) is None:
        numbered_texture = f"{body_texture_prefix}01"
        if installation.texture(numbered_texture) is None:
            raise RuntimeError(
                f"Player body texture could not be resolved: {body_texture_prefix}")
        body_texture = numbered_texture
    head_index = int(appearance.get_cell(appearance_id, "normalhead"))
    head_name = str(heads.get_cell(head_index, "head"))
    body, body_mdl, body_mdx = load_model_pair(installation, body_name)
    head, head_mdl, head_mdx = load_model_pair(installation, head_name)
    animation_model, animation_source_hash = load_animation_supermodel(
        installation, mdlops, animation_cache)
    animation_report = export_actor(
        output_path,
        body_model=body,
        body_name=body_name,
        body_texture=body_texture,
        head_model=head,
        head_name=head_name,
        head_texture=None,
        weapon_model=None,
        weapon_name=None,
        animation_model=animation_model,
        animation_names=("pause1", "walk", "run"),
        material_factory=lambda mesh, override: material_for(mesh, textures, override),
    )
    head_hook = find_node_transform(body, "headhook")
    talk_dummy = find_node_transform(head, "talkdummy")
    camera_hook = find_node_transform(body, "camerahook")
    talk_offset = None
    if head_hook is not None and talk_dummy is not None:
        talk_transform = head_hook @ talk_dummy
        talk_offset = [float(item) for item in talk_transform[:3, 3]]
    return {
        "schema": "nikami-aurora-kotor-player-v1",
        "glb": f"actors/{output_path.name}",
        "portraitId": portrait_id,
        "appearanceId": appearance_id,
        "appearanceLabel": str(appearance.get_cell(appearance_id, "label")),
        "bodyModel": body_name,
        "bodyTexture": body_texture,
        "headIndex": head_index,
        "headModel": head_name,
        "height": float(appearance.get_cell(appearance_id, "height")),
        "walkDistance": float(appearance.get_cell(appearance_id, "walkdist")),
        "runDistance": float(appearance.get_cell(appearance_id, "rundist")),
        "talkOffset": talk_offset,
        "cameraOffset": (
            [float(item) for item in camera_hook[:3, 3]]
            if camera_hook is not None else None
        ),
        "animationSource": "S_Male02",
        "animationSourceSha256": animation_source_hash,
        "animation": animation_report,
        "appearanceTableSha256": sha256_bytes(appearance_bytes),
        "headsTableSha256": sha256_bytes(heads_bytes),
        "portraitsTableSha256": sha256_bytes(portraits_bytes),
        "models": [
            {
                "model": body_name,
                "overrideTexture": body_texture,
                "mdlSha256": sha256_bytes(body_mdl),
                "mdxSha256": sha256_bytes(body_mdx),
            },
            {
                "model": head_name,
                "overrideTexture": None,
                "mdlSha256": sha256_bytes(head_mdl),
                "mdxSha256": sha256_bytes(head_mdx),
            },
        ],
    }


def export_dialogue(
    installation: Installation,
    dialogue_name: str,
    output_path: Path,
    lip_capsule: Capsule | None,
) -> dict[str, Any]:
    resource = installation.resource(dialogue_name, ResourceType.DLG)
    if resource is None:
        raise RuntimeError(f"{dialogue_name}.dlg could not be resolved")
    data = resource_data(resource)
    dialogue = read_dlg(data)
    talktable = installation.talktable()
    all_nodes = [*dialogue.all_entries(), *dialogue.all_replies()]

    def node_key(node: Any) -> str:
        kind = "entry" if isinstance(node, DLGEntry) else "reply"
        return f"{kind}:{int(node.list_index)}"

    def text_ref(node: Any) -> int:
        return int(node.text.stringref)

    def local_text(node: Any) -> str:
        stringref = text_ref(node)
        return talktable.string(stringref) if stringref >= 0 else ""

    def animation_record(animation: Any) -> dict[str, Any]:
        return {
            "animationId": int(animation.animation_id),
            "participant": str(animation.participant),
        }

    sound_names = {
        canonical_resref(getattr(node, "sound", ""))
        for node in all_nodes
        if canonical_resref(getattr(node, "sound", ""))
    }
    playable_sounds = installation.sounds(
        sound_names,
        [
            SearchLocation.OVERRIDE,
            SearchLocation.VOICE,
            SearchLocation.SOUND,
            SearchLocation.CHITIN,
        ],
    )
    bundle_root = output_path.parent.parent
    media_by_sound: dict[str, dict[str, Any]] = {}
    for sound_name in sorted(sound_names, key=str.lower):
        media: dict[str, Any] = {}
        playable = playable_sounds.get(sound_name)
        if playable:
            if playable.startswith(b"RIFF"):
                extension = "wav"
            elif playable.startswith(b"ID3") or playable[:2] in (b"\xff\xfb", b"\xff\xf3", b"\xff\xf2"):
                extension = "mp3"
            else:
                raise RuntimeError(
                    f"Unsupported playable audio payload for {sound_name}: {playable[:12]!r}")
            audio_relative = f"audio/{sound_name.lower()}.{extension}"
            audio_path = bundle_root / audio_relative
            audio_path.parent.mkdir(parents=True, exist_ok=True)
            audio_path.write_bytes(playable)
            media.update({
                "audioPath": audio_relative,
                "audioFormat": extension,
                "audioSha256": sha256_bytes(playable),
                "audioByteCount": len(playable),
            })
        lip_bytes = (
            lip_capsule.resource(sound_name, ResourceType.LIP)
            if lip_capsule is not None else None
        )
        if lip_bytes:
            lip = read_lip(lip_bytes)
            lip_relative = f"lips/{sound_name.lower()}.json"
            lip_path = bundle_root / lip_relative
            lip_path.parent.mkdir(parents=True, exist_ok=True)
            lip_payload = {
                "schema": "nikami-aurora-kotor-lip-v1",
                "resref": sound_name,
                "sourceSha256": sha256_bytes(lip_bytes),
                "length": float(lip.length),
                "frames": [
                    {"time": float(frame.time), "shape": int(frame.shape)}
                    for frame in lip.frames
                ],
            }
            lip_path.write_text(
                json.dumps(lip_payload, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            media.update({
                "lipPath": lip_relative,
                "lipSourceSha256": lip_payload["sourceSha256"],
                "lipLength": lip_payload["length"],
                "lipFrameCount": len(lip.frames),
            })
        if media:
            media_by_sound[sound_name.lower()] = media

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
    for node in all_nodes:
        key = node_key(node)
        sound_name = canonical_resref(getattr(node, "sound", ""))
        nodes[key] = {
            "kind": "entry" if isinstance(node, DLGEntry) else "reply",
            "listIndex": int(node.list_index),
            "textRef": text_ref(node),
            "text": local_text(node),
            "speaker": str(getattr(node, "speaker", "")),
            "listener": str(getattr(node, "listener", "")),
            "voice": canonical_resref(getattr(node, "vo_resref", "")),
            "sound": sound_name,
            "media": media_by_sound.get(sound_name.lower()) if sound_name else None,
            "cameraAngle": int(getattr(node, "camera_angle", 0)),
            "cameraId": getattr(node, "camera_id", None),
            "cameraFov": getattr(node, "camera_fov", None),
            "cameraHeight": getattr(node, "camera_height", None),
            "animations": [animation_record(item) for item in getattr(node, "animations", [])],
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
    output_path.write_bytes(patch_glb_texture_channels(scene.export(file_type="glb")))
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


def export_opening_locker(
    installation: Installation,
    module: str,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    utp_resource = find_named_module_resource(
        installation, module, "footlker001", "UTP")
    utp_bytes = resource_data(utp_resource)
    utp = read_utp(utp_bytes)
    placeables_resource = installation.resource("placeables", ResourceType.TwoDA)
    if placeables_resource is None:
        raise RuntimeError("placeables.2da could not be resolved")
    placeables = read_2da(resource_data(placeables_resource))
    baseitems_resource = installation.resource("baseitems", ResourceType.TwoDA)
    if baseitems_resource is None:
        raise RuntimeError("baseitems.2da could not be resolved")
    baseitems_bytes = resource_data(baseitems_resource)
    baseitems = read_2da(baseitems_bytes)
    model_name = str(placeables.get_cell(int(utp.appearance_id), "modelname"))
    if not model_name:
        raise RuntimeError("Opening locker model could not be resolved")
    scene = trimesh.Scene(base_frame="end_locker01")
    _, model_record = add_actor_model(
        scene, installation, model_name, textures, np.identity(4))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(patch_glb_texture_channels(scene.export(file_type="glb")))

    item_definitions: dict[str, dict[str, Any]] = {}
    item_stacks: dict[tuple[str, bool, bool], dict[str, Any]] = {}
    for inventory_item in utp.inventory:
        resref = canonical_resref(inventory_item.resref)
        key = (resref.lower(), bool(inventory_item.droppable), bool(inventory_item.infinite))
        if resref.lower() not in item_definitions:
            uti_resource = installation.resource(resref, ResourceType.UTI)
            if uti_resource is None:
                raise RuntimeError(f"Locker item {resref}.uti could not be resolved")
            uti_bytes = resource_data(uti_resource)
            uti = read_uti(uti_bytes)
            base_item = int(uti.base_item)

            def base_cell(column: str) -> str:
                value = str(baseitems.get_cell(base_item, column)).strip()
                return "" if value == "****" else value

            slots_text = base_cell("equipableslots")
            item_definitions[resref.lower()] = {
                "resref": resref,
                "displayName": installation.string(uti.name, resref),
                "tag": str(uti.tag),
                "baseItem": base_item,
                "charges": int(uti.charges),
                "stackSize": int(uti.stack_size),
                "modelVariation": int(uti.model_variation),
                "bodyVariation": int(uti.body_variation),
                "textureVariation": int(uti.texture_variation),
                "equipableSlots": int(slots_text, 0) if slots_text else 0,
                "itemClass": base_cell("itemclass"),
                "modelType": int(base_cell("modeltype") or "0"),
                "defaultModel": base_cell("defaultmodel"),
                "defaultIcon": base_cell("defaulticon"),
                "utiSha256": sha256_bytes(uti_bytes),
            }
        if key not in item_stacks:
            item_stacks[key] = {
                **item_definitions[resref.lower()],
                "quantity": 0,
                "droppable": bool(inventory_item.droppable),
                "infinite": bool(inventory_item.infinite),
            }
        item_stacks[key]["quantity"] += 1

    return {
        "glb": f"placeables/{output_path.name}",
        "model": model_name,
        "tag": str(utp.tag),
        "onInventory": canonical_resref(utp.on_inventory),
        "locked": bool(utp.locked),
        "useable": bool(utp.useable),
        "hasInventory": bool(utp.has_inventory),
        "inventory": list(item_stacks.values()),
        "animationState": int(utp.animation_state),
        "utpSha256": sha256_bytes(utp_bytes),
        "baseItemsSha256": sha256_bytes(baseitems_bytes),
        "modelSource": model_record,
    }


def ncs_signature(instruction: Any) -> tuple[str, tuple[Any, ...]]:
    return instruction.ins_type.name, tuple(instruction.args)


def find_instruction_sequence(
    instructions: list[Any], expected: list[tuple[str, tuple[Any, ...]]]
) -> int | None:
    signatures = [ncs_signature(instruction) for instruction in instructions]
    for start in range(len(signatures) - len(expected) + 1):
        if signatures[start:start + len(expected)] == expected:
            return start
    return None


def export_opening_script_contracts(installation: Installation, plot_table: Any) -> list[dict[str, Any]]:
    plot_rows = {
        str(plot_table.get_cell(index, "label")).lower(): int(plot_table.get_cell(index, "xp"))
        for index in range(plot_table.get_height())
    }
    plot_label = "end_tutorial"
    plot_base_xp = plot_rows[plot_label]

    def load_script(resref: str) -> tuple[bytes, Any]:
        resource = installation.resource(resref, ResourceType.NCS)
        if resource is None:
            raise RuntimeError(f"Opening NCS resource was not found: {resref}")
        data = resource_data(resource)
        return data, read_ncs(data)

    door_data, door_ncs = load_script("k_pend_door1xp")
    door_expected = [
        ("ACTION", (548, 0)),
        ("ACTION", (395, 1)),
        ("CONSTI", (50,)),
        ("EQUALII", ()),
        ("JZ", ()),
        ("CONSTI", (10,)),
        ("CONSTS", (plot_label,)),
        ("ACTION", (714, 2)),
    ]
    if find_instruction_sequence(door_ncs.instructions, door_expected) is None:
        raise RuntimeError("k_pend_door1xp no longer matches the verified XP contract")

    chest_data, chest_ncs = load_script("k_pend_chest02")
    chest_expected = [
        ("ACTION", (548, 0)),
        ("ACTION", (395, 1)),
        ("CONSTI", (0,)),
        ("EQUALII", ()),
        ("JZ", ()),
        ("CONSTI", (5,)),
        ("CONSTS", (plot_label,)),
        ("ACTION", (714, 2)),
    ]
    if find_instruction_sequence(chest_ncs.instructions, chest_expected) is None:
        raise RuntimeError("k_pend_chest02 no longer matches the verified XP contract")

    dialogue_data, dialogue_ncs = load_script("k_pend_traskdl40")
    dialogue_actions = [
        tuple(instruction.args)
        for instruction in dialogue_ncs.instructions
        if instruction.ins_type.name == "ACTION"
    ]
    if dialogue_actions != [
        (200, 2), (43, 1), (6, 2), (205, 0), (200, 2), (22, 3), (206, 0)
    ]:
        raise RuntimeError("k_pend_traskdl40 no longer matches the verified door sequence")

    def xp_contract(resref: str, data: bytes, ncs: Any, required_xp: int,
                    percentage: int) -> dict[str, Any]:
        return {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": resref,
            "kind": "plot-xp-if-player-xp",
            "sourceSha256": sha256_bytes(data),
            "instructionCount": len(ncs.instructions),
            "requiredPlayerXp": required_xp,
            "plotLabel": plot_label,
            "plotPercentage": percentage,
            "plotBaseXp": plot_base_xp,
            "awardedXp": plot_base_xp * percentage // 100,
        }

    return [
        xp_contract("k_pend_chest02", chest_data, chest_ncs, 0, 5),
        xp_contract("k_pend_door1xp", door_data, door_ncs, 50, 10),
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_traskdl40",
            "kind": "dialogue-open-door",
            "sourceSha256": sha256_bytes(dialogue_data),
            "instructionCount": len(dialogue_ncs.instructions),
            "doorTag": "end_door01",
            "pauseConversation": True,
            "moveTargetTag": "",
            "moveRun": True,
            "moveRange": 1.0,
            "resumeConversation": True,
        },
    ]


def import_module(game_root: Path, module: str, output_root: Path, mdlops: Path) -> Path:
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
    are_resource = find_module_resource(installation, module, "ARE")
    ifo = read_ifo(resource_data(ifo_resource))
    git = read_git(resource_data(git_resource))
    are = read_are(resource_data(are_resource))
    camera_style_resource = installation.resource("camerastyle", ResourceType.TwoDA)
    if camera_style_resource is None:
        raise RuntimeError("camerastyle.2da could not be resolved")
    camera_styles = read_2da(resource_data(camera_style_resource))
    dialogue_view_angle = float(camera_styles.get_cell(int(are.camera_style), "viewangle"))
    plot_resource = installation.resource("plot", ResourceType.TwoDA)
    if plot_resource is None:
        raise RuntimeError("plot.2da could not be resolved")
    plot_table = read_2da(resource_data(plot_resource))
    script_contracts = export_opening_script_contracts(installation, plot_table)
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

    trask_actor = export_trask_actor(
        installation,
        output_root / "actors" / "end_trask.glb",
        textures,
        mdlops,
        output_root / "_cache" / "animations",
    )
    player_actor = export_player_actor(
        installation,
        output_root / "actors" / "player.glb",
        textures,
        mdlops,
        output_root / "_cache" / "animations",
    )
    trask_actor["dialogue"] = export_dialogue(
        installation,
        trask_actor["conversation"],
        output_root / "dialogues" / f"{trask_actor['conversation']}.json",
        Capsule(game_root / "lips" / f"{module}_loc.mod")
        if (game_root / "lips" / f"{module}_loc.mod").is_file() else None,
    )
    opening_door = export_opening_door(
        installation, output_root / "doors" / "end_door01.glb", textures)
    opening_locker = export_opening_locker(
        installation, module, output_root / "placeables" / "end_locker01.glb", textures)
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
    placeables = []
    for placeable in git.placeables:
        record = {
            "template": canonical_resref(placeable.resref),
            "tag": str(placeable.tag),
            "position": vector3(placeable.position),
            "bearing": float(placeable.bearing),
        }
        if record["template"].lower() == "footlker001":
            record.update(opening_locker)
        placeables.append(record)
    waypoints = [
        {
            "template": canonical_resref(waypoint.resref),
            "tag": str(waypoint.tag),
            "position": vector3(waypoint.position),
            "bearing": float(waypoint.bearing),
        }
        for waypoint in git.waypoints
    ]
    cameras = []
    for source_camera in git.cameras:
        forward, up = camera_vectors(source_camera)
        cameras.append({
            "id": int(source_camera.camera_id),
            "position": vector3(source_camera.position),
            "height": float(source_camera.height),
            "fov": float(source_camera.fov),
            "pitchDegrees": float(source_camera.pitch),
            "orientationWxyz": [
                float(source_camera.orientation.x),
                float(source_camera.orientation.y),
                float(source_camera.orientation.z),
                float(source_camera.orientation.w),
            ],
            "forward": forward,
            "up": up,
        })
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
        "lighting": {
            "dynamicAmbient": color3(are.dynamic_light),
            "shadows": bool(are.shadows),
            "shadowOpacity": int(are.shadow_opacity),
            "sourceSha256": sha256_bytes(resource_data(are_resource)),
        },
        "cameraStyle": {
            "id": int(are.camera_style),
            "viewAngle": dialogue_view_angle,
            "distance": float(camera_styles.get_cell(int(are.camera_style), "distance")),
            "pitchDegrees": float(camera_styles.get_cell(int(are.camera_style), "pitch")),
            "height": float(camera_styles.get_cell(int(are.camera_style), "height")),
            "sourceSha256": sha256_bytes(resource_data(camera_style_resource)),
        },
        "player": player_actor,
        "rooms": room_records,
        "creatures": creatures,
        "doors": doors,
        "placeables": placeables,
        "waypoints": waypoints,
        "cameras": cameras,
        "scriptContracts": script_contracts,
        "counts": {
            "rooms": len(room_records),
            "creatures": len(creatures),
            "doors": len(doors),
            "waypoints": len(waypoints),
            "cameras": len(git.cameras),
            "placeables": len(git.placeables),
            "triggers": len(git.triggers),
            "walkmeshTriangles": sum(len(room["walkmeshTriangles"]) for room in room_records),
            "authoredLights": sum(len(room["lights"]) for room in room_records),
        },
        "limitations": [
            "Only Trask and the opening door are materialized; other creature and door records remain placements.",
            "Dialogue traversal is partial; scripts, per-node gestures, animated cameras, and shot obstruction remain.",
            "Room lightmaps and light nodes are source-authored; renderer transfer-function parity remains under test.",
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
    parser.add_argument("--mdlops", type=Path, required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        import_module(
            args.game_root.resolve(), args.module.lower(), args.output.resolve(), args.mdlops.resolve())
    except Exception as exc:
        print(f"KOTOR_IMPORT_FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
