using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeCharacterMorphModifier(string Resource, float Weight);
public sealed record DragonAgeCharacterMorphScalarOverride(
    string Mesh, string Parameter, int Index, float Value);
public sealed record DragonAgeCharacterMorphVectorOverride(
    string Mesh, string Parameter, int Index, Vector4 Value);
public sealed record DragonAgeCharacterMorphTextureOverride(
    string Mesh, string Parameter, string Resource);

public sealed record DragonAgeCharacterMorphDefinition(
    string ResRef,
    string PayloadSha256,
    IReadOnlyList<string> ModelParts,
    IReadOnlyList<string> PaletteResources,
    IReadOnlyList<DragonAgeCharacterMorphModifier> Modifiers,
    IReadOnlyList<DragonAgeCharacterMorphScalarOverride> ScalarOverrides,
    IReadOnlyList<DragonAgeCharacterMorphVectorOverride> VectorOverrides,
    IReadOnlyList<DragonAgeCharacterMorphTextureOverride> TextureOverrides);

/// <summary>
/// Focused, bounded decoder for the installed DAO MOP V1.0 character-morph
/// contract. It preserves model, morph-target, and material identities without
/// attempting to interpret or convert MMH/MSH geometry.
/// </summary>
public static class DragonAgeOriginsCharacterMorphDecoder
{
    private const uint Name = 23008;
    private const uint ModelParts = 23000;
    private const uint PaletteResources = 23001;
    private const uint Modifiers = 23018;
    private const uint ScalarOverrides = 23014;
    private const uint VectorOverrides = 23015;
    private const uint TextureOverrides = 23022;

