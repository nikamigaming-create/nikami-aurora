using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public enum DragonAgeMshCoordinateBasis
{
    SourceRightHandedZUp
}

public sealed record DragonAgeMshVertexDeclaration(
    int Stream,
    int Offset,
    int DataType,
    int Usage,
    int UsageIndex,
    int Method);

public sealed record DragonAgeMshBounds(
    Vector3 Minimum,
    Vector3 Maximum,
    Vector3 SphereCenter,
    float SphereRadius);

public sealed record DragonAgeMshSkinInfluence(
    ushort PaletteIndex0,
    ushort PaletteIndex1,
    ushort PaletteIndex2,
    ushort PaletteIndex3,
    Vector4 SourceWeights,
    Vector4 NormalizedWeights);

public sealed record DragonAgeMshSubmesh(
    string Name,
    DragonAgeMshBounds Bounds,
    int VertexStride,
    int VertexBufferOffset,
    int IndexBufferOffset,
    int ReconstructedTangentVertices,
    IReadOnlyList<DragonAgeMshVertexDeclaration> VertexDeclarations,
    IReadOnlyList<Vector3> Positions,
    IReadOnlyList<Vector3> Normals,
    IReadOnlyList<Vector4> Tangents,
    IReadOnlyList<Vector3> Binormals,
    IReadOnlyList<Vector2> TextureCoordinates,
    IReadOnlyList<Vector4> Colors,
    IReadOnlyList<DragonAgeMshSkinInfluence> SkinInfluences,
    IReadOnlyList<uint> Indices);

public sealed record DragonAgeMshDefinition(
    string ResRef,
    string PayloadSha256,
    DragonAgeMshCoordinateBasis CoordinateBasis,
    int VertexBufferBytes,
    int IndexBufferBytes,
    IReadOnlyList<DragonAgeMshSubmesh> Submeshes);

public sealed record DragonAgeMshMorphTarget(
    string Name,
    string SourceResource,
    string SourcePayloadSha256,
    float Weight,
    IReadOnlyList<Vector3> PositionDeltas,
    IReadOnlyList<Vector3> NormalDeltas,
    IReadOnlyList<Vector3> TangentDeltas);

/// <summary>
/// Bounded reader for the installed PC DAO MESH V0.1 skinned-geometry subset.
/// Vertex declaration values retain their Direct3D 9 numeric identities. The
/// decoder does not resolve an MMH bone palette, choose materials, convert the
/// source coordinate basis, or infer morph correspondence from names.
/// </summary>
public static class DragonAgeOriginsMshDecoder
{
    private const int MaximumSubmeshes = 4096;
    private const int MaximumVerticesPerSubmesh = 10_000_000;
    private const int MaximumIndicesPerSubmesh = 30_000_000;

    public static DragonAgeMshDefinition Decode(string pathOrResRef, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrResRef);
        ArgumentNullException.ThrowIfNull(payload);
        var expected = NormalizeResRef(pathOrResRef);
        var document = new MshDocument(payload);
        var sourceName = document.Root.String(2);
        var actual = NormalizeResRef(sourceName);
        if (!actual.Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"DAO MSH identity disagrees: expected={expected} actual={actual}.");
        if (document.Root.UInt32(8032) != 0 || document.Root.Byte(8033) != 0)
            throw new InvalidDataException("DAO MSH root semantics are unsupported.");

        var vertexBuffer = document.Root.ByteList(8022);
        var indexBuffer = document.Root.ByteList(8023);
        var chunkStructures = document.Root.ReferencedStructList(8021, 4);
        if (chunkStructures.Count == 0 || chunkStructures.Count > MaximumSubmeshes)
            throw new InvalidDataException("DAO MSH chunk count is invalid.");
        var submeshes = chunkStructures.Select(chunk => DecodeSubmesh(
                document, chunk, vertexBuffer, indexBuffer))
            .ToArray();
        if (submeshes.Select(value => value.Name).Distinct(StringComparer.Ordinal)
            .Count() != submeshes.Length)
            throw new InvalidDataException("DAO MSH chunk names are not unique.");

