using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public enum DragonAgeCharacterTextureSemantic
{
    BaseColor,
    Normal,
    MetallicRoughness,
    Occlusion,
    Emissive
}

public enum DragonAgeCharacterAlphaMode
{
    Opaque,
    Mask,
    Blend
}

public sealed record DragonAgeCharacterAssemblyTexture(
    DragonAgeCharacterTextureSemantic Semantic,
    string SourceResource,
    string SourcePayloadSha256,
    string EncodedMimeType,
    string EncodedPayloadSha256,
    byte[] EncodedPayload);

/// <summary>
/// BCL-only deterministic RGBA8-to-PNG boundary for already decoded source
/// textures. Retail DDS interpretation stays in the profile decoder; this
/// encoder neither resamples nor changes color channels.
/// </summary>
public static class DragonAgeOriginsCharacterTextureEncoder
{
    private static readonly byte[] PngSignature =
        [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];

    public static DragonAgeCharacterAssemblyTexture EncodeRgba8(
        DragonAgeCharacterTextureSemantic semantic, string sourceResource,
        string sourcePayloadSha256, int width, int height, byte[] rgba8)
    {
        ArgumentNullException.ThrowIfNull(rgba8);
        if (width is <= 0 or > 16384 || height is <= 0 or > 16384 ||
            rgba8.Length != checked(width * height * 4))
            throw new InvalidDataException(
                "DAO character decoded RGBA8 texture dimensions are invalid.");
        var scanlines = new byte[checked(height * (width * 4 + 1))];
        for (var row = 0; row < height; row++)
            rgba8.AsSpan(row * width * 4, width * 4).CopyTo(
                scanlines.AsSpan(row * (width * 4 + 1) + 1));

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 6;
        var png = new MemoryStream();
        png.Write(PngSignature);
        WriteChunk(png, "IHDR", header);
        WriteChunk(png, "IDAT", StoreZlib(scanlines));
        WriteChunk(png, "IEND", []);
        var payload = png.ToArray();
        return new DragonAgeCharacterAssemblyTexture(
            semantic, sourceResource, sourcePayloadSha256, "image/png",
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), payload);
    }

    private static byte[] StoreZlib(byte[] source)
    {
        var result = new MemoryStream();
        result.WriteByte(0x78);
        result.WriteByte(0x01);
        var offset = 0;
        var lengths = new byte[4];
        while (offset < source.Length)
        {
            var count = Math.Min(65535, source.Length - offset);
            result.WriteByte(offset + count == source.Length ? (byte)1 : (byte)0);
            BinaryPrimitives.WriteUInt16LittleEndian(lengths, (ushort)count);
            BinaryPrimitives.WriteUInt16LittleEndian(lengths.AsSpan(2),
                unchecked((ushort)~count));
            result.Write(lengths);
            result.Write(source, offset, count);
            offset += count;
        }
        uint a = 1;
        uint b = 0;
        foreach (var value in source)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, (b << 16) | a);
        result.Write(checksum);
        return result.ToArray();
    }

    private static void WriteChunk(Stream destination, string type, byte[] data)
    {
        var typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> scalar = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(scalar, (uint)data.Length);
        destination.Write(scalar);
        destination.Write(typeBytes);
        destination.Write(data);
        var crc = Crc32(typeBytes, data);
        BinaryPrimitives.WriteUInt32BigEndian(scalar, crc);
        destination.Write(scalar);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        var crc = uint.MaxValue;
        foreach (var value in type.Concat(data))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xedb88320u &
                    unchecked((uint)-(int)(crc & 1)));
        }
        return ~crc;
    }
}

public sealed record DragonAgeCharacterAssemblyMaterial(
    string Name,
    string MaterialResource,
    string MaterialPayloadSha256,
    string MaterialObjectResource,
    string MaterialObjectPayloadSha256,
    Vector4 BaseColorFactor,
    float MetallicFactor,
    float RoughnessFactor,
    float NormalScale,
    Vector3 EmissiveFactor,
    DragonAgeCharacterAlphaMode AlphaMode,
    float AlphaCutoff,
    bool DoubleSided,
    IReadOnlyList<DragonAgeCharacterAssemblyTexture> Textures);

public sealed record DragonAgeCharacterAssemblySkinInfluence(
    ushort Joint0,
    ushort Joint1,
    ushort Joint2,
    ushort Joint3,
    Vector4 Weights);

public sealed record DragonAgeCharacterAssemblyMorphTarget(
    string Name,
    string SourceResource,
    string SourcePayloadSha256,
    float Weight,
    IReadOnlyList<Vector3> PositionDeltas,
    IReadOnlyList<Vector3> NormalDeltas,
    IReadOnlyList<Vector3> TangentDeltas);

public sealed record DragonAgeCharacterDecodedMesh(
    string Name,
    string SourceResource,
    string SourcePayloadSha256,
    string PartResource,
    string MaterialName,
    int AttachmentNodeIndex,
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<Vector3> Normals,
    IReadOnlyList<Vector4> Tangents,
    IReadOnlyList<Vector2> TextureCoordinates,
    IReadOnlyList<DragonAgeCharacterAssemblySkinInfluence> SkinInfluences,
    IReadOnlyList<uint> Indices,
    IReadOnlyList<DragonAgeCharacterAssemblyMorphTarget> MorphTargets);

public sealed record DragonAgeCharacterAssemblySkeletonNode(
    int Index,
    string Name,
    int? SourceBoneId,
    int? ParentIndex,
    bool IsJoint,
    IReadOnlyList<float>? InverseBindMatrixColumnMajor);

