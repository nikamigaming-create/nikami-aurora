using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
{
    private static void DragonAgeMshDecoderIsSourceBound()
    {
        var source = SyntheticMsh("synthetic_skin");
        var definition = DragonAgeOriginsMshDecoder.Decode(
            "models/synthetic_skin.msh", source);
        Expect(definition.ResRef == "synthetic_skin" &&
               definition.PayloadSha256.Length == 64 &&
               definition.CoordinateBasis ==
               DragonAgeMshCoordinateBasis.SourceRightHandedZUp &&
               definition.Submeshes.Count == 1,
            "synthetic DAO MSH identity/basis did not decode");
        var mesh = definition.Submeshes[0];
        Expect(mesh.Name == "SyntheticSkinM1" &&
               mesh.VertexStride == 64 && mesh.Positions.Count == 3 &&
               mesh.Indices.SequenceEqual(new uint[] { 0, 1, 2 }) &&
               mesh.TextureCoordinates[1] == new Vector2(1, 0) &&
               mesh.Normals.All(value => value == Vector3.UnitZ) &&
               mesh.Tangents.All(value => value == new Vector4(1, 0, 0, 1)) &&
               mesh.ReconstructedTangentVertices == 0,
            "synthetic DAO MSH geometry/declaration contract drifted");
        Expect(mesh.SkinInfluences[0].PaletteIndex0 == 2 &&
               mesh.SkinInfluences[1].PaletteIndex0 == 3 &&
               mesh.SkinInfluences[2].PaletteIndex0 == 2 &&
               mesh.SkinInfluences.All(value =>
                   Math.Abs(value.NormalizedWeights.X +
                            value.NormalizedWeights.Y +
                            value.NormalizedWeights.Z +
                            value.NormalizedWeights.W - 1) < .00001f),
            "synthetic DAO MSH skin palette/weight contract drifted");

        var duplicateTangent = DragonAgeOriginsMshDecoder.Decode(
            "synthetic_duplicate_tangent",
            SyntheticMsh("synthetic_duplicate_tangent", exactPeerZeroTangent: true));
        var duplicateMesh = duplicateTangent.Submeshes[0];
        Expect(duplicateMesh.Positions.Count == 4 &&
               duplicateMesh.ReconstructedTangentVertices == 1 &&
               duplicateMesh.Tangents[3] == duplicateMesh.Tangents[0] &&
               duplicateMesh.Indices.SequenceEqual(new uint[] { 3, 1, 2 }),
            "zero tangent did not recover only from an exact duplicate source vertex");

        var target = DragonAgeOriginsMshDecoder.Decode(
            "synthetic_target.msh",
            SyntheticMsh("synthetic_target", positionDelta: .25f));
        var morph = DragonAgeOriginsMshDecoder.BuildMorphTarget(
            mesh, target, target.Submeshes[0], .6f);
        Expect(morph.Name == "synthetic_target" &&
               morph.SourceResource == "synthetic_target.msh" &&
               morph.SourcePayloadSha256 == target.PayloadSha256 &&
               morph.Weight == .6f &&
               morph.PositionDeltas.All(value => value == new Vector3(0, 0, .25f)) &&
               morph.NormalDeltas.All(value => value == Vector3.Zero) &&
               morph.TangentDeltas.All(value => value == Vector3.Zero),
            "exact-topology DAO morph deltas did not remain source-bound");
    }

    private static void DragonAgeMshDecoderFailsClosed()
    {
        var malformedHeader = SyntheticMsh("bad_header");
        malformedHeader[19] = (byte)'2';
        var headerRejected = false;
        try
        {
            _ = DragonAgeOriginsMshDecoder.Decode("bad_header", malformedHeader);
        }
        catch (InvalidDataException error)
        {
            headerRejected = error.Message.Contains("MESH V0.1", StringComparison.Ordinal);
        }
        Expect(headerRejected, "unknown DAO MSH version did not fail closed");

        var semanticRejected = false;
        try
        {
            _ = DragonAgeOriginsMshDecoder.Decode("bad_semantic",
                SyntheticMsh("bad_semantic", unsupportedSemantic: true));
        }
        catch (InvalidDataException error)
        {
            semanticRejected = error.Message.Contains(
                "vertex semantic is unsupported", StringComparison.Ordinal);
        }
        Expect(semanticRejected, "unknown DAO MSH vertex semantic did not fail closed");

        var indexRejected = false;
        try
        {
            _ = DragonAgeOriginsMshDecoder.Decode("bad_index",
                SyntheticMsh("bad_index", outOfRangeIndex: true));
        }
        catch (InvalidDataException error)
        {
            indexRejected = error.Message.Contains(
                "index exceeds", StringComparison.Ordinal);
        }
        Expect(indexRejected, "out-of-range DAO MSH index did not fail closed");

        var baseDefinition = DragonAgeOriginsMshDecoder.Decode(
            "morph_base", SyntheticMsh("morph_base"));
        var targetDefinition = DragonAgeOriginsMshDecoder.Decode(
            "morph_target", SyntheticMsh("morph_target", reverseWinding: true));
        var morphRejected = false;
        try
        {
            _ = DragonAgeOriginsMshDecoder.BuildMorphTarget(
                baseDefinition.Submeshes[0], targetDefinition,
                targetDefinition.Submeshes[0], .5f);
        }
        catch (InvalidDataException error)
        {
            morphRejected = error.Message.Contains(
                "no exact vertex/index correspondence", StringComparison.Ordinal);
        }
        Expect(morphRejected, "incompatible DAO morph topology did not fail closed");
    }

    private static byte[] SyntheticMsh(
        string resRef,
        float positionDelta = 0,
        bool unsupportedSemantic = false,
        bool outOfRangeIndex = false,
        bool reverseWinding = false,
        bool exactPeerZeroTangent = false)
    {
        const ushort list = 0x8000;
        const ushort reference = 0x2000;
        const ushort structure = 0x4000;
        var schemas = new[]
        {
            new MshTestStruct("mesh", 24,
                Mf(2, 14, 0), Mf(8032, 4, 4),
                Mf(8021, 4, 8, list | reference | structure),
                Mf(8022, 0, 12, list), Mf(8023, 0, 16, list),
                Mf(8033, 0, 20)),
            new MshTestStruct("decl", 24,
                Mf(8026, 5, 0), Mf(8027, 5, 4), Mf(8028, 4, 8),
                Mf(8029, 4, 12), Mf(8030, 4, 16), Mf(8031, 4, 20)),
            new MshTestStruct("bnds", 48,
                Mf(8017, 12, 0), Mf(8018, 12, 16), Mf(8019, 12, 32)),
            new MshTestStruct("strm", 20,
                Mf(8024, 0, 0, list), Mf(8012, 4, 4), Mf(8013, 4, 8),
                Mf(8014, 4, 12), Mf(8015, 0, 16), Mf(8016, 0, 17)),
            new MshTestStruct("chnk", 112,
                Mf(8020, 2, 0, structure), Mf(2, 14, 48),
                Mf(8000, 4, 52), Mf(8001, 4, 56), Mf(8002, 4, 60),
                Mf(8003, 4, 64), Mf(8004, 4, 68), Mf(8005, 4, 72),
                Mf(8006, 4, 76), Mf(8007, 4, 80), Mf(8008, 4, 84),
                Mf(8009, 4, 88), Mf(8011, 3, 92, list | structure),
                Mf(8025, 1, 96, list | structure), Mf(8034, 4, 100))
        };
        const int dataStart = 544;
        var data = new List<byte>();
        int Allocate(int bytes)
        {
            var result = data.Count;
            data.AddRange(new byte[bytes]);
            return result;
        }
        var root = Allocate(24);
        var chunk = Allocate(112);

        void Write(int at, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++) data[at + index] = bytes[index];
        }
        void U16(int at, ushort value)
        {
            Span<byte> bytes = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
            Write(at, bytes);
        }
        void I16(int at, short value) => U16(at, unchecked((ushort)value));
        void U32(int at, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(at, bytes);
        }
        void I32(int at, int value) => U32(at, unchecked((uint)value));
        void Float(int at, float value) => I32(at, BitConverter.SingleToInt32Bits(value));
        void Half(int at, float value) => U16(at,
            BitConverter.HalfToUInt16Bits((Half)value));
        void V4(int at, Vector4 value)
        {
            Float(at, value.X); Float(at + 4, value.Y);
            Float(at + 8, value.Z); Float(at + 12, value.W);
        }
        int String(string value)
        {
            var at = data.Count;
            var bytes = Encoding.Unicode.GetBytes(value + "\0");
            data.AddRange(new byte[4 + bytes.Length]);
            U32(at, checked((uint)(value.Length + 1)));
            Write(at + 4, bytes);
            return at;
        }

        U32(root, checked((uint)String(resRef + ".msh")));
        U32(root + 4, 0);
        U32(chunk + 48, checked((uint)String("SyntheticSkinM1")));
        V4(chunk, new Vector4(0, 0, positionDelta, 1));
        V4(chunk + 16, new Vector4(1, 1, positionDelta, 1));
        V4(chunk + 32, new Vector4(.5f, .5f, positionDelta, .8f));
        var vertexCount = exactPeerZeroTangent ? 4 : 3;
        U32(chunk + 52, 64); U32(chunk + 56, checked((uint)vertexCount));
        U32(chunk + 60, 3);
        U32(chunk + 76, 0); U32(chunk + 84, checked((uint)vertexCount));
        U32(chunk + 88, 0);
        I32(chunk + 92, -1); U32(chunk + 100, 1);

        var declarations = data.Count;
        U32(chunk + 96, checked((uint)declarations));
        Allocate(4 + 8 * 24);
        U32(declarations, 8);
        var declarationValues = new[]
        {
            new[] { 0, 0, 3, 0, 0, 0 },
            new[] { 0, 16, 15, 5, 0, 0 },
            new[] { 0, 20, 16, 6, 0, 0 },
            new[] { 0, 28, 16, unsupportedSemantic ? 4 : 7, 0, 0 },
            new[] { 0, 36, 2, 3, 0, 0 },
            new[] { 0, 48, 16, 1, 0, 0 },
            new[] { 0, 56, 7, 2, 0, 0 },
            new[] { -1, 0, -1, -1, 0, 0 }
        };
        for (var item = 0; item < declarationValues.Length; item++)
            for (var field = 0; field < 6; field++)
                I32(declarations + 4 + item * 24 + field * 4,
                    declarationValues[item][field]);

        var vertices = data.Count;
        U32(root + 12, checked((uint)vertices));
        Allocate(4 + vertexCount * 64);
        U32(vertices, checked((uint)(vertexCount * 64)));
        var sourcePositions = new List<Vector3>
        {
            new Vector3(0, 0, positionDelta),
            new Vector3(1, 0, positionDelta),
            new Vector3(0, 1, positionDelta)
        };
        var sourceUv = new List<Vector2> { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
        if (exactPeerZeroTangent)
        {
            sourcePositions.Add(sourcePositions[0]);
            sourceUv.Add(sourceUv[0]);
        }
        for (var index = 0; index < vertexCount; index++)
        {
            var at = vertices + 4 + index * 64;
            V4(at, new Vector4(sourcePositions[index], 1));
            Half(at + 16, sourceUv[index].X); Half(at + 18, sourceUv[index].Y);
            var zeroFrame = exactPeerZeroTangent && index == 3;
            foreach (var (offset, value) in new[]
                     {
                         (20, zeroFrame ? Vector4.Zero : new Vector4(1, 0, 0, 1)),
                         (28, zeroFrame ? Vector4.Zero : new Vector4(0, 1, 0, 1))
                     })
                for (var component = 0; component < 4; component++)
                    Half(at + offset + component * 2, value[component]);
            Float(at + 36, 0); Float(at + 40, 0); Float(at + 44, 1);
            Half(at + 48, 1); Half(at + 50, 0);
            Half(at + 52, 0); Half(at + 54, 0);
            I16(at + 56, checked((short)(index == 1 ? 3 : 2)));
            I16(at + 58, 0); I16(at + 60, 0); I16(at + 62, 0);
        }

        var indices = data.Count;
        U32(root + 16, checked((uint)indices));
        Allocate(4 + 6);
        U32(indices, 6);
        var sourceIndices = exactPeerZeroTangent
            ? new ushort[] { 3, 1, 2 }
            : reverseWinding ? new ushort[] { 0, 2, 1 } :
                new ushort[] { 0, 1, checked((ushort)(outOfRangeIndex ? 3 : 2)) };
        for (var index = 0; index < sourceIndices.Length; index++)
            U16(indices + 4 + index * 2, sourceIndices[index]);

        var chunks = data.Count;
        U32(root + 8, checked((uint)chunks));
        Allocate(8);
        U32(chunks, 1); U32(chunks + 4, checked((uint)chunk));

        var output = new byte[dataStart + data.Count];
        Encoding.ASCII.GetBytes("GFF V4.0PC  MESHV0.1").CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(20, 4), 5);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(24, 4), dataStart);
        var fieldAt = 28 + schemas.Length * 16;
        for (var index = 0; index < schemas.Length; index++)
        {
            var at = 28 + index * 16;
            Encoding.ASCII.GetBytes(schemas[index].Kind).CopyTo(output, at);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 4, 4),
                checked((uint)schemas[index].Fields.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 8, 4),
                checked((uint)fieldAt));
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(at + 12, 4),
                checked((uint)schemas[index].Size));
            foreach (var field in schemas[index].Fields)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fieldAt, 4),
                    field.Label);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(fieldAt + 4, 2),
                    field.Type);
                BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(fieldAt + 6, 2),
                    field.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(fieldAt + 8, 4),
                    checked((uint)field.Offset));
                fieldAt += 12;
            }
        }
        data.ToArray().CopyTo(output, dataStart);
        return output;
    }

    private static MshTestField Mf(uint label, ushort type, int offset, ushort flags = 0) =>
        new(label, type, flags, offset);

    private sealed record MshTestField(uint Label, ushort Type, ushort Flags, int Offset);
    private sealed record MshTestStruct(
        string Kind, int Size, params MshTestField[] Fields);
}
