"""Source-faithful Odyssey creature skin and animation GLB assembly."""

from __future__ import annotations

import json
import math
import struct
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

import numpy as np
import trimesh
from pykotor.resource.formats.mdl.mdl_types import MDLControllerType


KOTOR_TO_GODOT = trimesh.transformations.rotation_matrix(-math.pi / 2.0, [1.0, 0.0, 0.0])


def _quaternion_matrix_xyzw(value: Any) -> np.ndarray:
    quaternion = np.asarray(
        [float(value.w), float(value.x), float(value.y), float(value.z)], dtype=np.float64)
    magnitude = np.linalg.norm(quaternion)
    if magnitude <= 1e-12:
        quaternion = np.asarray([1.0, 0.0, 0.0, 0.0])
    else:
        quaternion /= magnitude
    return trimesh.transformations.quaternion_matrix(quaternion)


def _node_matrix(node: Any) -> np.ndarray:
    transform = _quaternion_matrix_xyzw(node.orientation)
    transform[:3, 3] = [float(node.position.x), float(node.position.y), float(node.position.z)]
    return transform


def _qbone_inverse_matrix(translation: Any, quaternion: Any) -> np.ndarray:
    # QBone binary order is w,x,y,z. PyKotor's generic Vector4 reader stores
    # those four values in x,y,z,w fields, so reorder deliberately.
    values = np.asarray(
        [float(quaternion.x), float(quaternion.y), float(quaternion.z), float(quaternion.w)],
        dtype=np.float64,
    )
    magnitude = np.linalg.norm(values)
    values = values / magnitude if magnitude > 1e-12 else np.asarray([1.0, 0.0, 0.0, 0.0])
    matrix = trimesh.transformations.quaternion_matrix(values)
    matrix[:3, 3] = [float(translation.x), float(translation.y), float(translation.z)]
    return matrix


@dataclass
class SkinSpec:
    mesh_node: str
    joints: list[str]
    inverse_bind_matrices: list[np.ndarray]


