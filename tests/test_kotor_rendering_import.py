import sys
import json
import struct
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path
from types import SimpleNamespace


SCRIPTS = Path(__file__).resolve().parents[1] / "scripts"
sys.path.insert(0, str(SCRIPTS))

try:
    import import_kotor_module as importer  # noqa: E402
    import kotor_actor_gltf  # noqa: E402
    import preflight_kotor_modules as preflight  # noqa: E402
    from pykotor.resource.formats.mdl.mdl_types import MDLControllerType  # noqa: E402
except (ImportError, SystemExit) as exc:
    raise unittest.SkipTest(f"KOTOR importer dependencies are unavailable: {exc}") from exc


class KotorRenderingImportTests(unittest.TestCase):
    def test_combat_xp_export_accepts_kotor2_numeric_challenge_columns(self) -> None:
        headers = ["level", *(str(value) for value in range(51))]

        class Table:
            def get_headers(self):
                return headers

            def get_height(self):
                return 2

            def get_cell(self, row, column):
                return str(row + 1) if column == "level" else str(
                    row * 100 + int(column))

        installation = SimpleNamespace(resource=lambda *_args: object())
        with (
            patch.object(importer, "resource_data", return_value=b"k2-xp"),
            patch.object(importer, "read_2da", return_value=Table()),
        ):
            result = importer.export_combat_experience_table(installation)

        self.assertEqual(51, len(result["rows"][0]["rewards"]))
        self.assertEqual(150, result["rows"][1]["rewards"][50])

    def test_shared_odyssey_combat_export_uses_source_class_and_weapon_rules(
        self,
    ) -> None:
        tables = {
            "classes": ({(7, "attackbonustable"): "cls_atk_1"}, b"classes"),
            "cls_atk_1": ({(2, "bab"): "3"}, b"attack"),
            "iprp_neg5cost": ({(1, "value"): "-2"}, b"negative"),
            "iprp_damagecost": ({
                (2, "numdice"): "1", (2, "rank"): "0", (2, "die"): "6",
            }, b"damage"),
        }

        class Table:
            def __init__(self, cells):
                self.cells = cells

            def get_cell(self, row, column):
                return self.cells.get((row, column), "****")

        baseitems = Table({
            (12, "numdice"): "1", (12, "dietoroll"): "8",
            (12, "critthreat"): "1", (12, "crithitmult"): "2",
            (12, "rangedweapon"): "1", (12, "damageflags"): "4096",
            (12, "weaponwield"): "4",
        })
        slot = type("Slot", (), {"value": 0x00010})()
        utc = SimpleNamespace(
            equipment={slot: SimpleNamespace(resref="laser")},
            classes=[SimpleNamespace(class_id=7, class_level=3)],
            challenge_rating=2.0,
            strength=10,
            dexterity=15,
            natural_ac=0,
        )
        uti = SimpleNamespace(
            base_item=12,
            properties=[
                SimpleNamespace(property_name=8, cost_value=1, subtype=0),
                SimpleNamespace(property_name=11, cost_value=2, subtype=16),
            ],
        )

        def source_table(_installation, name, _order):
            cells, payload = tables[name]
            return Table(cells), payload

        with (
            patch.object(importer, "source_table", side_effect=source_table),
            patch.object(importer, "find_item_resource",
                         return_value=SimpleNamespace(data=b"uti")),
            patch.object(importer, "resource_data", return_value=b"uti"),
            patch.object(importer, "read_uti", return_value=uti),
        ):
            result = importer.export_utc_combat(
                SimpleNamespace(), "001ebo", utc, baseitems, b"baseitems", [])

        self.assertEqual(12, result["defense"])
        self.assertEqual(5, result["attackBonus"])
        self.assertEqual(3, result["classLevels"][0]["baseAttackBonus"])
        self.assertEqual(4, result["weapon"]["weaponWield"])
        self.assertEqual(-2, result["weapon"]["attackModifier"])
        self.assertEqual(
            {"damageType": 16, "flat": 0, "diceCount": 1, "dieSides": 6},
            result["weapon"]["bonusDamage"][0],
        )

    def test_actor_effect_inventory_preserves_light_color_keys(
        self,
    ) -> None:
        light_node = SimpleNamespace(
            name="AuroraLight01",
            emitter=None,
            light=SimpleNamespace(
                color=SimpleNamespace(r=1.0, g=1.0, b=1.0),
                radius=0.0,
                multiplier=1.0,
                dynamic_type=1,
                affect_dynamic=False,
                ambient_only=False,
            ),
        )
        color_controller = SimpleNamespace(
            controller_type=MDLControllerType.COLOR,
            rows=[
                SimpleNamespace(time=0.25, data=[0.0, 0.0, 0.0]),
                SimpleNamespace(time=0.5, data=[1.0, 0.5, 0.25]),
            ],
        )
        animation_node = SimpleNamespace(
            name="AuroraLight01", controllers=[color_controller])
        animation = SimpleNamespace(
            name="weld",
            length=1.0,
            events=[SimpleNamespace(activation_time=0.5, name="detonate")],
            all_nodes=lambda: [animation_node],
        )
        result = importer.actor_effect_records(
            SimpleNamespace(),
            (("body", "C_DrdAstro", None,
              SimpleNamespace(all_nodes=lambda: [light_node]), b"mdl", b"mdx"),),
            SimpleNamespace(),
            Path("unused"),
            SimpleNamespace(anims=[animation]),
        )

        self.assertEqual("AuroraLight01", result["lights"][0]["anchorNode"])
        self.assertEqual([], result["animations"][0]["events"])
        self.assertEqual(
            [0.0, 0.0, 0.0],
            result["animations"][0]["tracks"][0]["keys"][0]["value"],
        )

    def test_actor_model_inventory_keeps_weapon_glow_and_effect_nodes_explicit(
        self,
    ) -> None:
        meshes = [
            SimpleNamespace(
                render=True,
                vertex_positions=[object()],
                faces=[object()],
                texture_1="w_lsabreblue01",
                texture_2="",
            ),
            SimpleNamespace(
                render=True,
                vertex_positions=[object()],
                faces=[object()],
                texture_1="w_lghtsbr_001",
                texture_2="",
            ),
        ]
        nodes = [
            SimpleNamespace(
                name="blade",
                mesh=meshes[0],
                aabb=None,
                emitter=None,
                light=None,
            ),
            SimpleNamespace(
                name="handle",
                mesh=meshes[1],
                aabb=None,
                emitter=object(),
                light=None,
            ),
        ]

        class FakeTextures:
            def source_environment_map(self, _name: str) -> None:
                return None

            def source_bump_map(self, _name: str) -> tuple[None, None]:
                return None, None

            def validate_material_txi(self, name: str, role: str) -> None:
                self.validated.append((role, name))

            def is_source_additive(self, name: str) -> bool:
                return name == "w_lsabreblue01"

            def __init__(self) -> None:
                self.validated = []

        textures = FakeTextures()
        report = importer.model_presentation_inventory(
            SimpleNamespace(all_nodes=lambda: nodes), textures)

        self.assertEqual(2, report["renderSurfaces"])
        self.assertEqual(1, report["additiveSurfaces"])
        self.assertEqual(1, report["emitterNodes"])
        self.assertEqual(0, report["lightNodes"])
        self.assertIn(("diffuse", "w_lsabreblue01"), textures.validated)
        records = importer.actor_model_records((
            ("rightWeapon", "w_lghtsbr_001", None,
             SimpleNamespace(all_nodes=lambda: nodes), b"mdl", b"mdx"),
        ), textures)
        self.assertEqual(1, records[0]["emitterNodes"])
        self.assertEqual(0, records[0]["lightNodes"])

    def test_actor_animation_position_keys_preserve_authored_rest_height(self) -> None:
        # C_DrdProt's source RootDummy stands at 1.07443 m and cpause1 adds a
        # small downward offset.  Replacing the rest pose with that delta is
        # the exact failure that previously buried the lower body in the floor.
        composed = kotor_actor_gltf.compose_animation_translation(
            [0.0, -0.01668, 1.07443],
            [0.0, 0.0, -0.0343],
        )

        self.assertAlmostEqual(0.0, float(composed[0]), places=6)
        self.assertAlmostEqual(-0.01668, float(composed[1]), places=6)
        self.assertAlmostEqual(1.04013, float(composed[2]), places=6)

    def test_level_matrix_keeps_import_runtime_transition_and_gallery_fail_closed(
        self,
    ) -> None:
        evidence = preflight.fresh_evidence()
        evidence["counts"].update({
            "roomPlacements": 2,
            "validMeshes": 3,
            "triangles": 12,
            "materialSurfaces": 4,
            "sourceUnshadedSurfaces": 1,
            "lights": 2,
            "emitters": 1,
            "supportedEmitters": 1,
            "walkmeshTriangles": 6,
            "cameras": 1,
            "entrySpawns": 1,
            "creatures": 2,
        })
        evidence["roomModels"].update({"room_a", "room_b"})
        evidence["emitterVisualSafety"]["validated"] = 1
        evidence["creatureTemplates"].update({"c_test": 2})

        row = preflight.level_matrix_record("test_mod", evidence, [], None)

        self.assertEqual("blocked", row["status"])
        self.assertIn("missing-fresh-manifest-evidence", row["blockers"])
        self.assertIn("missing-runtime-evidence", row["blockers"])
        self.assertEqual(2, row["creatureGallery"]["expectedPlacements"])
        self.assertEqual(2, row["creatureGallery"]["missing"])
        self.assertFalse(row["creatureGallery"]["strictPbr"])

    def test_runtime_level_evidence_requires_exact_module_scoped_markers(self) -> None:
        module = "test_mod"
        text = "\n".join((
            f"NIKAMI_AURORA_KOTOR_BOOT status=pass module={module}",
            f"NIKAMI_AURORA_ROOM_PBR status=ready module={module}",
            f"NIKAMI_AURORA_DYNAMIC_PBR status=ready module={module}",
            f"NIKAMI_AURORA_LIGHTING status=ready module={module}",
            f"NIKAMI_AURORA_ROOM_EMITTERS status=ready module={module}",
            f"NIKAMI_AURORA_GENERIC_SHOWCASE status=pass module={module}",
        ))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / module / "runtime.log"
            path.parent.mkdir()
            path.write_text(text, encoding="utf-8")

            runtime = preflight.runtime_level_evidence(Path(directory), module)

        self.assertIsNotNone(runtime)
        self.assertTrue(runtime["errorFree"])
        self.assertTrue(runtime["markers"]["boot"])
        self.assertTrue(runtime["markers"]["lights"])
        self.assertFalse(runtime["markers"]["transition"])

    def test_generic_module_identity_is_separate_from_endar_story_mode(self) -> None:
        self.assertEqual("tar_m02aa", importer.normalize_module_id(" TAR_M02AA "))
        self.assertEqual(
            importer.GENERIC_WORLD_MODE,
            importer.module_content_mode("tar_m02aa"),
        )
        self.assertEqual(
            importer.ENDAR_OPENING_MODE,
            importer.module_content_mode(importer.ENDAR_MODULE),
        )
        with self.assertRaisesRegex(RuntimeError, "module identifier"):
            importer.normalize_module_id("../end_m01aa")

    def test_odyssey_profiles_select_their_authored_pc_hud(self) -> None:
        self.assertEqual(
            "mipc8x6", importer.odyssey_hud_layout_resref(""))
        self.assertEqual(
            "mipc28x6_p", importer.odyssey_hud_layout_resref("_p"))

    def test_owned_mdl_reader_excludes_the_twelve_byte_resource_wrapper(self) -> None:
        original_reader = importer.MDLBinaryReader

        class FakeMdlReader:
            def __init__(self, _mdl: bytes, source_ext: bytes) -> None:
                self.source_ext = source_ext
                self._reader = None

            def load(self) -> tuple[int, int, bytes]:
                self.assert_reader()
                return self._reader.offset(), self._reader.size(), self.source_ext

            def assert_reader(self) -> None:
                if self._reader is None:
                    raise AssertionError("Geometry reader was not installed")

        try:
            importer.MDLBinaryReader = FakeMdlReader
            self.assertEqual(
                (12, 20, b"mdx"),
                importer.read_owned_mdl(b"\0" * 32, b"mdx"),
            )
        finally:
            importer.MDLBinaryReader = original_reader

    def test_mdlops_ascii_secondary_texture_is_restored_to_the_parsed_mesh(self) -> None:
        parsed_meshes = [
            SimpleNamespace(texture_2=""),
            SimpleNamespace(texture_2=""),
        ]
        parsed_model = SimpleNamespace(all_nodes=lambda: [
            SimpleNamespace(mesh=parsed_meshes[0]),
            SimpleNamespace(mesh=None),
            SimpleNamespace(mesh=parsed_meshes[1]),
        ])
        source = """\
node trimesh floor
  bitmap LEH_floor05
  bitmap2 001EBO9_lm2
endnode
node dummy hook
endnode
node trimesh screen
  bitmap LEH_scre02
endnode
"""
        original_reader = importer.read_mdl
        importer.read_mdl = lambda _path: parsed_model
        try:
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "room.mdl.ascii"
                path.write_text(source, encoding="ascii")
                result = importer.read_mdl_ascii_preserving_lightmaps(path)
        finally:
            importer.read_mdl = original_reader

        self.assertIs(parsed_model, result)
        self.assertEqual("001EBO9_lm2", parsed_meshes[0].texture_2)
        self.assertEqual("", parsed_meshes[1].texture_2)

    def test_odyssey_room_placeholder_is_preserved_without_fabricated_assets(self) -> None:
        record = importer.source_room_placeholder_record("****")

        self.assertTrue(record["sourcePlaceholder"])
        self.assertIsNone(record["glb"])
        self.assertIsNone(record["mdlSha256"])
        self.assertEqual([], record["walkmeshTriangles"])
        with self.assertRaisesRegex(RuntimeError, "Not an Odyssey room placeholder"):
            importer.source_room_placeholder_record("m01aa_01a")

        evidence = preflight.StaticScanner(SimpleNamespace()).scan_room("****")
        self.assertEqual(1, evidence["counts"]["sourceRoomPlaceholders"])
        self.assertFalse(evidence["blockers"])

    def test_mixed_source_material_counts_preserve_both_authored_semantics(self) -> None:
        rooms = [{
            "materialContracts": [
                {"blend": "additive", "environmentMap": "cm_window",
                 "lightmapTexture": "window_lm", "meshCount": 2},
                {"blend": "additive", "environmentMap": "cm_window",
                 "lightmapTexture": None, "meshCount": 3},
                {"blend": "opaque", "environmentMap": "cm_metal",
                 "lightmapTexture": "metal_lm", "meshCount": 5},
            ],
        }]

        self.assertEqual((5, 2), importer.mixed_source_material_counts(rooms))

    def test_source_loading_background_uses_the_area_table_when_module_art_is_absent(self) -> None:
        source_table = SimpleNamespace(data=b"owned-loadscreen-table")
        fallback_texture = object()
        installation = SimpleNamespace(
            texture_resource_result=lambda name: (
                (fallback_texture, "") if name.lower() == "load_default"
                else (None, "")),
            resource=lambda name, kind: (
                source_table
                if name == "loadscreens" and kind == importer.ResourceType.TwoDA
                else None),
        )
        original_reader = importer.read_2da
        importer.read_2da = lambda _data: SimpleNamespace(
            get_height=lambda: 1,
            get_cell=lambda row, column: (
                "LOAD_DEFAULT" if row == 0 and column == "bmpresref" else ""),
        )
        try:
            resref, selection, table_sha256 = importer.source_loading_background(
                installation, "stunt_00", SimpleNamespace(loadscreen_id=0))
        finally:
            importer.read_2da = original_reader

        self.assertEqual("LOAD_DEFAULT", resref)
        self.assertEqual("area-loadscreens-row-0", selection)
        self.assertEqual(importer.sha256_bytes(source_table.data), table_sha256)

    def test_module_resources_use_physical_rim_case_without_changing_identity(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            module_root = Path(directory)
            (module_root / "M12ab.rim").touch()
            (module_root / "M12ab_s.rim").touch()
            ifo = SimpleNamespace(data=b"ifo", restype="IFO", resname="module")
            installation = SimpleNamespace(
                module_path=lambda: module_root,
                module_resources=lambda filename: {
                    "M12ab.rim": [ifo],
                    "M12ab_s.rim": [],
                }.get(filename, []),
            )

            self.assertEqual("m12ab", importer.normalize_module_id("M12ab"))
            self.assertEqual(
                ("M12ab.rim", "M12ab_s.rim"),
                importer.resolve_module_rim_filenames(installation, "m12ab"),
            )
            self.assertIs(ifo, importer.find_module_resource(
                installation, "m12ab", "IFO"))

    def test_module_rim_resolution_fails_closed_on_case_collision(self) -> None:
        entries = [
            SimpleNamespace(name=name, is_file=lambda: True)
            for name in ("STUNT_00.rim", "stunt_00.rim", "STUNT_00_s.rim")
        ]
        module_root = SimpleNamespace(
            is_dir=lambda: True,
            iterdir=lambda: entries,
        )
        installation = SimpleNamespace(module_path=lambda: module_root)

        with self.assertRaisesRegex(RuntimeError, "Ambiguous on-disk RIM identity"):
            importer.resolve_module_rim_filenames(installation, "stunt_00")

    def test_preflight_discovers_mixed_case_pairs_without_renaming_them(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            module_root = Path(directory)
            (module_root / "M12ab.rim").touch()
            (module_root / "M12ab_s.rim").touch()
            (module_root / "orphan.rim").touch()

            pairs, unpaired = preflight.discover_module_pairs(module_root)

            self.assertEqual(
                {"m12ab": ("M12ab.rim", "M12ab_s.rim")}, pairs)
            self.assertEqual(["orphan"], unpaired)

    def test_preflight_coverage_classes_fail_closed(self) -> None:
        self.assertEqual(
            preflight.BLOCKED_CORE,
            preflight.coverage_class(
                preflight.Counter({"missing-ifo": 1}), preflight.Counter(),
                preflight.Counter()),
        )
        self.assertEqual(
            preflight.BLOCKED_SEMANTICS,
            preflight.coverage_class(
                preflight.Counter(), preflight.Counter({"emitter": 1}),
                preflight.Counter()),
        )
        self.assertEqual(
            preflight.PASS_WITH_GAPS,
            preflight.coverage_class(
                preflight.Counter(), preflight.Counter(),
                preflight.Counter({"wateralpha": 1})),
        )
        self.assertEqual(
            preflight.PASS_NO_GAPS,
            preflight.coverage_class(
                preflight.Counter(), preflight.Counter(), preflight.Counter()),
        )

    def test_preflight_emitter_histogram_reasons_are_independent(self) -> None:
        emitter = {
            "update": "Explosion",
            "render": "Billboard_to_world_z",
            "blend": "Punch-through",
            "xGrid": 0,
            "yGrid": 4,
            "birthRate": 0.0,
            "lifeExpectancy": 0.0,
            "randomVelocity": 0.0,
        }

        self.assertEqual(
            ("blend", "texture", "update"),
            preflight.emitter_unsupported_reasons(
                emitter, texture_available=False),
        )
        self.assertIs(
            importer.room_emitter_unsupported_reasons,
            preflight.emitter_unsupported_reasons,
        )

    def test_preflight_reports_txi_values_and_promotes_only_exact_decal(self) -> None:
        scanner = preflight.StaticScanner(SimpleNamespace())
        evidence = preflight.fresh_evidence()
        scanner.count_txi(evidence, "floor_mark", {
            "directives": {
                "decal": ["1"],
                "proceduretype": ["water"],
                "channelscale": ["4 0.2 0.2 0.2 30.2"],
            },
        })

        self.assertEqual(1, evidence["txiDirectiveValues"]["decal=1"])
        self.assertNotIn("decal", evidence["unsupportedTxiDirectives"])
        self.assertEqual(
            1, evidence["unsupportedTxiValues"]["proceduretype=water"])
        self.assertEqual(
            1,
            evidence["unsupportedTxiValues"][
                "channelscale=4 0.2 0.2 0.2 30.2"],
        )

    def test_txi_semantics_keep_additive_and_environment_map_distinct(self) -> None:
        cache = importer.TextureCache.__new__(importer.TextureCache)
        cache.images = {"lhr_flr01": object(), "lhr_dust01": object()}
        cache.alpha_tests = {"lhr_flr01": 1.0, "lhr_dust01": 1.0}
        cache.txi = {
            "lhr_flr01": "  envmaptexture\tCM_Endar # owned cubemap\n",
            "lhr_dust01": "blending   1\n",
        }
        cache.missing = set()
        cache.environment_maps = set()

        self.assertEqual("cm_endar", cache.source_environment_map("LHR_flr01"))
        self.assertFalse(cache.is_source_additive("LHR_flr01"))
        self.assertTrue(cache.is_source_additive("LHR_dust01"))
        self.assertEqual({"cm_endar"}, cache.environment_maps)
        self.assertEqual(
            "LHR_dust01__aurora_envmap_CM_Endar__aurora_additive",
            importer.material_name("LHR_dust01", True, "CM_Endar"),
        )

    def test_txi_cycle_exports_exact_atlas_timing_marker(self) -> None:
        cache = importer.TextureCache.__new__(importer.TextureCache)
        cache.images = {"ebo_ascrn": object()}
        cache.alpha_tests = {"ebo_ascrn": 1.0}
        cache.txi = {
            "ebo_ascrn": (
                "proceduretype cycle\nnumx 4\nnumy 4\nfps 35\n"
                "blending additive\ndecal 1\n")
        }
        cache.raw_txi = {}
        cache.missing = set()
        cache.environment_maps = set()

        self.assertEqual((4, 4, 35.0), cache.source_cycle("EBO_AScrn"))
        self.assertEqual(
            "EBO_AScrn__aurora_additive__aurora_decal__aurora_cycle_4_4_35",
            importer.material_name(
                "EBO_AScrn", True, None, None, True, False, (4, 4, 35.0)),
        )
        self.assertEqual(
            "rendered", importer.txi_directive_class("proceduretype", ["cycle"]))
        self.assertEqual(
            "unsupported", importer.txi_directive_class("proceduretype", ["water"]))

    def test_txi_bump_map_and_bumpy_shiny_environment_map_remain_distinct(self) -> None:
        cache = importer.TextureCache.__new__(importer.TextureCache)
        cache.images = {"lts_pwall01i": object(), "lts_bwall04b": object()}
        cache.alpha_tests = {"lts_pwall01i": 1.0, "lts_bwall04b": 1.0}
        cache.txi = {
            "lts_pwall01i": (
                "bumpmaptexture LTS_Bwall04B\n"
                "bumpmapscale 1.3\n"
                "bumpyshinytexture CM_m02aa\n"
            ),
            "lts_bwall04b": "",
        }
        cache.missing = set()
        cache.environment_maps = set()

        self.assertEqual(
            ("bumpmaptexture", "LTS_Bwall04B"),
            cache.source_bump_map("LTS_Pwall01i"),
        )
        self.assertEqual("cm_m02aa", cache.source_environment_map("LTS_Pwall01i"))
        self.assertEqual((1.3, True), cache.source_bump_scale("LTS_Pwall01i"))
        self.assertEqual({"cm_m02aa"}, cache.environment_maps)
        self.assertEqual(
            "LTS_Pwall01i__aurora_envmap_CM_m02aa__aurora_normal_scale_1.3",
            importer.material_name("LTS_Pwall01i", False, "CM_m02aa", 1.3),
        )

    def test_raw_tpc_txi_preserves_bump_scale_alias_and_glb_scale(self) -> None:
        txi = b"bumpmapscale 1.3\r\nbumpmaptexture LTS_Bwall04B\r\n"
        payload = bytearray(0x80 + 16)
        struct.pack_into("<I", payload, 0, 0)
        struct.pack_into("<HH", payload, 8, 2, 2)
        payload[12] = 4
        payload[13] = 1
        payload.extend(txi)

        recovered = importer.raw_tpc_txi(bytes(payload))

        self.assertEqual(txi.decode("ascii").strip(), recovered)
        self.assertEqual(
            ["1.3"],
            importer.parse_txi_directives(recovered)["bumpmapscaling"],
        )

        material_name = "wall__aurora_normal_scale_1.3"
        document = {
            "asset": {"version": "2.0"},
            "materials": [{"name": material_name, "normalTexture": {"index": 0}}],
        }
        encoded = json.dumps(document, separators=(",", ":")).encode("utf-8")
        encoded += b" " * ((4 - len(encoded) % 4) % 4)
        glb = (
            b"glTF" + struct.pack("<II", 2, 20 + len(encoded)) +
            struct.pack("<II", len(encoded), 0x4E4F534A) + encoded
        )

        patched = importer.patch_glb_texture_channels(glb, {material_name: 1.3})
        json_length = struct.unpack_from("<I", patched, 12)[0]
        patched_document = json.loads(patched[20:20 + json_length].decode("utf-8"))
        self.assertEqual(
            1.3, patched_document["materials"][0]["normalTexture"]["scale"])

    def test_txi_four_channel_continuations_are_not_invented_commands(self) -> None:
        directives = importer.parse_txi_directives(
            "proceduretype water\n"
            "channelscale 4\n0.2\n0.2\n0.2\n30.2\n"
            "channeltranslate 4\n0.5\n0.7\n0.6\n0.5\n"
            "distort 2\n"
        )

        self.assertEqual(["4 0.2 0.2 0.2 30.2"], directives["channelscale"])
        self.assertEqual(["4 0.5 0.7 0.6 0.5"], directives["channeltranslate"])
        self.assertFalse(any(key[0].isdigit() for key in directives))
        self.assertEqual(
            "unsupported",
            importer.txi_directive_class("channelscale", directives["channelscale"]),
        )

    def test_malformed_txi_continuation_and_decal_values_fail_closed(self) -> None:
        directives = importer.parse_txi_directives(
            "channelscale 4\n0.2\nnot-a-number\n0.2\n30.2\n")

        self.assertEqual(["4"], directives["channelscale"])
        self.assertIn("0.2", directives)
        self.assertEqual(
            "unsupported", importer.txi_directive_class("decal", ["1", "0"]))
        self.assertEqual(
            "unsupported", importer.txi_directive_class("decal", ["maybe"]))

    def test_tsl_presence_only_decal_suffix_is_canonicalized(self) -> None:
        directives = importer.parse_txi_directives(
            "decal1\nproceduretype cycle\nnumx 4\nnumy 4\nfps 16\n")

        self.assertEqual(["1"], directives["decal"])
        self.assertNotIn("decal1", directives)
        self.assertEqual(
            "rendered", importer.txi_directive_class("decal", directives["decal"]))

    def test_source_decal_is_alpha_no_depth_write_material_marker(self) -> None:
        cache = importer.TextureCache.__new__(importer.TextureCache)
        cache.images = {"floor_mark": object()}
        cache.alpha_tests = {"floor_mark": 1.0}
        cache.txi = {"floor_mark": "decal 1\n"}
        cache.raw_txi = {}
        cache.missing = set()
        cache.environment_maps = set()

        self.assertTrue(cache.is_source_decal("floor_mark"))
        self.assertTrue(cache.is_source_transparent("floor_mark"))
        self.assertEqual(
            "floor_mark__aurora_decal",
            importer.material_name("floor_mark", False, None, source_decal=True),
        )
        self.assertEqual(
            "rendered", importer.txi_directive_class("decal", ["1"]))

    def test_mesh_transparency_hint_preserves_source_render_queue(self) -> None:
        cache = importer.TextureCache.__new__(importer.TextureCache)
        cache.images = {"kolto": object(), "kolto_lm": object()}
        cache.alpha_tests = {"kolto": 1.0, "kolto_lm": 1.0}
        cache.txi = {"kolto": "", "kolto_lm": ""}
        cache.raw_txi = {}
        cache.missing = set()
        cache.environment_maps = set()
        cache.installation = SimpleNamespace(
            texture_resource_result=lambda _name: (None, ""))

        hinted = cache.material_semantics("kolto", "kolto_lm", True)
        opaque = cache.material_semantics("kolto", "kolto_lm", False)

        self.assertEqual("alpha", hinted["blend"])
        self.assertEqual("opaque", opaque["blend"])
        self.assertEqual(
            "kolto__aurora_transparency_hint", hinted["materialName"])
        self.assertEqual("kolto", opaque["materialName"])

    def test_lightmap_uv_uses_the_same_v_convention_as_gltf_diffuse_uv(self) -> None:
        converted = importer.gltf_lightmap_uv([
            SimpleNamespace(x=0.25, y=0.125),
            SimpleNamespace(x=0.75, y=0.875),
        ])

        importer.np.testing.assert_allclose(
            [[0.25, 0.875], [0.75, 0.125]], converted)

    def test_generic_room_emitter_policy_accepts_supported_source_semantics(self) -> None:
        emitter = {
            "update": "Fountain",
            "render": "Motion_Blur",
            "blend": "Lighten",
            "xGrid": 4,
            "yGrid": 4,
            "birthRate": 12.0,
            "lifeExpectancy": 1.5,
        }

        importer.validate_room_emitter_semantics(emitter, "tar_m02aa:steam")

        emitter["update"] = "Lightning"
        with self.assertRaisesRegex(RuntimeError, "tar_m02aa:steam"):
            importer.validate_room_emitter_semantics(emitter, "tar_m02aa:steam")

    def test_axial_render_modes_and_zero_grid_use_generic_source_contract(self) -> None:
        emitter = {
            "update": "Fountain",
            "render": "Billboard_to_World_Z",
            "blend": "Normal",
            "xGrid": 0,
            "yGrid": 0,
            "birthRate": 8.0,
            "lifeExpectancy": 2.0,
        }

        self.assertEqual((), importer.room_emitter_unsupported_reasons(emitter))
        for render in (
            "Billboard_to_Local_Z",
            "Aligned_to_Particle_Dir",
            "Motion_Blur",
        ):
            emitter["render"] = render
            self.assertEqual((), importer.room_emitter_unsupported_reasons(emitter))

        emitter["update"] = "Explosion"
        self.assertEqual(
            ("update",), importer.room_emitter_unsupported_reasons(emitter))

    def test_finite_single_and_zero_base_velocity_have_distinct_lifetimes(self) -> None:
        emitter = {
            "update": "Single",
            "render": "Normal",
            "blend": "Normal",
            "xGrid": 1,
            "yGrid": 1,
            "birthRate": 0.0,
            "lifeExpectancy": 0.75,
            "velocity": 2.0,
            "randomVelocity": 0.5,
            "gravity": 0.0,
            "sizeStart": 1.0,
            "sizeMid": 2.0,
            "sizeEnd": 0.0,
            "fps": 0.0,
            "frameStart": 0.0,
            "frameEnd": 0.0,
        }

        self.assertEqual((), importer.room_emitter_unsupported_reasons(emitter))

    def test_source_motion_and_render_metadata_fail_closed(self) -> None:
        emitter = {
            "update": "Single",
            "render": "Normal",
            "blend": "Normal",
            "xGrid": 1,
            "yGrid": 1,
            "birthRate": 0.0,
            "lifeExpectancy": 0.75,
            "velocity": 2.0,
            "randomVelocity": 0.5,
            "gravity": 0.0,
            "sizeStart": 1.0,
            "sizeMid": 2.0,
            "sizeEnd": 0.0,
            "fps": 0.0,
            "frameStart": 0.0,
            "frameEnd": 0.0,
        }
        inert_static_room_flags = 0x0002 | 0x0008 | 0x0020 | 0x0040 | 0x0100
        emitter.update({
            "flags": inert_static_room_flags,
            "windPower": 0,
            "spawnType": 0,
            "renderOrder": 1,
            "frameBlender": 0,
            "depthTexture": "NULL",
        })
        self.assertEqual((), importer.room_emitter_unsupported_reasons(emitter))
        for key, value in (
            ("flags", inert_static_room_flags | 0x0080),
            ("flags", inert_static_room_flags | 0x0200),
            ("flags", inert_static_room_flags | 0x0400),
            ("flags", inert_static_room_flags | 0x0800),
            ("flags", inert_static_room_flags | 0x1000),
            ("spawnType", 1),
            ("frameBlender", 1),
            ("depthTexture", "fx_depth"),
            ("renderOrder", 128),
            ("xSize", -1.0),
            ("ySize", float("nan")),
        ):
            candidate = {**emitter, key: value}
            self.assertEqual(
                ("render",),
                importer.room_emitter_unsupported_reasons(candidate),
                msg=f"{key}={value!r}",
            )
        wind_affected = {**emitter, "flags": inert_static_room_flags | 0x0004}
        self.assertEqual(
            (), importer.room_emitter_unsupported_reasons(wind_affected))
        self.assertEqual(
            ("render",), importer.room_emitter_unsupported_reasons({
                **wind_affected, "windPower": 1,
            }))
        collision_bounce = {
            **emitter,
            "flags": inert_static_room_flags | 0x0010,
            "bounceCoefficient": 0.3,
        }
        self.assertEqual(
            (), importer.room_emitter_unsupported_reasons(collision_bounce))
        for coefficient in (-0.01, 1.01, float("nan")):
            self.assertEqual(
                ("render",), importer.room_emitter_unsupported_reasons({
                    **collision_bounce,
                    "bounceCoefficient": coefficient,
                }))
        for candidate, expected_visual_reason in (
            ({**emitter, "frameEnd": -1.0}, "atlas_range"),
            ({**emitter, "sizeStart": 9.0}, "quad_extent"),
            ({
                **emitter,
                "render": "Motion_Blur",
                "sizeStart": 1.0,
                "sizeMid": 1.0,
                "sizeEnd": 1.0,
                "blurLength": 9.0,
            }, "quad_extent"),
        ):
            self.assertEqual(
                (expected_visual_reason,),
                importer.room_emitter_visual_safety_reasons(candidate))
            self.assertEqual(
                ("render",),
                importer.room_emitter_unsupported_reasons(candidate))
        self.assertEqual(
            (), importer.room_emitter_unsupported_reasons({
                **emitter,
                "xGrid": 4,
                "yGrid": 4,
                "frameStart": 0.0,
                "frameEnd": 20.0,
                "fps": 25.0,
            }))
        point_to_point = {
            **emitter,
            "flags": (inert_static_room_flags & ~0x0002) | 0x0001,
            "gravity": 3.0,
            "pointToPointTargetPosition": [0.25, 0.5, -0.125],
        }
        self.assertEqual(
            (), importer.room_emitter_unsupported_reasons(point_to_point))
        self.assertEqual(
            ("render",), importer.room_emitter_unsupported_reasons({
                **point_to_point,
                "flags": point_to_point["flags"] | 0x0002,
            }))
        self.assertEqual(
            ("render",), importer.room_emitter_unsupported_reasons({
                **point_to_point,
                "pointToPointTargetPosition": None,
            }))
        emitter.update({
            "lifeExpectancy": -1.0,
            "birthRate": 1.0,
            "velocity": 0.0,
            "randomVelocity": 0.6,
            "sizeStart": 3.0,
            "sizeMid": 3.0,
            "sizeEnd": 3.0,
            "frameStart": 1.0,
            "frameEnd": 1.0,
        })
        # The neutral room contract treats random-velocity modulation as
        # inactive when the authored base velocity is zero. Conflicting
        # clean-room implementations remain documented; no parity is claimed.
        self.assertEqual((), importer.room_emitter_unsupported_reasons(emitter))

    def test_persistent_single_emitter_requires_the_owned_sprite_signature(self) -> None:
        emitter = {
            "update": "Single",
            "render": "Normal",
            "blend": "Normal",
            "xGrid": 4,
            "yGrid": 4,
            "birthRate": 1.0,
            "lifeExpectancy": -1.0,
            "velocity": 0.0,
            "randomVelocity": 0.0,
            "gravity": 0.0,
            "sizeStart": 3.0,
            "sizeMid": 3.0,
            "sizeEnd": 3.0,
            "fps": 30.0,
            "frameStart": 1.0,
            "frameEnd": 16.0,
        }

        importer.validate_room_emitter_semantics(emitter, "kas_m22aa:bird")

        emitter["velocity"] = 1.0
        with self.assertRaisesRegex(RuntimeError, "room-emitter semantic"):
            importer.validate_room_emitter_semantics(emitter, "kas_m22aa:bird")

    def test_emitter_gravity_is_not_substituted_with_mass(self) -> None:
        def controller(kind: MDLControllerType, value: float) -> SimpleNamespace:
            return SimpleNamespace(
                controller_type=kind,
                rows=[SimpleNamespace(data=[value])],
            )

        node = SimpleNamespace(controllers=[
            controller(MDLControllerType.GRAV, 0.0),
            controller(MDLControllerType.MASS, 1.0),
            controller(MDLControllerType.BIRTHRATE, 5.0),
            controller(MDLControllerType.XSIZE, 2000.0),
            controller(MDLControllerType.YSIZE, 1500.0),
            controller(MDLControllerType.BOUNCECO, 0.3),
        ])

        values = importer.room_emitter_controller_values(node)

        self.assertEqual(0.0, values["gravity"])
        self.assertEqual(1.0, values["mass"])
        self.assertEqual(5.0, values["birthRate"])
        self.assertEqual(2000.0, values["xSize"])
        self.assertEqual(1500.0, values["ySize"])
        self.assertAlmostEqual(0.3, values["bounceCoefficient"])

    def test_straight_point_to_point_target_requires_static_child_pose(self) -> None:
        target_position = SimpleNamespace(x=0.25, y=0.5, z=-0.125)
        target = SimpleNamespace(
            position=target_position,
            children=[],
            controllers=[SimpleNamespace(
                controller_type=MDLControllerType.POSITION,
                rows=[SimpleNamespace(
                    time=0.0,
                    data=[0.25, 0.5, -0.125],
                )],
            )],
        )
        node = SimpleNamespace(
            emitter=SimpleNamespace(flags=0x0001),
            children=[target],
        )

        self.assertEqual(
            [0.25, 0.5, -0.125],
            importer.room_emitter_point_to_point_target(node),
        )

        target.controllers[0].rows.append(SimpleNamespace(
            time=1.0,
            data=[1.0, 2.0, 3.0],
        ))
        self.assertIsNone(importer.room_emitter_point_to_point_target(node))
        target.controllers[0].rows.pop()
        node.emitter.flags |= 0x0002
        self.assertIsNone(importer.room_emitter_point_to_point_target(node))

    def test_environment_map_exports_all_six_oriented_source_layers(self) -> None:
        source_pixels = [
            [
                (layer * 32 + 1, 10, 20, 255),
                (layer * 32 + 2, 11, 21, 255),
                (layer * 32 + 3, 12, 22, 255),
                (layer * 32 + 4, 13, 23, 255),
            ]
            for layer in range(6)
        ]
        mipmaps = [
            SimpleNamespace(
                width=2,
                height=2,
                data=bytes(channel for pixel in pixels for channel in pixel),
            )
            for pixels in source_pixels
        ]

        class FakeTexture:
            is_cube_map = True
            layers = [object()] * 6

            def convert(self, _target: object) -> None:
                return None

            def get(self, layer: int, _mipmap: int) -> SimpleNamespace:
                return mipmaps[layer]

        source = SimpleNamespace(data=b"source-cubemap", restype="TPC")
        texture = FakeTexture()
        installation = SimpleNamespace(
            texture_resource_result=lambda _name: (source, ""),
            texture=lambda _name: texture,
        )

        with tempfile.TemporaryDirectory() as directory:
            record = importer.export_environment_map(
                installation, "CM_Endar", Path(directory))

            self.assertEqual(6, len(record["faces"]))
            self.assertEqual(list(range(6)), [face["layer"] for face in record["faces"]])
            self.assertEqual(
                [
                    "positive-x", "negative-x", "positive-y",
                    "negative-y", "positive-z", "negative-z",
                ],
                record["faceOrder"],
            )
            self.assertEqual(
                record["faceOrder"],
                [face["face"] for face in record["faces"]],
            )
            self.assertEqual(
                {"flip-top-bottom"},
                {face["rowTransform"] for face in record["faces"]},
            )
            self.assertEqual("godot-to-odyssey:x,-z,y", record["sampleBasis"])
            self.assertTrue(all(
                (Path(directory) / face["path"]).is_file()
                for face in record["faces"]
            ))
            for layer, face in enumerate(record["faces"]):
                with importer.Image.open(Path(directory) / face["path"]) as image:
                    exported_bytes = image.convert("RGBA").tobytes()
                expected_pixels = source_pixels[layer][2:] + source_pixels[layer][:2]
                self.assertEqual(
                    bytes(channel for pixel in expected_pixels for channel in pixel),
                    exported_bytes,
                    f"cubemap layer {layer} lost its identity or vertical flip",
                )


if __name__ == "__main__":
    unittest.main()
