using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
{
    private static void DragonAgeCharacterModelHierarchyDecoderIsSourceBound()
    {
        var payload = SyntheticCharacterMmh("hf_synthetic_a_0", 0);
        var hierarchy = DragonAgeOriginsCharacterModelHierarchyDecoder.Decode(
            "models/hf_synthetic_a_0.mmh", payload);
        Expect(hierarchy.ResRef == "hf_synthetic_a_0" &&
               hierarchy.PayloadSha256.Length == 64 &&
               hierarchy.MeshResource == "hf_synthetic_0.msh" &&
               hierarchy.AnimationResource == "humanfemale.fxa" &&
               hierarchy.HeaderValue6256 == 1 && hierarchy.HeaderValue6275 == 1,
            "synthetic DAO character MMH header contract drifted");
        Expect(hierarchy.Nodes is [{ Name: "Root", BoneId: 0, ParentIndex: null }] &&
               hierarchy.Nodes[0].Translation == new Vector3(1, 2, 3) &&
               hierarchy.Nodes[0].Rotation == Quaternion.Identity &&
               hierarchy.Nodes[0].Scale == 1.25f,
            "synthetic DAO character skeleton graph did not preserve local TRS");
        Expect(hierarchy.MeshBindings is
               [
            {
                Name: "FaceM1", MaterialObject: "uh_hed_fema",
                Field6006: "HF_SYNTHETIC_FaceM1", ParentNodeIndex: 0
            }] &&
               hierarchy.MeshBindings[0].BoneIndices.SequenceEqual([0u]),
            "synthetic DAO character mesh binding did not preserve bone joins");
        Expect(hierarchy.ControllerExports is
               [
            {
                ParentNodeIndex: 0, ExportName: "Root_Position",
                VariableType: 1, ControllerIndex: 0
            }] &&
               hierarchy.AttributeBindings is
               [{ Name: "Skeleton", SourceName: "synthetic" }] &&
               hierarchy.BoundingBoxes is [{ Minimum: var minimum, Maximum: var maximum }] &&
               minimum == new Vector3(-1, -2, -3) &&
               maximum == new Vector3(1, 2, 3) &&
               hierarchy.CrustHooks is [{ Name: "FaceCrust", HookId: 7 }] &&
               hierarchy.UninterpretedStructureKinds.Count == 0,
            "synthetic DAO character MMH metadata joins were not decoded exactly");
        ExpectThrows<InvalidDataException>(() =>
                DragonAgeOriginsCharacterModelHierarchyDecoder.Decode(
                    "hf_synthetic_a_0", SyntheticCharacterMmh("hf_synthetic_a_0", 1)),
            "DAO character MMH accepted an out-of-range mesh bone index");
        ExpectThrows<InvalidDataException>(() =>
                DragonAgeOriginsCharacterModelHierarchyDecoder.Decode(
                    "different", payload),
            "DAO character MMH accepted a mismatched source identity");
    }

    private static byte[] SyntheticCharacterMmh(string resRef, uint meshBoneIndex)
    {
        const ushort heterogeneousList = 0xa000;
        const ushort scalarList = 0x8000;
        var specs = new[]
        {
            new CharacterMmhTestStruct("mdlh", 24,
                CF(6000, 14, 0), CF(6005, 14, 4), CF(6248, 14, 8),
                CF(6256, 4, 12), CF(6275, 4, 16),
                CF(6999, ushort.MaxValue, 20, heterogeneousList)),
            new CharacterMmhTestStruct("node", 12,
                CF(6000, 14, 0), CF(6254, 4, 4),
                CF(6999, ushort.MaxValue, 8, heterogeneousList)),
            new CharacterMmhTestStruct("mshh", 20,
                CF(6000, 14, 0), CF(6001, 14, 4), CF(6006, 14, 8),
                CF(6255, 4, 12, scalarList),
                CF(6999, ushort.MaxValue, 16, heterogeneousList)),
            new CharacterMmhTestStruct("trsl", 12, CF(6047, 10, 0)),
            new CharacterMmhTestStruct("rota", 16, CF(6048, 13, 0)),
            new CharacterMmhTestStruct("scal", 4, CF(6278, 8, 0)),
            new CharacterMmhTestStruct("xprt", 12,
                CF(6052, 14, 0), CF(6238, 4, 4), CF(6274, 4, 8)),
            new CharacterMmhTestStruct("attr", 8,
                CF(6049, 14, 0), CF(6050, 14, 4)),
            new CharacterMmhTestStruct("bbox", 24,
                CF(6054, 10, 0), CF(6055, 10, 12)),
            new CharacterMmhTestStruct("crst", 12,
                CF(6000, 14, 0), CF(6235, 0, 4),
                CF(6999, ushort.MaxValue, 8, heterogeneousList))
        };
        var dataStart = 28 + specs.Length * 16 + specs.Sum(value => value.Fields.Length) * 12;
        var data = new List<byte>();
        int Allocate(int type)
        {
            var result = data.Count;
            data.AddRange(new byte[specs[type].Size]);
            return result;
        }
        var root = Allocate(0);
        var node = Allocate(1);
        var mesh = Allocate(2);
        var translation = Allocate(3);
        var rotation = Allocate(4);
        var scale = Allocate(5);
        var export = Allocate(6);
        var attribute = Allocate(7);
        var bounds = Allocate(8);
        var crust = Allocate(9);

        void Write(int offset, ReadOnlySpan<byte> bytes)
        {
            for (var index = 0; index < bytes.Length; index++)
                data[offset + index] = bytes[index];
        }
        void U32(int offset, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
            Write(offset, bytes);
        }
        void Float(int offset, float value) =>
            U32(offset, unchecked((uint)BitConverter.SingleToInt32Bits(value)));
        void Vector(int offset, params float[] values)
        {
            for (var index = 0; index < values.Length; index++)
                Float(offset + index * 4, values[index]);
        }
        void String(int owner, int field, string value)
        {
            var relative = data.Count;
            var text = Encoding.Unicode.GetBytes(value);
            var bytes = new byte[4 + text.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value.Length));
            text.CopyTo(bytes, 4);
            data.AddRange(bytes);
            U32(owner + field, checked((uint)relative));
        }
        void Children(int owner, int field, params (int Type, int Offset)[] values)
        {
            var relative = data.Count;
            var bytes = new byte[4 + values.Length * 8];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)values.Length));
            for (var index = 0; index < values.Length; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(4 + index * 8, 4),
                    checked((uint)values[index].Type | 0x40000000));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(8 + index * 8, 4),
                    checked((uint)values[index].Offset));
            }
            data.AddRange(bytes);
            U32(owner + field, checked((uint)relative));
        }
        void UIntList(int owner, int field, params uint[] values)
        {
            var relative = data.Count;
            var bytes = new byte[4 + values.Length * 4];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)values.Length));
            for (var index = 0; index < values.Length; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(4 + index * 4, 4), values[index]);
            data.AddRange(bytes);
            U32(owner + field, checked((uint)relative));
        }

        String(root, 0, resRef + ".mmh");
        String(root, 4, "hf_synthetic_0.msh");
        String(root, 8, "humanfemale.fxa");
        U32(root + 12, 1);
        U32(root + 16, 1);
        Children(root, 20, (1, node), (7, attribute), (8, bounds), (9, crust));
        String(node, 0, "Root");
        U32(node + 4, 0);
        Children(node, 8, (3, translation), (4, rotation), (5, scale),
            (6, export), (2, mesh));
        Vector(translation, 1, 2, 3);
        Vector(rotation, 0, 0, 0, 1);
        Float(scale, 1.25f);
        String(export, 0, "Root_Position");
        U32(export + 4, 1);
        U32(export + 8, 0);
        String(mesh, 0, "FaceM1");
        String(mesh, 4, "uh_hed_fema");
        String(mesh, 8, "HF_SYNTHETIC_FaceM1");
        UIntList(mesh, 12, meshBoneIndex);
        Children(mesh, 16);
        String(attribute, 0, "Skeleton");
        String(attribute, 4, "synthetic");
        Vector(bounds, -1, -2, -3);
        Vector(bounds + 12, 1, 2, 3);
        String(crust, 0, "FaceCrust");
        data[crust + 4] = 7;
        Children(crust, 8);

        var result = new byte[dataStart + data.Count];
        Encoding.ASCII.GetBytes("GFF V4.0PC  MMH V0.1").CopyTo(result, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20, 4),
            checked((uint)specs.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24, 4),
            checked((uint)dataStart));
        var fieldsAt = 28 + specs.Length * 16;
        for (var index = 0; index < specs.Length; index++)
        {
            var definitionAt = 28 + index * 16;
            Encoding.ASCII.GetBytes(specs[index].Kind).CopyTo(result, definitionAt);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(definitionAt + 4, 4),
                checked((uint)specs[index].Fields.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(definitionAt + 8, 4),
                checked((uint)fieldsAt));
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(definitionAt + 12, 4),
                checked((uint)specs[index].Size));
            foreach (var field in specs[index].Fields)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fieldsAt, 4), field.Label);
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fieldsAt + 4, 2), field.Type);
                BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(fieldsAt + 6, 2), field.Flags);
                BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fieldsAt + 8, 4),
                    checked((uint)field.Offset));
                fieldsAt += 12;
            }
        }
        data.CopyTo(result, dataStart);
        return result;
    }

    private static CharacterMmhTestField CF(
        uint label, ushort type, int offset, ushort flags = 0) =>
        new(label, type, flags, offset);

    private sealed record CharacterMmhTestField(
        uint Label, ushort Type, ushort Flags, int Offset);

    private sealed record CharacterMmhTestStruct(
        string Kind, int Size, params CharacterMmhTestField[] Fields);
}