        return new DragonAgeMshDefinition(
            expected,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            DragonAgeMshCoordinateBasis.SourceRightHandedZUp,
            vertexBuffer.Length,
            indexBuffer.Length,
            submeshes);
    }

    public static DragonAgeMshMorphTarget BuildMorphTarget(
        DragonAgeMshSubmesh baseMesh,
        DragonAgeMshDefinition targetDefinition,
        DragonAgeMshSubmesh targetMesh,
        float weight)
    {
        ArgumentNullException.ThrowIfNull(baseMesh);
        ArgumentNullException.ThrowIfNull(targetDefinition);
        ArgumentNullException.ThrowIfNull(targetMesh);
        if (!float.IsFinite(weight))
            throw new InvalidDataException("DAO morph target weight is not finite.");
        if (baseMesh.Positions.Count != targetMesh.Positions.Count ||
            baseMesh.Normals.Count != targetMesh.Normals.Count ||
            baseMesh.Tangents.Count != targetMesh.Tangents.Count ||
            baseMesh.Indices.Count != targetMesh.Indices.Count ||
            !baseMesh.Indices.SequenceEqual(targetMesh.Indices))
            throw new InvalidDataException(
                "DAO morph target has no exact vertex/index correspondence.");

        var positions = new Vector3[baseMesh.Positions.Count];
        var normals = new Vector3[positions.Length];
        var tangents = new Vector3[positions.Length];
        for (var index = 0; index < positions.Length; index++)
        {
            positions[index] = targetMesh.Positions[index] - baseMesh.Positions[index];
            normals[index] = targetMesh.Normals[index] - baseMesh.Normals[index];
            tangents[index] = new Vector3(
                targetMesh.Tangents[index].X - baseMesh.Tangents[index].X,
                targetMesh.Tangents[index].Y - baseMesh.Tangents[index].Y,
                targetMesh.Tangents[index].Z - baseMesh.Tangents[index].Z);
            if (!Finite(positions[index]) || !Finite(normals[index]) ||
                !Finite(tangents[index]))
                throw new InvalidDataException("DAO morph target delta is not finite.");
        }

        return new DragonAgeMshMorphTarget(
            targetDefinition.ResRef,
            targetDefinition.ResRef + ".msh",
            targetDefinition.PayloadSha256,
            weight,
            positions,
            normals,
            tangents);
    }

    private static DragonAgeMshSubmesh DecodeSubmesh(
        MshDocument document,
        MshDocument.Structure chunk,
        ReadOnlyMemory<byte> vertexBuffer,
        ReadOnlyMemory<byte> indexBuffer)
    {
        var name = chunk.String(2).Trim();
        if (name.Length == 0)
            throw new InvalidDataException("DAO MSH chunk name is blank.");
        var bounds = DecodeBounds(chunk.Embedded(8020, 2));
        var stride = CheckedPositive(chunk.UInt32(8000), 4096,
            "DAO MSH vertex stride is invalid.");
        var vertexCount = CheckedPositive(chunk.UInt32(8001),
            MaximumVerticesPerSubmesh, "DAO MSH vertex count is invalid.");
        var indexCount = CheckedPositive(chunk.UInt32(8002),
            MaximumIndicesPerSubmesh, "DAO MSH index count is invalid.");
        if (indexCount % 3 != 0)
            throw new InvalidDataException("DAO MSH index count is not triangular.");
        if (chunk.UInt32(8003) != 0 || chunk.UInt32(8004) != 0 ||
            chunk.UInt32(8005) != 0 || chunk.UInt32(8007) != 0 ||
            chunk.UInt32(8008) != (uint)vertexCount || chunk.UInt32(8034) != 1 ||
            chunk.Int32(8011) != -1)
            throw new InvalidDataException("DAO MSH chunk semantics are unsupported.");
        var vertexOffset = CheckedOffset(chunk.UInt32(8006), vertexBuffer.Length,
            "DAO MSH vertex buffer offset is invalid.");
        var indexOffset = CheckedOffset(chunk.UInt32(8009), indexBuffer.Length / 2,
            "DAO MSH index buffer offset is invalid.");
        if ((long)vertexOffset + (long)vertexCount * stride > vertexBuffer.Length ||
            (long)indexOffset + indexCount > indexBuffer.Length / 2)
            throw new InvalidDataException("DAO MSH chunk exceeds its source buffers.");

        var declarations = chunk.InlineStructList(8025, 1)
            .Select(DecodeDeclaration).ToArray();
        ValidateDeclarations(declarations, stride);
        var active = declarations.Where(value => value.Stream >= 0).ToArray();
        var byUsage = active.ToDictionary(value => value.Usage);
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var tangents = new Vector4[vertexCount];
        var binormals = new Vector3[vertexCount];
        var textureCoordinates = new Vector2[vertexCount];
        var colors = byUsage.ContainsKey(10) ? new Vector4[vertexCount] : [];
        var influences = new DragonAgeMshSkinInfluence[vertexCount];
        var source = vertexBuffer.Span;
        for (var index = 0; index < vertexCount; index++)
        {
            var vertexAt = checked(vertexOffset + index * stride);
            var position = DecodeVector4(source, vertexAt, byUsage[0]);
            if (!Finite(position) || Math.Abs(position.W - 1) > .01f)
                throw new InvalidDataException("DAO MSH position is invalid.");
            positions[index] = new Vector3(position.X, position.Y, position.Z);
            normals[index] = Normalize(DecodeVector3(source, vertexAt, byUsage[3]),
                "DAO MSH normal is invalid.");
            var sourceTangent = DecodeVector3(source, vertexAt, byUsage[6]);
            var sourceBinormal = DecodeVector3(source, vertexAt, byUsage[7]);
            if (!Finite(sourceTangent) || !Finite(sourceBinormal))
                throw new InvalidDataException("DAO MSH tangent basis is invalid.");
            tangents[index] = new Vector4(sourceTangent, 0);
            binormals[index] = sourceBinormal;
            textureCoordinates[index] = DecodeVector2(source, vertexAt, byUsage[5]);
            if (!Finite(textureCoordinates[index]))
                throw new InvalidDataException("DAO MSH texture coordinate is invalid.");
            if (colors.Length > 0)
            {
                colors[index] = DecodeVector4(source, vertexAt, byUsage[10]);
                if (!Finite(colors[index]))
                    throw new InvalidDataException("DAO MSH vertex color is invalid.");
            }
            influences[index] = DecodeInfluence(source, vertexAt,
                byUsage[1], byUsage[2]);
        }
        ValidateBounds(bounds, positions);

        var indices = new uint[indexCount];
        var rawIndices = indexBuffer.Span;
        for (var index = 0; index < indices.Length; index++)
        {
            var sourceIndex = checked((indexOffset + index) * 2);
            var value = BinaryPrimitives.ReadUInt16LittleEndian(
                rawIndices.Slice(sourceIndex, 2));
            if (value >= vertexCount)
                throw new InvalidDataException("DAO MSH index exceeds the chunk vertex count.");
            indices[index] = value;
        }
        var reconstructedTangents = FinalizeTangents(
            positions, normals, textureCoordinates, tangents, binormals);

        return new DragonAgeMshSubmesh(
            name, bounds, stride, vertexOffset, indexOffset, reconstructedTangents,
            declarations,
            positions, normals, tangents, binormals, textureCoordinates, colors,
            influences, indices);
    }

    private static int FinalizeTangents(
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> textureCoordinates,
        Vector4[] tangents,
        Vector3[] binormals)
    {
        var reconstructed = 0;
        for (var index = 0; index < tangents.Length; index++)
        {
            var normal = normals[index];
            var sourceTangent = new Vector3(
                tangents[index].X, tangents[index].Y, tangents[index].Z);
            var tangent = sourceTangent - normal * Vector3.Dot(normal, sourceTangent);
            var repaired = tangent.LengthSquared() < .000001f;
            if (repaired)
            {
                var peer = FindEquivalentTangent(index, positions, normals,
                    textureCoordinates, tangents, binormals);
                if (!peer.HasValue)
                    throw new InvalidDataException(
                        "DAO MSH referenced tangent has no exact source peer.");
                var derived = peer.Value.Tangent;
                tangent = derived - normal * Vector3.Dot(normal, derived);
                if (!Finite(tangent) || tangent.LengthSquared() < .000001f)
                    throw new InvalidDataException(
                        "DAO MSH referenced tangent cannot be reconstructed.");
                if (peer.HasValue && binormals[index].LengthSquared() < .000001f)
                    binormals[index] = peer.Value.Binormal;
                reconstructed++;
            }
            tangent = Vector3.Normalize(tangent);

            var handednessBasis = binormals[index];
            var handedness = Vector3.Dot(Vector3.Cross(normal, tangent), handednessBasis);
            if (!float.IsFinite(handedness) || Math.Abs(handedness) < .000001f)
            {
                var peer = FindEquivalentTangent(index, positions, normals,
                    textureCoordinates, tangents, binormals);
                if (!peer.HasValue)
                    throw new InvalidDataException(
                        "DAO MSH tangent handedness has no exact source peer.");
                handednessBasis = peer.Value.Binormal;
                handedness = Vector3.Dot(
                    Vector3.Cross(normal, tangent), handednessBasis);
                if (!float.IsFinite(handedness) || Math.Abs(handedness) < .000001f)
                    throw new InvalidDataException(
                        "DAO MSH tangent handedness cannot be reconstructed.");
                if (!repaired) reconstructed++;
            }
            var sign = handedness < 0 ? -1 : 1;
            tangents[index] = new Vector4(tangent, sign);
            binormals[index] = handednessBasis.LengthSquared() >= .000001f
                ? Vector3.Normalize(handednessBasis)
                : Vector3.Cross(normal, tangent) * sign;
        }
        return reconstructed;
    }

    private static (Vector3 Tangent, Vector3 Binormal)? FindEquivalentTangent(
        int target,
        IReadOnlyList<Vector3> positions,
        IReadOnlyList<Vector3> normals,
        IReadOnlyList<Vector2> textureCoordinates,
        IReadOnlyList<Vector4> tangents,
        IReadOnlyList<Vector3> binormals)
    {
        for (var index = 0; index < positions.Count; index++)
        {
            if (index == target ||
                Vector3.DistanceSquared(positions[index], positions[target]) > .0000000001f ||
                Vector3.DistanceSquared(normals[index], normals[target]) > .00000001f ||
                Vector2.DistanceSquared(textureCoordinates[index],
                    textureCoordinates[target]) > .00000001f)
                continue;
            var tangent = new Vector3(
                tangents[index].X, tangents[index].Y, tangents[index].Z);
            if (tangent.LengthSquared() >= .000001f)
                return (tangent, binormals[index]);
        }
        return null;
    }

    private static DragonAgeMshBounds DecodeBounds(MshDocument.Structure source)
    {
        var minimum = source.Vector4(8017);
        var maximum = source.Vector4(8018);
        var sphere = source.Vector4(8019);
        if (!Finite(minimum) || !Finite(maximum) || !Finite(sphere) ||
            Math.Abs(minimum.W - 1) > .01f || Math.Abs(maximum.W - 1) > .01f ||
            minimum.X > maximum.X || minimum.Y > maximum.Y ||
            minimum.Z > maximum.Z || sphere.W <= 0)
            throw new InvalidDataException("DAO MSH bounds are invalid.");
        return new DragonAgeMshBounds(
            new Vector3(minimum.X, minimum.Y, minimum.Z),
            new Vector3(maximum.X, maximum.Y, maximum.Z),
            new Vector3(sphere.X, sphere.Y, sphere.Z), sphere.W);
    }

    private static DragonAgeMshVertexDeclaration DecodeDeclaration(
        MshDocument.Structure source) => new(
        source.Int32(8026), source.Int32(8027), source.Int32(8028),
        source.Int32(8029), source.Int32(8030), source.Int32(8031));

    private static void ValidateDeclarations(
        IReadOnlyList<DragonAgeMshVertexDeclaration> declarations, int stride)
    {
        if (declarations.Count < 8 || declarations.Count > 64)
            throw new InvalidDataException("DAO MSH vertex declaration count is invalid.");
        var sentinel = declarations[^1];
        if (sentinel != new DragonAgeMshVertexDeclaration(-1, 0, -1, -1, 0, 0) ||
            declarations.Take(declarations.Count - 1).Any(value => value.Stream < 0))
            throw new InvalidDataException("DAO MSH vertex declaration sentinel is invalid.");
        var active = declarations.Take(declarations.Count - 1).ToArray();
        if (active.Any(value => value.Stream != 0 || value.Offset < 0 ||
                value.Method != 0 || value.UsageIndex != 0) ||
            active.Select(value => value.Usage).Distinct().Count() != active.Length)
            throw new InvalidDataException("DAO MSH vertex declaration is unsupported.");
        var required = new[] { 0, 1, 2, 3, 5, 6, 7 };
        if (required.Any(usage => active.All(value => value.Usage != usage)) ||
            active.Any(value => value.Usage is not (0 or 1 or 2 or 3 or 5 or 6 or 7 or 10)))
            throw new InvalidDataException("DAO MSH vertex semantic is unsupported.");
        foreach (var declaration in active)
        {
            var size = DeclarationSize(declaration.DataType);
            if (declaration.Offset > stride - size ||
                !SupportedType(declaration.Usage, declaration.DataType))
                throw new InvalidDataException("DAO MSH vertex data type is unsupported.");
        }
    }

    private static bool SupportedType(int usage, int type) => usage switch
    {
        0 => type is 3 or 16,
        1 => type == 16,
        2 => type == 7,
        3 or 6 or 7 => type is 2 or 16,
        5 => type is 1 or 15,
        10 => type is 3 or 16,
        _ => false
    };

    private static int DeclarationSize(int type) => type switch
    {
        1 => 8,
        2 => 12,
        3 => 16,
        7 => 8,
        15 => 4,
        16 => 8,
        _ => throw new InvalidDataException("DAO MSH D3D declaration type is unsupported.")
    };

    private static DragonAgeMshSkinInfluence DecodeInfluence(
        ReadOnlySpan<byte> source, int vertexAt,
        DragonAgeMshVertexDeclaration weightDeclaration,
        DragonAgeMshVertexDeclaration indexDeclaration)
    {
        var weights = DecodeVector4(source, vertexAt, weightDeclaration);
        if (!Finite(weights) || weights.X < 0 || weights.Y < 0 ||
            weights.Z < 0 || weights.W < 0)
            throw new InvalidDataException("DAO MSH skin weights are invalid.");
        var sum = weights.X + weights.Y + weights.Z + weights.W;
        if (!float.IsFinite(sum) || sum <= .000001f)
            throw new InvalidDataException("DAO MSH skin weights are empty.");
        var at = checked(vertexAt + indexDeclaration.Offset);
        Span<ushort> indices = stackalloc ushort[4];
        for (var component = 0; component < 4; component++)
        {
            var value = BinaryPrimitives.ReadInt16LittleEndian(
                source.Slice(at + component * 2, 2));
            if (value < 0)
                throw new InvalidDataException("DAO MSH skin palette index is negative.");
            indices[component] = checked((ushort)value);
        }
        return new DragonAgeMshSkinInfluence(
            indices[0], indices[1], indices[2], indices[3],
            weights, weights / sum);
    }

    private static Vector2 DecodeVector2(ReadOnlySpan<byte> source, int vertexAt,
        DragonAgeMshVertexDeclaration declaration)
    {
        var at = checked(vertexAt + declaration.Offset);
        return declaration.DataType switch
        {
            1 => new Vector2(Single(source, at), Single(source, at + 4)),
            15 => new Vector2(Half(source, at), Half(source, at + 2)),
            _ => throw new InvalidDataException("DAO MSH vec2 declaration is unsupported.")
        };
    }

    private static Vector3 DecodeVector3(ReadOnlySpan<byte> source, int vertexAt,
        DragonAgeMshVertexDeclaration declaration)
    {
        var value = DecodeVector4(source, vertexAt, declaration);
        return new Vector3(value.X, value.Y, value.Z);
    }

    private static Vector4 DecodeVector4(ReadOnlySpan<byte> source, int vertexAt,
        DragonAgeMshVertexDeclaration declaration)
    {
        var at = checked(vertexAt + declaration.Offset);
        return declaration.DataType switch
        {
            2 => new Vector4(Single(source, at), Single(source, at + 4),
                Single(source, at + 8), 1),
            3 => new Vector4(Single(source, at), Single(source, at + 4),
                Single(source, at + 8), Single(source, at + 12)),
            16 => new Vector4(Half(source, at), Half(source, at + 2),
                Half(source, at + 4), Half(source, at + 6)),
            _ => throw new InvalidDataException("DAO MSH vec4 declaration is unsupported.")
        };
    }

    private static float Half(ReadOnlySpan<byte> source, int at) =>
        (float)BitConverter.UInt16BitsToHalf(
            BinaryPrimitives.ReadUInt16LittleEndian(source.Slice(at, 2)));

    private static float Single(ReadOnlySpan<byte> source, int at) =>
        BitConverter.Int32BitsToSingle(
            BinaryPrimitives.ReadInt32LittleEndian(source.Slice(at, 4)));

    private static Vector3 Normalize(Vector3 value, string reason)
    {
        if (!Finite(value) || value.LengthSquared() < .000001f)
            throw new InvalidDataException(reason);
        return Vector3.Normalize(value);
    }

    private static void ValidateBounds(DragonAgeMshBounds bounds,
        IReadOnlyList<Vector3> positions)
    {
        var tolerance = Math.Max(.001f, bounds.SphereRadius * .01f);
        foreach (var position in positions)
        {
            if (position.X < bounds.Minimum.X - tolerance ||
                position.Y < bounds.Minimum.Y - tolerance ||
                position.Z < bounds.Minimum.Z - tolerance ||
                position.X > bounds.Maximum.X + tolerance ||
                position.Y > bounds.Maximum.Y + tolerance ||
                position.Z > bounds.Maximum.Z + tolerance ||
                Vector3.Distance(position, bounds.SphereCenter) >
                bounds.SphereRadius + tolerance)
                throw new InvalidDataException("DAO MSH vertex exceeds source bounds.");
        }
    }

    private static int CheckedPositive(uint value, int maximum, string reason)
    {
        if (value == 0 || value > maximum) throw new InvalidDataException(reason);
        return checked((int)value);
    }

    private static int CheckedOffset(uint value, int limit, string reason)
    {
        if (value > limit) throw new InvalidDataException(reason);
        return checked((int)value);
    }

    private static string NormalizeResRef(string value)
    {
        var normalized = Path.GetFileNameWithoutExtension(
            value.Replace('\\', '/')).Trim().ToLowerInvariant();
        if (normalized.Length == 0)
            throw new InvalidDataException("DAO MSH resref is blank.");
        return normalized;
    }

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);
    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private sealed class MshDocument
    {
        private const ushort ListFlag = 0x8000;
        private const ushort ReferenceFlag = 0x2000;
        private const ushort StructFlag = 0x4000;
        private readonly byte[] payload;
        private readonly int dataStart;
        private readonly StructureDefinition[] structures;

        private static readonly StructureSchema[] ExpectedSchemas =
        [
            new("mesh", 24,
            [
                new(2, 14, 0, 0), new(8032, 4, 0, 4),
                new(8021, 4, ListFlag | ReferenceFlag | StructFlag, 8),
                new(8022, 0, ListFlag, 12), new(8023, 0, ListFlag, 16),
                new(8033, 0, 0, 20)
            ]),
            new("decl", 24,
            [
                new(8026, 5, 0, 0), new(8027, 5, 0, 4),
                new(8028, 4, 0, 8), new(8029, 4, 0, 12),
                new(8030, 4, 0, 16), new(8031, 4, 0, 20)
            ]),
            new("bnds", 48,
            [
                new(8017, 12, 0, 0), new(8018, 12, 0, 16),
                new(8019, 12, 0, 32)
            ]),
            new("strm", 20,
            [
                new(8024, 0, ListFlag, 0), new(8012, 4, 0, 4),
                new(8013, 4, 0, 8), new(8014, 4, 0, 12),
                new(8015, 0, 0, 16), new(8016, 0, 0, 17)
            ]),
            new("chnk", 112,
            [
                new(8020, 2, StructFlag, 0), new(2, 14, 0, 48),
                new(8000, 4, 0, 52), new(8001, 4, 0, 56),
                new(8002, 4, 0, 60), new(8003, 4, 0, 64),
                new(8004, 4, 0, 68), new(8005, 4, 0, 72),
                new(8006, 4, 0, 76), new(8007, 4, 0, 80),
                new(8008, 4, 0, 84), new(8009, 4, 0, 88),
                new(8011, 3, ListFlag | StructFlag, 92),
                new(8025, 1, ListFlag | StructFlag, 96),
                new(8034, 4, 0, 100)
            ])
        ];

        public MshDocument(byte[] payload)
        {
            this.payload = payload;
            if (payload.Length < 28 ||
                Encoding.ASCII.GetString(payload, 0, 20) != "GFF V4.0PC  MESHV0.1")
                throw new InvalidDataException("DAO MSH requires PC MESH V0.1.");
            var count = UInt32(20);
            dataStart = checked((int)UInt32(24));
            if (count != ExpectedSchemas.Length ||
                dataStart < 28 + count * 16L || dataStart > payload.Length)
                throw new InvalidDataException("DAO MSH structure table is invalid.");
            structures = new StructureDefinition[count];
            for (var index = 0; index < structures.Length; index++)
            {
                var at = checked(28 + index * 16);
                var kind = Encoding.ASCII.GetString(payload, at, 4).ToLowerInvariant();
                var fieldCount = UInt32(at + 4);
                var fieldOffset = UInt32(at + 8);
                var size = UInt32(at + 12);
                if (fieldCount > 4096 || fieldOffset > payload.Length ||
                    fieldCount * 12L > payload.Length - fieldOffset ||
                    size > payload.Length)
                    throw new InvalidDataException("DAO MSH structure definition is invalid.");
                var fields = new Dictionary<uint, Field>();
                for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    var fieldAt = checked((int)fieldOffset + ordinal * 12);
                    var field = new Field(UInt32(fieldAt), UInt16(fieldAt + 4),
                        UInt16(fieldAt + 6), checked((int)UInt32(fieldAt + 8)));
                    if (!fields.TryAdd(field.Label, field))
                        throw new InvalidDataException("DAO MSH field label is duplicated.");
                }
                structures[index] = new StructureDefinition(
                    kind, checked((int)size), fields);
                ValidateSchema(index, structures[index]);
            }
        }

        public Structure Root => new(this, 0, 0);

        private void ValidateSchema(int index, StructureDefinition actual)
        {
            var expected = ExpectedSchemas[index];
            if (actual.Kind != expected.Kind || actual.Size != expected.Size ||
                actual.Fields.Count != expected.Fields.Count)
                throw new InvalidDataException("DAO MSH structure schema is unsupported.");
            foreach (var field in expected.Fields)
            {
                if (!actual.Fields.TryGetValue(field.Label, out var value) ||
                    value.Type != field.Type || value.Flags != field.Flags ||
                    value.Offset != field.Offset)
                    throw new InvalidDataException("DAO MSH field schema is unsupported.");
            }
        }

        private int DataOffset(int relative, int length = 4)
        {
            var at = checked(dataStart + relative);
            if (relative < 0 || length < 0 || at < dataStart ||
                at > payload.Length - length)
                throw new InvalidDataException("DAO MSH data reference is outside the payload.");
            return at;
        }

        private ushort UInt16(int offset)
        {
            if (offset < 0 || offset > payload.Length - 2)
                throw new InvalidDataException("DAO MSH uint16 is out of range.");
            return BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        }

        private uint UInt32(int offset)
        {
            if (offset < 0 || offset > payload.Length - 4)
                throw new InvalidDataException("DAO MSH uint32 is out of range.");
            return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
        }

        private sealed record Field(uint Label, ushort Type, ushort Flags, int Offset);
        private sealed record StructureDefinition(
            string Kind, int Size, IReadOnlyDictionary<uint, Field> Fields);
        private sealed record StructureSchema(
            string Kind, int Size, IReadOnlyList<Field> Fields);

        public sealed class Structure
        {
            private readonly MshDocument document;
            private readonly int type;
            private readonly int relative;

            internal Structure(MshDocument document, int type, int relative)
            {
                this.document = document;
                this.type = type;
                this.relative = relative;
                _ = document.DataOffset(relative, Definition.Size);
            }

            private StructureDefinition Definition => document.structures[type];

            public string String(uint label)
            {
                var field = Require(label, 14, 0);
                var target = Int32At(field.Offset);
                if (target < 0)
                    throw new InvalidDataException("DAO MSH string is absent.");
                var at = document.DataOffset(target);
                var characters = document.UInt32(at);
                if (characters > 1_000_000 ||
                    characters * 2L > document.payload.Length - at - 4)
                    throw new InvalidDataException("DAO MSH string is out of range.");
                return Encoding.Unicode.GetString(document.payload, at + 4,
                    checked((int)characters * 2)).TrimEnd('\0');
            }

            public byte Byte(uint label)
            {
                var field = Require(label, 0, 0);
                return Read(field.Offset, 1)[0];
            }

            public int Int32(uint label)
            {
                var field = Definition.Fields.TryGetValue(label, out var value)
                    ? value : throw new InvalidDataException($"DAO MSH field {label} is absent.");
                var scalarInteger = field.Flags == 0 && field.Type is 4 or 5;
                var absentInlineList = field.Flags == (ListFlag | StructFlag) &&
                    field.Type == 3;
                if (!scalarInteger && !absentInlineList)
                    throw new InvalidDataException($"DAO MSH field {label} is not int32.");
                return Int32At(field.Offset);
            }

            public uint UInt32(uint label)
            {
                var field = Require(label, 4, 0);
                return BinaryPrimitives.ReadUInt32LittleEndian(Read(field.Offset, 4));
            }

            public Vector4 Vector4(uint label)
            {
                var field = Require(label, 12, 0);
                var source = Read(field.Offset, 16);
                return new Vector4(
                    ReadSingle(source, 0), ReadSingle(source, 4),
                    ReadSingle(source, 8), ReadSingle(source, 12));
            }

            public Structure Embedded(uint label, int expectedType)
            {
                var field = Require(label, checked((ushort)expectedType), StructFlag);
                return new Structure(document, expectedType, checked(relative + field.Offset));
            }

            public IReadOnlyList<Structure> ReferencedStructList(
                uint label, int expectedType)
            {
                var field = Require(label, checked((ushort)expectedType),
                    ListFlag | ReferenceFlag | StructFlag);
                var listRelative = Int32At(field.Offset);
                var at = document.DataOffset(listRelative);
                var count = document.UInt32(at);
                if (count > MaximumSubmeshes || 4L + count * 4L >
                    document.payload.Length - at)
                    throw new InvalidDataException("DAO MSH structure list is invalid.");
                var result = new Structure[count];
                for (var index = 0; index < result.Length; index++)
                {
                    var itemRelative = checked((int)document.UInt32(at + 4 + index * 4));
                    result[index] = new Structure(document, expectedType, itemRelative);
                }
                return result;
            }

            public IReadOnlyList<Structure> InlineStructList(uint label, int expectedType)
            {
                var field = Require(label, checked((ushort)expectedType),
                    ListFlag | StructFlag);
                var listRelative = Int32At(field.Offset);
                if (listRelative < 0) return [];
                var at = document.DataOffset(listRelative);
                var count = document.UInt32(at);
                var size = document.structures[expectedType].Size;
                if (count > 4096 || 4L + count * size > document.payload.Length - at)
                    throw new InvalidDataException("DAO MSH inline structure list is invalid.");
                var result = new Structure[count];
                for (var index = 0; index < result.Length; index++)
                    result[index] = new Structure(document, expectedType,
                        checked(listRelative + 4 + index * size));
                return result;
            }

            public ReadOnlyMemory<byte> ByteList(uint label)
            {
                var field = Require(label, 0, ListFlag);
                var listRelative = Int32At(field.Offset);
                var at = document.DataOffset(listRelative);
                var count = document.UInt32(at);
                if (count > int.MaxValue || count > document.payload.Length - at - 4)
                    throw new InvalidDataException("DAO MSH byte list is invalid.");
                return document.payload.AsMemory(at + 4, checked((int)count));
            }

            private Field Require(uint label, ushort expectedType, ushort expectedFlags)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Type != expectedType || field.Flags != expectedFlags)
                    throw new InvalidDataException($"DAO MSH field {label} is typed differently.");
                return field;
            }

            private int Int32At(int offset) => BinaryPrimitives.ReadInt32LittleEndian(
                Read(offset, 4));

            private ReadOnlySpan<byte> Read(int offset, int length) =>
                document.payload.AsSpan(
                    document.DataOffset(checked(relative + offset), length), length);

            private static float ReadSingle(ReadOnlySpan<byte> source, int offset) =>
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)));
        }
    }
}