class ActorSceneBuilder:
    def __init__(self, material_factory: Callable[[Any, str | None], Any]):
        self.scene = trimesh.Scene(base_frame="world")
        self.scene.graph.update(
            frame_to="actor_basis", frame_from="world", matrix=KOTOR_TO_GODOT)
        self.material_factory = material_factory
        self.nodes_by_name: dict[str, str] = {}
        self.rest_trs: dict[str, tuple[list[float], list[float], list[float]]] = {}
        self.skin_specs: list[SkinSpec] = []
        self.mesh_count = 0
        self.vertex_count = 0
        self.triangle_count = 0

    def register_model(
        self,
        model: Any,
        model_name: str,
        *,
        attach_parent: str,
        prefix: str = "",
        merge_by_name: bool,
        override_texture: str | None = None,
    ) -> dict[str, str]:
        source_to_scene: dict[int, str] = {}
        source_nodes: list[Any] = []
        parent_by_source: dict[int, Any | None] = {}

        def collect(node: Any, parent: Any | None) -> None:
            source_nodes.append(node)
            parent_by_source[id(node)] = parent
            for child in node.children:
                collect(child, node)

        collect(model.root, None)

        for index, node in enumerate(source_nodes):
            key = str(node.name).lower()
            if merge_by_name and key in self.nodes_by_name:
                source_to_scene[id(node)] = self.nodes_by_name[key]
                continue
            base_name = f"{prefix}{node.name}" if prefix else str(node.name)
            scene_name = base_name
            suffix = 1
            while scene_name in self.rest_trs:
                suffix += 1
                scene_name = f"{base_name}_{suffix}"
            parent = parent_by_source[id(node)]
            parent_scene = attach_parent if parent is None else source_to_scene[id(parent)]
            self.scene.graph.update(
                frame_to=scene_name,
                frame_from=parent_scene,
                matrix=_node_matrix(node),
            )
            source_to_scene[id(node)] = scene_name
            if key not in self.nodes_by_name:
                self.nodes_by_name[key] = scene_name
            self.rest_trs[scene_name] = (
                [float(node.position.x), float(node.position.y), float(node.position.z)],
                [float(node.orientation.x), float(node.orientation.y),
                 float(node.orientation.z), float(node.orientation.w)],
                [1.0, 1.0, 1.0],
            )

        for node_index, node in enumerate(source_nodes):
            mesh = node.mesh
            if mesh is None or not bool(mesh.render) or not mesh.vertex_positions or not mesh.faces:
                continue
            if node.aabb is not None or str(node.name).lower().startswith("walkmesh"):
                continue
            vertices = np.asarray(
                [[float(item.x), float(item.y), float(item.z)] for item in mesh.vertex_positions],
                dtype=np.float32,
            )
            faces = np.asarray([[face.v1, face.v2, face.v3] for face in mesh.faces], dtype=np.int64)
            if not len(vertices) or not len(faces) or faces.min(initial=0) < 0 or faces.max(initial=0) >= len(vertices):
                continue
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
                material=self.material_factory(mesh, override_texture),
            )
            attributes: dict[str, np.ndarray] = {}
            skin = node.skin
            if skin is not None and len(skin.vertex_bones) == len(vertices):
                joints = np.zeros((len(vertices), 4), dtype=np.uint16)
                weights = np.zeros((len(vertices), 4), dtype=np.float32)
                for vertex_index, bone_vertex in enumerate(skin.vertex_bones):
                    for influence in range(4):
                        weight = max(0.0, float(bone_vertex.vertex_weights[influence]))
                        joint = int(bone_vertex.vertex_indices[influence])
                        if weight > 0 and joint >= 0:
                            joints[vertex_index, influence] = joint
                            weights[vertex_index, influence] = weight
                    total = float(weights[vertex_index].sum())
                    if total > 1e-8:
                        weights[vertex_index] /= total
                    else:
                        weights[vertex_index, 0] = 1.0
                attributes["_JOINTS_0"] = joints
                attributes["_WEIGHTS_0"] = weights
            geometry = trimesh.Trimesh(
                vertices=vertices,
                faces=faces,
                vertex_normals=normals,
                visual=visual,
                process=False,
                maintain_order=True,
                vertex_attributes=attributes,
            )
            self.mesh_count += 1
            self.vertex_count += len(vertices)
            self.triangle_count += len(faces)
            geometry_name = f"{model_name}_{node.name}_{self.mesh_count}"
            mesh_node = f"mesh::{geometry_name}"
            self.scene.geometry[geometry_name] = geometry
            self.scene.graph.update(
                frame_to=mesh_node,
                frame_from=source_to_scene[id(node)],
                matrix=np.identity(4),
                geometry=geometry_name,
            )
            if skin is not None and attributes:
                slot_to_source_index = {
                    int(slot): source_index
                    for source_index, slot in enumerate(skin.bonemap)
                    if int(slot) >= 0
                }
                used_slots = sorted({
                    int(index)
                    for bone_vertex in skin.vertex_bones
                    for index, weight in zip(bone_vertex.vertex_indices, bone_vertex.vertex_weights)
                    if float(weight) > 0 and float(index) >= 0
                })
                if used_slots and used_slots != list(range(max(used_slots) + 1)):
                    raise RuntimeError(f"Non-contiguous skin slots in {model_name}:{node.name}: {used_slots}")
                joint_names: list[str] = []
                inverse_matrices: list[np.ndarray] = []
                for slot in used_slots:
                    source_index = slot_to_source_index.get(slot)
                    if source_index is None or source_index >= len(source_nodes):
                        raise RuntimeError(f"Unresolved skin slot {slot} in {model_name}:{node.name}")
                    joint_names.append(source_to_scene[id(source_nodes[source_index])])
                    inverse_matrices.append(_qbone_inverse_matrix(
                        skin.tbones[source_index], skin.qbones[source_index]))
                self.skin_specs.append(SkinSpec(mesh_node, joint_names, inverse_matrices))
        return source_to_scene


def _read_glb(data: bytes) -> tuple[dict[str, Any], bytearray]:
    if data[:4] != b"glTF":
        raise RuntimeError("Expected GLB data")
    json_length, json_type = struct.unpack_from("<II", data, 12)
    if json_type != 0x4E4F534A:
        raise RuntimeError("GLB JSON chunk is missing")
    json_start = 20
    json_end = json_start + json_length
    document = json.loads(data[json_start:json_end].decode("utf-8"))
    bin_length, bin_type = struct.unpack_from("<II", data, json_end)
    if bin_type != 0x004E4942:
        raise RuntimeError("GLB binary chunk is missing")
    binary = bytearray(data[json_end + 8:json_end + 8 + bin_length])
    return document, binary


