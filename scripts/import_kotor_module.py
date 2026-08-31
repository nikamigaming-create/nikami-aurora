#!/usr/bin/env python3
"""Import an owned KotOR module into a local Nikami Aurora runtime bundle."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import math
import struct
import subprocess
import sys
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from kotor_actor_gltf import export_actor
from kotor_audio import normalize_wav_for_godot

try:
    import numpy as np
    import trimesh
    from PIL import Image
    from pykotor.extract.capsule import Capsule
    from pykotor.extract.installation import Installation, SearchLocation
    from pykotor.common.stream import BinaryReader
    from pykotor.resource.formats.lip import read_lip
    from pykotor.resource.formats.lyt import read_lyt
    from pykotor.resource.formats.mdl import read_mdl
    from pykotor.resource.formats.mdl.io_mdl import MDLBinaryReader
    from pykotor.resource.formats.mdl.mdl_types import MDLControllerType
    from pykotor.resource.formats.ncs import read_ncs
    from pykotor.resource.formats.gff import read_gff
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
    from pykotor.resource.generics.utt import read_utt
    from pykotor.resource.type import ResourceType
    from pykotor.tools import creature as creature_tools
    from pykotor.tools import door as door_tools
    from utility.common.geometry import SurfaceMaterial
except ImportError as exc:
    raise SystemExit(
        "Missing importer dependency. Install requirements-import.txt with Python 3.12."
    ) from exc


SCHEMA = "nikami-aurora-kotor-module-v1"
ENDAR_MODULE = "end_m01aa"
SOURCE_ROOM_PLACEHOLDER = "****"
GENERIC_WORLD_MODE = "generic-world"
ENDAR_OPENING_MODE = "endar-opening"
KOTOR_TO_GODOT = trimesh.transformations.rotation_matrix(-math.pi / 2.0, [1.0, 0.0, 0.0])


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def normalize_module_id(value: str) -> str:
    module = value.strip().lower()
    if (not module or len(module) > 16 or
            any(not character.isascii() or
                (not character.isalnum() and character != "_")
                for character in module)):
        raise RuntimeError(f"Unsupported KOTOR module identifier: {value}")
    return module


def module_content_mode(module: str) -> str:
    return ENDAR_OPENING_MODE if normalize_module_id(module) == ENDAR_MODULE else GENERIC_WORLD_MODE


def is_source_room_placeholder(model_name: str) -> bool:
    return model_name.strip() == SOURCE_ROOM_PLACEHOLDER


def resolve_module_rim_filenames(
    installation: Installation, module: str
) -> tuple[str, str]:
    """Resolve physical RIM filename case without changing manifest identity."""
    normalized = normalize_module_id(module)
    expected = (f"{normalized}.rim", f"{normalized}_s.rim")
    matches: dict[str, list[str]] = {name: [] for name in expected}
    module_root = installation.module_path()
    if not module_root.is_dir():
        raise RuntimeError(f"KOTOR module directory was not found: {module_root}")
    for path in module_root.iterdir():
        if path.is_file() and path.name.casefold() in matches:
            matches[path.name.casefold()].append(path.name)
    ambiguous = {
        expected_name: sorted(names)
        for expected_name, names in matches.items()
        if len(names) > 1
    }
    if ambiguous:
        raise RuntimeError(
            f"Ambiguous on-disk RIM identity for module {normalized}: {ambiguous}")
    missing = [name for name, names in matches.items() if not names]
    if missing:
        raise RuntimeError(
            f"Paired module RIMs were not found for {normalized}: {missing}")
    return matches[expected[0]][0], matches[expected[1]][0]


def load_runtime_configuration(path: Path) -> dict[str, Any]:
    """Load and fail-closed validate public profile policy before owned-data import."""
    payload = path.read_bytes()
    configuration = json.loads(payload)
    if not isinstance(configuration, dict):
        raise RuntimeError("KOTOR runtime configuration must be a JSON object")
    if configuration.get("schema") != "nikami-aurora-kotor-runtime-config-v2":
        raise RuntimeError("Unsupported KOTOR runtime configuration schema")

    def require_object(parent: dict[str, Any], name: str) -> dict[str, Any]:
        value = parent.get(name)
        if not isinstance(value, dict):
            raise RuntimeError(f"KOTOR runtime configuration {name} must be an object")
        return value

    def require_int(parent: dict[str, Any], name: str, minimum: int = 0) -> int:
        value = parent.get(name)
        if isinstance(value, bool) or not isinstance(value, int) or value < minimum:
            raise RuntimeError(
                f"KOTOR runtime configuration {name} must be an integer >= {minimum}")
        return value

    def require_number(
        parent: dict[str, Any], name: str, minimum: float, maximum: float | None = None
    ) -> float:
        value = parent.get(name)
        if isinstance(value, bool) or not isinstance(value, (int, float)):
            raise RuntimeError(f"KOTOR runtime configuration {name} must be numeric")
        number = float(value)
        if not math.isfinite(number) or number < minimum or (
                maximum is not None and number > maximum):
            raise RuntimeError(f"KOTOR runtime configuration {name} is out of range")
        return number

    def require_box(parent: dict[str, Any], name: str) -> None:
        box = require_object(parent, name)
        require_int(box, "left")
        require_int(box, "top")
        require_int(box, "width", 1)
        require_int(box, "height", 1)

    gameplay = require_object(configuration, "gameplay")
    require_int(gameplay, "playerExperience")
    require_int(gameplay, "playerCredits")
    player = require_object(gameplay, "playerPartyMember")
    if not isinstance(player.get("id"), str) or not player["id"].strip():
        raise RuntimeError("Configured KOTOR player party-member ID is empty")
    if not isinstance(player.get("displayName"), str) or not player["displayName"].strip():
        raise RuntimeError("Configured KOTOR player party-member name is empty")
    current_vitality = require_int(player, "currentVitality")
    maximum_vitality = require_int(player, "maximumVitality", 1)
    require_int(player, "defense")
    if current_vitality > maximum_vitality:
        raise RuntimeError("Configured KOTOR player vitality exceeds its maximum")

    presentation = require_object(configuration, "presentation")
    require_int(presentation, "fallbackFontSize", 1)
    require_int(presentation, "descriptionFontSize", 1)
    require_number(presentation, "modalDimOpacity", 0.0, 1.0)
    for color_name in ("fallbackTextColor", "emphasisColor", "selectedTint"):
        color = require_object(presentation, color_name)
        for channel in ("red", "green", "blue"):
            require_number(color, channel, 0.0, 1.0)
    loading = require_object(presentation, "loading")
    loading_values = [
        require_number(loading, "initialProgress", 0.0, 1.0),
        require_number(loading, "roomLoadingStart", 0.0, 1.0),
        require_number(loading, "roomLoadingSpan", 0.0, 1.0),
        require_number(loading, "completeProgress", 0.0, 1.0),
    ]
    require_number(loading, "musicVolumeDb", -100.0, 100.0)
    if (loading_values[0] > loading_values[1] or
            loading_values[1] + loading_values[2] > loading_values[3] or
            loading_values[3] <= 0):
        raise RuntimeError("Configured KOTOR loading presentation is invalid")
    hud = require_object(presentation, "hud")
    minimap_inset = require_object(hud, "minimapInset")
    for edge in ("left", "top", "right", "bottom"):
        require_int(minimap_inset, edge)
    inventory = require_object(presentation, "inventory")
    require_int(inventory, "descriptionBottomInset")
    require_int(inventory, "scrollThumbHorizontalInset")
    require_int(inventory, "overflowAcceptanceRepeat", 2)
    inventory_row = require_object(inventory, "row")
    for box_name in ("icon", "name", "quantity"):
        require_box(inventory_row, box_name)
    equipment = require_object(presentation, "equipment")
    require_int(equipment, "descriptionBottomInset")
    require_int(equipment, "slotIconInset")
    equipment_row = require_object(equipment, "row")
    for box_name in ("icon", "name"):
        require_box(equipment_row, box_name)
    first_encounter = require_object(presentation, "firstEncounter")
    require_number(first_encounter, "fallbackMuzzleHeightMeters", 0.0, 10.0)
    require_number(first_encounter, "fallbackTargetHeightMeters", 0.0, 10.0)
    require_number(first_encounter, "projectileLengthMeters", 0.0001, 10.0)
    require_number(first_encounter, "projectileSpeedMetersPerSecond", 0.0001, 1000.0)
    require_number(first_encounter, "minimumProjectileTravelSeconds", 0.0001, 10.0)
    require_number(first_encounter, "muzzleFlareScale", 0.0001, 1.0)
    require_number(first_encounter, "impactSizeMeters", 0.0001, 10.0)
    require_number(first_encounter, "impactLifetimeSeconds", 0.0001, 10.0)
    require_number(first_encounter, "shotVolumeDb", -100.0, 24.0)
    require_number(first_encounter, "impactVolumeDb", -100.0, 24.0)
    for color_name in (
            "projectileColor", "muzzleColor", "muzzleFlareColor", "impactColor"):
        color = require_object(first_encounter, color_name)
        for channel in ("red", "green", "blue"):
            require_number(color, channel, 0.0, 1.0)

    automation = require_object(configuration, "automation")
    milestone_names = (
        "doorFrame", "choiceFrame", "stateFrame", "capturePreparationFrame",
        "menuOpenFrame", "primaryFrame", "secondaryFrame", "sceneReadyFrame")
    milestones = [require_int(automation, name, 1) for name in milestone_names]
    if any(left >= right for left, right in zip(milestones, milestones[1:])):
        raise RuntimeError("Configured KOTOR automation milestones must increase")
    transaction_frames = automation.get("equipmentTransactionFrames")
    if (not isinstance(transaction_frames, list) or len(transaction_frames) != 8 or
            any(isinstance(frame, bool) or not isinstance(frame, int) or frame <= 0
                for frame in transaction_frames) or
            any(left >= right for left, right in
                zip(transaction_frames, transaction_frames[1:]))):
        raise RuntimeError("Configured KOTOR equipment transaction frames are invalid")

    complexity = require_object(configuration, "complexity")
    sample_sizes = complexity.get("inventoryProjectionSampleSizes")
    if (not isinstance(sample_sizes, list) or len(sample_sizes) < 3 or
            any(isinstance(size, bool) or not isinstance(size, int) or size <= 0
                for size in sample_sizes) or
            any(left >= right for left, right in zip(sample_sizes, sample_sizes[1:]))):
        raise RuntimeError("Configured inventory projection sample sizes are invalid")
    require_number(complexity, "maximumExponent", 1.0)

    if "sourceSha256" in configuration:
        raise RuntimeError("sourceSha256 is importer-owned and cannot be configured")
    configuration["sourceSha256"] = sha256_bytes(payload)
    return configuration


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


def source_loading_background(
    installation: Installation,
    module: str,
    area: Any,
) -> tuple[str, str, str | None]:
    module_resref = f"load_{normalize_module_id(module)}"
    module_resource, _ = installation.texture_resource_result(module_resref)
    if module_resource is not None:
        return module_resref, "module-texture", None

    table_resource = installation.resource("loadscreens", ResourceType.TwoDA)
    if table_resource is None:
        raise RuntimeError(
            f"KOTOR loading background is unresolved for {module}: "
            "loadscreens.2da is missing")
    table_bytes = resource_data(table_resource)
    table = read_2da(table_bytes)
    row = int(area.loadscreen_id)
    if row < 0 or row >= table.get_height():
        raise RuntimeError(
            f"KOTOR loadscreen row is out of range for {module}: {row}")
    fallback_resref = str(table.get_cell(row, "bmpresref") or "").strip()
    if not fallback_resref or fallback_resref == "****":
        raise RuntimeError(
            f"KOTOR loadscreen row has no source bitmap for {module}: {row}")
    fallback_resource, _ = installation.texture_resource_result(fallback_resref)
    if fallback_resource is None:
        raise RuntimeError(
            f"KOTOR source loading background is missing for {module}: "
            f"{fallback_resref}")
    return fallback_resref, f"area-loadscreens-row-{row}", sha256_bytes(table_bytes)


def read_owned_mdl(mdl_bytes: bytes, mdx_bytes: bytes) -> Any:
    """Read a retail binary MDL with bounds relative to its geometry payload.

    Odyssey MDL offsets exclude the 12-byte resource wrapper. PyKotor 2.3.9
    rebases its reader at byte 12 but retains the wrapper-inclusive accessible
    length, so a valid pointer near the payload boundary can pass its bounds
    check and then read beyond EOF. Build the same reader over the exact
    geometry payload extent; no bytes are appended, removed, or substituted.
    """
    if len(mdl_bytes) < 16 or mdl_bytes[:4] != b"\0\0\0\0":
        return read_mdl(mdl_bytes, source_ext=mdx_bytes)
    reader = MDLBinaryReader(mdl_bytes, source_ext=mdx_bytes)
    reader._reader = BinaryReader.from_auto(  # noqa: SLF001
        mdl_bytes, offset=12, size=len(mdl_bytes) - 12)
    return reader.load()


def find_module_resource(installation: Installation, module: str, restype: str) -> Any:
    for filename in resolve_module_rim_filenames(installation, module):
        for resource in installation.module_resources(filename):
            if resource_type_name(resource) == restype:
                return resource
    raise RuntimeError(f"{restype} resource was not found for module {module}")


def find_named_module_resource(
    installation: Installation, module: str, resname: str, restype: str
) -> Any:
    for filename in resolve_module_rim_filenames(installation, module):
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
    # Static cameras compose the GFF WXYZ orientation with the authored X-axis
    # pitch. The destination camera must remain independent from SpringArm
    # updates for this basis to survive beyond the setup frame.
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


TXI_RENDERED_DIRECTIVES = frozenset({
    "blending", "bumpmapscaling", "bumpmaptexture", "bumpyshinytexture",
    "decal", "envmaptexture",
})
TXI_UNSUPPORTED_PRESENTATION_DIRECTIVES = frozenset({
    "channelscale", "channeltranslate",
    "defaultheight", "defaultwidth", "distort", "distortionamplitude", "fps",
    "height", "numx", "numy", "proceduretype", "speed", "wateralpha", "width",
})
TXI_SAMPLING_OR_METADATA_DIRECTIVES = frozenset({
    "alphamean", "arturoheight", "arturowidth", "clamp", "compresstexture",
    "cube", "downsamplemax", "downsamplemin", "filter", "isbumpmap",
    "isbumpmapcompressed", "isdiffusebumpmap", "islightmap",
    "isspecularbumpmap", "mipmap", "numchars", "priority", "temporary",
    "texturewidth", "unique", "xbox_downsample", "xboxdownsample",
})
TXI_FOUR_CHANNEL_CONTINUATIONS = frozenset({"channelscale", "channeltranslate"})


def parse_txi_directives(source: str) -> dict[str, list[str]]:
    """Parse source TXI while preserving bounded four-channel continuations.

    Odyssey water/arturo records put a declared channel mode/count on the
    command line and the four channel coefficients on the next four numeric
    lines.  Treating those coefficients as commands fabricated 216 unknown
    directives in the owned-install audit.  A continuation is joined only
    when all four following non-comment lines are finite numeric scalars;
    malformed or orphan rows stay visible as ordinary directives so callers
    continue to fail closed.
    """
    lines = []
    for raw_line in source.splitlines():
        line = raw_line.split("#", 1)[0].strip()
        if line:
            lines.append(line)
    directives: dict[str, list[str]] = {}
    index = 0
    while index < len(lines):
        line = lines[index]
        parts = line.split(None, 1)
        key = parts[0].lower()
        if key == "bumpmapscale":
            # This spelling occurs in the owned TPC footer even though the
            # canonical Odyssey/PyKotor property is bumpmapscaling.
            key = "bumpmapscaling"
        value = parts[1] if len(parts) > 1 else ""
        if key in TXI_FOUR_CHANNEL_CONTINUATIONS and index + 4 < len(lines):
            continuation = lines[index + 1:index + 5]
            try:
                declared = float(value)
                parsed = [declared, *(float(item) for item in continuation)]
                valid = all(math.isfinite(item) for item in parsed)
            except ValueError:
                valid = False
            if valid:
                value = " ".join([value.strip(), *continuation]).strip()
                index += 4
        directives.setdefault(key, []).append(value.strip())
        index += 1
    return directives


def txi_directive_class(directive: str, values: Iterable[str]) -> str:
    """Classify one material directive as rendered, metadata, or unsupported."""
    key = directive.strip().lower()
    source_values = list(values)
    if key == "decal":
        normalized = [value.strip() for value in source_values]
        return "rendered" if (
            normalized and all(value in {"0", "1"} for value in normalized) and
            all(value == normalized[0] for value in normalized[1:])
        ) else "unsupported"
    if key in TXI_RENDERED_DIRECTIVES:
        return "rendered"
    if key in TXI_SAMPLING_OR_METADATA_DIRECTIVES:
        return "metadata"
    return "unsupported" if key in TXI_UNSUPPORTED_PRESENTATION_DIRECTIVES else "unclassified"


def unsupported_material_txi(
    directives: dict[str, list[str]],
) -> dict[str, list[str]]:
    return {
        directive: values
        for directive, values in directives.items()
        if txi_directive_class(directive, values) in {"unsupported", "unclassified"}
    }


def raw_tpc_txi(payload: bytes) -> str:
    """Recover the exact embedded TPC TXI footer without normalizing commands."""
    if len(payload) < 0x80:
        return ""
    data_size = struct.unpack_from("<I", payload, 0)[0]
    width, height = struct.unpack_from("<HH", payload, 8)
    pixel_type = payload[12]
    mipmap_count = payload[13]
    if width <= 0 or height <= 0:
        return ""
    compressed = data_size != 0
    layer_count = 1
    face_height = height
    if compressed and height // width == 6:
        layer_count = 6
        face_height = height // 6

    def level_size(level_width: int, level_height: int) -> int:
        if compressed:
            block_bytes = {2: 8, 4: 16}.get(pixel_type)
            if block_bytes is None:
                return -1
            return (max(1, (level_width + 3) // 4) *
                    max(1, (level_height + 3) // 4) * block_bytes)
        bytes_per_pixel = {1: 1, 2: 3, 4: 4, 12: 4}.get(pixel_type)
        return -1 if bytes_per_pixel is None else (
            level_width * level_height * bytes_per_pixel)

    base_size = data_size if compressed else level_size(width, face_height)
    if base_size < 0:
        return ""
    complete_size = base_size
    for level in range(1, mipmap_count):
        size = level_size(max(width >> level, 1), max(face_height >> level, 1))
        if size < 0:
            return ""
        complete_size += size
    footer_offset = 0x80 + complete_size * layer_count
    if footer_offset > len(payload):
        return ""
    return payload[footer_offset:].decode("ascii", errors="ignore").strip("\x00\r\n ")


class TextureCache:
    def __init__(self, installation: Installation):
        self.installation = installation
        self.images: dict[str, Image.Image | None] = {}
        self.alpha_tests: dict[str, float] = {}
        self.txi: dict[str, str] = {}
        self.raw_txi: dict[str, str] = {}
        self.missing: set[str] = set()
        self.environment_maps: set[str] = set()

    def source_identity(self, name: str) -> dict[str, Any] | None:
        key = name.strip().lower()
        if not key or key == "null":
            return None
        source, _ = self.installation.texture_resource_result(name)
        if source is None:
            return None
        data = resource_data(source)
        return {
            "resref": name.strip(),
            "sourceSha256": sha256_bytes(data),
            "sourceByteCount": len(data),
            "sourceType": resource_type_name(source),
        }

    def image(self, name: str) -> Image.Image | None:
        key = name.strip().lower()
        if not key or key == "null":
            return None
        if key in self.images:
            return self.images[key]

        source, sidecar_txi = self.installation.texture_resource_result(name)
        texture = self.installation.texture(name)
        if texture is None:
            self.images[key] = None
            self.alpha_tests[key] = 1.0
            self.txi[key] = ""
            self.raw_txi[key] = ""
            self.missing.add(name.strip())
            return None
        self.alpha_tests[key] = float(texture.alpha_test)
        embedded_txi = (
            raw_tpc_txi(resource_data(source))
            if source is not None and resource_type_name(source) == "TPC"
            else str(sidecar_txi or "")
        )
        self.raw_txi[key] = embedded_txi
        self.txi[key] = str(texture.txi or "")
        texture.convert(TPCTextureFormat.RGBA)
        mipmap = texture.get()
        image = Image.frombytes("RGBA", (mipmap.width, mipmap.height), bytes(mipmap.data))
        image = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        self.images[key] = image
        return image

    def is_source_transparent(self, name: str) -> bool:
        key = name.strip().lower()
        if not key or key == "null":
            return False
        if key not in self.images:
            self.image(name)
        return (
            self.alpha_tests.get(key, 1.0) < 0.5 or
            self.is_source_additive(name) or
            self.is_source_decal(name)
        )

    def is_source_additive(self, name: str) -> bool:
        key = name.strip().lower()
        if not key or key == "null":
            return False
        if key not in self.images:
            self.image(name)
        values = {
            value.lower() for value in
            parse_txi_directives(self.source_txi(name)).get("blending", [])
        }
        return bool(values & {"1", "additive"})

    def is_source_decal(self, name: str) -> bool:
        key = name.strip().lower()
        if not key or key == "null":
            return False
        if key not in self.images:
            self.image(name)
        values = parse_txi_directives(self.source_txi(name)).get("decal", [])
        if not values:
            return False
        if any(value.strip() not in {"0", "1"} for value in values) or any(
                value.strip() != values[0].strip() for value in values[1:]):
            raise RuntimeError(f"Invalid KOTOR decal semantic for {name}: {values}")
        return values[-1].strip() == "1"

    def validate_material_txi(self, name: str, role: str) -> None:
        key = name.strip().lower()
        if not key or key == "null":
            return
        if key not in self.images:
            self.image(name)
        unsupported = unsupported_material_txi(
            parse_txi_directives(self.source_txi(name)))
        if unsupported:
            raise RuntimeError(
                f"Unsupported KOTOR {role} TXI semantics for {name}: {unsupported}")

    def source_txi(self, name: str) -> str:
        key = name.strip().lower()
        if not key or key == "null":
            return ""
        if key not in self.images:
            self.image(name)
        return getattr(self, "raw_txi", {}).get(key, "") or self.txi.get(key, "")

    def source_environment_map(self, name: str) -> str | None:
        key = name.strip().lower()
        if not key or key == "null":
            return None
        if key not in self.images:
            self.image(name)
        directives = parse_txi_directives(self.source_txi(name))
        candidates: list[tuple[str, str]] = []
        for directive in ("envmaptexture", "bumpyshinytexture"):
            values = directives.get(directive, [])
            if not values:
                continue
            tokens = values[-1].split()
            if not tokens or tokens[0].lower() == "null":
                continue
            candidates.append((directive, tokens[0].strip()))
        identities = {value.lower() for _, value in candidates}
        if len(identities) > 1:
            raise RuntimeError(
                f"Conflicting KOTOR environment-map semantics for {name}: {candidates}")
        if not candidates:
            return None
        # Odyssey resrefs are case-insensitive. Normalize at the identity
        # boundary so actor and room references cannot export duplicate
        # cubemaps that the runtime's case-insensitive dictionary must reject.
        environment_map = candidates[-1][1].lower()
        self.environment_maps.add(environment_map)
        return environment_map

    def source_bump_map(self, name: str) -> tuple[str | None, str | None]:
        key = name.strip().lower()
        if not key or key == "null":
            return None, None
        if key not in self.images:
            self.image(name)
        directives = parse_txi_directives(self.source_txi(name))
        candidates: list[tuple[str, str]] = []
        for directive in ("bumpmaptexture",):
            values = directives.get(directive, [])
            if not values:
                continue
            tokens = values[-1].split()
            if not tokens or tokens[0].lower() == "null":
                continue
            candidates.append((directive, tokens[0].strip()))
        identities = {value.lower() for _, value in candidates}
        if len(identities) > 1:
            raise RuntimeError(
                f"Conflicting KOTOR bump-map semantics for {name}: {candidates}")
        return candidates[-1] if candidates else (None, None)

    def source_bump_scale(self, name: str) -> tuple[float, bool]:
        key = name.strip().lower()
        if not key or key == "null":
            return 1.0, False
        if key not in self.images:
            self.image(name)
        values = parse_txi_directives(
            self.source_txi(name)).get("bumpmapscaling", [])
        if not values:
            return 1.0, False
        parsed: list[float] = []
        for source_value in values:
            tokens = source_value.split()
            if len(tokens) != 1:
                raise RuntimeError(
                    f"Invalid KOTOR bump-map scale semantic for {name}: {source_value}")
            try:
                value = float(tokens[0])
            except ValueError as exc:
                raise RuntimeError(
                    f"Invalid KOTOR bump-map scale semantic for {name}: {source_value}") from exc
            if not math.isfinite(value):
                raise RuntimeError(
                    f"Non-finite KOTOR bump-map scale semantic for {name}: {source_value}")
            parsed.append(value)
        if any(value != parsed[0] for value in parsed[1:]):
            raise RuntimeError(
                f"Conflicting KOTOR bump-map scale semantics for {name}: {values}")
        return parsed[-1], True

    def material_semantics(self, diffuse: str, lightmap: str) -> dict[str, Any]:
        diffuse_image = self.image(diffuse)
        lightmap_image = self.image(lightmap)
        environment_map = self.source_environment_map(diffuse)
        bump_directive, bump_map = self.source_bump_map(diffuse)
        bump_scale, bump_scale_authored = self.source_bump_scale(diffuse)
        bump_image = self.image(bump_map or "")
        for role, name in (
            ("diffuse", diffuse),
            ("lightmap", lightmap),
            ("bump-map", bump_map or ""),
            ("environment-map", environment_map or ""),
        ):
            self.validate_material_txi(name, role)
        if bump_scale_authored and not bump_map:
            raise RuntimeError(
                f"KOTOR bump-map scale lacks a bump-map texture: {diffuse}")
        key = diffuse.strip().lower()
        diffuse_resref = diffuse if key and key != "null" else None
        lightmap_key = lightmap.strip().lower()
        lightmap_resref = lightmap if lightmap_key and lightmap_key != "null" else None
        source_decal = self.is_source_decal(diffuse)
        blend = "additive" if self.is_source_additive(diffuse) else (
            "alpha" if self.is_source_transparent(diffuse) else "opaque")
        return {
            "diffuseTexture": diffuse_resref,
            "lightmapTexture": lightmap_resref,
            "missingDiffuse": bool(diffuse_resref and diffuse_image is None),
            "missingLightmap": bool(lightmap_resref and lightmap_image is None),
            "bumpMapTexture": bump_map,
            "bumpMapDirective": bump_directive,
            "bumpMapScale": bump_scale if bump_map else None,
            "bumpMapScaleAuthored": bool(bump_map and bump_scale_authored),
            "missingBumpMap": bool(bump_map and bump_image is None),
            "diffuseSource": self.source_identity(diffuse),
            "lightmapSource": self.source_identity(lightmap),
            "bumpMapSource": self.source_identity(bump_map or ""),
            "alphaTest": self.alpha_tests.get(key, 1.0),
            "blend": blend,
            "sourceDecal": source_decal,
            "environmentMap": environment_map,
            "materialName": material_name(
                diffuse, blend == "additive", environment_map,
                bump_scale if bump_map and bump_scale_authored else None,
                source_decal),
            "sourceTxi": self.source_txi(diffuse),
        }


def export_effect_texture(
    installation: Installation,
    textures: TextureCache,
    resref: str,
    output_root: Path,
) -> dict[str, Any]:
    source, txi = installation.texture_resource_result(resref)
    image = textures.image(resref)
    if source is None or image is None:
        raise RuntimeError(f"Effect texture is missing: {resref}")
    source_bytes = resource_data(source)
    encoded = io.BytesIO()
    image.save(encoded, format="PNG", optimize=False)
    payload = encoded.getvalue()
    relative = f"effects/{resref.lower()}.png"
    path = output_root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(payload)
    return {
        "resref": resref,
        "path": relative,
        "sourceSha256": sha256_bytes(source_bytes),
        "sourceByteCount": len(source_bytes),
        "sourceType": str(source.restype),
        "sourceTxi": txi,
        "payloadSha256": sha256_bytes(payload),
        "byteCount": len(payload),
    }


def export_environment_map(
    installation: Installation,
    resref: str,
    output_root: Path,
) -> dict[str, Any]:
    # PyKotor normalizes Odyssey's packed cubemap to the conventional DDS/Godot
    # X+, X-, Y+, Y-, Z+, Z- order before exposing its six layers. Preserve
    # that order explicitly; the PNG row flip matches the other TPC payloads
    # exported by this importer and is declared for runtime fail-closed checks.
    face_order = (
        "positive-x", "negative-x", "positive-y",
        "negative-y", "positive-z", "negative-z",
    )
    source, txi = installation.texture_resource_result(resref)
    texture = installation.texture(resref)
    if source is None or texture is None:
        raise RuntimeError(f"Environment map is missing: {resref}")
    if not texture.is_cube_map or len(texture.layers) != 6:
        raise RuntimeError(f"Environment map is not a six-face cubemap: {resref}")
    source_bytes = resource_data(source)
    texture.convert(TPCTextureFormat.RGBA)
    faces: list[dict[str, Any]] = []
    for layer in range(6):
        mipmap = texture.get(layer, 0)
        image = Image.frombytes(
            "RGBA", (mipmap.width, mipmap.height), bytes(mipmap.data))
        image = image.transpose(Image.Transpose.FLIP_TOP_BOTTOM)
        encoded = io.BytesIO()
        image.save(encoded, format="PNG", optimize=False)
        payload = encoded.getvalue()
        relative = f"environment-maps/{resref.lower()}/face-{layer}.png"
        path = output_root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        faces.append({
            "layer": layer,
            "face": face_order[layer],
            "rowTransform": "flip-top-bottom",
            "path": relative,
            "payloadSha256": sha256_bytes(payload),
            "byteCount": len(payload),
            "width": mipmap.width,
            "height": mipmap.height,
        })
    return {
        "schema": "nikami-aurora-kotor-environment-map-v2",
        "resref": resref,
        "sourceSha256": sha256_bytes(source_bytes),
        "sourceByteCount": len(source_bytes),
        "sourceType": str(source.restype),
        "sourceTxi": txi,
        "faceOrder": list(face_order),
        "sampleBasis": "godot-to-odyssey:x,-z,y",
        "faces": faces,
    }


def export_kotor_ui(
    installation: Installation,
    module: str,
    area_resref: str,
    area: Any,
    output_root: Path,
    textures: TextureCache,
    portrait_resref: str,
    inventory_items: list[dict[str, Any]],
    runtime_configuration: dict[str, Any],
    include_endar_party: bool = True,
) -> dict[str, Any]:
    """Export source GUI contracts and owned textures for the flat runtime shell."""
    talktable = installation.talktable()
    exported_textures: dict[str, dict[str, Any]] = {}

    def export_bitmap_font(
        resref: str,
        source_txi: str,
        image: Image.Image,
        width: int,
        height: int,
        requested_size_override: int | None = None,
    ) -> dict[str, Any] | None:
        lines = [line.strip() for line in source_txi.splitlines() if line.strip()]
        settings: dict[str, str] = {}
        for line in lines:
            command, _, argument = line.partition(" ")
            if command.lower() in {
                    "numchars", "fontheight", "baselineheight", "texturewidth"}:
                settings[command.lower()] = argument.strip()
        if "numchars" not in settings:
            return None
        count = int(settings["numchars"])

        def coordinates(command: str) -> list[tuple[float, float]]:
            try:
                start = next(
                    index for index, line in enumerate(lines)
                    if line.lower() == command) + 1
            except StopIteration as error:
                raise RuntimeError(
                    f"KOTOR bitmap font is missing {command}: {resref}") from error
            result: list[tuple[float, float]] = []
            for line in lines[start:start + count]:
                values = line.split()
                if len(values) < 2:
                    raise RuntimeError(
                        f"KOTOR bitmap font has malformed {command}: {resref}")
                result.append((float(values[0]), float(values[1])))
            if len(result) != count:
                raise RuntimeError(
                    f"KOTOR bitmap font has incomplete {command}: {resref}")
            return result

        upper_left = coordinates("upperleftcoords")
        lower_right = coordinates("lowerrightcoords")
        source_requested_size = max(
            1, round(float(settings["fontheight"]) * 100.0))
        requested_size = requested_size_override or source_requested_size
        source_baseline = max(
            1, round(float(settings["baselineheight"]) * 100.0))
        doubled_atlas = (
            resref.lower() == "fnt_d16x16"
            and count > 1
            and round(abs(upper_left[1][0] - upper_left[0][0]) * width) <= 16
        )
        descriptor_line_height = source_requested_size
        descriptor_baseline = source_baseline
        coordinate_indices = range(count)
        if doubled_atlas:
            # fnt_d16x16 stores 16 text columns in 32-pixel cells and reserves
            # the first 16-pixel row for controller glyphs.  Its TXI coordinate
            # table describes half-cell slots, so indexing every second entry
            # without the reserved-row offset maps every character sixteen code
            # points too early (for example, T renders as D).  Walk the encoded
            # character set directly and derive the real source cell instead.
            coordinate_indices = range(256)
            descriptor_line_height = 16
            descriptor_baseline = descriptor_line_height
        glyphs: list[tuple[int, int, int, int, int, int, int, int]] = []
        for coordinate_index in coordinate_indices:
            byte_value = coordinate_index if doubled_atlas else coordinate_index
            try:
                character = bytes([byte_value]).decode("cp1252")
            except UnicodeDecodeError:
                continue
            codepoint = ord(character)
            xoffset = 0
            yoffset = 0
            if doubled_atlas:
                if byte_value == 32:
                    # The printable atlas starts after a reserved controller-icon
                    # row, but its space remains in the unshifted blank cell.
                    # Keep it non-rendering while preserving the source font's
                    # proportional spacing.
                    glyphs.append((codepoint, 0, 32, 1, 1, 0, 0, 8))
                    continue
                left = (byte_value % 16) * 32
                top = (byte_value // 16) * descriptor_line_height
                if byte_value >= 64:
                    top += descriptor_line_height
                cell_right = min(width, left + 32)
                cell_bottom = min(height, top + descriptor_line_height)
                alpha = image.getchannel("A")
                bounds = alpha.crop((left, top, cell_right, cell_bottom)).getbbox()
                if bounds is None:
                    right = min(width, left + 1)
                    bottom = min(height, top + 1)
                    advance = max(1, requested_size // 2)
                else:
                    xoffset, yoffset, local_right, local_bottom = bounds
                    left += xoffset
                    top += yoffset
                    right = min(cell_right, left + local_right - xoffset)
                    bottom = min(cell_bottom, top + local_bottom - yoffset)
                    advance = max(1, local_right + 1)
            else:
                upper = upper_left[coordinate_index]
                lower = lower_right[coordinate_index]
                left = max(0, min(width, round(upper[0] * width)))
                top = max(0, min(height, round((1.0 - upper[1]) * height)))
                right = max(left, min(width, round(lower[0] * width)))
                bottom = max(top, min(height, round((1.0 - lower[1]) * height)))
                advance = max(1, right - left)
            glyph_width = right - left
            glyph_height = bottom - top
            if glyph_width <= 0 or glyph_height <= 0:
                continue
            glyphs.append((
                codepoint, left, top, glyph_width, glyph_height,
                xoffset, yoffset, advance))

        descriptor_lines = [
            f'info face="{resref}" size={descriptor_line_height} bold=0 italic=0 charset="" ' +
            "unicode=1 stretchH=100 smooth=0 aa=1 padding=0,0,0,0 " +
            "spacing=0,0 outline=0",
            f"common lineHeight={descriptor_line_height} base={descriptor_baseline} " +
            f"scaleW={width} " +
            f"scaleH={height} pages=1 packed=0 alphaChnl=0 redChnl=4 " +
            "greenChnl=4 blueChnl=4",
            f'page id=0 file="{resref.lower()}.png"',
            f"chars count={len(glyphs)}",
        ]
        descriptor_lines.extend(
            f"char id={codepoint} x={left} y={top} width={glyph_width} " +
            f"height={glyph_height} xoffset={xoffset} yoffset={yoffset} " +
            f"xadvance={advance} page=0 chnl=15"
            for (codepoint, left, top, glyph_width, glyph_height,
                 xoffset, yoffset, advance) in glyphs
        )
        descriptor_lines.append("kernings count=0")
        payload = ("\n".join(descriptor_lines) + "\n").encode("utf-8")
        relative = f"ui/{resref.lower()}.fnt"
        path = output_root / relative
        path.write_bytes(payload)
        return {
            "bitmapFontPath": relative,
            "bitmapFontSha256": sha256_bytes(payload),
            "bitmapFontByteCount": len(payload),
            "bitmapFontSize": requested_size,
            "bitmapFontBaseline": descriptor_baseline,
            "bitmapFontNativeSize": descriptor_line_height,
            "bitmapFontGlyphCount": len(glyphs),
        }

    def export_texture(resref: str) -> dict[str, Any]:
        key = resref.strip().lower()
        if not key:
            raise RuntimeError("KOTOR UI texture resref cannot be empty")
        if key in exported_textures:
            return exported_textures[key]
        source_resref = resref
        source = None
        requested_font_size = None
        if key == "fnt_d16x16":
            # The Windows resource set provides the corrected PC font under the
            # engine alias fnt_d16x16b.  Preserve the GUI's logical resref while
            # importing the exact source selected by the retail font manager.
            source, _ = installation.texture_resource_result("fnt_d16x16b")
            if source is not None:
                source_resref = "fnt_d16x16b"
                textures.image(resref)
                logical_txi = textures.txi.get(key, "")
                logical_font_height = next(
                    (line.partition(" ")[2].strip()
                     for line in logical_txi.splitlines()
                     if line.strip().lower().startswith("fontheight ")),
                    "")
                if not logical_font_height:
                    raise RuntimeError(
                        "KOTOR logical fnt_d16x16 font height is missing")
                requested_font_size = max(
                    1, round(float(logical_font_height) * 100.0))
        if source is None:
            source, _ = installation.texture_resource_result(resref)
        image = textures.image(source_resref)
        if source is None or image is None:
            raise RuntimeError(f"KOTOR UI texture is missing: {resref}")
        source_bytes = resource_data(source)
        encoded = io.BytesIO()
        image.save(encoded, format="PNG", optimize=False)
        payload = encoded.getvalue()
        relative = f"ui/{key}.png"
        path = output_root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(payload)
        source_txi = textures.txi.get(source_resref.lower(), "")
        record = {
            "resref": resref,
            "sourceResref": source_resref,
            "path": relative,
            "width": int(image.width),
            "height": int(image.height),
            "sourceSha256": sha256_bytes(source_bytes),
            "sourceByteCount": len(source_bytes),
            "sourceType": resource_type_name(source),
            "sourceTxi": source_txi,
            "payloadSha256": sha256_bytes(payload),
            "byteCount": len(payload),
        }
        bitmap_font = export_bitmap_font(
            resref,
            source_txi,
            image,
            int(image.width),
            int(image.height),
            requested_font_size)
        if bitmap_font is not None:
            record.update(bitmap_font)
        exported_textures[key] = record
        return record

    def extent(source: Any) -> dict[str, int] | None:
        if source is None:
            return None
        return {
            "left": int(source.get("LEFT", 0)),
            "top": int(source.get("TOP", 0)),
            "width": int(source.get("WIDTH", 0)),
            "height": int(source.get("HEIGHT", 0)),
        }

    def color(source: Any) -> list[float] | None:
        if source is None:
            return None
        return [float(source.x), float(source.y), float(source.z)]

    def surface(source: Any) -> dict[str, Any] | None:
        if source is None:
            return None
        return {
            "corner": canonical_resref(source.get("CORNER", "")),
            "edge": canonical_resref(source.get("EDGE", "")),
            "fill": canonical_resref(source.get("FILL", "")),
            "fillStyle": int(source.get("FILLSTYLE", 0)),
            "dimension": int(source.get("DIMENSION", 0)),
            "innerOffset": int(source.get("INNEROFFSET", 0)),
            "color": color(source.get("COLOR")),
            "pulsing": bool(source.get("PULSING", 0)),
        }

    def text_record(source: Any) -> dict[str, Any] | None:
        if source is None:
            return None
        strref = int(source.get("STRREF", 0xFFFFFFFF))
        literal = str(source.get("TEXT", ""))
        resolved = talktable.string(strref) if 0 <= strref < 0xFFFFFFFF else literal
        return {
            "alignment": int(source.get("ALIGNMENT", 0)),
            "color": color(source.get("COLOR")),
            "font": canonical_resref(source.get("FONT", "")),
            "literal": literal,
            "strref": strref,
            "resolved": resolved,
            "pulsing": bool(source.get("PULSING", 0)),
        }

    def image_record(source: Any) -> dict[str, Any] | None:
        if source is None:
            return None
        return {
            "image": canonical_resref(source.get("IMAGE", "")),
            "drawStyle": int(source.get("DRAWSTYLE", 0)),
            "flipStyle": int(source.get("FLIPSTYLE", 0)),
            "rotate": float(source.get("ROTATE", 0.0)),
            "alignment": int(source.get("ALIGNMENT", 0)),
        }

    def control_record(source: Any, nested: bool = False) -> dict[str, Any]:
        record: dict[str, Any] = {
            "tag": str(source.get("TAG", "")),
            "type": int(source.get("CONTROLTYPE", -1)),
            "extent": extent(source.get("EXTENT")),
            "border": surface(source.get("BORDER")),
            "highlight": surface(source.get("HILIGHT")),
            "progress": surface(source.get("PROGRESS")),
            "text": text_record(source.get("TEXT")),
            "direction": image_record(source.get("DIR")),
            "thumb": image_record(source.get("THUMB")),
            "startFromLeft": bool(source.get("STARTFROMLEFT", 1)),
            "currentValue": int(source.get("CURVALUE", 0)),
            "maxValue": int(source.get("MAXVALUE", 0)),
            "visibleValue": int(source.get("VISIBLEVALUE", 0)),
        }
        if not nested:
            if source.get("PROTOITEM") is not None:
                record["prototype"] = control_record(source.get("PROTOITEM"), True)
            if source.get("SCROLLBAR") is not None:
                record["scrollbar"] = control_record(source.get("SCROLLBAR"), True)
        return record

    def load_gui(resref: str) -> tuple[dict[str, Any], list[dict[str, Any]]]:
        source = installation.resource(resref, ResourceType.GUI)
        if source is None:
            raise RuntimeError(f"KOTOR UI layout is missing: {resref}.gui")
        source_bytes = resource_data(source)
        root = read_gff(source_bytes).root
        root_extent = extent(root.get("EXTENT"))
        if root_extent is None or root_extent["width"] <= 0 or root_extent["height"] <= 0:
            raise RuntimeError(f"KOTOR UI layout has an invalid root extent: {resref}")
        record = {
            "resref": resref,
            "sourceSha256": sha256_bytes(source_bytes),
            "sourceByteCount": len(source_bytes),
            "extent": root_extent,
            "border": surface(root.get("BORDER")),
        }
        controls = [control_record(control) for control in root.get_list("CONTROLS")]
        return record, controls

    def referenced_textures(value: Any) -> set[str]:
        found: set[str] = set()
        if isinstance(value, dict):
            for key, child in value.items():
                if key in {"corner", "edge", "fill", "font", "image"} and isinstance(child, str) and child:
                    found.add(child)
                else:
                    found.update(referenced_textures(child))
        elif isinstance(value, list):
            for child in value:
                found.update(referenced_textures(child))
        return found

    loading_layout, loading_controls = load_gui("loadscreen")
    inventory_layout, inventory_controls = load_gui("inventory")
    equipment_layout, equipment_controls = load_gui("equip")
    top_layout, top_controls = load_gui("top")
    hud_layout, hud_controls = load_gui("mipc8x6")
    (module_loading_resref, loading_background_selection,
     loading_background_table_sha256) = source_loading_background(
        installation, module, area)

    portraits_resource = installation.resource("portraits", ResourceType.TwoDA)
    if portraits_resource is None:
        raise RuntimeError("portraits.2da could not be resolved for the party UI")
    portraits_bytes = resource_data(portraits_resource)
    portraits = read_2da(portraits_bytes)
    companion_party_member: dict[str, Any] | None = None
    companion_portrait_resref: str | None = None
    if include_endar_party:
        trask_resource = find_named_module_resource(
            installation, module, "end_trask", "UTC")
        trask_utc_bytes = resource_data(trask_resource)
        trask = read_utc(trask_utc_bytes)
        companion_portrait_resref = str(
            portraits.get_cell(int(trask.portrait_id), "baseresref"))
        if not companion_portrait_resref:
            raise RuntimeError("Endar Spire party portrait could not be resolved")
        trask_armor = next(
            (item for slot, item in trask.equipment.items() if int(slot.value) == 0x00002),
            None)
        if trask_armor is None:
            raise RuntimeError("Endar Spire party member has no equipped armor definition")
        trask_armor_resref = canonical_resref(trask_armor.resref)
        trask_armor_resource = installation.resource(trask_armor_resref, ResourceType.UTI)
        if trask_armor_resource is None:
            raise RuntimeError(
                f"Endar Spire party armor could not be resolved: {trask_armor_resref}")
        trask_armor_bytes = resource_data(trask_armor_resource)
        trask_armor_uti = read_uti(trask_armor_bytes)
        baseitems_resource = installation.resource("baseitems", ResourceType.TwoDA)
        if baseitems_resource is None:
            raise RuntimeError("baseitems.2da could not be resolved for party defense")
        baseitems_bytes = resource_data(baseitems_resource)
        baseitems = read_2da(baseitems_bytes)
        armor_base_ac = int(
            baseitems.get_cell(int(trask_armor_uti.base_item), "baseac") or "0")
        armor_dexterity_limit = int(
            baseitems.get_cell(int(trask_armor_uti.base_item), "dexbonus") or "-1")
        dexterity_modifier = math.floor((int(trask.dexterity) - 10) / 2)
        applied_dexterity_modifier = (
            dexterity_modifier
            if armor_dexterity_limit < 0
            else min(dexterity_modifier, armor_dexterity_limit)
        )
        trask_display_name = talktable.string(int(trask.first_name.stringref))
        if not trask_display_name:
            raise RuntimeError("Endar Spire party member name could not be resolved")
        companion_party_member = {
            "id": canonical_resref(trask.tag).lower(),
            "displayName": trask_display_name,
            "portrait": export_texture(companion_portrait_resref),
            "currentVitality": int(trask.current_hp),
            "maximumVitality": int(trask.max_hp),
            "defense": 10 + int(trask.natural_ac) + armor_base_ac + applied_dexterity_modifier,
            "isPlayer": False,
            "sourceKind": "utc",
            "utcSha256": sha256_bytes(trask_utc_bytes),
            "armorResref": trask_armor_resref,
            "armorUtiSha256": sha256_bytes(trask_armor_bytes),
            "baseItemsSha256": sha256_bytes(baseitems_bytes),
        }

    loading_music = installation.sounds(
        {"mus_loadscreen"}, [SearchLocation.MUSIC]).get("mus_loadscreen")
    if not loading_music or not loading_music.startswith(b"RIFF"):
        raise RuntimeError("KOTOR loading music could not be decoded as WAV")
    loading_music_relative = "audio/mus_loadscreen.wav"
    loading_music_path = output_root / loading_music_relative
    loading_music_path.parent.mkdir(parents=True, exist_ok=True)
    loading_music_path.write_bytes(loading_music)
    loading_music_source_path = installation.streammusic_path() / "mus_loadscreen.wav"
    loading_music_source = loading_music_source_path.read_bytes()
    loading_music_record = {
        "resref": "mus_loadscreen",
        "path": loading_music_relative,
        "format": "wav",
        "sourceSha256": sha256_bytes(loading_music_source),
        "sourceByteCount": len(loading_music_source),
        "payloadSha256": sha256_bytes(loading_music),
        "byteCount": len(loading_music),
    }

    loadscreen_hints_resource = installation.resource(
        "loadscreenhints", ResourceType.TwoDA)
    if loadscreen_hints_resource is None:
        raise RuntimeError("loadscreenhints.2da could not be resolved")
    loadscreen_hints_bytes = resource_data(loadscreen_hints_resource)
    loadscreen_hints = read_2da(loadscreen_hints_bytes)
    story_hint_strref = int(loadscreen_hints.get_cell(0, "storyhint"))

    item_records: list[dict[str, Any]] = []
    for item in inventory_items:
        variation = (
            int(item["textureVariation"])
            if int(item["modelType"]) == 1 and int(item["textureVariation"]) > 0
            else int(item["modelVariation"])
        )
        icon_resref = f"i{str(item['itemClass']).lower()}_{variation:03d}"
        item_records.append({
            "resref": item["resref"],
            "displayName": item["displayName"],
            "description": item["description"],
            "cost": int(item["cost"]),
            "baseItem": int(item["baseItem"]),
            "equipableSlots": int(item["equipableSlots"]),
            "plot": bool(item["plot"]),
            "icon": export_texture(icon_resref),
            "utiSha256": item["utiSha256"],
        })

    player_party_member = runtime_configuration["gameplay"]["playerPartyMember"]
    player_party_record = {
        "id": player_party_member["id"],
        "displayName": player_party_member["displayName"],
        "portrait": export_texture(portrait_resref),
        "currentVitality": player_party_member["currentVitality"],
        "maximumVitality": player_party_member["maximumVitality"],
        "defense": player_party_member["defense"],
        "isPlayer": True,
        "sourceKind": "profile",
        "utcSha256": None,
        "armorResref": None,
        "armorUtiSha256": None,
        "baseItemsSha256": None,
    }
    party_portraits = [export_texture(portrait_resref)]
    party_members = [player_party_record]
    if companion_portrait_resref is not None and companion_party_member is not None:
        party_portraits.append(export_texture(companion_portrait_resref))
        party_members.append(companion_party_member)
    minimap_resref = f"lbl_map{area_resref}"
    minimap_source, _ = installation.texture_resource_result(minimap_resref)
    minimap_record = None if minimap_source is None else {
        "texture": export_texture(minimap_resref),
        "mapPoint1": [float(area.map_point_1.x), float(area.map_point_1.y)],
        "mapPoint2": [float(area.map_point_2.x), float(area.map_point_2.y)],
        "worldPoint1": [float(area.world_point_1.x), float(area.world_point_1.y)],
        "worldPoint2": [float(area.world_point_2.x), float(area.world_point_2.y)],
        "resolutionX": int(area.map_res_x),
        "zoom": int(area.map_zoom),
        "northAxis": int(area.north_axis),
    }
    ui_contract: dict[str, Any] = {
        "schema": "nikami-aurora-kotor-ui-v1",
        "loading": {
            "layout": loading_layout,
            "controls": loading_controls,
            "background": export_texture(module_loading_resref),
            "backgroundSelection": loading_background_selection,
            "backgroundTableSha256": loading_background_table_sha256,
            "logo": export_texture("logo_sw_02"),
            "progress": export_texture("bluefill"),
            "loadingText": talktable.string(42493),
            "loadingStrref": 42493,
            "hintText": talktable.string(story_hint_strref),
            "hintStrref": story_hint_strref,
            "hintsSourceSha256": sha256_bytes(loadscreen_hints_bytes),
            "musicResref": "mus_loadscreen",
            "music": loading_music_record,
        },
        "inventory": {
            "layout": inventory_layout,
            "controls": inventory_controls,
            "topLayout": top_layout,
            "topControls": top_controls,
            "background": export_texture("lbl_invent"),
            "portrait": export_texture(portrait_resref),
            "partyPortraits": party_portraits,
            "partyPortraitsSourceSha256": sha256_bytes(portraits_bytes),
            "partyMembers": party_members,
            "items": item_records,
            "allItems": {
                "text": talktable.string(41822),
                "strref": 41822,
            },
        },
        "equipment": {
            "layout": equipment_layout,
            "controls": equipment_controls,
            "topLayout": top_layout,
            "topControls": top_controls,
            "background": export_texture("lbl_equip"),
            "portrait": export_texture(portrait_resref),
            "partyPortraits": party_portraits,
            "partyPortraitsSourceSha256": sha256_bytes(portraits_bytes),
            "items": item_records,
            "slotIcons": {
                "Head": export_texture("ihead"),
                "Implant": export_texture("iimplant"),
                "Armor": export_texture("iarmor"),
                "LeftArm": export_texture("ihand_l"),
                "LeftHand": export_texture("iweap_l"),
                "Belt": export_texture("ibelt"),
                "RightHand": export_texture("iweap_r"),
                "RightArm": export_texture("ihand_r"),
                "Gauntlet": export_texture("ihands"),
            },
            "none": {
                "text": talktable.string(363),
                "strref": 363,
            },
            "noneIcon": export_texture("inone"),
            "equipped": {
                "text": talktable.string(32346),
                "strref": 32346,
            },
            "slotNames": {
                "Head": {"text": talktable.string(31375), "strref": 31375},
                "LeftArm": {"text": talktable.string(31376), "strref": 31376},
                "RightArm": {"text": talktable.string(31377), "strref": 31377},
                "LeftHand": {"text": talktable.string(31378), "strref": 31378},
                "RightHand": {"text": talktable.string(31379), "strref": 31379},
                "Armor": {"text": talktable.string(31380), "strref": 31380},
                "Belt": {"text": talktable.string(31382), "strref": 31382},
                "Gauntlet": {"text": talktable.string(31383), "strref": 31383},
                "Implant": {"text": talktable.string(31388), "strref": 31388},
            },
        },
        "hud": {
            "layout": hud_layout,
            "controls": hud_controls,
            "portrait": export_texture(portrait_resref),
            "partyPortraits": party_portraits,
            "minimap": minimap_record,
        },
    }
    unresolved_references: list[str] = []
    for resref in sorted(referenced_textures(ui_contract)):
        try:
            export_texture(resref)
        except RuntimeError as exc:
            # Some GUI resources carry design-time placeholders that retail
            # replaces from live party state (equip.gui's po_mhk47 portrait is
            # one example).  Preserve the unresolved source reference without
            # pretending it is an asset required by the materialized screen.
            if "texture is missing" not in str(exc):
                raise
            unresolved_references.append(resref)
    ui_contract["unresolvedReferencedTextures"] = unresolved_references
    ui_contract["textures"] = sorted(
        exported_textures.values(), key=lambda record: record["resref"].lower())
    return ui_contract


def export_first_encounter_effects(
    installation: Installation,
    output_root: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    projectile, projectile_mdl, projectile_mdx = load_model_pair(
        installation, "w_laserfire_r")
    muzzle, muzzle_mdl, muzzle_mdx = load_model_pair(
        installation, "v_muzflash_01")
    if (sha256_bytes(projectile_mdl) !=
            "01DFB4FECFF9286E2E9194324A0EDE63A5FA2C8D4CEAA25F1567EE69682A735C" or
            sha256_bytes(projectile_mdx) !=
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"):
        # The MDX digest is installation-bound evidence; fail closed if a
        # patched source changes the emitter contract we are transferring.
        raise RuntimeError("First-encounter projectile emitter source drifted")
    if (sha256_bytes(muzzle_mdl) !=
            "10501A23FE8DBEF9A03F17929DC88F83AC165DCCC4CBAF70A7E065B5FEDA8A76" or
            sha256_bytes(muzzle_mdx) !=
            "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"):
        raise RuntimeError("First-encounter muzzle emitter source drifted")

    def emitters(model: Any) -> list[Any]:
        found: list[Any] = []

        def visit(node: Any) -> None:
            if node.emitter is not None:
                found.append((node, node.emitter))
            for child in node.children:
                visit(child)

        visit(model.root)
        return found

    def scalar(node: Any, controller_type: MDLControllerType) -> float:
        values = controller_value(node, controller_type, [float("nan")])
        if not values or not math.isfinite(values[0]):
            raise RuntimeError(
                f"Emitter controller {controller_type.name} is missing on {node.name}")
        return float(values[0])

    def vector(node: Any, controller_type: MDLControllerType,
               fallback: list[float]) -> list[float]:
        values = controller_value(node, controller_type, fallback)
        if len(values) < len(fallback) or not all(math.isfinite(value) for value in values):
            raise RuntimeError(
                f"Emitter controller {controller_type.name} is invalid on {node.name}")
        return values

    projectile_emitters = emitters(projectile)
    muzzle_emitters = emitters(muzzle)
    if len(projectile_emitters) != 1 or len(muzzle_emitters) != 5:
        raise RuntimeError("First-encounter emitter topology drifted")
    projectile_node, projectile_emitter = projectile_emitters[0]
    if (str(projectile_emitter.texture).lower() != "fx_laser_01" or
            str(projectile_emitter.blend).lower() != "lighten" or
            str(projectile_emitter.render).lower() != "motion_blur"):
        raise RuntimeError("First-encounter projectile emitter semantics drifted")
    muzzle_textures = {str(emitter.texture).lower() for _, emitter in muzzle_emitters}
    if (muzzle_textures != {"fx_muzflash", "fx_flare02"} or
            any(str(emitter.blend).lower() != "lighten" for _, emitter in muzzle_emitters)):
        raise RuntimeError("First-encounter muzzle emitter semantics drifted")
    projectile_size = scalar(projectile_node, MDLControllerType.SIZESTART)
    muzzle_size = max(
        scalar(node, MDLControllerType.SIZESTART) for node, _ in muzzle_emitters)
    muzzle_lifetime = max(
        scalar(node, MDLControllerType.LIFEEXP) for node, _ in muzzle_emitters)
    if (abs(projectile_size - 0.09) > 0.0001 or
            abs(muzzle_size - 0.3) > 0.0001 or
            abs(muzzle_lifetime - 0.02) > 0.0001):
        raise RuntimeError("First-encounter emitter dimensions drifted")
    projectile_blur_length = scalar(projectile_node, MDLControllerType.BLURLENGTH)
    projectile_life_expectancy = scalar(projectile_node, MDLControllerType.LIFEEXP)
    if (abs(projectile_blur_length - 0.01) > 0.0001 or
            projectile_life_expectancy != -1.0 or
            str(projectile_emitter.update).lower() != "explosion" or
            int(projectile_emitter.flags) != 0x42):
        raise RuntimeError("First-encounter projectile motion contract drifted")

    muzzle_records = []
    for node, emitter in muzzle_emitters:
        size = scalar(node, MDLControllerType.SIZESTART)
        lifetime = scalar(node, MDLControllerType.LIFEEXP)
        node_transform = quaternion_matrix(node)
        if (str(emitter.update).lower() != "explosion" or
                str(emitter.render).lower() != "billboard_to_local_z" or
                str(emitter.blend).lower() != "lighten" or
                int(emitter.flags) != 0x42 or size <= 0 or lifetime <= 0):
            raise RuntimeError(
                f"First-encounter muzzle layer contract drifted: {node.name}")
        muzzle_records.append({
            "node": str(node.name),
            "position": vector(
                node, MDLControllerType.POSITION, vector3(node.position)),
            "basisRight": [float(value) for value in node_transform[:3, 0]],
            "basisUp": [float(value) for value in node_transform[:3, 1]],
            "basisForward": [float(value) for value in node_transform[:3, 2]],
            "textureResref": str(emitter.texture),
            "update": str(emitter.update),
            "render": str(emitter.render),
            "blend": str(emitter.blend),
            "flags": int(emitter.flags),
            "size": size,
            "lifetime": lifetime,
            "color": vector(
                node, MDLControllerType.COLORSTART, [1.0, 1.0, 1.0]),
            "alpha": scalar(node, MDLControllerType.ALPHASTART),
        })
    return {
        "schema": "nikami-aurora-kotor-first-encounter-effects-v2",
        "projectileModel": "w_laserfire_r",
        "projectileMdlSha256": sha256_bytes(projectile_mdl),
        "projectileMdxSha256": sha256_bytes(projectile_mdx),
        "muzzleModel": "v_muzflash_01",
        "muzzleMdlSha256": sha256_bytes(muzzle_mdl),
        "muzzleMdxSha256": sha256_bytes(muzzle_mdx),
        "projectileSize": projectile_size,
        "projectileUpdate": str(projectile_emitter.update),
        "projectileRender": str(projectile_emitter.render),
        "projectileBlend": str(projectile_emitter.blend),
        "projectileFlags": int(projectile_emitter.flags),
        "projectileBlurLength": projectile_blur_length,
        "projectileLifeExpectancy": projectile_life_expectancy,
        "muzzleSize": muzzle_size,
        "muzzleLifetime": muzzle_lifetime,
        "muzzleEmitters": muzzle_records,
        "laserTexture": export_effect_texture(
            installation, textures, "Fx_laser_01", output_root),
        "muzzleTexture": export_effect_texture(
            installation, textures, "fx_muzflash", output_root),
        "flareTexture": export_effect_texture(
            installation, textures, "fx_flare02", output_root),
    }


def material_name(texture_name: str, source_additive: bool,
                  environment_map: str | None,
                  authored_bump_scale: float | None = None,
                  source_decal: bool = False) -> str:
    name = texture_name or "untextured"
    if environment_map:
        name += f"__aurora_envmap_{environment_map}"
    if authored_bump_scale is not None:
        name += f"__aurora_normal_scale_{format(authored_bump_scale, '.9g')}"
    if source_additive:
        name += "__aurora_additive"
    if source_decal:
        name += "__aurora_decal"
    return name


def material_for(mesh: Any, textures: TextureCache, override_texture: str | None = None) -> Any:
    texture_name = str(override_texture or mesh.texture_1 or "").strip()
    image = textures.image(texture_name)
    lightmap_name = str(mesh.texture_2 or "").strip()
    lightmap = textures.image(lightmap_name)
    source_additive = image is not None and textures.is_source_additive(texture_name)
    source_decal = image is not None and textures.is_source_decal(texture_name)
    source_transparent = image is not None and textures.is_source_transparent(texture_name)
    environment_map = textures.source_environment_map(texture_name)
    _, bump_map_name = textures.source_bump_map(texture_name)
    bump_scale, bump_scale_authored = textures.source_bump_scale(texture_name)
    bump_map = textures.image(bump_map_name or "")
    for role, name in (
        ("diffuse", texture_name),
        ("lightmap", lightmap_name),
        ("bump-map", bump_map_name or ""),
        ("environment-map", environment_map or ""),
    ):
        textures.validate_material_txi(name, role)
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
        name=material_name(
            texture_name, source_additive, environment_map,
            bump_scale if bump_map is not None and bump_scale_authored else None,
            source_decal),
        baseColorTexture=image,
        baseColorFactor=color,
        emissiveTexture=lightmap,
        emissiveFactor=[1.0, 1.0, 1.0] if lightmap is not None else None,
        normalTexture=bump_map,
        metallicFactor=0.0,
        roughnessFactor=1.0,
        alphaMode="BLEND" if source_transparent else "OPAQUE",
        doubleSided=source_transparent,
    )


def model_presentation_inventory(
    model: Any,
    textures: TextureCache,
    override_texture: str | None = None,
) -> dict[str, int]:
    """Inventory every render/effect node before a model crosses into glTF.

    glTF can carry the mesh materials used by current actor assemblies, but it
    cannot silently stand in for Odyssey emitter or light nodes.  Keeping this
    check next to the material boundary makes body, head, and equipped-weapon
    imports obey the same source-completeness rule.
    """
    surfaces = 0
    additive_surfaces = 0
    emitter_nodes = 0
    light_nodes = 0
    for node in model.all_nodes():
        emitter_nodes += node.emitter is not None
        light_nodes += node.light is not None
        mesh = node.mesh
        if (mesh is None or not bool(mesh.render) or not mesh.vertex_positions or
                not mesh.faces or node.aabb is not None or
                str(node.name).lower().startswith("walkmesh")):
            continue
        surfaces += 1
        texture_name = str(override_texture or mesh.texture_1 or "").strip()
        lightmap_name = str(mesh.texture_2 or "").strip()
        environment_map = textures.source_environment_map(texture_name)
        _, bump_map_name = textures.source_bump_map(texture_name)
        for role, name in (
            ("diffuse", texture_name),
            ("lightmap", lightmap_name),
            ("bump-map", bump_map_name or ""),
            ("environment-map", environment_map or ""),
        ):
            textures.validate_material_txi(name, role)
        additive_surfaces += textures.is_source_additive(texture_name)
    return {
        "renderSurfaces": surfaces,
        "additiveSurfaces": additive_surfaces,
        "emitterNodes": emitter_nodes,
        "lightNodes": light_nodes,
    }


def actor_model_records(
    source_models: Iterable[tuple[
        str, str | None, str | None, Any | None, bytes | None, bytes | None]],
    textures: TextureCache,
) -> list[dict[str, Any]]:
    records = []
    for role, name, override, source_model, mdl_bytes, mdx_bytes in source_models:
        if (not name or source_model is None or mdl_bytes is None or
                mdx_bytes is None):
            continue
        records.append({
            "role": role,
            "model": name,
            "overrideTexture": override,
            "mdlSha256": sha256_bytes(mdl_bytes),
            "mdxSha256": sha256_bytes(mdx_bytes),
            **model_presentation_inventory(source_model, textures, override),
        })
    return records


def actor_effect_records(
    installation: Installation,
    source_models: Iterable[tuple[
        str, str | None, str | None, Any | None, bytes | None, bytes | None]],
    textures: TextureCache,
    output_root: Path,
    animation_model: Any,
) -> dict[str, Any]:
    """Export attached actor effects without activating them out of context."""
    role_prefix = {
        "body": "",
        "head": "head::",
        "rightWeapon": "weapon::",
        "leftWeapon": "left-weapon::",
    }
    emitters: list[dict[str, Any]] = []
    lights: list[dict[str, Any]] = []
    anchors: set[str] = set()
    source_models = tuple(source_models)
    for role, model_name, _override, model, _mdl, _mdx in source_models:
        if not model_name or model is None:
            continue
        prefix = role_prefix[role]
        for node in model.all_nodes():
            if node.emitter is None and node.light is None:
                continue
            anchor = prefix + str(node.name)
            if anchor.casefold() in anchors:
                raise RuntimeError(
                    f"Ambiguous actor effect anchor: {role}:{model_name}:{anchor}")
            anchors.add(anchor.casefold())
            if node.emitter is not None:
                emitter = node.emitter
                update = str(emitter.update).casefold()
                render = str(emitter.render).casefold()
                blend = str(emitter.blend).casefold()
                if (update not in {"explosion", "fountain"} or
                        render not in {"normal", "motion_blur"} or
                        blend not in SUPPORTED_ROOM_EMITTER_BLENDS):
                    raise RuntimeError(
                        "Unsupported actor emitter semantic: "
                        f"{role}:{model_name}:{anchor} "
                        f"update={emitter.update} render={emitter.render} "
                        f"blend={emitter.blend}")
                texture_name = str(emitter.texture or "").strip()
                if not texture_name or texture_name.casefold() == "null":
                    raise RuntimeError(
                        f"Actor emitter has no texture: {role}:{model_name}:{anchor}")
                values = room_emitter_controller_values(node)
                if (float(values["lifeExpectancy"]) <= 0 or
                        max(float(values["birthRate"]),
                            float(values["randomBirthRate"])) <= 0):
                    raise RuntimeError(
                        f"Actor emitter has no finite burst: {role}:{model_name}:{anchor}")
                emitters.append({
                    "schema": "nikami-aurora-kotor-actor-emitter-v1",
                    "role": role,
                    "model": model_name,
                    "anchorNode": anchor,
                    "texture": export_effect_texture(
                        installation, textures, texture_name, output_root),
                    "update": str(emitter.update),
                    "render": str(emitter.render),
                    "blend": str(emitter.blend),
                    "flags": int(emitter.flags),
                    "loop": int(emitter.loop),
                    "twoSidedTexture": int(emitter.two_sided_texture),
                    "xGrid": max(1, int(emitter.x_grid)),
                    "yGrid": max(1, int(emitter.y_grid)),
                    **values,
                })
            if node.light is not None:
                light = node.light
                lights.append({
                    "schema": "nikami-aurora-kotor-actor-light-v1",
                    "role": role,
                    "model": model_name,
                    "anchorNode": anchor,
                    "color": color3(light.color),
                    "radius": float(light.radius),
                    "multiplier": float(light.multiplier),
                    "dynamicType": int(light.dynamic_type),
                    "affectDynamic": bool(light.affect_dynamic),
                    "ambientOnly": bool(light.ambient_only),
                })

    effect_anchors = {item["anchorNode"].casefold() for item in [*emitters, *lights]}
    has_explosion_emitter = any(
        item["update"].casefold() == "explosion" for item in emitters)
    animations = []
    for animation in animation_model.anims:
        events = [
            {"time": float(event.activation_time), "name": str(event.name)}
            for event in animation.events
            if has_explosion_emitter and
            str(event.name).casefold() == "detonate"
        ]
        tracks = []
        for node in animation.all_nodes():
            anchor = str(node.name)
            matching = [candidate for candidate in effect_anchors
                        if candidate.endswith(anchor.casefold())]
            if len(matching) > 1:
                raise RuntimeError(
                    f"Ambiguous actor effect animation anchor: {animation.name}:{anchor}")
            if not matching:
                continue
            for controller in node.controllers:
                if controller.controller_type not in {
                        MDLControllerType.RADIUS, MDLControllerType.COLOR}:
                    continue
                tracks.append({
                    "anchorNode": next(item["anchorNode"] for item in [*emitters, *lights]
                                       if item["anchorNode"].casefold() == matching[0]),
                    "controller": controller.controller_type.name.lower(),
                    "keys": [
                        {"time": float(row.time),
                         "value": [float(value) for value in row.data[:3]]}
                        for row in controller.rows
                    ],
                })
        if events or tracks:
            animations.append({
                "name": str(animation.name),
                "length": float(animation.length),
                "events": events,
                "tracks": tracks,
            })
    return {
        "schema": "nikami-aurora-kotor-actor-effects-v1",
        "emitters": emitters,
        "lights": lights,
        "animations": animations,
    }


def room_emitter_controller_values(node: Any) -> dict[str, Any]:
    def scalar(controller_type: MDLControllerType, fallback: float) -> float:
        return controller_value(node, controller_type, [fallback])[0]

    def color(controller_type: MDLControllerType) -> list[float]:
        return controller_value(node, controller_type, [1.0, 1.0, 1.0])

    return {
        # Gravity and mass are distinct Odyssey controllers. Mass remains
        # provenance and must not be substituted for authored gravity.
        "birthRate": scalar(MDLControllerType.BIRTHRATE, 0.0),
        "randomBirthRate": scalar(MDLControllerType.RANDOMBIRTHRATE, 0.0),
        "velocity": scalar(MDLControllerType.VELOCITY, 0.0),
        "randomVelocity": scalar(MDLControllerType.RANDVEL, 0.0),
        # Odyssey emitter extents are authored in hundredths of a metre.
        # Keep the raw controller values and export the converted footprint
        # separately so the runtime cannot silently reinterpret their units.
        "xSize": scalar(MDLControllerType.XSIZE, 0.0),
        "ySize": scalar(MDLControllerType.YSIZE, 0.0),
        "gravity": scalar(MDLControllerType.GRAV, 0.0),
        "mass": scalar(MDLControllerType.MASS, 0.0),
        "particleRotation": scalar(MDLControllerType.PARTICLEROT, 0.0),
        "spreadRadians": scalar(MDLControllerType.SPREAD, 0.0),
        "lifeExpectancy": scalar(MDLControllerType.LIFEEXP, 1.0),
        "colorStart": color(MDLControllerType.COLORSTART),
        "colorMid": color(MDLControllerType.COLORMID),
        "colorEnd": color(MDLControllerType.COLOREND),
        "percentStart": scalar(MDLControllerType.PERCENTSTART, 0.0),
        "percentMid": scalar(MDLControllerType.PERCENTMID, 0.5),
        "percentEnd": scalar(MDLControllerType.PERCENTEND, 1.0),
        "alphaStart": scalar(MDLControllerType.ALPHASTART, 1.0),
        "alphaMid": scalar(MDLControllerType.ALPHAMID, 1.0),
        "alphaEnd": scalar(MDLControllerType.ALPHAEND, 0.0),
        "sizeStart": scalar(MDLControllerType.SIZESTART, 1.0),
        "sizeMid": scalar(MDLControllerType.SIZEMID, 1.0),
        "sizeEnd": scalar(MDLControllerType.SIZEEND, 1.0),
        "frameStart": scalar(MDLControllerType.FRAMESTART, 0.0),
        "frameEnd": scalar(MDLControllerType.FRAMEEND, 0.0),
        "fps": scalar(MDLControllerType.FPS, 0.0),
        "blurLength": scalar(MDLControllerType.BLURLENGTH, 0.0),
        # Odyssey controller 92 is the authored restitution coefficient used
        # when EMITTER_FLAG_BOUNCE is set.  Keep the value source-bound in the
        # v2 manifest instead of substituting a destination-engine default.
        "bounceCoefficient": scalar(MDLControllerType.BOUNCECO, 0.0),
    }


SUPPORTED_ROOM_EMITTER_UPDATES = frozenset({"fountain", "single"})
SUPPORTED_ROOM_EMITTER_RENDERS = frozenset({
    "normal",
    "motion_blur",
    "billboard_to_local_z",
    "billboard_to_world_z",
    "aligned_to_particle_dir",
})
SUPPORTED_ROOM_EMITTER_BLENDS = frozenset({"normal", "lighten"})
ROOM_EMITTER_POINT_TO_POINT_FLAG = 0x0001
ROOM_EMITTER_POINT_TO_POINT_BEZIER_FLAG = 0x0002
ROOM_EMITTER_COLLISION_BOUNCE_FLAG = 0x0010
# These flags change particle motion or require a render target/collision join
# that this source-room path does not own. Straight point-to-point acceleration
# is handled separately through a static authored child target. P2P_SEL without
# P2P is only a mode selector; TINTED and RANDOM are rendered below.
UNSUPPORTED_ROOM_EMITTER_FLAGS = (
    0x0004 |  # wind
    0x0080 |  # parent velocity inheritance
    0x0200 |  # collision splat
    0x0400 |  # particle inheritance
    0x0800 |  # depth-texture sampling
    0x1000    # unknown source flag
)
GODOT_RENDER_PRIORITY_MIN = -128
GODOT_RENDER_PRIORITY_MAX = 127
ROOM_EMITTER_MAXIMUM_QUAD_EXTENT_METERS = 8.0


def room_emitter_visual_safety_reasons(emitter: dict[str, Any]) -> tuple[str, ...]:
    """Validate atlas addressing and final card extent without rescaling source."""
    reasons: set[str] = set()
    x_grid = max(1, int(emitter.get("xGrid", 0)))
    y_grid = max(1, int(emitter.get("yGrid", 0)))
    frame_count = x_grid * y_grid
    frame_start = float(emitter.get("frameStart", float("nan")))
    frame_end = float(emitter.get("frameEnd", float("nan")))
    fps = float(emitter.get("fps", float("nan")))
    persistent_single = (
        str(emitter.get("update", "")).lower() == "single" and
        float(emitter.get("lifeExpectancy", 0.0)) == -1.0)
    minimum_frame = 1.0 if persistent_single else 0.0
    maximum_frame = float(frame_count if persistent_single else frame_count - 1)
    if (not math.isfinite(frame_start) or not math.isfinite(frame_end) or
            frame_start != math.trunc(frame_start) or
            frame_end != math.trunc(frame_end) or
            frame_start < minimum_frame or frame_end > maximum_frame or
            frame_start > frame_end or not math.isfinite(fps) or fps < 0):
        reasons.add("atlas_range")

    sizes = [
        float(emitter.get(name, float("nan")))
        for name in ("sizeStart", "sizeMid", "sizeEnd")
    ]
    blur_length = float(emitter.get("blurLength", 0.0))
    if (any(not math.isfinite(size) or size < 0 for size in sizes) or
            not math.isfinite(blur_length) or blur_length < 0 or
            max(sizes, default=0.0) <= 0):
        reasons.add("quad_extent")
    else:
        maximum_size = max(sizes)
        aspect = 1.0
        if str(emitter.get("render", "")).lower() == "motion_blur":
            aspect = max(1.0, blur_length / max(0.001, sizes[0]))
        extent = maximum_size * aspect
        if (not math.isfinite(extent) or
                extent > ROOM_EMITTER_MAXIMUM_QUAD_EXTENT_METERS):
            reasons.add("quad_extent")
    return tuple(sorted(reasons))


def room_emitter_point_to_point_target(node: Any) -> list[float] | None:
    """Return a static straight-P2P child target, otherwise fail closed.

    The gravity-target source form has P2P set, the Bezier selector clear, and
    exactly one child reference. A time-varying child position would require a
    frame-pose join, so only an absent position controller or one authored row
    at time zero matching the rest position is accepted.
    """
    source = node.emitter
    if source is None or not int(source.flags) & ROOM_EMITTER_POINT_TO_POINT_FLAG:
        return None
    if int(source.flags) & ROOM_EMITTER_POINT_TO_POINT_BEZIER_FLAG:
        return None
    if len(node.children) != 1:
        return None
    target = node.children[0]
    if target.children:
        return None
    position_controllers = [
        controller for controller in target.controllers
        if controller.controller_type == MDLControllerType.POSITION
    ]
    if len(position_controllers) > 1:
        return None
    target_position = vector3(target.position)
    if position_controllers:
        rows = position_controllers[0].rows
        if len(rows) != 1 or abs(float(rows[0].time)) > 1e-6:
            return None
        authored = [float(item) for item in rows[0].data[:3]]
        if (len(authored) != 3 or
                any(not math.isfinite(item) for item in authored) or
                any(abs(authored[index] - target_position[index]) > 1e-5
                    for index in range(3))):
            return None
    if any(not math.isfinite(item) for item in target_position):
        return None
    return target_position


def room_emitter_unsupported_reasons(
    emitter: dict[str, Any], texture_available: bool = True
) -> tuple[str, ...]:
    """Classify source semantics without selecting on a module or room ID.

    Odyssey treats an authored zero atlas dimension as one cell. Keep that
    behavior in the predicate; export records retain the authored dimensions
    separately from their effective, runtime-safe grid.
    """
    update = str(emitter["update"]).lower()
    render = str(emitter["render"]).lower()
    blend = str(emitter["blend"]).lower()
    reasons: set[str] = set()
    if update not in SUPPORTED_ROOM_EMITTER_UPDATES:
        reasons.add("update")
    if render not in SUPPORTED_ROOM_EMITTER_RENDERS:
        reasons.add("render")
    if blend not in SUPPORTED_ROOM_EMITTER_BLENDS:
        reasons.add("blend")
    source_flags = int(emitter.get("flags", 0))
    point_to_point = bool(source_flags & ROOM_EMITTER_POINT_TO_POINT_FLAG)
    collision_bounce = bool(source_flags & ROOM_EMITTER_COLLISION_BOUNCE_FLAG)
    depth_texture = str(emitter.get("depthTexture", "") or "").strip().lower()
    render_order = int(emitter.get("renderOrder", 0))
    if (
        source_flags & UNSUPPORTED_ROOM_EMITTER_FLAGS or
        int(emitter.get("spawnType", 0)) != 0 or
        int(emitter.get("frameBlender", 0)) != 0 or
        depth_texture not in {"", "null"} or
        render_order < GODOT_RENDER_PRIORITY_MIN or
        render_order > GODOT_RENDER_PRIORITY_MAX
    ):
        reasons.add("render")
    if point_to_point:
        target = emitter.get("pointToPointTargetPosition")
        gravity = float(emitter.get("gravity", 0.0))
        if (source_flags & ROOM_EMITTER_POINT_TO_POINT_BEZIER_FLAG or
                not isinstance(target, (list, tuple)) or len(target) != 3 or
                any(not math.isfinite(float(item)) for item in target) or
                not math.isfinite(gravity) or gravity <= 0):
            reasons.add("render")
    if collision_bounce:
        bounce_coefficient = float(emitter.get("bounceCoefficient", float("nan")))
        if (not math.isfinite(bounce_coefficient) or
                bounce_coefficient < 0.0 or bounce_coefficient > 1.0):
            reasons.add("render")
    for extent_name in ("xSize", "ySize"):
        extent = float(emitter.get(extent_name, 0.0))
        if not math.isfinite(extent) or extent < 0:
            reasons.add("render")
    authored_x_grid = int(emitter["xGrid"])
    authored_y_grid = int(emitter["yGrid"])
    if authored_x_grid < 0 or authored_y_grid < 0:
        reasons.add("grid")
    birth_rate = float(emitter["birthRate"])
    life_expectancy = float(emitter["lifeExpectancy"])
    if update == "fountain" and (birth_rate <= 0 or life_expectancy <= 0):
        reasons.add("lifetime")
    if update == "single":
        frame_count = max(1, authored_x_grid) * max(1, authored_y_grid)
        constant_size = (
            float(emitter["sizeStart"]) == float(emitter["sizeMid"]) ==
            float(emitter["sizeEnd"])
        )
        persistent_sprite = (
            render in {"normal", "billboard_to_local_z"} and blend == "normal" and
            life_expectancy == -1.0 and birth_rate == 1.0 and
            float(emitter["velocity"]) == 0.0 and
            float(emitter["gravity"]) == 0.0 and
            constant_size and float(emitter["sizeStart"]) > 0 and
            int(emitter["frameStart"]) >= 1 and
            int(emitter["frameEnd"]) <= frame_count and
            int(emitter["frameStart"]) <= int(emitter["frameEnd"])
        )
        finite_particle = life_expectancy > 0
        if not persistent_sprite and not finite_particle:
            reasons.add("lifetime")
    visual_fields = {
        "sizeStart", "sizeMid", "sizeEnd", "frameStart", "frameEnd", "fps"
    }
    if visual_fields.issubset(emitter) and room_emitter_visual_safety_reasons(emitter):
        reasons.add("render")
    if not texture_available:
        reasons.add("texture")
    return tuple(sorted(reasons))


def validate_room_emitter_semantics(emitter: dict[str, Any], identity: str) -> None:
    reasons = room_emitter_unsupported_reasons(emitter)
    if reasons:
        raise RuntimeError(
            f"Unsupported KOTOR room-emitter semantic: {identity} "
            f"reasons={','.join(reasons)} update={emitter['update']} "
            f"render={emitter['render']} blend={emitter['blend']}")


def patch_glb_texture_channels(
    data: bytes, normal_scales: dict[str, float] | None = None
) -> bytes:
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
        material_name_value = str(material.get("name", ""))
        if (normal_scales and material_name_value in normal_scales and
                isinstance(material.get("normalTexture"), dict)):
            material["normalTexture"]["scale"] = normal_scales[material_name_value]
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
    model = read_owned_mdl(mdl_bytes, mdx_bytes)
    scene = trimesh.Scene(base_frame="kotor_model")
    mesh_count = 0
    vertex_count = 0
    triangle_count = 0
    diffuse_textures: set[str] = set()
    lightmaps: set[str] = set()
    material_contracts: dict[tuple[Any, ...], dict[str, Any]] = {}
    lights: list[dict[str, Any]] = []
    emitters: list[dict[str, Any]] = []
    walkmesh_triangles: list[list[list[float]]] = []

    def visit(node: Any, parent_transform: np.ndarray, parent_path: str) -> None:
        nonlocal mesh_count, vertex_count, triangle_count
        world_transform = parent_transform @ quaternion_matrix(node)
        node_path = f"{parent_path}/{node.name}" if parent_path else str(node.name)
        if node.emitter is not None:
            emitter = node.emitter

            texture_name = str(emitter.texture or "").strip()
            if not texture_name or texture_name.lower() == "null":
                raise RuntimeError(
                    f"Room emitter {model_name}/{node_path} has no texture")
            direction = world_transform @ np.asarray([0.0, 0.0, 1.0, 0.0])
            direction_length = np.linalg.norm(direction[:3])
            if direction_length <= 1e-12:
                raise RuntimeError(
                    f"Room emitter {model_name}/{node_path} has no direction")
            direction /= direction_length
            basis_right = world_transform @ np.asarray([1.0, 0.0, 0.0, 0.0])
            basis_up = world_transform @ np.asarray([0.0, 1.0, 0.0, 0.0])
            basis_forward = world_transform @ np.asarray([0.0, 0.0, 1.0, 0.0])
            authored_x_grid = int(emitter.x_grid)
            authored_y_grid = int(emitter.y_grid)
            controller_values = room_emitter_controller_values(node)
            point_to_point_target = room_emitter_point_to_point_target(node)
            point_to_point_target_position = None
            if point_to_point_target is not None:
                resolved_target = world_transform @ np.asarray(
                    [*point_to_point_target, 1.0], dtype=np.float64)
                point_to_point_target_position = [
                    float(item) for item in resolved_target[:3]
                ]
            emitter_record = {
                "schema": "nikami-aurora-kotor-room-emitter-v2",
                "nodePath": node_path,
                "authoredPosition": vector3(node.position),
                "position": [float(item) for item in world_transform[:3, 3]],
                "direction": [float(item) for item in direction[:3]],
                "basisRight": [float(item) for item in basis_right[:3]],
                "basisUp": [float(item) for item in basis_up[:3]],
                "basisForward": [float(item) for item in basis_forward[:3]],
                "texture": export_effect_texture(
                    installation, textures, texture_name, output_path.parent.parent),
                "update": str(emitter.update),
                "render": str(emitter.render),
                "blend": str(emitter.blend),
                "flags": int(emitter.flags),
                "spawnType": int(emitter.spawn_type),
                "loop": int(emitter.loop),
                "twoSidedTexture": int(emitter.two_sided_texture),
                "renderOrder": int(emitter.render_order),
                "frameBlender": int(emitter.frame_blender),
                "depthTexture": "" if str(emitter.depth_texture or "").strip().lower() == "null"
                else str(emitter.depth_texture or ""),
                "authoredXGrid": authored_x_grid,
                "authoredYGrid": authored_y_grid,
                "xGrid": max(1, authored_x_grid),
                "yGrid": max(1, authored_y_grid),
                "spawnWidthMeters": float(controller_values["xSize"]) * 0.01,
                "spawnHeightMeters": float(controller_values["ySize"]) * 0.01,
                "pointToPointTargetPosition": point_to_point_target_position,
                **controller_values,
            }
            validate_room_emitter_semantics(
                {**emitter_record,
                 "xGrid": authored_x_grid, "yGrid": authored_y_grid},
                f"{model_name}/{node_path}")
            emitters.append(emitter_record)
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
                contract_key = (
                    texture_name.lower(), lightmap_name.lower(),
                    int(mesh.transparency_hint), bool(mesh.animate_uv),
                    bool(mesh.background_geometry), bool(mesh.tangent_space),
                )
                if contract_key not in material_contracts:
                    contract = textures.material_semantics(
                        texture_name, lightmap_name)
                    contract.update({
                        "meshCount": 0,
                        "meshTransparencyHint": int(mesh.transparency_hint),
                        "animateUv": bool(mesh.animate_uv),
                        "backgroundGeometry": bool(mesh.background_geometry),
                        "tangentSpace": bool(mesh.tangent_space),
                    })
                    material_contracts[contract_key] = contract
                material_contracts[contract_key]["meshCount"] += 1
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
            visit(child, world_transform, node_path)

    visit(model.root, np.identity(4, dtype=np.float64), "")
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
        "materialContracts": sorted(
            material_contracts.values(),
            key=lambda item: (
                str(item["diffuseTexture"] or "").lower(),
                str(item["lightmapTexture"] or "").lower()),
        ),
        "lights": lights,
        "emitters": emitters,
        "walkmeshTriangles": walkmesh_triangles,
    }
    if mesh_count > 0:
        output_path.parent.mkdir(parents=True, exist_ok=True)
        normal_scales = {
            str(contract["materialName"]): float(contract["bumpMapScale"])
            for contract in material_contracts.values()
            if contract["bumpMapScaleAuthored"]
        }
        output_path.write_bytes(patch_glb_texture_channels(
            scene.export(file_type="glb"), normal_scales))
        record["glb"] = output_path.as_posix()
    return record


def source_room_placeholder_record(model_name: str) -> dict[str, Any]:
    if not is_source_room_placeholder(model_name):
        raise RuntimeError(f"Not an Odyssey room placeholder: {model_name}")
    return {
        "model": SOURCE_ROOM_PLACEHOLDER,
        "glb": None,
        "sourcePlaceholder": True,
        "mdlSha256": None,
        "mdxSha256": None,
        "meshCount": 0,
        "vertexCount": 0,
        "triangleCount": 0,
        "diffuseTextures": [],
        "lightmaps": [],
        "materialContracts": [],
        "lights": [],
        "emitters": [],
        "walkmeshTriangles": [],
    }


def mixed_source_material_counts(
    room_records: list[dict[str, Any]],
) -> tuple[int, int]:
    additive_environment = 0
    additive_lightmapped = 0
    for room in room_records:
        for contract in room["materialContracts"]:
            if contract["blend"] != "additive":
                continue
            surfaces = int(contract["meshCount"])
            if contract["environmentMap"]:
                additive_environment += surfaces
            if contract["lightmapTexture"]:
                additive_lightmapped += surfaces
    return additive_environment, additive_lightmapped


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


def export_humanoid_actor(
    installation: Installation,
    utc_resref: str,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
    animation_names: tuple[str, ...],
    module: str | None = None,
    animation_supermodels: tuple[str, ...] = ("S_Male02",),
) -> dict[str, Any]:
    utc_resource = (
        find_named_module_resource(installation, module, utc_resref, "UTC")
        if module else installation.resource(utc_resref, ResourceType.UTC)
    )
    if utc_resource is None:
        raise RuntimeError(f"{utc_resref}.utc could not be resolved")
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
        raise RuntimeError(f"{utc_resref} body model could not be resolved")

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
    animation_models = []
    animation_sources = []
    for animation_supermodel in animation_supermodels:
        model, source_hash = load_animation_supermodel(
            installation, mdlops, animation_cache, animation_supermodel)
        animation_models.append(model)
        animation_sources.append({
            "model": animation_supermodel,
            "sourceSha256": source_hash,
        })
    animations_by_name = {}
    for model in animation_models:
        for animation in model.anims:
            animations_by_name.setdefault(animation.name.lower(), animation)
    animation_model = SimpleNamespace(anims=list(animations_by_name.values()))
    source_models = (
        ("body", body_model, body_texture, body, body_mdl, body_mdx),
        ("head", head_model, head_texture, head, head_mdl, head_mdx),
        ("rightWeapon", right_model, None, right, right_mdl, right_mdx),
    )
    model_records = actor_model_records(source_models, textures)
    effect_records = actor_effect_records(
        installation, source_models, textures, output_path.parent.parent,
        animation_model)
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
        animation_names=animation_names,
        material_factory=lambda mesh, override: material_for(mesh, textures, override),
    )
    return {
        "glb": f"actors/{output_path.name}",
        "tag": str(utc.tag),
        "conversation": canonical_resref(utc.conversation),
        "utcSha256": sha256_bytes(utc_bytes),
        "factionId": int(utc.faction_id),
        "hitPoints": int(utc.hp),
        "currentHitPoints": int(utc.current_hp),
        "maxHitPoints": int(utc.max_hp),
        "minimumOneHitPoint": bool(utc.min1_hp),
        "noPermanentDeath": bool(utc.no_perm_death),
        "models": model_records,
        "effects": effect_records,
        "animationSource": animation_sources[0]["model"],
        "animationSourceSha256": animation_sources[0]["sourceSha256"],
        "animationSources": animation_sources,
        "animation": animation_report,
        "talkOffset": talk_offset,
    }


def export_source_creature_actor(
    installation: Installation,
    module: str,
    utc_resref: str,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
) -> dict[str, Any]:
    """Assemble one placed creature from its module UTC and owned model graph.

    Unlike the story-specific actor helpers, this path follows the UTC's exact
    appearance, equipment, body supermodel chain, and hook attachments.  A
    failed resolution is reported by the caller and is never substituted with
    a different creature.
    """
    utc_resource = find_named_module_resource(
        installation, module, utc_resref, "UTC")
    if utc_resource is None:
        raise RuntimeError(f"{utc_resref}.utc could not be resolved in {module}")
    utc_bytes = resource_data(utc_resource)
    utc = read_utc(utc_bytes)
    order = [SearchLocation.OVERRIDE, SearchLocation.CHITIN]

    def table(name: str) -> tuple[Any, bytes]:
        resource = installation.resource(name, ResourceType.TwoDA, order)
        if resource is None:
            raise RuntimeError(f"{name}.2da could not be resolved")
        data = resource_data(resource)
        return read_2da(data), data

    appearance, appearance_bytes = table("appearance")
    heads, heads_bytes = table("heads")
    baseitems, baseitems_bytes = table("baseitems")
    body_name, body_texture = creature_tools.get_body_model(
        utc, installation, appearance=appearance, baseitems=baseitems)
    head_name, head_texture = creature_tools.get_head_model(
        utc, installation, appearance=appearance, heads=heads)
    right_name, left_name = creature_tools.get_weapon_models(
        utc, installation, appearance=appearance, baseitems=baseitems)
    if not body_name:
        raise RuntimeError(f"{utc_resref} body model could not be resolved")

    body, body_mdl, body_mdx = load_model_pair(installation, body_name)
    head = head_mdl = head_mdx = None
    if head_name:
        head, head_mdl, head_mdx = load_model_pair(installation, head_name)
    right = right_mdl = right_mdx = None
    if right_name:
        right, right_mdl, right_mdx = load_model_pair(installation, right_name)
    left = left_mdl = left_mdx = None
    if left_name:
        left, left_mdl, left_mdx = load_model_pair(installation, left_name)

    animation_models = [body]
    animation_sources = [{
        "model": body_name,
        "sourceSha256": sha256_bytes(body_mdl + body_mdx),
        "kind": "body",
    }]
    supermodel_name = str(getattr(body, "supermodel", "") or "").strip()
    visited = {str(body_name).casefold()}
    while supermodel_name and supermodel_name.casefold() not in {"null", "****"}:
        key = supermodel_name.casefold()
        if key in visited:
            raise RuntimeError(
                f"Creature supermodel cycle for {utc_resref}: {supermodel_name}")
        if len(visited) >= 16:
            raise RuntimeError(
                f"Creature supermodel depth exceeded for {utc_resref}")
        visited.add(key)
        supermodel, source_hash = load_animation_supermodel(
            installation, mdlops, animation_cache, supermodel_name)
        animation_models.append(supermodel)
        animation_sources.append({
            "model": supermodel_name,
            "sourceSha256": source_hash,
            "kind": "supermodel",
        })
        supermodel_name = str(
            getattr(supermodel, "supermodel", "") or "").strip()

    animations_by_name: dict[str, Any] = {}
    animation_origins: dict[str, dict[str, str]] = {}
    for model_index, model in enumerate(animation_models):
        for animation in model.anims:
            key = animation.name.casefold()
            if key in animations_by_name:
                continue
            animations_by_name[key] = animation
            animation_origins[key] = animation_sources[model_index]
    def translation_policy(origin: dict[str, str]) -> str:
        kind = origin.get("kind")
        if kind == "body":
            return "rest-plus-source-delta"
        if kind == "supermodel":
            return "source-absolute"
        raise RuntimeError(
            f"Unsupported creature animation origin for {utc_resref}: {kind}")

    animation_model = SimpleNamespace(
        anims=list(animations_by_name.values()),
        translation_policies={
            key: translation_policy(animation_origins[key])
            for key in animations_by_name
        },
    )
    idle_animation = next(
        (animation.name for animation in animation_model.anims
         if animation.name.casefold() == "pause1"),
        next(
            (animation.name for animation in animation_model.anims
             if animation.name.casefold().endswith("pause1")),
            None),
    )
    idle_origin = animation_origins.get(
        idle_animation.casefold() if idle_animation else "")
    source_models = (
        ("body", body_name, body_texture, body, body_mdl, body_mdx),
        ("head", head_name, head_texture, head, head_mdl, head_mdx),
        ("rightWeapon", right_name, None, right, right_mdl, right_mdx),
        ("leftWeapon", left_name, None, left, left_mdl, left_mdx),
    )
    model_records = actor_model_records(source_models, textures)
    effect_records = actor_effect_records(
        installation, source_models, textures, output_path.parent.parent,
        animation_model)
    exported_animation_names = tuple(dict.fromkeys([
        *([idle_animation] if idle_animation else []),
        *(record["name"] for record in effect_records["animations"]),
    ]))
    animation_report = export_actor(
        output_path,
        body_model=body,
        body_name=body_name,
        body_texture=body_texture,
        head_model=head,
        head_name=head_name,
        head_texture=head_texture,
        weapon_model=right,
        weapon_name=right_name,
        left_weapon_model=left,
        left_weapon_name=left_name,
        animation_model=animation_model,
        animation_names=exported_animation_names,
        material_factory=lambda mesh, override: material_for(
            mesh, textures, override),
    )
    talk_offset = None
    if head is not None:
        head_hook = find_node_transform(body, "headhook")
        talk_dummy = find_node_transform(head, "talkdummy")
        if head_hook is not None and talk_dummy is not None:
            talk_offset = [
                float(item) for item in (head_hook @ talk_dummy)[:3, 3]]

    return {
        "renderImportSchema": "nikami-aurora-kotor-source-creature-v1",
        "renderStatus": "ready",
        "glb": f"actors/{output_path.name}",
        "sourceTemplate": normalize_module_id(utc_resref),
        "utcSha256": sha256_bytes(utc_bytes),
        "appearanceId": int(utc.appearance_id),
        "appearanceTableSha256": sha256_bytes(appearance_bytes),
        "headsTableSha256": sha256_bytes(heads_bytes),
        "baseItemsTableSha256": sha256_bytes(baseitems_bytes),
        "models": model_records,
        "effects": effect_records,
        "animationSources": animation_sources,
        "idleAnimationOrigin": idle_origin,
        "animation": animation_report,
        "idleAnimation": idle_animation,
        "renderExtent": animation_report["extent"],
        "talkOffset": talk_offset,
    }


def export_source_creature_records(
    installation: Installation,
    module: str,
    git: Any,
    output_root: Path,
    textures: TextureCache,
    mdlops: Path,
) -> list[dict[str, Any]]:
    exports: dict[str, dict[str, Any]] = {}
    records: list[dict[str, Any]] = []
    for creature in git.creatures:
        template = canonical_resref(creature.resref)
        key = template.casefold()
        if key not in exports:
            try:
                exports[key] = export_source_creature_actor(
                    installation,
                    module,
                    template,
                    output_root / "actors" / f"creature-{key}.glb",
                    textures,
                    mdlops,
                    output_root / "_cache" / "animations",
                )
            except Exception as exc:
                exports[key] = {
                    "renderImportSchema":
                        "nikami-aurora-kotor-source-creature-v1",
                    "renderStatus": "unsupported",
                    "glb": None,
                    "sourceTemplate": key,
                    "renderBlocker":
                        f"{type(exc).__name__}:{str(exc).strip() or '<empty>'}",
                }
        records.append({
            "template": template,
            "tag": str(getattr(creature, "tag", "")),
            "position": vector3(creature.position),
            "bearing": float(creature.bearing),
            **exports[key],
        })
    return records


def creature_presentation_counts(
    creatures: Iterable[dict[str, Any]],
) -> dict[str, int]:
    records = list(creatures)
    models = [
        model
        for creature in records
        for model in creature.get("models", [])
    ]
    weapons = [
        model for model in models
        if model["role"] in {"rightWeapon", "leftWeapon"}
    ]
    return {
        "authoredCreatureModels": len(models),
        "equippedWeaponModels": len(weapons),
        "equippedWeaponAdditiveSurfaces": sum(
            int(model["additiveSurfaces"]) for model in weapons),
        "authoredCreatureEmitters": sum(
            len(creature.get("effects", {}).get("emitters", []))
            for creature in records),
        "authoredCreatureLights": sum(
            len(creature.get("effects", {}).get("lights", []))
            for creature in records),
        "authoredCreatureEffectAnimations": sum(
            len(creature.get("effects", {}).get("animations", []))
            for creature in records),
    }


def export_trask_actor(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
    module: str | None = None,
) -> dict[str, Any]:
    return export_humanoid_actor(
        installation,
        "end_trask",
        output_path,
        textures,
        mdlops,
        animation_cache,
        ("pause1", "tlknorm", "walk", "talk", "getfromgnd", "usecomplp"),
        module,
    )


def export_carth_actor(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
    module: str | None = None,
) -> dict[str, Any]:
    return export_humanoid_actor(
        installation,
        "p_carth001",
        output_path,
        textures,
        mdlops,
        animation_cache,
        ("pause1", "tlknorm", "walk", "talk"),
        module,
    )


def export_player_actor(
    installation: Installation,
    output_path: Path,
    equipped_output_path: Path,
    equipment_items: list[dict[str, Any]],
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
    portrait_resref = str(portraits.get_cell(portrait_id, "baseresref"))
    if not portrait_resref or installation.texture(portrait_resref) is None:
        raise RuntimeError(
            f"Player portrait texture could not be resolved: portrait row {portrait_id}")
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
        animation_names=("pause1", "walk", "run", "talk"),
        material_factory=lambda mesh, override: material_for(mesh, textures, override),
    )

    armor_items = [item for item in equipment_items if int(item["equipableSlots"]) & 0x00002]
    right_hand_items = [
        item for item in equipment_items
        if int(item["equipableSlots"]) & 0x00010 and item["defaultModel"]
    ]
    if len(armor_items) != 1 or len(right_hand_items) != 1:
        raise RuntimeError(
            "Opening locker must resolve exactly one armor and one right-hand item")
    armor_item = armor_items[0]
    right_hand_item = right_hand_items[0]
    body_variation = str(armor_item["bodyVar"]).lower()
    if len(body_variation) != 1 or not body_variation.isalpha():
        raise RuntimeError(
            f"Opening clothing body variation is invalid: {armor_item['bodyVar']}")
    equipped_body_name = str(appearance.get_cell(
        appearance_id, f"model{body_variation}"))
    equipped_texture_prefix = str(appearance.get_cell(
        appearance_id, f"tex{body_variation}"))
    equipped_texture = (
        f"{equipped_texture_prefix}{int(armor_item['textureVariation']):02d}")
    if installation.texture(equipped_texture) is None:
        equipped_texture = f"{equipped_texture_prefix}01"
        if installation.texture(equipped_texture) is None:
            raise RuntimeError(
                f"Opening clothing texture could not be resolved: {equipped_texture_prefix}")
    weapon_model_name = str(right_hand_item["defaultModel"]).replace(
        "001", f"{int(right_hand_item['modelVariation']):03d}")
    equipped_body, equipped_body_mdl, equipped_body_mdx = load_model_pair(
        installation, equipped_body_name)
    weapon, weapon_mdl, weapon_mdx = load_model_pair(installation, weapon_model_name)

    def equipment_variant(
        variant_id: str,
        variant_output_path: Path,
        variant_body: Any,
        variant_body_name: str,
        variant_body_texture: str,
        variant_body_mdl: bytes,
        variant_body_mdx: bytes,
        armor: dict[str, Any] | None,
        left_hand: dict[str, Any] | None,
        right_hand: dict[str, Any] | None,
    ) -> dict[str, Any]:
        if left_hand is not None and right_hand is not None:
            raise RuntimeError(
                "Opening single-weapon variant cannot target both hands")
        hand_item = right_hand if right_hand is not None else left_hand
        variant_weapon = weapon if hand_item is not None else None
        variant_weapon_name = weapon_model_name if hand_item is not None else None
        weapon_hook = "rhand" if right_hand is not None else "lhand"
        variant_animation = export_actor(
            variant_output_path,
            body_model=variant_body,
            body_name=variant_body_name,
            body_texture=variant_body_texture,
            head_model=head,
            head_name=head_name,
            head_texture=None,
            weapon_model=variant_weapon,
            weapon_name=variant_weapon_name,
            animation_model=animation_model,
            animation_names=("pause1", "walk", "run", "talk"),
            material_factory=lambda mesh, override: material_for(mesh, textures, override),
            weapon_hook=weapon_hook,
        )
        variant_head_hook = find_node_transform(variant_body, "headhook")
        variant_camera_hook = find_node_transform(variant_body, "camerahook")
        variant_talk_offset = None
        if variant_head_hook is not None and talk_dummy is not None:
            variant_talk_transform = variant_head_hook @ talk_dummy
            variant_talk_offset = [
                float(item) for item in variant_talk_transform[:3, 3]]
        models = [
            {
                "model": variant_body_name,
                "overrideTexture": variant_body_texture,
                "mdlSha256": sha256_bytes(variant_body_mdl),
                "mdxSha256": sha256_bytes(variant_body_mdx),
            },
            {
                "model": head_name,
                "overrideTexture": None,
                "mdlSha256": sha256_bytes(head_mdl),
                "mdxSha256": sha256_bytes(head_mdx),
            },
        ]
        if hand_item is not None:
            models.append({
                "model": weapon_model_name,
                "overrideTexture": None,
                "mdlSha256": sha256_bytes(weapon_mdl),
                "mdxSha256": sha256_bytes(weapon_mdx),
            })
        return {
            "schema": "nikami-aurora-kotor-player-equipment-v1",
            "id": variant_id,
            "glb": f"actors/{variant_output_path.name}",
            "armorResref": armor["resref"] if armor is not None else None,
            "leftHandResref": (
                left_hand["resref"] if left_hand is not None else None),
            "rightHandResref": (
                right_hand["resref"] if right_hand is not None else None),
            "bodyModel": variant_body_name,
            "bodyTexture": variant_body_texture,
            "headModel": head_name,
            "weaponModel": variant_weapon_name,
            "weaponHook": weapon_hook if hand_item is not None else None,
            "talkOffset": variant_talk_offset,
            "cameraOffset": (
                [float(item) for item in variant_camera_hook[:3, 3]]
                if variant_camera_hook is not None else None
            ),
            "animation": variant_animation,
            "armorUtiSha256": (
                armor["utiSha256"] if armor is not None else None),
            "leftHandUtiSha256": (
                left_hand["utiSha256"] if left_hand is not None else None),
            "rightHandUtiSha256": (
                right_hand["utiSha256"] if right_hand is not None else None),
            "baseItemsSha256": armor_item["baseItemsSha256"],
            "models": models,
        }

    head_hook = find_node_transform(body, "headhook")
    talk_dummy = find_node_transform(head, "talkdummy")
    camera_hook = find_node_transform(body, "camerahook")
    talk_offset = None
    if head_hook is not None and talk_dummy is not None:
        talk_transform = head_hook @ talk_dummy
        talk_offset = [float(item) for item in talk_transform[:3, 3]]
    clothing_output_path = equipped_output_path.with_name(
        "player-opening-clothing.glb")
    sword_output_path = equipped_output_path.with_name(
        "player-opening-short-sword.glb")
    left_sword_output_path = equipped_output_path.with_name(
        "player-opening-left-short-sword.glb")
    clothing_left_sword_output_path = equipped_output_path.with_name(
        "player-opening-clothing-left-short-sword.glb")
    equipment_variants = [
        equipment_variant(
            "opening-clothing",
            clothing_output_path,
            equipped_body,
            equipped_body_name,
            equipped_texture,
            equipped_body_mdl,
            equipped_body_mdx,
            armor_item,
            None,
            None,
        ),
        equipment_variant(
            "opening-left-short-sword",
            left_sword_output_path,
            body,
            body_name,
            body_texture,
            body_mdl,
            body_mdx,
            None,
            right_hand_item,
            None,
        ),
        equipment_variant(
            "opening-clothing-left-short-sword",
            clothing_left_sword_output_path,
            equipped_body,
            equipped_body_name,
            equipped_texture,
            equipped_body_mdl,
            equipped_body_mdx,
            armor_item,
            right_hand_item,
            None,
        ),
        equipment_variant(
            "opening-short-sword",
            sword_output_path,
            body,
            body_name,
            body_texture,
            body_mdl,
            body_mdx,
            None,
            None,
            right_hand_item,
        ),
        equipment_variant(
            "opening-clothing-short-sword",
            equipped_output_path,
            equipped_body,
            equipped_body_name,
            equipped_texture,
            equipped_body_mdl,
            equipped_body_mdx,
            armor_item,
            None,
            right_hand_item,
        ),
    ]
    return {
        "schema": "nikami-aurora-kotor-player-v1",
        "glb": f"actors/{output_path.name}",
        "portraitId": portrait_id,
        "portraitResref": portrait_resref,
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
        "equipmentVariants": equipment_variants,
    }


def export_generic_player_actor(
    installation: Installation,
    output_path: Path,
    textures: TextureCache,
    mdlops: Path,
    animation_cache: Path,
    appearance_id: int = 137,
    portrait_id: int = 18,
) -> dict[str, Any]:
    """Export the source-bound base avatar without Endar locker variants."""
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
    portrait_resref = str(portraits.get_cell(portrait_id, "baseresref"))
    if not portrait_resref or installation.texture(portrait_resref) is None:
        raise RuntimeError(
            f"Player portrait texture could not be resolved: portrait row {portrait_id}")
    body_name = str(appearance.get_cell(appearance_id, "modela"))
    body_texture_prefix = str(appearance.get_cell(appearance_id, "texa"))
    body_texture = body_texture_prefix
    if installation.texture(body_texture) is None:
        body_texture = f"{body_texture_prefix}01"
        if installation.texture(body_texture) is None:
            raise RuntimeError(
                f"Player body texture could not be resolved: {body_texture_prefix}")
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
        animation_names=("pause1", "walk", "run", "talk"),
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
        "portraitResref": portrait_resref,
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
        "equipmentVariants": [],
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
    animations_resource = installation.resource("animations", ResourceType.TwoDA)
    if animations_resource is None:
        raise RuntimeError("animations.2da could not be resolved")
    animations = read_2da(resource_data(animations_resource))

    def node_key(node: Any) -> str:
        kind = "entry" if isinstance(node, DLGEntry) else "reply"
        return f"{kind}:{int(node.list_index)}"

    def text_ref(node: Any) -> int:
        return int(node.text.stringref)

    def local_text(node: Any) -> str:
        stringref = text_ref(node)
        return talktable.string(stringref) if stringref >= 0 else ""

    def animation_record(animation: Any) -> dict[str, Any]:
        animation_id = int(animation.animation_id)
        return {
            "animationId": animation_id,
            "animationName": str(animations.get_cell(animation_id, "name")),
            "looping": bool(int(animations.get_cell(animation_id, "looping"))),
            "fireForget": bool(int(animations.get_cell(animation_id, "fireforget"))),
            "participant": str(animation.participant),
        }

    def media_resref(node: Any) -> str:
        sound = canonical_resref(getattr(node, "sound", ""))
        return sound or canonical_resref(getattr(node, "vo_resref", ""))

    sound_names = {
        media_resref(node)
        for node in all_nodes
        if media_resref(node)
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
        voice_name = canonical_resref(getattr(node, "vo_resref", ""))
        media_name = sound_name or voice_name
        nodes[key] = {
            "kind": "entry" if isinstance(node, DLGEntry) else "reply",
            "listIndex": int(node.list_index),
            "textRef": text_ref(node),
            "text": local_text(node),
            "speaker": str(getattr(node, "speaker", "")),
            "listener": str(getattr(node, "listener", "")),
            "voice": voice_name,
            "sound": sound_name,
            "media": media_by_sound.get(media_name.lower()) if media_name else None,
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
    if dialogue_name.lower() == "end_trask01":
        if len(starters) <= 8:
            raise RuntimeError("end_trask01 no longer contains starter 8")
        corridor_starter = starters[8]
        corridor_node = nodes[corridor_starter["target"]]
        if (corridor_starter["condition1"].lower() != "k_pend_traskdl14" or
                corridor_node["speaker"].lower() != "carth" or
                corridor_node["sound"].lower() != "nm01aatras02057_" or
                int(corridor_node["cameraId"]) != 1):
            raise RuntimeError("end_trask01 starter 8 no longer matches the corridor contract")

        continuation = {
            "entry:32": (
                "carth", "nm01aatras02057_", "k_pend_cadlg_inc", 45144,
                "F9FD9BC2306476F33575EEE4571179565EBE7C6664045C69D39FD706B81BBE35",
                58, "A5414EA825DE77A5E8D3C358B8204D375B0993B4044B3F96F4A234F66880B777"),
            "entry:33": (
                "end_trask", "nm01aatras02058_", "", 49896,
                "59FA95B831CCD2882B1B440B5B263C9E9D16DD7682A2DDA7486CC19F5E10DB4A",
                78, "AB84D55486948D302F3F19D5E4A7CE28834AB57F4B88FAABDED03B44673EA411"),
            "entry:34": (
                "end_trask", "nm01aatras02059_", "k_pend_traskdl47", 30240,
                "34C398210BC4D2C59325EAEA5BDFB5AC548EABACA2C8C1349CC57BDB8DDDC868",
                43, "8BAC67FB32447637BF62605E1FBB830894EF907DB98A011ED352DC164C6C593F"),
            "entry:35": (
                "end_trask", "nm01aatras02243_", "k_pend_map", 33696,
                "C66D925EDB856DF253B0EEE29DB2710FE9CC89E49EB1AC56AB22AFF3B56FD6B7",
                59, "E5BDFA7038B1CC30F397276C7218B01E5BCE14E683263AC954378D17E1B87F30"),
        }
        for key, expected in continuation.items():
            node = nodes[key]
            media = node["media"] or {}
            actual = (
                node["speaker"].lower(), node["sound"].lower(), node["script1"].lower(),
                media.get("audioByteCount"), media.get("audioSha256"),
                media.get("lipFrameCount"), media.get("lipSourceSha256"),
            )
            if actual != expected:
                raise RuntimeError(f"end_trask01 continuation node drifted: {key}")
        automatic_chain = [
            ("entry:32", "reply:43", "entry:33", ""),
            ("entry:33", "reply:44", "entry:34", ""),
            ("entry:34", "reply:45", "entry:35", "k_pend_carth11"),
        ]
        for entry_key, reply_key, next_key, reply_script in automatic_chain:
            entry = nodes[entry_key]
            reply = nodes[reply_key]
            if (len(entry["links"]) != 1 or entry["links"][0]["target"] != reply_key or
                    reply["kind"] != "reply" or reply["text"].strip() or
                    reply["script1"].lower() != reply_script or len(reply["links"]) != 1 or
                    reply["links"][0]["target"] != next_key):
                raise RuntimeError(
                    f"end_trask01 automatic continuation drifted: {entry_key}")
        if [link["target"] for link in nodes["entry:35"]["links"]] != ["reply:50", "reply:46"]:
            raise RuntimeError("end_trask01 journal choices no longer match the corridor contract")
        showcase_sounds = {
            "entry:55": "nm01aatras02000_",
            "entry:57": "nm01aatras02002_",
            "entry:58": "nm01aatras02003_",
            "entry:61": "nm01aatras02005_",
            "entry:62": "nm01aatras02007_",
            "entry:69": "nm01aatras02233_",
            "entry:70": "nm01aatras02234_",
            "entry:71": "nm01aatras02008_",
            "entry:73": "nm01aatras02010_",
        }
        if (starters[0]["target"] != "entry:54" or
                nodes["entry:54"]["script1"].lower() != "k_pend_traskdl40" or
                any(nodes[key]["sound"].lower() != sound
                    for key, sound in showcase_sounds.items())):
            raise RuntimeError("end_trask01 showcase opening path drifted")
        showcase_choices = {
            "entry:55": ("reply:74", "reply:72"),
            "entry:58": ("reply:79", "reply:76"),
            "entry:71": ("reply:90", "reply:88"),
            "entry:73": ("reply:92", "reply:91"),
            "entry:35": ("reply:50", "reply:46"),
        }
        for key, expected_links in showcase_choices.items():
            if tuple(link["target"] for link in nodes[key]["links"]) != expected_links:
                raise RuntimeError(f"end_trask01 showcase choice drifted: {key}")
        showcase_automatic = [
            ("entry:54", "reply:71", "entry:55"),
            ("entry:57", "reply:75", "entry:58"),
            ("entry:61", "reply:80", "entry:62"),
            ("entry:69", "reply:86", "entry:70"),
            ("entry:70", "reply:87", "entry:71"),
        ]
        for entry_key, reply_key, next_key in showcase_automatic:
            entry = nodes[entry_key]
            reply = nodes[reply_key]
            if (len(entry["links"]) != 1 or entry["links"][0]["target"] != reply_key or
                    reply["text"].strip() or not reply["links"] or
                    reply["links"][0]["target"] != next_key):
                raise RuntimeError(
                    f"end_trask01 showcase automatic path drifted: {entry_key}")
        if ([link["target"] for link in nodes["reply:81"]["links"]][0] != "entry:69" or
                nodes["reply:92"]["links"] or nodes["reply:50"]["links"]):
            raise RuntimeError("end_trask01 showcase terminal path drifted")
    if dialogue_name.lower() == "end_room3":
        expected_controls = {
            "entry:0": (26, "k_pend_camera", ["c3d4", "c3d4", "c3d4"]),
            "entry:1": (20, "k_pend_cut1_1", ["c3d4", "c3d4", "c3d4"]),
        }
        for key, expected in expected_controls.items():
            node = nodes[key]
            actual = (
                int(node["cameraId"]), node["script1"].lower(),
                [item["animationName"].lower() for item in node["animations"]],
            )
            if actual != expected:
                raise RuntimeError(f"end_room3 cutscene control drifted: {key}")
        expected_voice = {
            "entry:4": (
                "nm01aaroom03000_", 12960,
                "3D6A8D62D0DD9BEEBDF6EF5DAD75CD4BF7C4C90182422C112067321CBC87C201",
                24, "14CC5EB73807302BC824E3097CDAE95D6E2510414F536D2366655235C835676C"),
            "entry:5": (
                "nm01aaroom03001_", 6264,
                "0D6A915951F1BC2877009F0BD017B5C44999E43E8A2D79EF6C908C19BA4BC337",
                8, "24A4DC81553662FECBD0621E3612A2A79CE9873A8F4BE0DABD8CB0BF4C23E909"),
        }
        for key, expected in expected_voice.items():
            node = nodes[key]
            media = node["media"] or {}
            actual = (
                node["voice"].lower(), media.get("audioByteCount"),
                media.get("audioSha256"), media.get("lipFrameCount"),
                media.get("lipSourceSha256"),
            )
            if actual != expected:
                raise RuntimeError(f"end_room3 voice/LIP drifted: {key}")
        reachable_chain = [
            ("entry:0", "reply:0", "entry:1"),
            ("entry:1", "reply:1", "entry:4"),
            ("entry:4", "reply:4", "entry:5"),
            ("entry:5", "reply:5", None),
        ]
        for entry_key, reply_key, next_key in reachable_chain:
            entry = nodes[entry_key]
            reply = nodes[reply_key]
            if len(entry["links"]) != 1 or entry["links"][0]["target"] != reply_key:
                raise RuntimeError(f"end_room3 entry link drifted: {entry_key}")
            if reply["text"].strip() or (next_key is not None and
                    (not reply["links"] or reply["links"][0]["target"] != next_key)):
                raise RuntimeError(f"end_room3 control reply drifted: {reply_key}")
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


def export_door(
    installation: Installation,
    module: str,
    template: str,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    utd_resource = find_named_module_resource(
        installation, module, template, "UTD")
    utd_bytes = resource_data(utd_resource)
    utd = read_utd(utd_bytes)
    order = [SearchLocation.OVERRIDE, SearchLocation.CHITIN]
    genericdoors_resource = installation.resource("genericdoors", ResourceType.TwoDA, order)
    if genericdoors_resource is None:
        raise RuntimeError("genericdoors.2da could not be resolved")
    genericdoors = read_2da(resource_data(genericdoors_resource))
    model_name = door_tools.get_model(utd, installation, genericdoors=genericdoors)
    if not model_name:
        raise RuntimeError(f"Door model could not be resolved: {template}")
    scene = trimesh.Scene(base_frame=template)
    _, model_record = add_actor_model(
        scene, installation, model_name, textures, np.identity(4))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(patch_glb_texture_channels(scene.export(file_type="glb")))
    return {
        "glb": f"doors/{output_path.name}",
        "model": model_name,
        "tag": str(utd.tag),
        "conversation": canonical_resref(utd.conversation),
        "onOpen": canonical_resref(utd.on_open),
        "onOpenFailed": canonical_resref(utd.on_open_failed),
        "onDeath": canonical_resref(utd.on_death),
        "locked": bool(utd.locked),
        "plot": bool(utd.plot),
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
                "description": installation.string(uti.description, resref),
                "cost": int(uti.cost),
                "tag": str(uti.tag),
                "baseItem": base_item,
                "charges": int(uti.charges),
                "stackSize": int(uti.stack_size),
                "modelVariation": int(uti.model_variation),
                "bodyVariation": int(uti.body_variation),
                "textureVariation": int(uti.texture_variation),
                "equipableSlots": int(slots_text, 0) if slots_text else 0,
                "plot": bool(uti.plot),
                "itemClass": base_cell("itemclass"),
                "modelType": int(base_cell("modeltype") or "0"),
                "defaultModel": base_cell("defaultmodel"),
                "defaultIcon": base_cell("defaulticon"),
                "bodyVar": base_cell("bodyvar"),
                "utiSha256": sha256_bytes(uti_bytes),
                "baseItemsSha256": sha256_bytes(baseitems_bytes),
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
        "static": bool(utp.static),
        "useable": bool(utp.useable),
        "hasInventory": bool(utp.has_inventory),
        "inventory": list(item_stacks.values()),
        "animationState": int(utp.animation_state),
        "utpSha256": sha256_bytes(utp_bytes),
        "baseItemsSha256": sha256_bytes(baseitems_bytes),
        "modelSource": model_record,
    }


def export_static_placeable(
    installation: Installation,
    template: str,
    output_path: Path,
    textures: TextureCache,
) -> dict[str, Any]:
    utp_resource = installation.resource(template, ResourceType.UTP)
    if utp_resource is None:
        raise RuntimeError(f"{template}.utp could not be resolved")
    utp_bytes = resource_data(utp_resource)
    utp = read_utp(utp_bytes)
    placeables_resource = installation.resource("placeables", ResourceType.TwoDA)
    if placeables_resource is None:
        raise RuntimeError("placeables.2da could not be resolved")
    placeables = read_2da(resource_data(placeables_resource))
    model_name = str(placeables.get_cell(int(utp.appearance_id), "modelname"))
    if not model_name:
        raise RuntimeError(f"{template} placeable model could not be resolved")
    scene = trimesh.Scene(base_frame=template)
    _, model_record = add_actor_model(
        scene, installation, model_name, textures, np.identity(4))
    output_path.parent.mkdir(parents=True, exist_ok=True)
    output_path.write_bytes(patch_glb_texture_channels(scene.export(file_type="glb")))
    return {
        "glb": f"placeables/{output_path.name}",
        "model": model_name,
        "tag": str(utp.tag),
        "onInventory": canonical_resref(utp.on_inventory),
        "locked": bool(utp.locked),
        "static": bool(utp.static),
        "useable": bool(utp.useable),
        "hasInventory": bool(utp.has_inventory),
        "animationState": int(utp.animation_state),
        "utpSha256": sha256_bytes(utp_bytes),
        "modelSource": model_record,
    }


def export_triggers(installation: Installation, triggers: list[Any]) -> list[dict[str, Any]]:
    records = []
    for trigger in triggers:
        template = canonical_resref(trigger.resref)
        utt_resource = installation.resource(template, ResourceType.UTT)
        if utt_resource is None:
            raise RuntimeError(f"Trigger template could not be resolved: {template}.utt")
        utt_bytes = resource_data(utt_resource)
        utt = read_utt(utt_bytes)
        records.append({
            "template": template,
            "tag": str(utt.tag),
            "position": vector3(trigger.position),
            "geometry": [vector3(point) for point in trigger.geometry],
            "onEnter": canonical_resref(utt.on_enter),
            "highlightHeight": float(utt.highlight_height),
            "uttSha256": sha256_bytes(utt_bytes),
        })
    return records


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


def export_opening_script_contracts(
    installation: Installation, plot_table: Any, module: str
) -> list[dict[str, Any]]:
    plot_rows = {
        str(plot_table.get_cell(index, "label")).lower(): int(plot_table.get_cell(index, "xp"))
        for index in range(plot_table.get_height())
    }
    plot_label = "end_tutorial"
    plot_base_xp = plot_rows[plot_label]

    def load_script(resref: str) -> tuple[bytes, Any]:
        resource = find_named_module_resource(installation, module, resref, "NCS")
        data = resource_data(resource)
        return data, read_ncs(data)

    def initialized_integer(ncs: Any, bp_offset: int) -> int | None:
        if bp_offset >= 0 or bp_offset % 4:
            raise ValueError(f"Invalid NCS base-pointer offset: {bp_offset}")
        save_bp = next(
            index for index, instruction in enumerate(ncs.instructions)
            if instruction.ins_type.name == "SAVEBP")
        initializers: list[int | None] = []
        for index, instruction in enumerate(ncs.instructions[:save_bp]):
            if not instruction.ins_type.name.startswith("RSADD"):
                continue
            following = ncs.instructions[index + 1]
            initializers.append(
                int(following.args[0])
                if following.ins_type.name == "CONSTI" else None)
        slot = len(initializers) + bp_offset // 4
        return initializers[slot] if 0 <= slot < len(initializers) else None

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

    trigger_data, trigger_ncs = load_script("k_pend_trig02")
    trigger_global_expected = [
        ("CPTOPSP", (-4, 4)),
        ("CONSTS", ("END_TRASK_DLG",)),
        ("ACTION", (581, 2)),
        ("MOVSP", (-4,)),
        ("RETN", ()),
    ]
    trigger_signal_expected = [
        ("CONSTF", (0.5,)),
        ("ACTION", (759, 1)),
        ("STORE_STATE", (760, 4)),
        ("JMP", ()),
        ("CONSTI", (50,)),
        ("ACTION", (132, 1)),
        ("CPTOPSP", (-8, 4)),
        ("ACTION", (131, 2)),
        ("RETN", ()),
        ("CONSTF", (0.10000000149011612,)),
        ("ACTION", (7, 2)),
    ]
    trigger_tag_expected = [
        ("CONSTI", (0,)),
        ("CPTOPBP", (-180, 4)),
        ("ACTION", (200, 2)),
    ]
    if (find_instruction_sequence(trigger_ncs.instructions, trigger_global_expected) is None or
            find_instruction_sequence(trigger_ncs.instructions, trigger_signal_expected) is None or
            find_instruction_sequence(trigger_ncs.instructions, trigger_tag_expected) is None):
        raise RuntimeError("k_pend_trig02 no longer matches the verified trigger contract")

    actor_data, actor_ncs = load_script("k_pend_trask_d")
    actor_actions = [
        tuple(instruction.args)
        for instruction in actor_ncs.instructions
        if instruction.ins_type.name == "ACTION"
    ]
    if ((247, 0) not in actor_actions or (9, 0) not in actor_actions or
            (548, 0) not in actor_actions or (204, 11) not in actor_actions or
            not any(instruction.ins_type.name == "CONSTI" and instruction.args == [50]
                    for instruction in actor_ncs.instructions)):
        raise RuntimeError("k_pend_trask_d no longer matches the verified event-50 contract")

    condition_data, condition_ncs = load_script("k_pend_traskdl14")
    condition_global_expected = [
        ("JSR", ()),
        ("CPTOPBP", (-76, 4)),
        ("EQUALII", ()),
        ("CPDOWNSP", (-8, 4)),
        ("MOVSP", (-4,)),
    ]
    condition_read_expected = [
        ("CONSTS", ("END_TRASK_DLG",)),
        ("ACTION", (580, 1)),
    ]
    if (find_instruction_sequence(condition_ncs.instructions, condition_global_expected) is None or
            find_instruction_sequence(condition_ncs.instructions, condition_read_expected) is None):
        raise RuntimeError("k_pend_traskdl14 no longer selects END_TRASK_DLG value 10")

    carth_dialogue_data, carth_dialogue_ncs = load_script("k_pend_cadlg_inc")
    carth_dialogue_expected = [
        ("JSR", ()),
        ("RETN", ()),
        ("RSADDI", ()),
        ("CONSTS", ("END_CARTH_DLG",)),
        ("ACTION", (580, 1)),
        ("CPDOWNSP", (-8, 4)),
        ("MOVSP", (-4,)),
        ("CPTOPSP", (-4, 4)),
        ("CONSTI", (1,)),
        ("ADDII", ()),
        ("CONSTS", ("END_CARTH_DLG",)),
        ("ACTION", (581, 2)),
        ("MOVSP", (-4,)),
        ("RETN", ()),
    ]
    if [ncs_signature(item) for item in carth_dialogue_ncs.instructions] != carth_dialogue_expected:
        raise RuntimeError("k_pend_cadlg_inc no longer increments END_CARTH_DLG")

    trask_dialogue_data, trask_dialogue_ncs = load_script("k_pend_traskdl47")
    trask_dialogue_expected = [
        ("CPTOPBP", (-72, 4)),
        ("JSR", ()),
        ("RETN", ()),
        ("CPTOPSP", (-4, 4)),
        ("CONSTS", ("END_TRASK_DLG",)),
        ("ACTION", (581, 2)),
        ("MOVSP", (-4,)),
        ("RETN", ()),
    ]
    if (find_instruction_sequence(trask_dialogue_ncs.instructions, trask_dialogue_expected) is None or
            initialized_integer(trask_dialogue_ncs, -72) != 11):
        raise RuntimeError("k_pend_traskdl47 no longer sets END_TRASK_DLG value 11")

    map_data, map_ncs = load_script("k_pend_map")
    map_expected = [
        ("JSR", ()),
        ("RETN", ()),
        ("CONSTI", (4294967295,)),
        ("CONSTF", (0.0,)),
        ("CONSTF", (0.0,)),
        ("CONSTF", (0.0,)),
        ("ACTION", (515, 2)),
        ("RETN", ()),
    ]
    if [ncs_signature(item) for item in map_ncs.instructions] != map_expected:
        raise RuntimeError("k_pend_map no longer reveals the complete module map")

    encounter_dialogue_data, encounter_dialogue_ncs = load_script("k_pend_traskdl49")
    encounter_dialogue_expected = [
        ("CPTOPBP", (-120, 4)),
        ("JSR", ()),
        ("RETN", ()),
        ("CPTOPSP", (-4, 4)),
        ("CONSTS", ("END_TRASK_DLG",)),
        ("ACTION", (581, 2)),
        ("MOVSP", (-4,)),
        ("RETN", ()),
    ]
    if (find_instruction_sequence(
            encounter_dialogue_ncs.instructions, encounter_dialogue_expected) is None or
            initialized_integer(encounter_dialogue_ncs, -120) != 1):
        raise RuntimeError("k_pend_traskdl49 no longer sets END_TRASK_DLG value 1")

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
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_trig02",
            "kind": "trigger-dialogue",
            "sourceSha256": sha256_bytes(trigger_data),
            "instructionCount": len(trigger_ncs.instructions),
            "triggerTemplate": "end_trig02",
            "globalName": "END_TRASK_DLG",
            "globalValue": 10,
            "actorTag": "end_trask",
            "userEvent": 50,
            "inputLockSeconds": 0.5,
            "delaySeconds": 0.1,
            "conversation": "end_trask01",
            "dialogueStarter": 8,
            "actorScriptSourceSha256": sha256_bytes(actor_data),
            "actorScriptInstructionCount": len(actor_ncs.instructions),
            "conditionScriptSourceSha256": sha256_bytes(condition_data),
            "conditionScriptInstructionCount": len(condition_ncs.instructions),
        },
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_cadlg_inc",
            "kind": "global-number-add",
            "sourceSha256": sha256_bytes(carth_dialogue_data),
            "instructionCount": len(carth_dialogue_ncs.instructions),
            "globalName": "END_CARTH_DLG",
            "globalValue": 1,
        },
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_traskdl47",
            "kind": "global-number-set",
            "sourceSha256": sha256_bytes(trask_dialogue_data),
            "instructionCount": len(trask_dialogue_ncs.instructions),
            "globalName": "END_TRASK_DLG",
            "globalValue": 11,
        },
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_map",
            "kind": "reveal-map",
            "sourceSha256": sha256_bytes(map_data),
            "instructionCount": len(map_ncs.instructions),
        },
        {
            "schema": "nikami-aurora-kotor-script-contract-v1",
            "resref": "k_pend_traskdl49",
            "kind": "global-number-set",
            "sourceSha256": sha256_bytes(encounter_dialogue_data),
            "instructionCount": len(encounter_dialogue_ncs.instructions),
            "globalName": "END_TRASK_DLG",
            "globalValue": 1,
        },
    ]


def _import_generic_module(
    game_root: Path,
    module: str,
    output_root: Path,
    mdlops: Path,
    runtime_configuration_path: Path,
    player_appearance_id: int = 137,
    player_portrait_id: int = 18,
) -> Path:
    module = normalize_module_id(module)
    if module == ENDAR_MODULE:
        raise RuntimeError("Generic KOTOR world importer cannot omit Endar story semantics")
    executable = game_root / "swkotor.exe"
    if not executable.is_file():
        raise RuntimeError(f"KOTOR executable not found: {executable}")
    installation = Installation(game_root)
    base_filename, story_filename = resolve_module_rim_filenames(
        installation, module)
    base_rim = installation.module_path() / base_filename
    story_rim = installation.module_path() / story_filename
    runtime_configuration = load_runtime_configuration(runtime_configuration_path)
    ifo_resource = find_module_resource(installation, module, "IFO")
    git_resource = find_module_resource(installation, module, "GIT")
    are_resource = find_module_resource(installation, module, "ARE")
    ifo_bytes = resource_data(ifo_resource)
    git_bytes = resource_data(git_resource)
    are_bytes = resource_data(are_resource)
    ifo = read_ifo(ifo_bytes)
    git = read_git(git_bytes)
    are = read_are(are_bytes)
    camera_style_resource = installation.resource("camerastyle", ResourceType.TwoDA)
    if camera_style_resource is None:
        raise RuntimeError("camerastyle.2da could not be resolved")
    camera_style_bytes = resource_data(camera_style_resource)
    camera_styles = read_2da(camera_style_bytes)
    camera_style_id = int(are.camera_style)
    dialogue_view_angle = float(camera_styles.get_cell(camera_style_id, "viewangle"))
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
        print(f"[{index:02d}/{len(layout.rooms):02d}] exporting {model_name}")
        if is_source_room_placeholder(model_name):
            record = source_room_placeholder_record(model_name)
        else:
            filename = f"{model_name.lower()}.glb"
            record = export_room(
                installation, model_name, rooms_root / filename, textures)
            if record["glb"] is not None:
                record["glb"] = f"rooms/{filename}"
        record["position"] = vector3(room.position)
        room_records.append(record)

    player_actor = export_generic_player_actor(
        installation,
        output_root / "actors" / "player.glb",
        textures,
        mdlops,
        output_root / "_cache" / "animations",
        appearance_id=player_appearance_id,
        portrait_id=player_portrait_id,
    )
    ui_contract = export_kotor_ui(
        installation,
        module,
        area_resref,
        are,
        output_root,
        textures,
        player_actor["portraitResref"],
        [],
        runtime_configuration,
        include_endar_party=False,
    )
    creatures = export_source_creature_records(
        installation, module, git, output_root, textures, mdlops)
    doors = [
        {
            "template": canonical_resref(door.resref),
            "tag": str(door.tag),
            "position": vector3(door.position),
            "bearing": float(door.bearing),
            "linkedToModule": canonical_resref(door.linked_to_module),
        }
        for door in git.doors
    ]
    placeables = [
        {
            "template": canonical_resref(placeable.resref),
            "tag": str(placeable.tag),
            "position": vector3(placeable.position),
            "bearing": float(placeable.bearing),
        }
        for placeable in git.placeables
    ]
    triggers = export_triggers(installation, git.triggers)
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

    environment_maps = [
        export_environment_map(installation, resref, output_root)
        for resref in sorted(textures.environment_maps, key=str.lower)
    ]
    unresolved_texture_references = [
        {
            "room": room["model"],
            "diffuseTexture": contract["diffuseTexture"],
            "lightmapTexture": contract["lightmapTexture"],
            "bumpMapTexture": contract["bumpMapTexture"],
            "missingDiffuse": contract["missingDiffuse"],
            "missingLightmap": contract["missingLightmap"],
            "missingBumpMap": contract["missingBumpMap"],
            "meshCount": contract["meshCount"],
        }
        for room in room_records
        for contract in room["materialContracts"]
        if (contract["missingDiffuse"] or contract["missingLightmap"] or
            contract["missingBumpMap"])
    ]
    fatal_texture_references = [
        item for item in unresolved_texture_references
        if item["missingDiffuse"] or item["missingBumpMap"]
    ]
    if fatal_texture_references:
        missing = sorted({
            str(value).lower()
            for item in fatal_texture_references
            for value, absent in (
                (item["diffuseTexture"], item["missingDiffuse"]),
                (item["bumpMapTexture"], item["missingBumpMap"]),
            )
            if absent
        })
        raise RuntimeError(
            f"Generic module has unresolved source texture semantics: {module} missing={missing}")

    material_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
    )
    authored_bump_scale_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
        if contract["bumpMapScaleAuthored"]
    )
    source_decal_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
        if contract["sourceDecal"]
    )
    additive_environment_surface_count, additive_lightmapped_surface_count = (
        mixed_source_material_counts(room_records))
    resolved_diffuse = {
        str(contract["diffuseTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["diffuseTexture"] and not contract["missingDiffuse"]
    }
    resolved_lightmaps = {
        str(contract["lightmapTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["lightmapTexture"] and not contract["missingLightmap"]
    }
    resolved_bump_maps = {
        str(contract["bumpMapTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["bumpMapTexture"] and not contract["missingBumpMap"]
    }
    manifest = {
        "schema": SCHEMA,
        "profileId": "kotor",
        "engineFamily": "Odyssey",
        "module": module,
        "contentMode": GENERIC_WORLD_MODE,
        "missingSourceAssetPolicy": "source-absence-report-no-fabrication-v1",
        "areaResRef": area_resref,
        "target": {
            "executableSha256": sha256_file(executable),
            "moduleRimSha256": sha256_file(base_rim),
            "storyRimSha256": sha256_file(story_rim),
            "layoutSha256": sha256_bytes(lyt_bytes),
            "gitSha256": sha256_bytes(git_bytes),
            "ifoSha256": sha256_bytes(ifo_bytes),
        },
        "entry": {
            "position": vector3(ifo.entry_position),
            "directionRadians": float(ifo.entry_direction),
        },
        "lighting": {
            "dynamicAmbient": color3(are.dynamic_light),
            "shadows": bool(are.shadows),
            "shadowOpacity": int(are.shadow_opacity),
            "sourceSha256": sha256_bytes(are_bytes),
        },
        "cameraStyle": {
            "id": camera_style_id,
            "viewAngle": dialogue_view_angle,
            "distance": float(camera_styles.get_cell(camera_style_id, "distance")),
            "pitchDegrees": float(camera_styles.get_cell(camera_style_id, "pitch")),
            "height": float(camera_styles.get_cell(camera_style_id, "height")),
            "sourceSha256": sha256_bytes(camera_style_bytes),
        },
        "runtimeConfiguration": runtime_configuration,
        "ui": ui_contract,
        "player": player_actor,
        "rooms": room_records,
        "environmentMaps": environment_maps,
        "unresolvedTextureReferences": unresolved_texture_references,
        "creatures": creatures,
        "doors": doors,
        "placeables": placeables,
        "triggers": triggers,
        "waypoints": waypoints,
        "cameras": cameras,
        "firstEncounter": None,
        "scriptContracts": [],
        "counts": {
            "rooms": len(room_records),
            "sourceRoomPlaceholders": sum(
                1 for room in room_records if room.get("sourcePlaceholder", False)),
            "creatures": len(creatures),
            "uniqueCreatureTemplates": len({
                creature["template"].casefold() for creature in creatures}),
            "renderReadyCreatures": sum(
                creature["renderStatus"] == "ready" for creature in creatures),
            "unsupportedCreatures": sum(
                creature["renderStatus"] != "ready" for creature in creatures),
            **creature_presentation_counts(creatures),
            "doors": len(doors),
            "waypoints": len(waypoints),
            "cameras": len(cameras),
            "placeables": len(placeables),
            "triggers": len(triggers),
            "walkmeshTriangles": sum(len(room["walkmeshTriangles"]) for room in room_records),
            "authoredLights": sum(len(room["lights"]) for room in room_records),
            "authoredEmitters": sum(len(room["emitters"]) for room in room_records),
            "materialSurfaces": material_surface_count,
            "resolvedDiffuseTextures": len(resolved_diffuse),
            "resolvedLightmaps": len(resolved_lightmaps),
            "resolvedBumpMaps": len(resolved_bump_maps),
            "authoredBumpMapScaleSurfaces": authored_bump_scale_surface_count,
            "sourceDecalSurfaces": source_decal_surface_count,
            "additiveEnvironmentSurfaces": additive_environment_surface_count,
            "additiveLightmappedSurfaces": additive_lightmapped_surface_count,
            "environmentMaps": len(environment_maps),
            "unresolvedTextureReferences": len(unresolved_texture_references),
        },
        "limitations": [
            "Generic module import materializes available player, creature, door, and placeable models; behavioral assembly, scripts, dialogue traversal, pathfinding, and retail camera-state parity remain incomplete.",
            "Missing diffuse or TXI-declared bump textures and unsupported emitter semantics fail the import instead of receiving fabricated replacements.",
            "Missing source lightmaps remain reported by exact identity and are not fabricated; affected surfaces retain their resolved diffuse source only.",
            "Source and enhanced tiers share source identity; enhanced PBR is an explicit non-parity presentation policy.",
        ],
    }
    output_root.mkdir(parents=True, exist_ok=True)
    manifest_path = output_root / "module-manifest.json"
    manifest_path.write_text(
        json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(
        f"Imported {module}: mode={GENERIC_WORLD_MODE} rooms={len(room_records)} "
        f"creatures={len(creatures)} triangles="
        f"{sum(room['triangleCount'] for room in room_records)} "
        f"emitters={manifest['counts']['authoredEmitters']} "
        f"renderReadyCreatures={manifest['counts']['renderReadyCreatures']} "
        f"unsupportedCreatures={manifest['counts']['unsupportedCreatures']}"
    )
    print(f"Manifest: {manifest_path}")
    return manifest_path


def _import_endar_module(
    game_root: Path,
    module: str,
    output_root: Path,
    mdlops: Path,
    runtime_configuration_path: Path,
    player_appearance_id: int = 137,
    player_portrait_id: int = 18,
) -> Path:
    module = normalize_module_id(module)
    if module != ENDAR_MODULE:
        raise RuntimeError(
            f"Endar story importer cannot process generic module {module}")
    executable = game_root / "swkotor.exe"
    if not executable.is_file():
        raise RuntimeError(f"KOTOR executable not found: {executable}")
    installation = Installation(game_root)
    base_filename, story_filename = resolve_module_rim_filenames(
        installation, module)
    base_rim = installation.module_path() / base_filename
    story_rim = installation.module_path() / story_filename
    runtime_configuration = load_runtime_configuration(runtime_configuration_path)
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
    script_contracts = export_opening_script_contracts(installation, plot_table, module)
    triggers = export_triggers(installation, git.triggers)
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
        print(f"[{index:02d}/{len(layout.rooms):02d}] exporting {model_name}")
        if is_source_room_placeholder(model_name):
            record = source_room_placeholder_record(model_name)
        else:
            filename = f"{model_name.lower()}.glb"
            record = export_room(
                installation, model_name, rooms_root / filename, textures)
            if record["glb"] is not None:
                record["glb"] = f"rooms/{filename}"
        record["position"] = vector3(room.position)
        room_records.append(record)

    if module == ENDAR_MODULE:
        room_emitters = [
            (room["model"], emitter)
            for room in room_records
            for emitter in room["emitters"]
        ]
        smoke_emitters = [
            item for item in room_emitters
            if item[1]["texture"]["resref"].lower() == "fx_smoke"
        ]
        spark_emitters = [
            item for item in room_emitters
            if item[1]["texture"]["resref"].lower() == "fx_spark"
        ]
        damaged_end = [
            emitter for room_model, emitter in room_emitters
            if room_model.lower() == "m01aa_03a"
            and emitter["nodePath"].lower().endswith("object107/smoke044")
        ]
        if len(room_emitters) != 12 or len(smoke_emitters) != 9 or len(spark_emitters) != 3:
            raise RuntimeError("Endar Spire room-emitter topology drifted")
        if (len(damaged_end) != 1 or
                damaged_end[0]["authoredPosition"] != [
                    -4.944839954376221, -16.427499771118164, 1.4598400592803955] or
                any(abs(actual - expected) > 0.0001 for actual, expected in zip(
                    damaged_end[0]["position"],
                    [-0.1933298110961914, -26.34709930419922, 1.6498400568962097])) or
                abs(damaged_end[0]["birthRate"] - 40.0) > 0.0001 or
                abs(damaged_end[0]["lifeExpectancy"] - 6.0) > 0.0001 or
                abs(damaged_end[0]["gravity"]) > 0.0001 or
                [damaged_end[0][key] for key in
                 ("sizeStart", "sizeMid", "sizeEnd")] != [2.0, 4.0, 5.0]):
            raise RuntimeError("Endar Spire damaged-end smoke contract drifted")

    opening_locker = export_opening_locker(
        installation, module, output_root / "placeables" / "end_locker01.glb", textures)
    opening_chair = export_static_placeable(
        installation, "plc_chair2", output_root / "placeables" / "plc_chair2.glb", textures)
    encounter_placeable_templates = (
        "rsldcrps001", "plc_brokndrd", "plc_rsldcrps", "plc_pwrcond")
    encounter_placeable_exports = {
        template: export_static_placeable(
            installation,
            template,
            output_root / "placeables" / f"{template}.glb",
            textures,
        )
        for template in encounter_placeable_templates
    }
    trask_actor = export_trask_actor(
        installation,
        output_root / "actors" / "end_trask.glb",
        textures,
        mdlops,
        output_root / "_cache" / "animations",
        module,
    )
    carth_actor = export_carth_actor(
        installation,
        output_root / "actors" / "p_carth001.glb",
        textures,
        mdlops,
        output_root / "_cache" / "animations",
        module,
    )
    encounter_animation_names = (
        "pause1", "walk", "run", "talk", "c3d4", "b7a1", "die", "dead")
    encounter_actors = {
        "end_repsol004": export_humanoid_actor(
            installation, "end_repsol004", output_root / "actors" / "end_sith2.glb",
            textures, mdlops, output_root / "_cache" / "animations",
            encounter_animation_names, module,
            animation_supermodels=("S_Female01", "S_Male02")),
        "end_repsol005": export_humanoid_actor(
            installation, "end_repsol005", output_root / "actors" / "end_sith3.glb",
            textures, mdlops, output_root / "_cache" / "animations",
            encounter_animation_names, module,
            animation_supermodels=("S_Female01", "S_Male02")),
        "n_repsold002": export_humanoid_actor(
            installation, "n_repsold002", output_root / "actors" / "end_soldier2.glb",
            textures, mdlops, output_root / "_cache" / "animations",
            encounter_animation_names, module,
            animation_supermodels=("S_Female01", "S_Male02")),
    }
    encounter_effects = export_first_encounter_effects(
        installation, output_root, textures)
    player_actor = export_player_actor(
        installation,
        output_root / "actors" / "player.glb",
        output_root / "actors" / "player-opening-equipped.glb",
        opening_locker["inventory"],
        textures,
        mdlops,
        output_root / "_cache" / "animations",
        appearance_id=player_appearance_id,
        portrait_id=player_portrait_id,
    )
    ui_contract = export_kotor_ui(
        installation,
        module,
        area_resref,
        are,
        output_root,
        textures,
        player_actor["portraitResref"],
        opening_locker["inventory"],
        runtime_configuration,
    )
    lip_capsule = (
        Capsule(game_root / "lips" / f"{module}_loc.mod")
        if (game_root / "lips" / f"{module}_loc.mod").is_file() else None)
    trask_actor["dialogue"] = export_dialogue(
        installation,
        trask_actor["conversation"],
        output_root / "dialogues" / f"{trask_actor['conversation']}.json",
        lip_capsule,
    )
    encounter_dialogue = export_dialogue(
        installation, "end_room3", output_root / "dialogues" / "end_room3.json",
        lip_capsule)
    opening_door = export_door(
        installation, module, "sw_door_test001",
        output_root / "doors" / "end_door01.glb", textures)
    encounter_door = export_door(
        installation, module, "end_door02",
        output_root / "doors" / "end_door02.glb", textures)
    creatures = []
    source_creatures = export_source_creature_records(
        installation, module, git, output_root, textures, mdlops)
    for creature, source_record in zip(git.creatures, source_creatures, strict=True):
        record = dict(source_record)
        if record["template"].lower() == "end_trask":
            record.update(trask_actor)
        elif record["template"].lower() == "p_carth001":
            record.update(carth_actor)
        elif record["template"].lower() in encounter_actors:
            record.update(encounter_actors[record["template"].lower()])
        if record.get("glb"):
            record["renderImportSchema"] = (
                "nikami-aurora-kotor-source-creature-v1")
            record["renderStatus"] = "ready"
            record.pop("renderBlocker", None)
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
        elif record["template"].lower() == "end_door02":
            record.update(encounter_door)
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
        elif record["template"].lower() == "plc_chair2":
            record.update(opening_chair)
        elif record["template"].lower() in encounter_placeable_exports:
            record.update(encounter_placeable_exports[record["template"].lower()])
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

    def encounter_script_record(
        resref: str,
        required_actions: tuple[tuple[int, int], ...],
        required_strings: tuple[str, ...],
    ) -> dict[str, Any]:
        resource = find_named_module_resource(installation, module, resref, "NCS")
        data = resource_data(resource)
        ncs = read_ncs(data)
        actions = [
            tuple(instruction.args)
            for instruction in ncs.instructions
            if instruction.ins_type.name == "ACTION"
        ]
        strings = {
            str(instruction.args[0]).lower()
            for instruction in ncs.instructions
            if instruction.ins_type.name == "CONSTS" and instruction.args
        }
        if (any(action not in actions for action in required_actions) or
                any(value.lower() not in strings for value in required_strings)):
            raise RuntimeError(f"First-encounter script drifted: {resref}")
        return {
            "resref": resref,
            "sourceSha256": sha256_bytes(data),
            "instructionCount": len(ncs.instructions),
        }

    encounter_scripts = [
        encounter_script_record(
            "k_pend_door18",
            ((720, 5), (719, 5), (204, 11), (759, 1)),
            ("wp_end_room3_1", "wp_end_room3_2", "end01_sceneobj0")),
        encounter_script_record(
            "k_pend_camera",
            ((205, 0), (206, 0), (461, 1), (503, 4), (426, 1), (430, 1)),
            ("end_sith2", "end_sith3", "end_soldier2")),
        encounter_script_record(
            "k_pend_cut1_1",
            ((205, 0), (503, 4)),
            ("end_sith2", "end_sith3", "end_soldier2")),
        encounter_script_record(
            "k_pend_cut1_end",
            ((716, 2), (412, 2), (37, 2), (431, 1), (425, 1)),
            ("end_sith2", "end_sith3", "k_pman_npcstart")),
        encounter_script_record("k_pend_resume", ((206, 0),), ()),
    ]
    scene_placement = next(
        placeable for placeable in git.placeables
        if canonical_resref(placeable.resref).lower() == "invisible002")
    scene_utp_resource = find_named_module_resource(
        installation, module, "invisible002", "UTP")
    scene_utp_bytes = resource_data(scene_utp_resource)
    scene_utp = read_utp(scene_utp_bytes)
    if (str(scene_utp.tag).lower() != "end01_sceneobj01" or
            canonical_resref(scene_utp.conversation).lower() != "end_room3" or
            canonical_resref(scene_utp.on_user_defined).lower() != "k_pend_resume"):
        raise RuntimeError("First-encounter scene object drifted")
    encounter_waypoint_tags = {"wp_end_room3_1", "wp_end_room3_2"}
    encounter_waypoints = [
        waypoint for waypoint in waypoints
        if waypoint["tag"].lower() in encounter_waypoint_tags
    ]
    if len(encounter_waypoints) != 2:
        raise RuntimeError("First-encounter party waypoints were not resolved")
    encounter_participant_tags = {"end_sith2", "end_sith3", "end_soldier2"}
    encounter_participants = [
        creature for creature in creatures
        if creature["tag"].lower() in encounter_participant_tags
    ]
    if len(encounter_participants) != 3:
        raise RuntimeError("First-encounter participants were not resolved")
    encounter_environment_placeables = [
        placeable for placeable in placeables
        if placeable["template"].lower() in encounter_placeable_exports and
        float(placeable["position"][0]) > 38.0 and
        15.0 < float(placeable["position"][1]) < 40.0
    ]
    if (len(encounter_environment_placeables) != 6 or
            any(not placeable.get("glb") for placeable in encounter_environment_placeables)):
        raise RuntimeError("First-encounter environment placeables drifted")

    def export_audio(
        resref: str,
        data: bytes,
        audio_format: str,
        source_data: bytes | None = None,
        source_encoding: str | None = None,
        payload_encoding: str | None = None,
    ) -> dict[str, Any]:
        if (audio_format == "wav" and not data.startswith(b"RIFF")) or (
                audio_format == "mp3" and not (
                    data.startswith(b"ID3") or data[:2] in (b"\xff\xfb", b"\xff\xf3", b"\xff\xf2"))):
            raise RuntimeError(f"Encounter audio is not playable {audio_format}: {resref}")
        relative = f"audio/{resref.lower()}.{audio_format}"
        path = output_root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        source = source_data if source_data is not None else data
        record = {
            "resref": resref,
            "path": relative,
            "format": audio_format,
            "sourceSha256": sha256_bytes(source),
            "sourceByteCount": len(source),
            "sourceEncoding": source_encoding or audio_format,
            "payloadSha256": sha256_bytes(data),
            "byteCount": len(data),
            "payloadEncoding": payload_encoding or audio_format,
        }
        return record

    ammunition_resource = installation.resource("ammunitiontypes", ResourceType.TwoDA)
    ambient_music_resource = installation.resource("ambientmusic", ResourceType.TwoDA)
    if ammunition_resource is None or ambient_music_resource is None:
        raise RuntimeError("First-encounter audio tables could not be resolved")
    ammunition_bytes = resource_data(ammunition_resource)
    ambient_music_bytes = resource_data(ambient_music_resource)
    ammunition = read_2da(ammunition_bytes)
    ambient_music = read_2da(ambient_music_bytes)
    shot_resref = str(ammunition.get_cell(1, "shotsound0"))
    impact_resref = str(ammunition.get_cell(1, "impactsound0"))
    background_resref = str(ambient_music.get_cell(int(git.music_standard_id), "resource"))
    battle_resref = str(ambient_music.get_cell(int(git.music_battle_id), "resource"))
    if (shot_resref.lower() != "cb_sh_blast1" or
            impact_resref.lower() != "cb_ht_blastleth1" or
            background_resref.lower() != "mus_theme_sith" or
            battle_resref.lower() != "mus_bat_sithbs"):
        raise RuntimeError("First-encounter audio table rows drifted")
    shot_bytes = installation.sound(shot_resref)
    impact_bytes = installation.sound(impact_resref)
    if not shot_bytes or not impact_bytes:
        raise RuntimeError("First-encounter weapon sounds could not be resolved")
    shot_playable, shot_source_encoding, shot_payload_encoding = (
        normalize_wav_for_godot(shot_bytes, shot_resref)
    )
    impact_playable, impact_source_encoding, impact_payload_encoding = (
        normalize_wav_for_godot(impact_bytes, impact_resref)
    )
    background_path = game_root / "streammusic" / f"{background_resref}.wav"
    battle_path = game_root / "streammusic" / f"{battle_resref}.wav"
    if not background_path.is_file() or not battle_path.is_file():
        raise RuntimeError("First-encounter music files could not be resolved")
    background_container = background_path.read_bytes()
    battle_container = battle_path.read_bytes()

    def stream_music_mp3(container: bytes, resref: str) -> bytes:
        offset = container.find(b"ID3")
        if offset < 0:
            raise RuntimeError(f"KOTOR streammusic MP3 header was not found: {resref}")
        return container[offset:]

    encounter_audio = {
        "ammunitionTypesSha256": sha256_bytes(ammunition_bytes),
        "ambientMusicSha256": sha256_bytes(ambient_music_bytes),
        "standardMusicId": int(git.music_standard_id),
        "battleMusicId": int(git.music_battle_id),
        "musicDelayMilliseconds": int(git.music_delay),
        "blasterShot": export_audio(
            shot_resref, shot_playable, "wav", shot_bytes,
            shot_source_encoding, shot_payload_encoding),
        "blasterImpact": export_audio(
            impact_resref, impact_playable, "wav", impact_bytes,
            impact_source_encoding, impact_payload_encoding),
        "backgroundMusic": export_audio(
            background_resref,
            stream_music_mp3(background_container, background_resref),
            "mp3",
            background_container,
            "kotor-wrapped-mp3",
            "mp3"),
        "battleMusic": export_audio(
            battle_resref,
            stream_music_mp3(battle_container, battle_resref),
            "mp3",
            battle_container,
            "kotor-wrapped-mp3",
            "mp3"),
    }
    first_encounter = {
        "schema": "nikami-aurora-kotor-first-encounter-v1",
        "doorTag": "end_door02",
        "sceneObject": {
            "template": "invisible002",
            "tag": str(scene_utp.tag),
            "position": vector3(scene_placement.position),
            "bearing": float(scene_placement.bearing),
            "conversation": canonical_resref(scene_utp.conversation),
            "onUserDefined": canonical_resref(scene_utp.on_user_defined),
            "utpSha256": sha256_bytes(scene_utp_bytes),
            "dialogue": encounter_dialogue,
        },
        "participants": encounter_participants,
        "environmentPlaceables": encounter_environment_placeables,
        "partyWaypoints": encounter_waypoints,
        "cameraIds": [26, 19, 20],
        "animationIds": {
            "damage": 148,
            "cutsceneAttack": 239,
            "traskFirstLine": 40,
            "traskCharge": 44,
        },
        "effects": encounter_effects,
        "timingSeconds": {
            "cameraSwitch": 0.15,
            "battleMusic": 1.5,
            "firstControlResume": 3.0,
            "secondAttack": 1.0,
            "thirdAttack": 1.5,
        },
        "audio": encounter_audio,
        "scripts": encounter_scripts,
    }
    environment_maps = [
        export_environment_map(installation, resref, output_root)
        for resref in sorted(textures.environment_maps, key=str.lower)
    ]
    unresolved_texture_references = [
        {
            "room": room["model"],
            "diffuseTexture": contract["diffuseTexture"],
            "lightmapTexture": contract["lightmapTexture"],
            "bumpMapTexture": contract["bumpMapTexture"],
            "missingDiffuse": contract["missingDiffuse"],
            "missingLightmap": contract["missingLightmap"],
            "missingBumpMap": contract["missingBumpMap"],
            "meshCount": contract["meshCount"],
        }
        for room in room_records
        for contract in room["materialContracts"]
        if (contract["missingDiffuse"] or contract["missingLightmap"] or
            contract["missingBumpMap"])
    ]
    if module == ENDAR_MODULE:
        actual_environment_maps = {
            item["resref"].lower() for item in environment_maps
        }
        required_environment_maps = {"cm_endar", "cm_baremetal", "mycube"}
        if not required_environment_maps.issubset(actual_environment_maps):
            raise RuntimeError(
                "Endar Spire environment-map identity drifted: "
                f"required={sorted(required_environment_maps)} "
                f"actual={sorted(actual_environment_maps)}")
        unresolved = {
            str(value).lower()
            for item in unresolved_texture_references
            for value, absent in (
                (item["diffuseTexture"], item["missingDiffuse"]),
                (item["lightmapTexture"], item["missingLightmap"]),
                (item["bumpMapTexture"], item["missingBumpMap"]),
            )
            if absent
        }
        if unresolved != {"m01aa_04a_a0002t"}:
            raise RuntimeError("Endar Spire unresolved-texture inventory drifted")
    material_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
    )
    authored_bump_scale_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
        if contract["bumpMapScaleAuthored"]
    )
    source_decal_surface_count = sum(
        int(contract["meshCount"])
        for room in room_records
        for contract in room["materialContracts"]
        if contract["sourceDecal"]
    )
    additive_environment_surface_count, additive_lightmapped_surface_count = (
        mixed_source_material_counts(room_records))
    resolved_diffuse = {
        str(contract["diffuseTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["diffuseTexture"] and not contract["missingDiffuse"]
    }
    resolved_lightmaps = {
        str(contract["lightmapTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["lightmapTexture"] and not contract["missingLightmap"]
    }
    resolved_bump_maps = {
        str(contract["bumpMapTexture"]).lower()
        for room in room_records
        for contract in room["materialContracts"]
        if contract["bumpMapTexture"] and not contract["missingBumpMap"]
    }
    manifest = {
        "schema": SCHEMA,
        "profileId": "kotor",
        "engineFamily": "Odyssey",
        "module": module,
        "contentMode": ENDAR_OPENING_MODE,
        "missingSourceAssetPolicy": "source-absence-report-no-fabrication-v1",
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
        "runtimeConfiguration": runtime_configuration,
        "ui": ui_contract,
        "player": player_actor,
        "rooms": room_records,
        "environmentMaps": environment_maps,
        "unresolvedTextureReferences": unresolved_texture_references,
        "creatures": creatures,
        "doors": doors,
        "placeables": placeables,
        "triggers": triggers,
        "waypoints": waypoints,
        "cameras": cameras,
        "firstEncounter": first_encounter,
        "scriptContracts": script_contracts,
        "counts": {
            "rooms": len(room_records),
            "sourceRoomPlaceholders": sum(
                1 for room in room_records if room.get("sourcePlaceholder", False)),
            "creatures": len(creatures),
            "uniqueCreatureTemplates": len({
                creature["template"].casefold() for creature in creatures}),
            "renderReadyCreatures": sum(
                creature["renderStatus"] == "ready" for creature in creatures),
            "unsupportedCreatures": sum(
                creature["renderStatus"] != "ready" for creature in creatures),
            **creature_presentation_counts(creatures),
            "doors": len(doors),
            "waypoints": len(waypoints),
            "cameras": len(git.cameras),
            "placeables": len(git.placeables),
            "triggers": len(git.triggers),
            "walkmeshTriangles": sum(len(room["walkmeshTriangles"]) for room in room_records),
            "authoredLights": sum(len(room["lights"]) for room in room_records),
            "authoredEmitters": sum(len(room["emitters"]) for room in room_records),
            "materialSurfaces": material_surface_count,
            "resolvedDiffuseTextures": len(resolved_diffuse),
            "resolvedLightmaps": len(resolved_lightmaps),
            "resolvedBumpMaps": len(resolved_bump_maps),
            "authoredBumpMapScaleSurfaces": authored_bump_scale_surface_count,
            "sourceDecalSurfaces": source_decal_surface_count,
            "additiveEnvironmentSurfaces": additive_environment_surface_count,
            "additiveLightmappedSurfaces": additive_lightmapped_surface_count,
            "environmentMaps": len(environment_maps),
            "unresolvedTextureReferences": len(unresolved_texture_references),
        },
        "limitations": [
            "Every placed creature has an exact UTC-bound render import attempt; unsupported source assemblies remain explicit and block runtime/gallery coverage.",
            "Dialogue traversal is partial; unsupported scripts, per-node gestures, animated cameras, and shot obstruction remain.",
            "Room lightmaps and light nodes are source-authored; renderer transfer-function parity remains under test.",
            "The retail Endar Spire model M01aa_04a references absent lightmap M01aa_04a_a0002t; the unresolved source identity is reported and no replacement texture is fabricated.",
        ],
    }
    output_root.mkdir(parents=True, exist_ok=True)
    manifest_path = output_root / "module-manifest.json"
    manifest_path.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    print(
        f"Imported {module}: rooms={len(room_records)} creatures={len(creatures)} "
        f"triangles={sum(room['triangleCount'] for room in room_records)} "
        f"emitters={sum(len(room['emitters']) for room in room_records)}"
    )
    print(f"Manifest: {manifest_path}")
    return manifest_path


def import_module(
    game_root: Path,
    module: str,
    output_root: Path,
    mdlops: Path,
    runtime_configuration_path: Path,
    player_appearance_id: int = 137,
    player_portrait_id: int = 18,
) -> Path:
    normalized_module = normalize_module_id(module)
    importer = (
        _import_endar_module
        if normalized_module == ENDAR_MODULE
        else _import_generic_module
    )
    return importer(
        game_root,
        normalized_module,
        output_root,
        mdlops,
        runtime_configuration_path,
        player_appearance_id,
        player_portrait_id,
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-root", type=Path, required=True)
    parser.add_argument("--module", default="end_m01aa")
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--mdlops", type=Path, required=True)
    parser.add_argument("--player-appearance-id", type=int, default=137)
    parser.add_argument("--player-portrait-id", type=int, default=18)
    parser.add_argument(
        "--runtime-config",
        type=Path,
        default=Path(__file__).resolve().parents[1] / "config" / "kotor-runtime.json",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    try:
        import_module(
            args.game_root.resolve(),
            args.module.lower(),
            args.output.resolve(),
            args.mdlops.resolve(),
            args.runtime_config.resolve(),
            args.player_appearance_id,
            args.player_portrait_id,
        )
    except Exception as exc:
        print(f"KOTOR_IMPORT_FAIL: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
