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
    from pykotor.resource.formats.lip import read_lip
    from pykotor.resource.formats.lyt import read_lyt
    from pykotor.resource.formats.mdl import read_mdl
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


class TextureCache:
    def __init__(self, installation: Installation):
        self.installation = installation
        self.images: dict[str, Image.Image | None] = {}
        self.alpha_tests: dict[str, float] = {}
        self.txi: dict[str, str] = {}

    def image(self, name: str) -> Image.Image | None:
        key = name.strip().lower()
        if not key or key == "null":
            return None
        if key in self.images:
            return self.images[key]

        texture = self.installation.texture(name)
        if texture is None:
            self.images[key] = None
            self.alpha_tests[key] = 1.0
            self.txi[key] = ""
            return None
        self.alpha_tests[key] = float(texture.alpha_test)
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
        return self.alpha_tests.get(key, 1.0) < 0.5 or self.is_source_additive(name)

    def is_source_additive(self, name: str) -> bool:
        key = name.strip().lower()
        if not key or key == "null":
            return False
        if key not in self.images:
            self.image(name)
        directives = {
            line.strip().lower() for line in self.txi.get(key, "").splitlines()
        }
        return "blending 1" in directives or "blending additive" in directives


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


def export_kotor_ui(
    installation: Installation,
    module: str,
    area_resref: str,
    area: Any,
    output_root: Path,
    textures: TextureCache,
    portrait_resref: str,
    inventory_items: list[dict[str, Any]],
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
    module_loading_resref = f"load_{module}"

    trask_resource = find_named_module_resource(
        installation, module, "end_trask", "UTC")
    if trask_resource is None:
        raise RuntimeError("Endar Spire party member UTC could not be resolved")
    trask_utc_bytes = resource_data(trask_resource)
    trask = read_utc(trask_utc_bytes)
    portraits_resource = installation.resource("portraits", ResourceType.TwoDA)
    if portraits_resource is None:
        raise RuntimeError("portraits.2da could not be resolved for the party UI")
    portraits_bytes = resource_data(portraits_resource)
    portraits = read_2da(portraits_bytes)
    trask_portrait_resref = str(portraits.get_cell(int(trask.portrait_id), "baseresref"))
    if not trask_portrait_resref:
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
    armor_base_ac = int(baseitems.get_cell(int(trask_armor_uti.base_item), "baseac") or "0")
    armor_dexterity_limit = int(
        baseitems.get_cell(int(trask_armor_uti.base_item), "dexbonus") or "-1")
    dexterity_modifier = math.floor((int(trask.dexterity) - 10) / 2)
    applied_dexterity_modifier = (
        dexterity_modifier
        if armor_dexterity_limit < 0
        else min(dexterity_modifier, armor_dexterity_limit)
    )
    trask_defense = (
        10 + int(trask.natural_ac) + armor_base_ac + applied_dexterity_modifier)
    trask_display_name = talktable.string(int(trask.first_name.stringref))
    if not trask_display_name:
        raise RuntimeError("Endar Spire party member name could not be resolved")

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

    ui_contract: dict[str, Any] = {
        "schema": "nikami-aurora-kotor-ui-v1",
        "loading": {
            "layout": loading_layout,
            "controls": loading_controls,
            "background": export_texture(module_loading_resref),
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
            "partyPortraits": [
                export_texture(portrait_resref),
                export_texture(trask_portrait_resref),
            ],
            "partyPortraitsSourceSha256": sha256_bytes(portraits_bytes),
            "partyMembers": [
                {
                    "id": "player",
                    "displayName": "Player",
                    "portrait": export_texture(portrait_resref),
                    "currentVitality": 20,
                    "maximumVitality": 20,
                    "defense": 10,
                    "sourceKind": "profile",
                    "utcSha256": None,
                    "armorResref": None,
                    "armorUtiSha256": None,
                    "baseItemsSha256": None,
                },
                {
                    "id": canonical_resref(trask.tag).lower(),
                    "displayName": trask_display_name,
                    "portrait": export_texture(trask_portrait_resref),
                    "currentVitality": int(trask.current_hp),
                    "maximumVitality": int(trask.max_hp),
                    "defense": trask_defense,
                    "sourceKind": "utc",
                    "utcSha256": sha256_bytes(trask_utc_bytes),
                    "armorResref": trask_armor_resref,
                    "armorUtiSha256": sha256_bytes(trask_armor_bytes),
                    "baseItemsSha256": sha256_bytes(baseitems_bytes),
                },
            ],
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
            "partyPortraits": [
                export_texture(portrait_resref),
                export_texture(trask_portrait_resref),
            ],
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
            "partyPortraits": [
                export_texture(portrait_resref),
                export_texture(trask_portrait_resref),
            ],
            "minimap": {
                "texture": export_texture(f"lbl_map{area_resref}"),
                "mapPoint1": [float(area.map_point_1.x), float(area.map_point_1.y)],
                "mapPoint2": [float(area.map_point_2.x), float(area.map_point_2.y)],
                "worldPoint1": [float(area.world_point_1.x), float(area.world_point_1.y)],
                "worldPoint2": [float(area.world_point_2.x), float(area.world_point_2.y)],
                "resolutionX": int(area.map_res_x),
                "zoom": int(area.map_zoom),
                "northAxis": int(area.north_axis),
            },
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
    return {
        "schema": "nikami-aurora-kotor-first-encounter-effects-v1",
        "projectileModel": "w_laserfire_r",
        "projectileMdlSha256": sha256_bytes(projectile_mdl),
        "projectileMdxSha256": sha256_bytes(projectile_mdx),
        "muzzleModel": "v_muzflash_01",
        "muzzleMdlSha256": sha256_bytes(muzzle_mdl),
        "muzzleMdxSha256": sha256_bytes(muzzle_mdx),
        "projectileSize": projectile_size,
        "muzzleSize": muzzle_size,
        "muzzleLifetime": muzzle_lifetime,
        "laserTexture": export_effect_texture(
            installation, textures, "Fx_laser_01", output_root),
        "muzzleTexture": export_effect_texture(
            installation, textures, "fx_muzflash", output_root),
        "flareTexture": export_effect_texture(
            installation, textures, "fx_flare02", output_root),
    }


def material_for(mesh: Any, textures: TextureCache, override_texture: str | None = None) -> Any:
    texture_name = str(override_texture or mesh.texture_1 or "").strip()
    image = textures.image(texture_name)
    lightmap_name = str(mesh.texture_2 or "").strip()
    lightmap = textures.image(lightmap_name)
    source_additive = image is not None and textures.is_source_additive(texture_name)
    source_transparent = image is not None and textures.is_source_transparent(texture_name)
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
        name=(texture_name + "__aurora_additive") if source_additive else
        (texture_name or "untextured"),
        baseColorTexture=image,
        baseColorFactor=color,
        emissiveTexture=lightmap,
        emissiveFactor=[1.0, 1.0, 1.0] if lightmap is not None else None,
        metallicFactor=0.0,
        roughnessFactor=1.0,
        alphaMode="BLEND" if source_transparent else "OPAQUE",
        doubleSided=source_transparent,
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
    emitters: list[dict[str, Any]] = []
    walkmesh_triangles: list[list[list[float]]] = []

    def visit(node: Any, parent_transform: np.ndarray, parent_path: str) -> None:
        nonlocal mesh_count, vertex_count, triangle_count
        world_transform = parent_transform @ quaternion_matrix(node)
        node_path = f"{parent_path}/{node.name}" if parent_path else str(node.name)
        if node.emitter is not None:
            emitter = node.emitter

            def scalar(controller_type: MDLControllerType, fallback: float) -> float:
                return controller_value(node, controller_type, [fallback])[0]

            def emitter_color(controller_type: MDLControllerType) -> list[float]:
                return controller_value(node, controller_type, [1.0, 1.0, 1.0])

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
            emitters.append({
                "schema": "nikami-aurora-kotor-room-emitter-v1",
                "nodePath": node_path,
                "authoredPosition": vector3(node.position),
                "position": [float(item) for item in world_transform[:3, 3]],
                "direction": [float(item) for item in direction[:3]],
                "texture": export_effect_texture(
                    installation, textures, texture_name, output_path.parent.parent),
                "update": str(emitter.update),
                "render": str(emitter.render),
                "blend": str(emitter.blend),
                "flags": int(emitter.flags),
                "xGrid": int(emitter.x_grid),
                "yGrid": int(emitter.y_grid),
                # Controller slot 88 is BIRTHRATE for emitter nodes (and RADIUS
                # for light nodes); slot 140 is emitter RANDVEL/light MULTIPLIER.
                "birthRate": scalar(MDLControllerType.BIRTHRATE, 0.0),
                "randomBirthRate": scalar(MDLControllerType.RANDOMBIRTHRATE, 0.0),
                "velocity": scalar(MDLControllerType.VELOCITY, 0.0),
                "randomVelocity": scalar(MDLControllerType.RANDVEL, 0.0),
                "mass": scalar(MDLControllerType.MASS, 0.0),
                "particleRotation": scalar(MDLControllerType.PARTICLEROT, 0.0),
                "spreadRadians": scalar(MDLControllerType.SPREAD, 0.0),
                "lifeExpectancy": scalar(MDLControllerType.LIFEEXP, 1.0),
                "colorStart": emitter_color(MDLControllerType.COLORSTART),
                "colorMid": emitter_color(MDLControllerType.COLORMID),
                "colorEnd": emitter_color(MDLControllerType.COLOREND),
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
            })
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
        "lights": lights,
        "emitters": emitters,
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
        "animationSource": animation_sources[0]["model"],
        "animationSourceSha256": animation_sources[0]["sourceSha256"],
        "animationSources": animation_sources,
        "animation": animation_report,
        "talkOffset": talk_offset,
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
        filename = f"{model_name.lower()}.glb"
        print(f"[{index:02d}/{len(layout.rooms):02d}] exporting {model_name}")
        record = export_room(installation, model_name, rooms_root / filename, textures)
        if record["glb"] is not None:
            record["glb"] = f"rooms/{filename}"
        record["position"] = vector3(room.position)
        room_records.append(record)

    if module == "end_m01aa":
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
    for creature in git.creatures:
        record = {
            "template": canonical_resref(creature.resref),
            "tag": str(getattr(creature, "tag", "")),
            "position": vector3(creature.position),
            "bearing": float(creature.bearing),
        }
        if record["template"].lower() == "end_trask":
            record.update(trask_actor)
        elif record["template"].lower() == "p_carth001":
            record.update(carth_actor)
        elif record["template"].lower() in encounter_actors:
            record.update(encounter_actors[record["template"].lower()])
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
        "ui": ui_contract,
        "player": player_actor,
        "rooms": room_records,
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
            "creatures": len(creatures),
            "doors": len(doors),
            "waypoints": len(waypoints),
            "cameras": len(git.cameras),
            "placeables": len(git.placeables),
            "triggers": len(git.triggers),
            "walkmeshTriangles": sum(len(room["walkmeshTriangles"]) for room in room_records),
            "authoredLights": sum(len(room["lights"]) for room in room_records),
            "authoredEmitters": sum(len(room["emitters"]) for room in room_records),
        },
        "limitations": [
            "Only Trask, Carth, the player, and the opening door have assembled render models; other creature and door records remain placements.",
            "Dialogue traversal is partial; unsupported scripts, per-node gestures, animated cameras, and shot obstruction remain.",
            "Room lightmaps and light nodes are source-authored; renderer transfer-function parity remains under test.",
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