    public static DragonAgeCharacterMorphDefinition Decode(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var document = new MopDocument(payload);
        if (document.FileType != "MOP " || document.Version != "V1.0" ||
            document.Root.Kind != "mop ")
            throw new InvalidDataException("DAO character morph requires MOP V1.0.");

        var root = document.Root;
        var resRef = root.String(Name).Trim().ToLowerInvariant();
        if (resRef.Length == 0 || resRef.IndexOfAny(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
            throw new InvalidDataException("DAO character morph resref is invalid.");
        var parts = root.StringList(ModelParts)
            .Select(value => value.Trim()).ToArray();
        var palettes = root.StringList(PaletteResources)
            .Select(value => value.Trim()).ToArray();
        if (parts.Length == 0 || parts.All(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("DAO character morph has no model parts.");

        var modifiers = root.StructList(Modifiers, "mod ").Select(value =>
        {
            var weight = value.Single(23017);
            RequireFinite(weight, "modifier weight");
            return new DragonAgeCharacterMorphModifier(
                value.String(23016).Trim(), weight);
        }).ToArray();
        var scalars = root.StructList(ScalarOverrides, "mat ").Select(value =>
        {
            var scalar = value.Single(23012);
            RequireFinite(scalar, "material scalar");
            return new DragonAgeCharacterMorphScalarOverride(
                value.String(23009).Trim(), value.String(23010).Trim(),
                value.Int32(23011), scalar);
        }).ToArray();
        var vectors = root.StructList(VectorOverrides, "mat4").Select(value =>
        {
            var vector = value.Vector4(23013);
            if (!Finite(vector))
                throw new InvalidDataException("DAO character material vector is non-finite.");
            return new DragonAgeCharacterMorphVectorOverride(
                value.String(23009).Trim(), value.String(23010).Trim(),
                value.Int32(23011), vector);
        }).ToArray();
        var textures = root.StructList(TextureOverrides, "tex ").Select(value =>
            new DragonAgeCharacterMorphTextureOverride(
                value.String(23019).Trim(), value.String(23020).Trim(),
                value.String(23021).Trim())).ToArray();

        if (modifiers.Any(value => string.IsNullOrWhiteSpace(value.Resource)) ||
            scalars.Any(value => string.IsNullOrWhiteSpace(value.Mesh) ||
                                 string.IsNullOrWhiteSpace(value.Parameter)) ||
            vectors.Any(value => string.IsNullOrWhiteSpace(value.Mesh) ||
                                 string.IsNullOrWhiteSpace(value.Parameter)) ||
            textures.Any(value => string.IsNullOrWhiteSpace(value.Mesh) ||
                                  string.IsNullOrWhiteSpace(value.Parameter) ||
                                  string.IsNullOrWhiteSpace(value.Resource)))
            throw new InvalidDataException("DAO character morph contains an incomplete override.");

        return new DragonAgeCharacterMorphDefinition(
            resRef,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            parts,
            palettes,
            modifiers,
            scalars,
            vectors,
            textures);
    }

    private static void RequireFinite(float value, string label)
    {
        if (!float.IsFinite(value))
            throw new InvalidDataException($"DAO character {label} is non-finite.");
    }

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private sealed class MopDocument
    {
        private const ushort ListFlag = 0x8000;
        private const ushort StructFlag = 0x4000;
        private readonly byte[] payload;
        private readonly int dataStart;
        private readonly StructureDefinition[] structures;

        public MopDocument(byte[] payload)
        {
            this.payload = payload;
            if (payload.Length < 28 ||
                Encoding.ASCII.GetString(payload, 0, 12) != "GFF V4.0PC  ")
                throw new InvalidDataException("DAO character morph requires PC GFF V4.0.");
            FileType = Encoding.ASCII.GetString(payload, 12, 4);
            Version = Encoding.ASCII.GetString(payload, 16, 4);
            var count = UInt32(20);
            dataStart = checked((int)UInt32(24));
            if (count == 0 || count > 64 || dataStart < 28 + count * 16L ||
                dataStart > payload.Length)
                throw new InvalidDataException("DAO character morph structure table is invalid.");
            structures = new StructureDefinition[count];
            for (var index = 0; index < structures.Length; index++)
            {
                var at = checked(28 + index * 16);
                var kind = Encoding.ASCII.GetString(payload, at, 4).ToLowerInvariant();
                var fieldCount = UInt32(at + 4);
                var fieldOffset = UInt32(at + 8);
                var size = UInt32(at + 12);
                if (fieldCount > 128 || fieldOffset > payload.Length ||
                    fieldCount * 12L > payload.Length - fieldOffset ||
                    size > payload.Length)
                    throw new InvalidDataException(
                        "DAO character morph structure definition is invalid.");
                var fields = new Dictionary<uint, Field>();
                for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    var fieldAt = checked((int)fieldOffset + ordinal * 12);
                    var field = new Field(
                        UInt32(fieldAt), UInt16(fieldAt + 4), UInt16(fieldAt + 6),
                        checked((int)UInt32(fieldAt + 8)));
                    if (!fields.TryAdd(field.Label, field))
                        throw new InvalidDataException(
                            "DAO character morph contains a duplicate field label.");
                }
                structures[index] = new StructureDefinition(kind, checked((int)size), fields);
            }
        }

        public string FileType { get; }
        public string Version { get; }
        public Structure Root => new(this, 0, 0);

        private int DataOffset(int relative, int length = 4)
        {
            var at = checked(dataStart + relative);
            if (relative < 0 || length < 0 || at < dataStart ||
                at > payload.Length - length)
                throw new InvalidDataException(
                    "DAO character morph data reference is outside the payload.");
            return at;
        }

        private ushort UInt16(int offset)
        {
            if (offset < 0 || offset > payload.Length - 2)
                throw new InvalidDataException("DAO character morph uint16 is out of range.");
            return BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        }

        private uint UInt32(int offset)
        {
            if (offset < 0 || offset > payload.Length - 4)
                throw new InvalidDataException("DAO character morph uint32 is out of range.");
            return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
        }

        private string ReadString(int relative)
        {
            if (relative < 0) return string.Empty;
            var at = DataOffset(relative);
            var characters = UInt32(at);
            if (characters > 1_000_000 || characters * 2L > payload.Length - at - 4)
                throw new InvalidDataException("DAO character morph string is out of range.");
            return Encoding.Unicode.GetString(payload, at + 4,
                checked((int)characters * 2)).TrimEnd('\0');
        }

        private sealed record Field(uint Label, ushort Type, ushort Flags, int Offset);
        private sealed record StructureDefinition(
            string Kind, int Size, IReadOnlyDictionary<uint, Field> Fields);

        public sealed class Structure
        {
            private readonly MopDocument document;
            private readonly int type;
            private readonly int baseOffset;

            internal Structure(MopDocument document, int type, int baseOffset)
            {
                this.document = document;
                this.type = type;
                this.baseOffset = baseOffset;
            }

            private StructureDefinition Definition => document.structures[type];
            public string Kind => Definition.Kind;

            public string String(uint label)
            {
                var field = RequireField(label, 14, 0);
                return document.ReadString(Int32At(field.Offset));
            }

            public IReadOnlyList<string> StringList(uint label)
            {
                var field = RequireField(label, 14, ListFlag);
                var relative = Int32At(field.Offset);
                if (relative < 0) return [];
                var at = document.DataOffset(relative);
                var count = document.UInt32(at);
                if (count > 4096 || count * 4L > document.payload.Length - at - 4)
                    throw new InvalidDataException(
                        "DAO character morph string list is out of range.");
                var values = new string[count];
                for (var index = 0; index < values.Length; index++)
                {
                    var item = BinaryPrimitives.ReadInt32LittleEndian(
                        document.payload.AsSpan(checked(at + 4 + index * 4), 4));
                    values[index] = document.ReadString(item);
                }
                return values;
            }

            public IReadOnlyList<Structure> StructList(uint label, string expectedKind)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Flags != (ListFlag | StructFlag) ||
                    field.Type >= document.structures.Length)
                    throw new InvalidDataException(
                        $"DAO character morph struct list {label} is absent or typed differently.");
                var definition = document.structures[field.Type];
                if (definition.Kind != expectedKind)
                    throw new InvalidDataException(
                        $"DAO character morph struct list {label} has an unexpected kind.");
                var relative = Int32At(field.Offset);
                if (relative < 0) return [];
                var at = document.DataOffset(relative);
                var count = document.UInt32(at);
                if (count > 65_536 || count * (long)definition.Size >
                    document.payload.Length - at - 4)
                    throw new InvalidDataException(
                        "DAO character morph homogeneous struct list is out of range.");
                var values = new Structure[count];
                var firstRelative = checked(relative + 4);
                for (var index = 0; index < values.Length; index++)
                    values[index] = new Structure(document, field.Type,
                        checked(firstRelative + index * definition.Size));
                return values;
            }

            public int Int32(uint label)
            {
                var field = RequireField(label, 5, 0);
                return BinaryPrimitives.ReadInt32LittleEndian(Read(field.Offset, 4));
            }

            public float Single(uint label)
            {
                var field = RequireField(label, 8, 0);
                return BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(Read(field.Offset, 4)));
            }

            public Vector4 Vector4(uint label)
            {
                var field = RequireField(label, 12, 0);
                var source = Read(field.Offset, 16);
                return new Vector4(
                    ReadSingle(source, 0), ReadSingle(source, 4),
                    ReadSingle(source, 8), ReadSingle(source, 12));
            }

            private Field RequireField(uint label, ushort type, ushort flags)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Type != type || field.Flags != flags)
                    throw new InvalidDataException(
                        $"DAO character morph field {label} is absent or typed differently.");
                return field;
            }

            private int Int32At(int offset) => BinaryPrimitives.ReadInt32LittleEndian(
                Read(offset, 4));

            private ReadOnlySpan<byte> Read(int offset, int length) =>
                document.payload.AsSpan(
                    document.DataOffset(checked(baseOffset + offset), length), length);

            private static float ReadSingle(ReadOnlySpan<byte> source, int offset) =>
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)));
        }
    }
}