def _write_glb(document: dict[str, Any], binary: bytearray) -> bytes:
    while len(binary) % 4:
        binary.append(0)
    document.setdefault("buffers", [{"byteLength": 0}])[0]["byteLength"] = len(binary)
    encoded = json.dumps(document, separators=(",", ":"), ensure_ascii=True).encode("utf-8")
    encoded += b" " * ((4 - len(encoded) % 4) % 4)
    total = 12 + 8 + len(encoded) + 8 + len(binary)
    return b"".join([
        b"glTF", struct.pack("<II", 2, total),
        struct.pack("<II", len(encoded), 0x4E4F534A), encoded,
        struct.pack("<II", len(binary), 0x004E4942), bytes(binary),
    ])


def _append_accessor(
    document: dict[str, Any],
    binary: bytearray,
    values: np.ndarray,
    accessor_type: str,
    *,
    include_bounds: bool = False,
) -> int:
    while len(binary) % 4:
        binary.append(0)
    values = np.ascontiguousarray(values)
    offset = len(binary)
    binary.extend(values.tobytes())
    view_index = len(document.setdefault("bufferViews", []))
    document["bufferViews"].append({
        "buffer": 0,
        "byteOffset": offset,
        "byteLength": values.nbytes,
    })
    component_types = {
        np.dtype("float32"): 5126,
        np.dtype("uint16"): 5123,
    }
    accessor: dict[str, Any] = {
        "bufferView": view_index,
        "componentType": component_types[values.dtype],
        "count": int(values.shape[0]),
        "type": accessor_type,
    }
    if include_bounds:
        accessor["min"] = np.min(values, axis=0).reshape(-1).astype(float).tolist()
        accessor["max"] = np.max(values, axis=0).reshape(-1).astype(float).tolist()
    accessor_index = len(document.setdefault("accessors", []))
    document["accessors"].append(accessor)
    return accessor_index


def patch_actor_glb(
    data: bytes,
    builder: ActorSceneBuilder,
    animation_model: Any,
    animation_names: tuple[str, ...],
) -> tuple[bytes, list[str]]:
    document, binary = _read_glb(data)
    node_lookup = {node.get("name", ""): index for index, node in enumerate(document.get("nodes", []))}

    for mesh in document.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            attributes = primitive.get("attributes", {})
            if "_JOINTS_0" in attributes:
                attributes["JOINTS_0"] = attributes.pop("_JOINTS_0")
            if "_WEIGHTS_0" in attributes:
                attributes["WEIGHTS_0"] = attributes.pop("_WEIGHTS_0")

    for scene_name, (translation, rotation, scale) in builder.rest_trs.items():
        node_index = node_lookup.get(scene_name)
        if node_index is None:
            continue
        node = document["nodes"][node_index]
        node.pop("matrix", None)
        node["translation"] = translation
        node["rotation"] = rotation
        node["scale"] = scale

    skins = document.setdefault("skins", [])
    for spec in builder.skin_specs:
        mesh_node_index = node_lookup.get(spec.mesh_node)
        if mesh_node_index is None:
            raise RuntimeError(f"GLB mesh node missing for skin: {spec.mesh_node}")
        joint_indices = [node_lookup[name] for name in spec.joints]
        matrices = np.asarray(
            [matrix.flatten(order="F") for matrix in spec.inverse_bind_matrices], dtype="<f4")
        inverse_accessor = _append_accessor(document, binary, matrices, "MAT4")
        skin_index = len(skins)
        skins.append({
            "name": f"skin::{spec.mesh_node}",
            "joints": joint_indices,
            "inverseBindMatrices": inverse_accessor,
            "skeleton": joint_indices[0] if joint_indices else None,
        })
        document["nodes"][mesh_node_index]["skin"] = skin_index

    exported_animations: list[str] = []
    animations = document.setdefault("animations", [])
    by_name = {animation.name.lower(): animation for animation in animation_model.anims}
    for requested in animation_names:
        animation = by_name.get(requested.lower())
        if animation is None:
            continue
        samplers: list[dict[str, Any]] = []
        channels: list[dict[str, Any]] = []
        for animation_node in animation.all_nodes():
            scene_name = builder.nodes_by_name.get(str(animation_node.name).lower())
            node_index = node_lookup.get(scene_name or "")
            if node_index is None:
                continue
            for controller in animation_node.controllers:
                if not controller.rows:
                    continue
                if controller.controller_type == MDLControllerType.POSITION:
                    path = "translation"
                    output = np.asarray([row.data[:3] for row in controller.rows], dtype="<f4")
                    accessor_type = "VEC3"
                elif controller.controller_type == MDLControllerType.ORIENTATION:
                    path = "rotation"
                    output = np.asarray([row.data[:4] for row in controller.rows], dtype="<f4")
                    for index in range(len(output)):
                        magnitude = float(np.linalg.norm(output[index]))
                        output[index] = output[index] / magnitude if magnitude > 1e-8 else [0, 0, 0, 1]
                        if index > 0 and float(np.dot(output[index - 1], output[index])) < 0:
                            output[index] *= -1
                    accessor_type = "VEC4"
                elif controller.controller_type == MDLControllerType.SCALE:
                    path = "scale"
                    output = np.asarray(
                        [[row.data[0], row.data[0], row.data[0]] for row in controller.rows], dtype="<f4")
                    accessor_type = "VEC3"
                else:
                    continue
                times = np.asarray([[float(row.time)] for row in controller.rows], dtype="<f4")
                time_accessor = _append_accessor(document, binary, times, "SCALAR", include_bounds=True)
                output_accessor = _append_accessor(document, binary, output, accessor_type)
                sampler_index = len(samplers)
                samplers.append({
                    "input": time_accessor,
                    "output": output_accessor,
                    "interpolation": "LINEAR",
                })
                channels.append({
                    "sampler": sampler_index,
                    "target": {"node": node_index, "path": path},
                })
        if channels:
            animations.append({"name": animation.name, "samplers": samplers, "channels": channels})
            exported_animations.append(animation.name)

    return _write_glb(document, binary), exported_animations