public sealed record DragonAgeCharacterAssemblyNodePose(
    int NodeIndex,
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 Scale);

public sealed record DragonAgeCharacterAssemblyPose(
    string Name,
    string SourceResource,
    string SourcePayloadSha256,
    IReadOnlyList<DragonAgeCharacterAssemblyNodePose> Nodes);

public sealed record DragonAgeCharacterAssemblyPart(
    string Kind,
    string Resource,
    string PayloadSha256,
    string HierarchyResource,
    string HierarchyPayloadSha256,
    IReadOnlyList<string> MeshNames);

public sealed record DragonAgeCharacterAssemblyOutfitContext(
    string Resource,
    string PayloadSha256,
    IReadOnlyList<string> PartResources);

public sealed record DragonAgeCharacterAppliedMorphContract(
    string Resource,
    string PayloadSha256,
    IReadOnlyList<string> AppliedTargetNames);

public sealed record DragonAgeCharacterAssemblyRequest(
    DragonAgeCharacterCreationAppearance Appearance,
    DragonAgeCharacterAssemblyOutfitContext? Outfit,
    DragonAgeCharacterAppliedMorphContract Morph,
    IReadOnlyList<DragonAgeCharacterAssemblySkeletonNode> Skeleton,
    IReadOnlyList<DragonAgeCharacterAssemblyPart> Parts,
    IReadOnlyList<DragonAgeCharacterDecodedMesh> Meshes,
    IReadOnlyList<DragonAgeCharacterAssemblyMaterial> Materials,
    DragonAgeCharacterAssemblyPose? StandingPose,
    DragonAgeCharacterAssemblyPose? BedPose);

public sealed record DragonAgeCharacterAssemblyBundle(
    byte[] StandingGlb,
    byte[] BedGlb,
    DragonAgeCharacterCreationImportManifest Manifest,
    string ManifestJson);

public sealed record DragonAgeCharacterAssemblyWriteResult(
    string StandingPath,
    string BedPath,
    string ManifestPath,
    DragonAgeCharacterCreationImportManifest Manifest);

public interface IDragonAgeOriginsCharacterGlbAssembler
{
    DragonAgeCharacterAssemblyBundle Assemble(DragonAgeCharacterAssemblyRequest request);

    DragonAgeCharacterAssemblyWriteResult WriteIgnoredLocalBundle(
        string ignoredOutputRoot, DragonAgeCharacterAssemblyRequest request);
}

