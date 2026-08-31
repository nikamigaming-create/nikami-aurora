using System.Buffers.Binary;
using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
{
    private static void DragonAgeCharacterGlbAssemblerPreservesContracts(
        string suiteRoot)
    {
        var assembler = new DragonAgeOriginsCharacterGlbAssembler();
        var request = SyntheticCharacterAssemblyRequest();
        ValidateSyntheticPng(request.Materials[0].Textures[0].EncodedPayload);
        var first = assembler.Assemble(request);
        var second = assembler.Assemble(request);
        Expect(first.StandingGlb.SequenceEqual(second.StandingGlb) &&
               first.BedGlb.SequenceEqual(second.BedGlb) &&
               first.ManifestJson == second.ManifestJson,
            "DAO character GLB assembly is not deterministic");
        Expect(!first.StandingGlb.SequenceEqual(first.BedGlb),
            "DAO standing and bed source poses produced the same GLB");
        Expect(AssemblyHash(first.StandingGlb) == first.Manifest.StandingSha256 &&
               AssemblyHash(first.BedGlb) == first.Manifest.BedSha256,
            "DAO character v1 manifest did not bind exact GLB payload hashes");

        using var standing = ReadCharacterGlbJson(first.StandingGlb);
        var root = standing.RootElement;
        var primitive = root.GetProperty("meshes")[0]
            .GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        foreach (var semantic in new[] { "POSITION", "NORMAL", "TANGENT",
                     "TEXCOORD_0", "JOINTS_0", "WEIGHTS_0" })
            Expect(attributes.TryGetProperty(semantic, out _),
                "DAO assembled GLB lost vertex semantic " + semantic);
        Expect(primitive.GetProperty("targets").GetArrayLength() == 1 &&
               root.GetProperty("meshes")[0].GetProperty("weights")[0]
                   .GetSingle() == .25f,
            "DAO assembled GLB lost its morph target or source weight");
        var material = root.GetProperty("materials")[0];
        Expect(material.GetProperty("alphaMode").GetString() == "MASK" &&
               material.GetProperty("alphaCutoff").GetSingle() == .42f &&
               material.GetProperty("doubleSided").GetBoolean() &&
               material.GetProperty("pbrMetallicRoughness")
                   .GetProperty("roughnessFactor").GetSingle() == .7f &&
               material.GetProperty("normalTexture").GetProperty("scale")
                   .GetSingle() == .8f,
            "DAO assembled GLB lost the source PBR/alpha contract");
        Expect(root.GetProperty("images")[0].TryGetProperty("bufferView", out _) &&
               root.GetProperty("images")[0].GetProperty("mimeType").GetString() ==
               "image/png",
            "DAO assembled GLB did not embed the hash-bound converted texture");
        Expect(root.GetProperty("asset").GetProperty("extras")
                   .GetProperty("outfit_resource").GetString() == "outfit_source.utc" &&
               root.GetProperty("asset").GetProperty("extras")
                   .GetProperty("pose_variant").GetString() == "standing",
            "DAO assembled GLB lost outfit/pose provenance");

        var outputRoot = Path.Combine(suiteRoot, "cache", "dao-character-assembly");
        var written = assembler.WriteIgnoredLocalBundle(outputRoot, request);
        Expect(File.ReadAllBytes(written.StandingPath).SequenceEqual(first.StandingGlb) &&
               File.ReadAllBytes(written.BedPath).SequenceEqual(first.BedGlb) &&
               File.ReadAllText(written.ManifestPath) == first.ManifestJson,
            "DAO character bundle writer did not preserve deterministic payloads");
        Expect(DragonAgeOriginsCharacterCreationCatalog.ClassifyImport(
                   request.Appearance, written.Manifest,
                   AssemblyHash(File.ReadAllBytes(written.StandingPath)),
                   AssemblyHash(File.ReadAllBytes(written.BedPath))) ==
               DragonAgeCharacterImportReadiness.FreshImport,
            "DAO character assembler output did not satisfy exact v1 readiness");
    }

    private static void DragonAgeCharacterGlbAssemblerFailsClosed(string suiteRoot)
    {
        var assembler = new DragonAgeOriginsCharacterGlbAssembler();
        var request = SyntheticCharacterAssemblyRequest();
        ExpectThrows<InvalidDataException>(() =>
                assembler.Assemble(request with { Outfit = null }),
            "DAO character assembler invented a missing outfit context");
        ExpectThrows<InvalidDataException>(() =>
                assembler.Assemble(request with { BedPose = null }),
            "DAO character assembler invented a missing bed pose");
        ExpectThrows<InvalidDataException>(() => assembler.Assemble(request with
        {
            Materials = request.Materials.Select(value => value with
            {
                MaterialObjectResource = string.Empty
            }).ToArray()
        }),
            "DAO character assembler accepted a missing MAO identity");
        ExpectThrows<InvalidDataException>(() => assembler.Assemble(request with
        {
            Materials = request.Materials.Select(value => value with
            {
                Textures = []
            }).ToArray()
        }),
            "DAO character assembler accepted a missing texture contract");
        var nonIgnored = Path.Combine(suiteRoot, "not-generated", "character-output");
        ExpectThrows<InvalidDataException>(() =>
                assembler.WriteIgnoredLocalBundle(nonIgnored, request),
            "DAO character assembler wrote outside an explicit ignored cache tree");
    }

    private static DragonAgeCharacterAssemblyRequest SyntheticCharacterAssemblyRequest()
    {
        var appearance = DragonAgeOriginsCharacterCreationCatalog.Resolve(
            "human", "female", "preset-1")!;
        var identityMatrix = new float[]
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1
        };
        var skeleton = new[]
        {
            new DragonAgeCharacterAssemblySkeletonNode(
                0, "root", 0, null, true, identityMatrix),
            new DragonAgeCharacterAssemblySkeletonNode(
                1, "head", 1, 0, true, identityMatrix)
        };
        DragonAgeCharacterAssemblyPose Pose(string name, Vector3 head)
        {
            return new DragonAgeCharacterAssemblyPose(name, name + ".ani",
                AssemblyHash(Encoding.UTF8.GetBytes(name)),
                new[]
                {
                    new DragonAgeCharacterAssemblyNodePose(0, Vector3.Zero,
                        Quaternion.Identity, Vector3.One),
                    new DragonAgeCharacterAssemblyNodePose(1, head,
                        Quaternion.Identity, Vector3.One)
                });
        }

        var baseColor = DragonAgeOriginsCharacterTextureEncoder.EncodeRgba8(
            DragonAgeCharacterTextureSemantic.BaseColor,
            "synthetic_diffuse.dds", AssemblyHash(Encoding.UTF8.GetBytes("source-dds")),
            1, 1, new byte[] { 194, 133, 102, 255 });
        var normal = baseColor with
        {
            Semantic = DragonAgeCharacterTextureSemantic.Normal,
            SourceResource = "synthetic_normal.dds",
            SourcePayloadSha256 = AssemblyHash(Encoding.UTF8.GetBytes("source-normal"))
        };
        var material = new DragonAgeCharacterAssemblyMaterial(
            "synthetic_skin", "synthetic_skin.mat",
            AssemblyHash(Encoding.UTF8.GetBytes("mat")), "synthetic_skin.mao",
            AssemblyHash(Encoding.UTF8.GetBytes("mao")), Vector4.One, .1f, .7f, .8f,
            Vector3.Zero, DragonAgeCharacterAlphaMode.Mask, .42f, true,
            new[] { baseColor, normal });
        var positions = new[]
        {
            new Vector3(-.5f, 0, 0), new Vector3(.5f, 0, 0),
            new Vector3(0, 1, 0)
        };
        var zeros = positions.Select(_ => Vector3.Zero).ToArray();
        var target = new DragonAgeCharacterAssemblyMorphTarget(
            "nose_width", "synthetic_nose.msh",
            AssemblyHash(Encoding.UTF8.GetBytes("morph")), .25f, zeros, zeros, zeros);
        var influence = new DragonAgeCharacterAssemblySkinInfluence(
            0, 1, 0, 0, new Vector4(.5f, .5f, 0, 0));
        var mesh = new DragonAgeCharacterDecodedMesh(
            "head_mesh", "synthetic_head.msh",
            AssemblyHash(Encoding.UTF8.GetBytes("mesh")), "synthetic_part.mmh",
            material.Name, 1, positions,
            positions.Select(_ => Vector3.UnitZ).ToArray(),
            positions.Select(_ => new Vector4(1, 0, 0, 1)).ToArray(),
            new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY },
            positions.Select(_ => influence).ToArray(), new uint[] { 0, 1, 2 },
            new[] { target });
        var part = new DragonAgeCharacterAssemblyPart(
            "source-selected-outfit-and-head", "synthetic_part.mmh",
            AssemblyHash(Encoding.UTF8.GetBytes("part")), "synthetic_part.mmh",
            AssemblyHash(Encoding.UTF8.GetBytes("hierarchy")), new[] { mesh.Name });
        return new DragonAgeCharacterAssemblyRequest(
            appearance,
            new DragonAgeCharacterAssemblyOutfitContext(
                "outfit_source.utc", AssemblyHash(Encoding.UTF8.GetBytes("outfit")),
                new[] { part.Resource }),
            new DragonAgeCharacterAppliedMorphContract(
                appearance.MorphResource, appearance.MorphSha256,
                new[] { target.Name }),
            skeleton, new[] { part }, new[] { mesh }, new[] { material },
            Pose("standing", new Vector3(0, 1, 0)),
            Pose("bed", new Vector3(0, .25f, -.8f)));
    }

    private static JsonDocument ReadCharacterGlbJson(byte[] glb)
    {
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(glb) == 0x46546c67 &&
               BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)) == 2 &&
               BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)) == glb.Length,
            "DAO character assembler emitted a malformed GLB header");
        var jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            glb.AsSpan(12)));
        Expect(BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)) == 0x4e4f534a,
            "DAO character assembler emitted a malformed GLB JSON chunk");
        return JsonDocument.Parse(glb.AsMemory(20, jsonLength));
    }

    private static void ValidateSyntheticPng(byte[] png)
    {
        Expect(png.AsSpan().StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "DAO deterministic texture encoder emitted a malformed PNG signature");
        var offset = 8;
        var compressed = new MemoryStream();
        while (offset < png.Length)
        {
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
                png.AsSpan(offset)));
            var type = Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") compressed.Write(png, offset + 8, length);
            offset = checked(offset + 12 + length);
        }
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        Expect(decoded.ToArray().SequenceEqual(new byte[] { 0, 194, 133, 102, 255 }),
            "DAO deterministic texture encoder did not preserve RGBA8 channels");
    }

    private static string AssemblyHash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