def export_actor(
    output_path: Path,
    *,
    body_model: Any,
    body_name: str,
    body_texture: str | None,
    head_model: Any | None,
    head_name: str | None,
    head_texture: str | None,
    weapon_model: Any | None,
    weapon_name: str | None,
    animation_model: Any,
    animation_names: tuple[str, ...],
    material_factory: Callable[[Any, str | None], Any],
    weapon_hook: str = "rhand",
) -> dict[str, Any]:
    builder = ActorSceneBuilder(material_factory)
    head_skin_count = 0
    builder.register_model(
        body_model, body_name, attach_parent="actor_basis", merge_by_name=False,
        override_texture=body_texture)
    if head_model is not None and head_name:
        head_parent = builder.nodes_by_name.get("headhook", "actor_basis")
        skin_count_before_head = len(builder.skin_specs)
        head_nodes = builder.register_model(
            # Heads are hook-attached models with their own skin bind space.
            # Keep that hierarchy intact. Shared supermodel names remain mapped
            # to the body, while head-only neck/face names still receive their
            # inherited animation channels.
            head_model, head_name, attach_parent=head_parent, prefix="head::",
            merge_by_name=False,
            override_texture=head_texture)

        # Unique heads can repeat the body's generic neck/head dummy names.
        # Animation channels below Hturn_g must target the hook-bound head
        # hierarchy, while torso/supermodel channels must stay on the body.
        def prefer_head_animation_nodes(node: Any, in_head: bool = False) -> None:
            key = str(node.name).lower()
            in_head = in_head or key == "hturn_g"
            if in_head:
                builder.nodes_by_name[key] = head_nodes[id(node)]
            for child in node.children:
                prefer_head_animation_nodes(child, in_head)

        prefer_head_animation_nodes(head_model.root)
        head_skin_count = len(builder.skin_specs) - skin_count_before_head
        if head_skin_count <= 0:
            raise RuntimeError(f"Hook-bound head has no skin: {head_name}")
        if not builder.nodes_by_name.get("head_g", "").startswith("head::"):
            raise RuntimeError(f"Hook-bound head animation target was not isolated: {head_name}")
    if weapon_model is not None and weapon_name:
        weapon_parent = builder.nodes_by_name.get(weapon_hook.lower(), "actor_basis")
        if weapon_parent == "actor_basis":
            raise RuntimeError(
                f"Actor weapon hook is missing: {weapon_hook} ({weapon_name})")
        builder.register_model(
            weapon_model, weapon_name, attach_parent=weapon_parent, prefix="weapon::",
            merge_by_name=False)
    raw = builder.scene.export(file_type="glb")
    animated, exported = patch_actor_glb(raw, builder, animation_model, animation_names)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(animated)
    return {
        "meshCount": builder.mesh_count,
        "vertexCount": builder.vertex_count,
        "triangleCount": builder.triangle_count,
        "skinCount": len(builder.skin_specs),
        "headSkinCount": head_skin_count,
        "animations": exported,
    }