/// <summary>
/// Deterministic, profile-owned glTF 2.0 binary assembler. It consumes decoded,
/// source-hash-bound contracts only. It does not decode retail files, select an
/// outfit, invent a bind/bed pose, or write outside an explicitly ignored local
/// cache tree.
/// </summary>
public sealed class DragonAgeOriginsCharacterGlbAssembler :
    IDragonAgeOriginsCharacterGlbAssembler
{
    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private static readonly HashSet<string> IgnoredRootSegments =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "local", "cache", "artifacts", "imports"
        };

    public DragonAgeCharacterAssemblyBundle Assemble(
        DragonAgeCharacterAssemblyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var standing = BuildGlb(request, request.StandingPose!, "standing");
        var bed = BuildGlb(request, request.BedPose!, "bed");
        var standingHash = Hash(standing);
        var bedHash = Hash(bed);
        var appearance = request.Appearance;
        var manifest = new DragonAgeCharacterCreationImportManifest(
            DragonAgeOriginsCharacterCreationCatalog.ManifestSchema,
            DragonAgeOriginsCharacterCreationCatalog.ImporterId,
            appearance.SelectionKey,
            DragonAgeOriginsCharacterCreationCatalog.CatalogContainerRelativePath,
            DragonAgeOriginsCharacterCreationCatalog.CatalogContainerSha256,
            DragonAgeOriginsCharacterCreationCatalog.CatalogResource,
            DragonAgeOriginsCharacterCreationCatalog.CatalogResourceSha256,
            DragonAgeOriginsCharacterCreationCatalog.SourceContainerRelativePath,
            DragonAgeOriginsCharacterCreationCatalog.SourceContainerSha256,
            appearance.MorphResource,
            appearance.MorphSha256,
            appearance.StandingRelativePath,
            standingHash,
            appearance.BedRelativePath,
            bedHash);
        var json = JsonSerializer.Serialize(manifest, ManifestOptions) + "\n";
        return new DragonAgeCharacterAssemblyBundle(standing, bed, manifest, json);
    }

    public DragonAgeCharacterAssemblyWriteResult WriteIgnoredLocalBundle(
        string ignoredOutputRoot, DragonAgeCharacterAssemblyRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ignoredOutputRoot);
        var root = Path.GetFullPath(ignoredOutputRoot);
        if (!Segments(root).TakeLast(3).Any(IgnoredRootSegments.Contains))
            throw new InvalidDataException(
                "DAO character output root is not an ignored local/cache/artifacts/imports tree.");
        var bundle = Assemble(request);
        var standingPath = Contained(root, request.Appearance.StandingRelativePath);
        var bedPath = Contained(root, request.Appearance.BedRelativePath);
        var manifestPath = Contained(root, request.Appearance.ImportManifestRelativePath);
        WriteAtomically(standingPath, bundle.StandingGlb);
        WriteAtomically(bedPath, bundle.BedGlb);
        WriteAtomically(manifestPath, Encoding.UTF8.GetBytes(bundle.ManifestJson));
        return new DragonAgeCharacterAssemblyWriteResult(
            standingPath, bedPath, manifestPath, bundle.Manifest);
    }

    private static byte[] BuildGlb(DragonAgeCharacterAssemblyRequest request,
        DragonAgeCharacterAssemblyPose pose, string variant)
    {
        var binary = new BinaryBuffer();
        var bufferViews = new JsonArray();
        var accessors = new JsonArray();
        var images = new JsonArray();
        var textures = new JsonArray();
        var samplers = new JsonArray
        {
            new JsonObject
            {
                ["magFilter"] = 9729,
                ["minFilter"] = 9987,
                ["wrapS"] = 10497,
                ["wrapT"] = 10497
            }
        };
        var textureIndices = BuildTextures(request.Materials, binary,
            bufferViews, images, textures);
        var materials = BuildMaterials(request.Materials, textureIndices);

        var skeleton = request.Skeleton.OrderBy(value => value.Index).ToArray();
        var poseByNode = pose.Nodes.ToDictionary(value => value.NodeIndex);
        var nodes = new JsonArray();
        var childIndices = skeleton.ToDictionary(value => value.Index,
            _ => new List<int>());
        foreach (var node in skeleton)
            if (node.ParentIndex is int parent)
                childIndices[parent].Add(node.Index);

        var orderedMeshes = request.Meshes
            .OrderBy(value => value.PartResource, StringComparer.Ordinal)
            .ThenBy(value => value.SourceResource, StringComparer.Ordinal)
            .ThenBy(value => value.Name, StringComparer.Ordinal).ToArray();
        for (var meshIndex = 0; meshIndex < orderedMeshes.Length; meshIndex++)
            childIndices[orderedMeshes[meshIndex].AttachmentNodeIndex]
                .Add(skeleton.Length + meshIndex);

        foreach (var node in skeleton)
        {
            var transform = poseByNode[node.Index];
            var nodeJson = new JsonObject
            {
                ["name"] = node.Name,
                ["translation"] = Vector(transform.Translation),
                ["rotation"] = QuaternionValue(transform.Rotation),
                ["scale"] = Vector(transform.Scale),
                ["extras"] = new JsonObject
                {
                    ["source_node_index"] = node.Index,
                    ["source_bone_id"] = node.SourceBoneId
                }
            };
            var children = childIndices[node.Index].Order().ToArray();
            if (children.Length > 0)
                nodeJson["children"] = Array(children.Select(value => (JsonNode?)value));
            nodes.Add(nodeJson);
        }

        var materialIndices = request.Materials
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .Select((value, index) => (value.Name, index))
            .ToDictionary(value => value.Name, value => value.index,
                StringComparer.Ordinal);
        var meshRecords = new JsonArray();
        foreach (var mesh in orderedMeshes)
        {
            var meshIndex = meshRecords.Count;
            meshRecords.Add(BuildMesh(mesh, materialIndices[mesh.MaterialName],
                binary, bufferViews, accessors));
            nodes.Add(new JsonObject
            {
                ["name"] = mesh.Name,
                ["mesh"] = meshIndex,
                ["skin"] = 0,
                ["extras"] = new JsonObject
                {
                    ["source_resource"] = mesh.SourceResource,
                    ["source_payload_sha256"] = mesh.SourcePayloadSha256,
                    ["part_resource"] = mesh.PartResource
                }
            });
        }

        var jointNodes = skeleton.Where(value => value.IsJoint).ToArray();
        var inverseBytes = new byte[jointNodes.Length * 16 * sizeof(float)];
        for (var joint = 0; joint < jointNodes.Length; joint++)
        {
            var matrix = jointNodes[joint].InverseBindMatrixColumnMajor!;
            for (var component = 0; component < 16; component++)
                BinaryPrimitives.WriteSingleLittleEndian(
                    inverseBytes.AsSpan((joint * 16 + component) * sizeof(float)),
                    matrix[component]);
        }
        var inverseView = AddBufferView(binary, bufferViews, inverseBytes);
        var inverseAccessor = AddAccessor(accessors, inverseView, 5126,
            jointNodes.Length, "MAT4");
        var skins = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "source-skeleton",
                ["inverseBindMatrices"] = inverseAccessor,
                ["joints"] = Array(jointNodes.Select(value => (JsonNode?)value.Index)),
                ["skeleton"] = skeleton.First(value => value.ParentIndex is null).Index
            }
        };
        var roots = skeleton.Where(value => value.ParentIndex is null)
            .Select(value => (JsonNode?)value.Index).ToArray();

        var root = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0",
                ["generator"] = DragonAgeOriginsCharacterCreationCatalog.ImporterId,
                ["extras"] = Provenance(request, pose, variant)
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray
            {
                new JsonObject { ["name"] = variant, ["nodes"] = Array(roots) }
            },
            ["nodes"] = nodes,
            ["meshes"] = meshRecords,
            ["skins"] = skins,
            ["materials"] = materials,
            ["samplers"] = samplers,
            ["textures"] = textures,
            ["images"] = images,
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new JsonArray
            {
                new JsonObject { ["byteLength"] = binary.Length }
            }
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(root,
            new JsonSerializerOptions { WriteIndented = false });
        return PackGlb(json, binary.ToArray());
    }

    private static JsonObject BuildMesh(DragonAgeCharacterDecodedMesh mesh,
        int materialIndex, BinaryBuffer binary, JsonArray views, JsonArray accessors)
    {
        var attributes = new JsonObject
        {
            ["POSITION"] = AddVector3Accessor(mesh.Positions, binary, views,
                accessors, includeBounds: true),
            ["NORMAL"] = AddVector3Accessor(mesh.Normals, binary, views, accessors),
            ["TANGENT"] = AddVector4Accessor(mesh.Tangents, binary, views, accessors),
            ["TEXCOORD_0"] = AddVector2Accessor(mesh.TextureCoordinates, binary,
                views, accessors),
            ["JOINTS_0"] = AddJointAccessor(mesh.SkinInfluences, binary, views,
                accessors),
            ["WEIGHTS_0"] = AddWeightAccessor(mesh.SkinInfluences, binary, views,
                accessors)
        };
        var indexBytes = new byte[mesh.Indices.Count * sizeof(uint)];
        for (var index = 0; index < mesh.Indices.Count; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                indexBytes.AsSpan(index * sizeof(uint)), mesh.Indices[index]);
        var indexView = AddBufferView(binary, views, indexBytes, 34963);
        var indexAccessor = AddAccessor(accessors, indexView, 5125,
            mesh.Indices.Count, "SCALAR");
        var targets = new JsonArray();
        foreach (var target in mesh.MorphTargets.OrderBy(value => value.Name,
                     StringComparer.Ordinal))
        {
            targets.Add(new JsonObject
            {
                ["POSITION"] = AddVector3Accessor(target.PositionDeltas, binary,
                    views, accessors),
                ["NORMAL"] = AddVector3Accessor(target.NormalDeltas, binary,
                    views, accessors),
                ["TANGENT"] = AddVector3Accessor(target.TangentDeltas, binary,
                    views, accessors)
            });
        }
        var primitive = new JsonObject
        {
            ["attributes"] = attributes,
            ["indices"] = indexAccessor,
            ["material"] = materialIndex,
            ["mode"] = 4
        };
        if (targets.Count > 0)
            primitive["targets"] = targets;
        var orderedTargets = mesh.MorphTargets.OrderBy(value => value.Name,
            StringComparer.Ordinal).ToArray();
        return new JsonObject
        {
            ["name"] = mesh.Name,
            ["primitives"] = new JsonArray { primitive },
            ["weights"] = Array(orderedTargets.Select(value => (JsonNode?)value.Weight)),
            ["extras"] = new JsonObject
            {
                ["targetNames"] = Array(orderedTargets.Select(value =>
                    (JsonNode?)value.Name)),
                ["targetIdentities"] = Array(orderedTargets.Select(value =>
                    (JsonNode?)$"{value.SourceResource}:{value.SourcePayloadSha256}"))
            }
        };
    }

    private static Dictionary<(string Resource, string SourceHash, string EncodedHash), int>
        BuildTextures(IEnumerable<DragonAgeCharacterAssemblyMaterial> source,
            BinaryBuffer binary, JsonArray views, JsonArray images, JsonArray textures)
    {
        var values = source.SelectMany(value => value.Textures)
            .GroupBy(value => (value.SourceResource, value.SourcePayloadSha256,
                value.EncodedPayloadSha256))
            .Select(value => value.First())
            .OrderBy(value => value.SourceResource, StringComparer.Ordinal)
            .ThenBy(value => value.SourcePayloadSha256, StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<(string, string, string), int>();
        foreach (var texture in values)
        {
            var view = AddBufferView(binary, views, texture.EncodedPayload);
            var image = images.Count;
            images.Add(new JsonObject
            {
                ["name"] = texture.SourceResource,
                ["bufferView"] = view,
                ["mimeType"] = texture.EncodedMimeType,
                ["extras"] = new JsonObject
                {
                    ["source_payload_sha256"] = texture.SourcePayloadSha256,
                    ["encoded_payload_sha256"] = texture.EncodedPayloadSha256
                }
            });
            var index = textures.Count;
            textures.Add(new JsonObject
            {
                ["name"] = texture.SourceResource,
                ["sampler"] = 0,
                ["source"] = image
            });
            result[(texture.SourceResource, texture.SourcePayloadSha256,
                texture.EncodedPayloadSha256)] = index;
        }
        return result;
    }

    private static JsonArray BuildMaterials(
        IEnumerable<DragonAgeCharacterAssemblyMaterial> source,
        IReadOnlyDictionary<(string Resource, string SourceHash, string EncodedHash), int>
            textureIndices)
    {
        var result = new JsonArray();
        foreach (var material in source.OrderBy(value => value.Name,
                     StringComparer.Ordinal))
        {
            var slots = material.Textures.ToDictionary(value => value.Semantic);
            JsonObject Texture(DragonAgeCharacterTextureSemantic semantic)
            {
                var value = slots[semantic];
                return new JsonObject
                {
                    ["index"] = textureIndices[(value.SourceResource,
                        value.SourcePayloadSha256, value.EncodedPayloadSha256)]
                };
            }
            var pbr = new JsonObject
            {
                ["baseColorFactor"] = Vector(material.BaseColorFactor),
                ["metallicFactor"] = material.MetallicFactor,
                ["roughnessFactor"] = material.RoughnessFactor,
                ["baseColorTexture"] = Texture(
                    DragonAgeCharacterTextureSemantic.BaseColor)
            };
            if (slots.ContainsKey(DragonAgeCharacterTextureSemantic.MetallicRoughness))
                pbr["metallicRoughnessTexture"] = Texture(
                    DragonAgeCharacterTextureSemantic.MetallicRoughness);
            var json = new JsonObject
            {
                ["name"] = material.Name,
                ["pbrMetallicRoughness"] = pbr,
                ["emissiveFactor"] = Vector(material.EmissiveFactor),
                ["alphaMode"] = material.AlphaMode switch
                {
                    DragonAgeCharacterAlphaMode.Opaque => "OPAQUE",
                    DragonAgeCharacterAlphaMode.Mask => "MASK",
                    DragonAgeCharacterAlphaMode.Blend => "BLEND",
                    _ => throw new InvalidDataException("DAO material alpha mode is unsupported.")
                },
                ["alphaCutoff"] = material.AlphaCutoff,
                ["doubleSided"] = material.DoubleSided,
                ["extras"] = new JsonObject
                {
                    ["material_resource"] = material.MaterialResource,
                    ["material_payload_sha256"] = material.MaterialPayloadSha256,
                    ["material_object_resource"] = material.MaterialObjectResource,
                    ["material_object_payload_sha256"] =
                        material.MaterialObjectPayloadSha256
                }
            };
            if (slots.ContainsKey(DragonAgeCharacterTextureSemantic.Normal))
            {
                var normal = Texture(DragonAgeCharacterTextureSemantic.Normal);
                normal["scale"] = material.NormalScale;
                json["normalTexture"] = normal;
            }
            if (slots.ContainsKey(DragonAgeCharacterTextureSemantic.Occlusion))
                json["occlusionTexture"] = Texture(
                    DragonAgeCharacterTextureSemantic.Occlusion);
            if (slots.ContainsKey(DragonAgeCharacterTextureSemantic.Emissive))
                json["emissiveTexture"] = Texture(
                    DragonAgeCharacterTextureSemantic.Emissive);
            result.Add(json);
        }
        return result;
    }

    private static JsonObject Provenance(DragonAgeCharacterAssemblyRequest request,
        DragonAgeCharacterAssemblyPose pose, string variant) => new()
        {
            ["schema"] = DragonAgeOriginsCharacterCreationCatalog.ManifestSchema,
            ["selection_key"] = request.Appearance.SelectionKey,
            ["morph_resource"] = request.Morph.Resource,
            ["morph_payload_sha256"] = request.Morph.PayloadSha256,
            ["outfit_resource"] = request.Outfit!.Resource,
            ["outfit_payload_sha256"] = request.Outfit.PayloadSha256,
            ["pose_variant"] = variant,
            ["pose_name"] = pose.Name,
            ["pose_resource"] = pose.SourceResource,
            ["pose_payload_sha256"] = pose.SourcePayloadSha256,
            ["parts"] = Array(request.Parts.OrderBy(value => value.Resource,
            StringComparer.Ordinal).Select(value => (JsonNode?)new JsonObject
            {
                ["kind"] = value.Kind,
                ["resource"] = value.Resource,
                ["payload_sha256"] = value.PayloadSha256,
                ["hierarchy_resource"] = value.HierarchyResource,
                ["hierarchy_payload_sha256"] = value.HierarchyPayloadSha256
            }))
        };

    private static void Validate(DragonAgeCharacterAssemblyRequest request)
    {
        var appearance = request.Appearance ?? throw new InvalidDataException(
            "DAO character appearance contract is missing.");
        var catalogAppearance = DragonAgeOriginsCharacterCreationCatalog.Resolve(
            appearance.Race, appearance.Gender, appearance.Preset);
        if (catalogAppearance is null || catalogAppearance != appearance)
            throw new InvalidDataException(
                "DAO character appearance is not the exact source-bound catalog entry.");
        if (request.Outfit is null)
            throw new InvalidDataException(
                "DAO character source-bound outfit context is missing.");
        RequireResourceHash(request.Outfit.Resource, request.Outfit.PayloadSha256,
            "outfit");
        RequireResourceHash(request.Morph.Resource, request.Morph.PayloadSha256,
            "morph");
        if (!request.Morph.Resource.Equals(appearance.MorphResource,
                StringComparison.OrdinalIgnoreCase) ||
            request.Morph.PayloadSha256 != appearance.MorphSha256)
            throw new InvalidDataException(
                "DAO character applied morph identity disagrees with the selected preset.");
        if (request.StandingPose is null || request.BedPose is null)
            throw new InvalidDataException(
                "DAO character standing and bed pose contracts are both required.");
        if (request.StandingPose.Name.Equals(request.BedPose.Name,
                StringComparison.Ordinal) &&
            request.StandingPose.SourceResource.Equals(request.BedPose.SourceResource,
                StringComparison.Ordinal) &&
            request.StandingPose.SourcePayloadSha256 == request.BedPose.SourcePayloadSha256)
            throw new InvalidDataException(
                "DAO character standing and bed variants cannot share an unqualified pose identity.");

        var skeleton = request.Skeleton.OrderBy(value => value.Index).ToArray();
        if (skeleton.Length == 0 || skeleton.Select(value => value.Index)
                .Where((value, index) => value != index).Any())
            throw new InvalidDataException(
                "DAO character skeleton node indices must be contiguous from zero.");
        if (skeleton.Count(value => value.ParentIndex is null) == 0 ||
            skeleton.Any(value => value.ParentIndex is int parent &&
                                  (parent < 0 || parent >= value.Index)))
            throw new InvalidDataException(
                "DAO character skeleton parent graph is invalid or not parent-first.");
        var joints = skeleton.Where(value => value.IsJoint).ToArray();
        if (joints.Length == 0 || joints.Any(value =>
                value.SourceBoneId is null or < 0 ||
                value.InverseBindMatrixColumnMajor is null ||
                value.InverseBindMatrixColumnMajor.Count != 16 ||
                value.InverseBindMatrixColumnMajor.Any(component => !float.IsFinite(component))))
            throw new InvalidDataException(
                "DAO character skin joints require source bone IDs and finite inverse-bind matrices.");
        if (joints.Select(value => value.SourceBoneId!.Value).Distinct().Count() !=
            joints.Length)
            throw new InvalidDataException(
                "DAO character source joint bone identities are ambiguous.");

        ValidatePose(request.StandingPose, skeleton.Length, "standing");
        ValidatePose(request.BedPose, skeleton.Length, "bed");
        if (request.Parts.Count == 0 || request.Meshes.Count == 0 ||
            request.Materials.Count == 0)
            throw new InvalidDataException(
                "DAO character decoded part, mesh, and material contracts are required.");
        var partResources = Unique(request.Parts.Select(value => value.Resource), "part resource");
        var outfitParts = Unique(request.Outfit.PartResources, "outfit part resource");
        if (outfitParts.Count == 0 || !outfitParts.SetEquals(partResources))
            throw new InvalidDataException(
                "DAO character outfit context does not bind the complete assembled part set.");
        var meshNames = Unique(request.Meshes.Select(value => value.Name), "mesh name");
        foreach (var part in request.Parts)
        {
            RequireResourceHash(part.Resource, part.PayloadSha256, "part");
            RequireResourceHash(part.HierarchyResource, part.HierarchyPayloadSha256,
                "part hierarchy");
            if (part.MeshNames.Count == 0 ||
                part.MeshNames.Any(value => !meshNames.Contains(value)))
                throw new InvalidDataException(
                    $"DAO character part does not bind decoded meshes: {part.Resource}");
        }
        var boundMeshNames = request.Parts.SelectMany(value => value.MeshNames)
            .ToArray();
        if (boundMeshNames.Length != request.Meshes.Count ||
            boundMeshNames.Distinct(StringComparer.Ordinal).Count() != request.Meshes.Count)
            throw new InvalidDataException(
                "DAO character decoded meshes are missing or multiply bound to parts.");

        var materials = Unique(request.Materials.Select(value => value.Name), "material name");
        foreach (var material in request.Materials)
            ValidateMaterial(material);
        var appliedTargets = Unique(request.Morph.AppliedTargetNames, "morph target");
        foreach (var mesh in request.Meshes)
            ValidateMesh(mesh, skeleton.Length, joints.Length, partResources,
                materials, appliedTargets);
        var assembledTargets = request.Meshes.SelectMany(value => value.MorphTargets)
            .Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        if (!assembledTargets.SetEquals(appliedTargets))
            throw new InvalidDataException(
                "DAO character assembled meshes do not preserve the applied morph target set.");

        foreach (var group in request.Materials.SelectMany(value => value.Textures)
                     .GroupBy(value => (value.SourceResource, value.SourcePayloadSha256,
                         value.EncodedPayloadSha256)))
            if (group.Select(value => value.EncodedMimeType)
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                throw new InvalidDataException(
                    $"DAO character converted texture MIME identity is ambiguous: {group.Key.SourceResource}");
    }

    private static void ValidatePose(DragonAgeCharacterAssemblyPose pose,
        int nodeCount, string label)
    {
        if (string.IsNullOrWhiteSpace(pose.Name))
            throw new InvalidDataException(
                $"DAO character {label} pose name is missing.");
        RequireResourceHash(pose.SourceResource, pose.SourcePayloadSha256,
            $"{label} pose");
        if (pose.Nodes.Count != nodeCount ||
            pose.Nodes.Select(value => value.NodeIndex).Distinct().Count() != nodeCount ||
            pose.Nodes.Any(value => value.NodeIndex < 0 || value.NodeIndex >= nodeCount))
            throw new InvalidDataException(
                $"DAO character {label} pose does not cover every skeleton node exactly once.");
        foreach (var value in pose.Nodes)
        {
            if (!Finite(value.Translation) || !Finite(value.Scale) ||
                !Finite(value.Rotation) || value.Scale.X <= 0 || value.Scale.Y <= 0 ||
                value.Scale.Z <= 0 || MathF.Abs(value.Rotation.LengthSquared() - 1) > .0001f)
                throw new InvalidDataException(
                    $"DAO character {label} pose contains an invalid source transform.");
        }
    }

    private static void ValidateMaterial(DragonAgeCharacterAssemblyMaterial material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(material.Name);
        RequireResourceHash(material.MaterialResource, material.MaterialPayloadSha256,
            "material definition");
        RequireResourceHash(material.MaterialObjectResource,
            material.MaterialObjectPayloadSha256, "material object (MAO)");
        if (!Finite(material.BaseColorFactor) || !Finite(material.EmissiveFactor) ||
            !float.IsFinite(material.MetallicFactor) ||
            !float.IsFinite(material.RoughnessFactor) ||
            !float.IsFinite(material.NormalScale) ||
            !float.IsFinite(material.AlphaCutoff) ||
            Components(material.BaseColorFactor).Any(value => value is < 0 or > 1) ||
            Components(material.EmissiveFactor).Any(value => value is < 0 or > 1) ||
            material.MetallicFactor is < 0 or > 1 ||
            material.RoughnessFactor is < 0 or > 1 || material.NormalScale < 0 ||
            material.AlphaCutoff is < 0 or > 1)
            throw new InvalidDataException("DAO character material PBR values are invalid.");
        var semantics = material.Textures.Select(value => value.Semantic).ToArray();
        if (!semantics.Contains(DragonAgeCharacterTextureSemantic.BaseColor) ||
            semantics.Distinct().Count() != semantics.Length)
            throw new InvalidDataException(
                $"DAO character material texture contract is missing or ambiguous: {material.Name}");
        foreach (var texture in material.Textures)
        {
            RequireResourceHash(texture.SourceResource, texture.SourcePayloadSha256,
                "texture source");
            RequireHash(texture.EncodedPayloadSha256, "encoded texture");
            if (texture.EncodedMimeType is not ("image/png" or "image/jpeg") ||
                texture.EncodedPayload.Length == 0 ||
                Hash(texture.EncodedPayload) != texture.EncodedPayloadSha256 ||
                !HasImageSignature(texture.EncodedMimeType, texture.EncodedPayload))
                throw new InvalidDataException(
                    $"DAO character encoded texture identity is invalid: {texture.SourceResource}");
        }
    }

    private static void ValidateMesh(DragonAgeCharacterDecodedMesh mesh,
        int nodeCount, int jointCount, IReadOnlySet<string> partResources,
        IReadOnlySet<string> materialNames, IReadOnlySet<string> appliedTargets)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mesh.Name);
        RequireResourceHash(mesh.SourceResource, mesh.SourcePayloadSha256, "mesh");
        var count = mesh.Positions.Count;
        if (count == 0 || mesh.Normals.Count != count || mesh.Tangents.Count != count ||
            mesh.TextureCoordinates.Count != count || mesh.SkinInfluences.Count != count)
            throw new InvalidDataException(
                $"DAO character mesh vertex streams disagree: {mesh.Name}");
        if (!partResources.Contains(mesh.PartResource) ||
            !materialNames.Contains(mesh.MaterialName) ||
            mesh.AttachmentNodeIndex < 0 || mesh.AttachmentNodeIndex >= nodeCount)
            throw new InvalidDataException(
                $"DAO character mesh dependency binding is invalid: {mesh.Name}");
        if (mesh.Indices.Count == 0 || mesh.Indices.Count % 3 != 0 ||
            mesh.Indices.Any(value => value >= count))
            throw new InvalidDataException(
                $"DAO character mesh indices are invalid: {mesh.Name}");
        if (mesh.Positions.Any(value => !Finite(value)) ||
            mesh.Normals.Any(value => !Finite(value)) ||
            mesh.Tangents.Any(value => !Finite(value) || value.W is not (-1f or 1f)) ||
            mesh.TextureCoordinates.Any(value => !Finite(value)))
            throw new InvalidDataException(
                $"DAO character mesh attributes contain non-finite values: {mesh.Name}");
        foreach (var influence in mesh.SkinInfluences)
        {
            var jointIndices = new[] { influence.Joint0, influence.Joint1,
                influence.Joint2, influence.Joint3 };
            if (jointIndices.Any(value => value >= jointCount) ||
                !Finite(influence.Weights) ||
                Components(influence.Weights).Any(value => value < 0) ||
                MathF.Abs(Components(influence.Weights).Sum() - 1) > .0001f)
                throw new InvalidDataException(
                    $"DAO character skin influence is invalid: {mesh.Name}");
        }
        var targets = Unique(mesh.MorphTargets.Select(value => value.Name), "mesh morph target");
        if (!targets.IsSubsetOf(appliedTargets))
            throw new InvalidDataException(
                $"DAO character mesh contains an unapplied morph target: {mesh.Name}");
        foreach (var target in mesh.MorphTargets)
        {
            RequireResourceHash(target.SourceResource, target.SourcePayloadSha256,
                "morph target");
            if (!float.IsFinite(target.Weight) ||
                target.PositionDeltas.Count != count || target.NormalDeltas.Count != count ||
                target.TangentDeltas.Count != count ||
                target.PositionDeltas.Any(value => !Finite(value)) ||
                target.NormalDeltas.Any(value => !Finite(value)) ||
                target.TangentDeltas.Any(value => !Finite(value)))
                throw new InvalidDataException(
                    $"DAO character morph target streams are invalid: {target.Name}");
        }
    }

    private static int AddVector2Accessor(IReadOnlyList<Vector2> values,
        BinaryBuffer binary, JsonArray views, JsonArray accessors)
    {
        var bytes = new byte[values.Count * 2 * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            WriteFloat(bytes, index * 8, values[index].X);
            WriteFloat(bytes, index * 8 + 4, values[index].Y);
        }
        return AddAccessor(accessors, AddBufferView(binary, views, bytes, 34962),
            5126, values.Count, "VEC2");
    }

    private static int AddVector3Accessor(IReadOnlyList<Vector3> values,
        BinaryBuffer binary, JsonArray views, JsonArray accessors,
        bool includeBounds = false)
    {
        var bytes = new byte[values.Count * 3 * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
        {
            WriteFloat(bytes, index * 12, values[index].X);
            WriteFloat(bytes, index * 12 + 4, values[index].Y);
            WriteFloat(bytes, index * 12 + 8, values[index].Z);
        }
        JsonArray? minimum = null;
        JsonArray? maximum = null;
        if (includeBounds)
        {
            minimum = Vector(new Vector3(values.Min(value => value.X),
                values.Min(value => value.Y), values.Min(value => value.Z)));
            maximum = Vector(new Vector3(values.Max(value => value.X),
                values.Max(value => value.Y), values.Max(value => value.Z)));
        }
        return AddAccessor(accessors, AddBufferView(binary, views, bytes, 34962),
            5126, values.Count, "VEC3", minimum, maximum);
    }

    private static int AddVector4Accessor(IReadOnlyList<Vector4> values,
        BinaryBuffer binary, JsonArray views, JsonArray accessors)
    {
        var bytes = new byte[values.Count * 4 * sizeof(float)];
        for (var index = 0; index < values.Count; index++)
            for (var component = 0; component < 4; component++)
                WriteFloat(bytes, index * 16 + component * 4,
                    Components(values[index])[component]);
        return AddAccessor(accessors, AddBufferView(binary, views, bytes, 34962),
            5126, values.Count, "VEC4");
    }

    private static int AddJointAccessor(
        IReadOnlyList<DragonAgeCharacterAssemblySkinInfluence> values,
        BinaryBuffer binary, JsonArray views, JsonArray accessors)
    {
        var bytes = new byte[values.Count * 4 * sizeof(ushort)];
        for (var index = 0; index < values.Count; index++)
        {
            var joints = new[] { values[index].Joint0, values[index].Joint1,
                values[index].Joint2, values[index].Joint3 };
            for (var component = 0; component < 4; component++)
                BinaryPrimitives.WriteUInt16LittleEndian(
                    bytes.AsSpan(index * 8 + component * 2), joints[component]);
        }
        return AddAccessor(accessors, AddBufferView(binary, views, bytes, 34962),
            5123, values.Count, "VEC4");
    }

    private static int AddWeightAccessor(
        IReadOnlyList<DragonAgeCharacterAssemblySkinInfluence> values,
        BinaryBuffer binary, JsonArray views, JsonArray accessors) =>
        AddVector4Accessor(values.Select(value => value.Weights).ToArray(), binary,
            views, accessors);

    private static int AddBufferView(BinaryBuffer binary, JsonArray views,
        byte[] bytes, int? target = null)
    {
        var (offset, length) = binary.Add(bytes);
        var json = new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = length
        };
        if (target is int value) json["target"] = value;
        var index = views.Count;
        views.Add(json);
        return index;
    }

    private static int AddAccessor(JsonArray accessors, int view, int componentType,
        int count, string type, JsonArray? minimum = null, JsonArray? maximum = null)
    {
        var json = new JsonObject
        {
            ["bufferView"] = view,
            ["byteOffset"] = 0,
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type
        };
        if (minimum is not null) json["min"] = minimum;
        if (maximum is not null) json["max"] = maximum;
        var index = accessors.Count;
        accessors.Add(json);
        return index;
    }

    private static byte[] PackGlb(byte[] json, byte[] binary)
    {
        var jsonLength = Aligned(json.Length);
        var binaryLength = Aligned(binary.Length);
        var length = checked(12 + 8 + jsonLength + 8 + binaryLength);
        var result = new byte[length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, 0x46546c67);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)jsonLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4e4f534a);
        json.CopyTo(result.AsSpan(20));
        result.AsSpan(20 + json.Length, jsonLength - json.Length).Fill(0x20);
        var binaryHeader = 20 + jsonLength;
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader),
            (uint)binaryLength);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(binaryHeader + 4),
            0x004e4942);
        binary.CopyTo(result.AsSpan(binaryHeader + 8));
        return result;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string Contained(string root, string relative)
    {
        if (Path.IsPathRooted(relative))
            throw new InvalidDataException("DAO character output path is rooted.");
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DAO character output path escapes its local root.");
        return result;
    }

    private static IEnumerable<string> Segments(string path) => path.Split(
        [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static HashSet<string> Unique(IEnumerable<string> values, string label)
    {
        var array = values.ToArray();
        if (array.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException($"DAO character {label} is blank.");
        var result = new HashSet<string>(array, StringComparer.Ordinal);
        if (result.Count != array.Length)
            throw new InvalidDataException($"DAO character {label} is duplicated.");
        return result;
    }

    private static void RequireResourceHash(string resource, string hash, string label)
    {
        if (string.IsNullOrWhiteSpace(resource) || Path.IsPathRooted(resource) || resource.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException($"DAO character {label} identity is not a resref.");
        RequireHash(hash, label);
    }

    private static void RequireHash(string value, string label)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new InvalidDataException(
                $"DAO character {label} identity is not lowercase SHA-256.");
    }

    private static bool HasImageSignature(string mimeType, byte[] payload) =>
        mimeType switch
        {
            "image/png" => payload.AsSpan().StartsWith(
                new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => payload.Length >= 4 && payload[0] == 0xff &&
                            payload[1] == 0xd8 && payload[^2] == 0xff &&
                            payload[^1] == 0xd9,
            _ => false
        };

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static bool Finite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
    private static float[] Components(Vector4 value) =>
        [value.X, value.Y, value.Z, value.W];
    private static float[] Components(Vector3 value) => [value.X, value.Y, value.Z];
    private static JsonArray Vector(Vector2 value) =>
        new(value.X, value.Y);
    private static JsonArray Vector(Vector3 value) =>
        new(value.X, value.Y, value.Z);
    private static JsonArray Vector(Vector4 value) =>
        new(value.X, value.Y, value.Z, value.W);
    private static JsonArray QuaternionValue(Quaternion value) =>
        new(value.X, value.Y, value.Z, value.W);
    private static JsonArray Array(IEnumerable<JsonNode?> values) => new(values.ToArray());
    private static void WriteFloat(byte[] destination, int offset, float value) =>
        BinaryPrimitives.WriteSingleLittleEndian(destination.AsSpan(offset), value);
    private static int Aligned(int value) => checked((value + 3) & ~3);
    private static string Hash(byte[] value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class BinaryBuffer
    {
        private readonly MemoryStream stream = new();
        public int Length => checked((int)stream.Length);

        public (int Offset, int Length) Add(byte[] bytes)
        {
            while (stream.Length % 4 != 0) stream.WriteByte(0);
            var offset = checked((int)stream.Position);
            stream.Write(bytes);
            return (offset, bytes.Length);
        }

        public byte[] ToArray() => stream.ToArray();
    }
}
