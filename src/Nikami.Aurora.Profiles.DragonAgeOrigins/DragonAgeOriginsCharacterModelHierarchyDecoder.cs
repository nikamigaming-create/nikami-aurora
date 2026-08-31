using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeCharacterModelNode(
    int Index,
    string Name,
    int? BoneId,
    int? ParentIndex,
    Vector3 Translation,
    Quaternion Rotation,
    float Scale,
    string SourceKind);

public sealed record DragonAgeCharacterMeshBinding(
    string Name,
    string MaterialObject,
    string Field6002,
    string Field6004,
    string Field6006,
    string Field6307,
    int? ParentNodeIndex,
    Vector3 Translation,
    Quaternion Rotation,
    float Scale,
    IReadOnlyList<uint> BoneIndices);

public sealed record DragonAgeCharacterControllerExport(
    int? ParentNodeIndex,
    string TagName,
    string ExportName,
    uint VariableType,
    uint ControllerIndex);

public sealed record DragonAgeCharacterAttributeBinding(
    int? ParentNodeIndex,
    string Name,
    string SourceName);

public sealed record DragonAgeCharacterBoundingBox(
    int? ParentNodeIndex,
    Vector3 Minimum,
    Vector3 Maximum);

public sealed record DragonAgeCharacterCrustHook(
    int? ParentNodeIndex,
    string Name,
    uint HookId);

public sealed record DragonAgeCharacterStructureSchema(
    string Kind,
    IReadOnlyList<uint> FieldLabels);

public sealed record DragonAgeCharacterModelHierarchy(
    string ResRef,
    string PayloadSha256,
    string MeshResource,
    string AnimationResource,
    uint? HeaderValue6256,
    uint? HeaderValue6275,
    IReadOnlyList<DragonAgeCharacterModelNode> Nodes,
    IReadOnlyList<DragonAgeCharacterMeshBinding> MeshBindings,
    IReadOnlyList<DragonAgeCharacterControllerExport> ControllerExports,
    IReadOnlyList<DragonAgeCharacterAttributeBinding> AttributeBindings,
    IReadOnlyList<DragonAgeCharacterBoundingBox> BoundingBoxes,
    IReadOnlyList<DragonAgeCharacterCrustHook> CrustHooks,
    IReadOnlyDictionary<string, int> StructureKinds,
    IReadOnlyList<DragonAgeCharacterStructureSchema> StructureSchemas,
    IReadOnlyList<string> UninterpretedStructureKinds);

/// <summary>
/// Bounded reader for the render/skeleton portion of installed DAO MMH V0.1
/// payloads. The reader preserves source field identities where their gameplay
/// meaning has not yet been proven. It does not decode MSH geometry or infer
/// animation, material, or attachment behavior from names.
/// </summary>
public static class DragonAgeOriginsCharacterModelHierarchyDecoder
{
    private const uint Children = 6999;
    private static readonly HashSet<string> InterpretedKinds =
        new(StringComparer.Ordinal)
        {
            "mdlh", "node", "ntrn", "mshh", "trsl", "rota", "scal",
            "xprt", "attr", "bbox", "crst"
        };

    public static DragonAgeCharacterModelHierarchy Decode(
        string pathOrResRef, byte[] payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pathOrResRef);
        ArgumentNullException.ThrowIfNull(payload);
        var expected = Path.GetFileNameWithoutExtension(
            pathOrResRef.Replace('\\', '/')).Trim().ToLowerInvariant();
        if (expected.Length == 0)
            throw new InvalidDataException("DAO character MMH resref is invalid.");

        var document = new MmhDocument(payload);
        if (document.FileType != "MMH " || document.Version != "V0.1" ||
            document.Root.Kind != "mdlh")
            throw new InvalidDataException("DAO character hierarchy requires MMH V0.1.");
        var sourceName = document.Root.String(6000);
        var actual = Path.GetFileNameWithoutExtension(
            sourceName.Replace('\\', '/')).Trim().ToLowerInvariant();
        if (!actual.Equals(expected, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"DAO character MMH identity disagrees: expected={expected} actual={actual}.");

        var nodes = new List<DragonAgeCharacterModelNode>();
        var bindings = new List<DragonAgeCharacterMeshBinding>();
        var exports = new List<DragonAgeCharacterControllerExport>();
        var attributes = new List<DragonAgeCharacterAttributeBinding>();
        var boundingBoxes = new List<DragonAgeCharacterBoundingBox>();
        var crustHooks = new List<DragonAgeCharacterCrustHook>();
        var kinds = new Dictionary<string, int>(StringComparer.Ordinal);
        Walk(document, document.Root, null, nodes, bindings, exports, attributes,
            boundingBoxes, crustHooks, kinds, 0);
        if (bindings.Count == 0)
            throw new InvalidDataException("DAO character MMH has no mesh binding.");
        if (nodes.Select(value => value.Index).Distinct().Count() != nodes.Count)
            throw new InvalidDataException("DAO character MMH node indices are not unique.");
        var declaredBones = document.Root.OptionalUInt32(6256);
        var indexedBones = nodes.Where(value => value.BoneId.HasValue).ToArray();
        if (declaredBones.HasValue &&
            (indexedBones.Length != declaredBones.Value ||
             indexedBones.Select(value => value.BoneId!.Value).Distinct().Count() !=
             indexedBones.Length ||
             indexedBones.Any(value => value.BoneId < 0 ||
                                       value.BoneId >= declaredBones.Value)))
            throw new InvalidDataException(
                "DAO character MMH indexed bone contract disagrees with its header.");
        var declaredExports = document.Root.OptionalUInt32(6275);
        if (declaredExports.HasValue &&
            (exports.Count != declaredExports.Value ||
             exports.Any(value => value.ControllerIndex >= declaredExports.Value)))
            throw new InvalidDataException(
                "DAO character MMH controller export contract disagrees with its header.");
        if (declaredBones.HasValue && bindings.SelectMany(value => value.BoneIndices)
                .Any(value => value >= declaredBones.Value))
            throw new InvalidDataException(
                "DAO character MMH mesh binding references an out-of-range bone.");

        return new DragonAgeCharacterModelHierarchy(
            expected,
            Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(),
            document.Root.OptionalString(6005)?.Trim().ToLowerInvariant() ?? string.Empty,
            document.Root.OptionalString(6248)?.Trim().ToLowerInvariant() ?? string.Empty,
            document.Root.OptionalUInt32(6256),
            document.Root.OptionalUInt32(6275),
            nodes,
            bindings,
            exports,
            attributes,
            boundingBoxes,
            crustHooks,
            kinds,
            document.StructureSchemas,
            kinds.Keys.Where(value => !InterpretedKinds.Contains(value))
                .Order(StringComparer.Ordinal).ToArray());
    }

    private static void Walk(
        MmhDocument document,
        MmhDocument.Structure owner,
        int? parentNode,
        ICollection<DragonAgeCharacterModelNode> nodes,
        ICollection<DragonAgeCharacterMeshBinding> bindings,
        ICollection<DragonAgeCharacterControllerExport> exports,
        ICollection<DragonAgeCharacterAttributeBinding> attributes,
        ICollection<DragonAgeCharacterBoundingBox> boundingBoxes,
        ICollection<DragonAgeCharacterCrustHook> crustHooks,
        IDictionary<string, int> kinds,
        int depth)
    {
        if (depth > 256)
            throw new InvalidDataException("DAO character MMH graph is too deep.");
        AddKind(kinds, owner.Kind);
        foreach (var child in document.Children(owner))
        {
            if (child.Kind is "trsl" or "rota" or "scal")
            {
                AddKind(kinds, child.Kind);
                continue;
            }

            if (child.Kind is "node" or "ntrn")
            {
                var transform = LocalTransform(document, child);
                var name = child.OptionalString(6000)?.Trim() ?? string.Empty;
                if (name.Length == 0)
                    throw new InvalidDataException("DAO character MMH node name is blank.");
                var sourceBoneId = child.OptionalInt32(6254);
                var boneId = sourceBoneId is >= 0 ? sourceBoneId : null;
                var index = nodes.Count;
                nodes.Add(new DragonAgeCharacterModelNode(
                    index,
                    name,
                    boneId,
                    parentNode,
                    transform.Translation,
                    transform.Rotation,
                    transform.Scale,
                    child.Kind));
                Walk(document, child, index, nodes, bindings, exports, attributes,
                    boundingBoxes, crustHooks, kinds, depth + 1);
                continue;
            }

            if (child.Kind == "mshh")
            {
                var transform = LocalTransform(document, child);
                var name = child.OptionalString(6000)?.Trim() ?? string.Empty;
                var material = child.OptionalString(6001)?.Trim() ?? string.Empty;
                if (name.Length == 0 || material.Length == 0)
                    throw new InvalidDataException(
                        "DAO character MMH mesh binding identity is incomplete.");
                bindings.Add(new DragonAgeCharacterMeshBinding(
                    name,
                    material,
                    child.OptionalString(6002)?.Trim() ?? string.Empty,
                    child.OptionalString(6004)?.Trim() ?? string.Empty,
                    child.OptionalString(6006)?.Trim() ?? string.Empty,
                    child.OptionalString(6307)?.Trim() ?? string.Empty,
                    parentNode,
                    transform.Translation,
                    transform.Rotation,
                    transform.Scale,
                    child.UInt32List(6255)));
                Walk(document, child, parentNode, nodes, bindings, exports, attributes,
                    boundingBoxes, crustHooks, kinds, depth + 1);
                continue;
            }

            if (child.Kind == "xprt")
            {
                var exportName = child.OptionalString(6052)?.Trim() ?? string.Empty;
                if (exportName.Length == 0)
                    throw new InvalidDataException(
                        "DAO character MMH controller export name is blank.");
                exports.Add(new DragonAgeCharacterControllerExport(
                    parentNode,
                    child.OptionalString(6051)?.Trim() ?? string.Empty,
                    exportName,
                    child.UInt32(6238),
                    child.UInt32(6274)));
            }
            else if (child.Kind == "attr")
            {
                attributes.Add(new DragonAgeCharacterAttributeBinding(
                    parentNode,
                    child.OptionalString(6049)?.Trim() ?? string.Empty,
                    child.OptionalString(6050)?.Trim() ?? string.Empty));
            }
            else if (child.Kind == "bbox")
            {
                var minimum = child.Vector3(6054);
                var maximum = child.Vector3(6055);
                if (!Finite(minimum) || !Finite(maximum) ||
                    minimum.X > maximum.X || minimum.Y > maximum.Y ||
                    minimum.Z > maximum.Z)
                    throw new InvalidDataException(
                        "DAO character MMH bounding box is invalid.");
                boundingBoxes.Add(new DragonAgeCharacterBoundingBox(
                    parentNode, minimum, maximum));
            }
            else if (child.Kind == "crst")
            {
                var name = child.OptionalString(6000)?.Trim() ?? string.Empty;
                if (name.Length == 0)
                    throw new InvalidDataException(
                        "DAO character MMH crust-hook name is blank.");
                crustHooks.Add(new DragonAgeCharacterCrustHook(
                    parentNode, name, child.Byte(6235)));
            }

            Walk(document, child, parentNode, nodes, bindings, exports, attributes,
                boundingBoxes, crustHooks, kinds, depth + 1);
        }
    }

    private static (Vector3 Translation, Quaternion Rotation, float Scale) LocalTransform(
        MmhDocument document, MmhDocument.Structure owner)
    {
        var translation = Vector3.Zero;
        var rotation = Quaternion.Identity;
        var scale = 1f;
        var translationCount = 0;
        var rotationCount = 0;
        var scaleCount = 0;
        foreach (var child in document.Children(owner))
        {
            switch (child.Kind)
            {
                case "trsl":
                    translation = child.Vector3(6047);
                    translationCount++;
                    break;
                case "rota":
                    rotation = child.Quaternion(6048);
                    rotationCount++;
                    break;
                case "scal":
                    scale = child.Single(6278);
                    scaleCount++;
                    break;
            }
        }
        if (translationCount > 1 || rotationCount > 1 || scaleCount > 1)
            throw new InvalidDataException(
                "DAO character MMH contains duplicate local transform controllers.");
        if (!Finite(translation) || !Finite(rotation) ||
            !float.IsFinite(scale) || scale <= 0)
            throw new InvalidDataException("DAO character MMH local transform is invalid.");
        return (translation, Quaternion.Normalize(rotation), scale);
    }

    private static void AddKind(IDictionary<string, int> kinds, string kind) =>
        kinds[kind] = kinds.TryGetValue(kind, out var count) ? count + 1 : 1;

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool Finite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W) &&
        value.LengthSquared() >= .000001f;

    private sealed class MmhDocument
    {
        private const ushort ListFlag = 0x8000;
        private const ushort ReferenceFlag = 0x2000;
        private const ushort StructFlag = 0x4000;
        private readonly byte[] payload;
        private readonly int dataStart;
        private readonly StructureDefinition[] structures;

        public MmhDocument(byte[] payload)
        {
            this.payload = payload;
            if (payload.Length < 28 ||
                Encoding.ASCII.GetString(payload, 0, 12) != "GFF V4.0PC  ")
                throw new InvalidDataException("DAO character MMH requires PC GFF V4.0.");
            FileType = Encoding.ASCII.GetString(payload, 12, 4);
            Version = Encoding.ASCII.GetString(payload, 16, 4);
            var count = UInt32(20);
            dataStart = checked((int)UInt32(24));
            if (count == 0 || count > 4096 || dataStart < 28 + count * 16L ||
                dataStart > payload.Length)
                throw new InvalidDataException("DAO character MMH structure table is invalid.");
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
                    throw new InvalidDataException(
                        "DAO character MMH structure definition is invalid.");
                var fields = new Dictionary<uint, Field>();
                for (var ordinal = 0; ordinal < fieldCount; ordinal++)
                {
                    var fieldAt = checked((int)fieldOffset + ordinal * 12);
                    var field = new Field(UInt32(fieldAt), UInt16(fieldAt + 4),
                        UInt16(fieldAt + 6), checked((int)UInt32(fieldAt + 8)));
                    if (!fields.TryAdd(field.Label, field))
                        throw new InvalidDataException(
                            "DAO character MMH contains a duplicate field label.");
                }
                structures[index] = new StructureDefinition(
                    kind, checked((int)size), fields);
            }
        }

        public string FileType { get; }
        public string Version { get; }
        public Structure Root => new(this, 0, 0);
        public IReadOnlyList<DragonAgeCharacterStructureSchema> StructureSchemas =>
            structures.Select(value => new DragonAgeCharacterStructureSchema(
                    value.Kind, value.Fields.Keys.Order().ToArray()))
                .GroupBy(value => value.Kind + ":" + string.Join(',', value.FieldLabels),
                    StringComparer.Ordinal).Select(group => group.First())
                .OrderBy(value => value.Kind, StringComparer.Ordinal)
                .ThenBy(value => string.Join(',', value.FieldLabels), StringComparer.Ordinal)
                .ToArray();

        public IReadOnlyList<Structure> Children(Structure owner)
        {
            if (!owner.Definition.Fields.TryGetValue(ChildrenLabel, out var field))
                return [];
            if (field.Type != ushort.MaxValue ||
                (field.Flags & (ListFlag | ReferenceFlag)) !=
                (ListFlag | ReferenceFlag))
                throw new InvalidDataException(
                    "DAO character MMH heterogeneous child list is invalid.");
            var relative = owner.Int32At(field.Offset);
            if (relative < 0) return [];
            var at = DataOffset(relative);
            var count = UInt32(at);
            if (count > 1_000_000 || 4L + count * 8L > payload.Length - at)
                throw new InvalidDataException("DAO character MMH child list is invalid.");
            var result = new Structure[count];
            for (var index = 0; index < result.Length; index++)
            {
                var itemAt = checked(at + 4 + index * 8);
                var typeAndFlags = UInt32(itemAt);
                var type = checked((ushort)(typeAndFlags & 0xffff));
                var flags = checked((ushort)(typeAndFlags >> 16));
                var itemRelative = checked((int)UInt32(itemAt + 4));
                if ((flags & StructFlag) == 0 || type >= structures.Length)
                    throw new InvalidDataException(
                        "DAO character MMH child is not a structure reference.");
                _ = DataOffset(itemRelative, structures[type].Size);
                result[index] = new Structure(this, type, itemRelative);
            }
            return result;
        }

        private const uint ChildrenLabel =
            DragonAgeOriginsCharacterModelHierarchyDecoder.Children;

        private int DataOffset(int relative, int length = 4)
        {
            var at = checked(dataStart + relative);
            if (relative < 0 || length < 0 || at < dataStart ||
                at > payload.Length - length)
                throw new InvalidDataException(
                    "DAO character MMH data reference is outside the payload.");
            return at;
        }

        private ushort UInt16(int offset)
        {
            if (offset < 0 || offset > payload.Length - 2)
                throw new InvalidDataException("DAO character MMH uint16 is out of range.");
            return BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset, 2));
        }

        private uint UInt32(int offset)
        {
            if (offset < 0 || offset > payload.Length - 4)
                throw new InvalidDataException("DAO character MMH uint32 is out of range.");
            return BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
        }

        internal sealed record Field(uint Label, ushort Type, ushort Flags, int Offset);
        internal sealed record StructureDefinition(
            string Kind, int Size, IReadOnlyDictionary<uint, Field> Fields);

        public sealed class Structure
        {
            private readonly MmhDocument document;
            private readonly int type;
            private readonly int baseOffset;

            internal Structure(MmhDocument document, int type, int baseOffset)
            {
                this.document = document;
                this.type = type;
                this.baseOffset = baseOffset;
            }

            internal StructureDefinition Definition => document.structures[type];
            public string Kind => Definition.Kind;

            public string String(uint label) => OptionalString(label) ??
                throw new InvalidDataException(
                    $"DAO character MMH string field {label} is absent.");

            public string? OptionalString(uint label)
            {
                if (!Definition.Fields.TryGetValue(label, out var field)) return null;
                if (field.Type != 14 || field.Flags != 0)
                    throw new InvalidDataException(
                        $"DAO character MMH field {label} is not an ECString.");
                var relative = Int32At(field.Offset);
                if (relative < 0) return string.Empty;
                var at = document.DataOffset(relative);
                var characters = document.UInt32(at);
                if (characters > 1_000_000 ||
                    characters * 2L > document.payload.Length - at - 4)
                    throw new InvalidDataException("DAO character MMH string is out of range.");
                return Encoding.Unicode.GetString(document.payload, at + 4,
                    checked((int)characters * 2)).TrimEnd('\0');
            }

            public uint? OptionalUInt32(uint label)
            {
                if (!Definition.Fields.TryGetValue(label, out var field)) return null;
                if (field.Flags != 0 || field.Type is not (4 or 5))
                    throw new InvalidDataException(
                        $"DAO character MMH integer field {label} is typed differently " +
                        $"(type={field.Type}, flags=0x{field.Flags:x4}).");
                return BinaryPrimitives.ReadUInt32LittleEndian(Read(field.Offset, 4));
            }

            public int? OptionalInt32(uint label)
            {
                var value = OptionalUInt32(label);
                return value.HasValue ? unchecked((int)value.Value) : null;
            }

            public uint UInt32(uint label) => OptionalUInt32(label) ??
                throw new InvalidDataException(
                    $"DAO character MMH integer field {label} is absent.");

            public byte Byte(uint label)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Type != 0 || field.Flags != 0)
                    throw new InvalidDataException(
                        $"DAO character MMH byte field {label} is absent or typed differently.");
                return Read(field.Offset, 1)[0];
            }

            public IReadOnlyList<uint> UInt32List(uint label)
            {
                if (!Definition.Fields.TryGetValue(label, out var field)) return [];
                if (field.Type != 4 || field.Flags != ListFlag)
                    throw new InvalidDataException(
                        $"DAO character MMH uint32 list {label} is typed differently.");
                var relative = Int32At(field.Offset);
                if (relative < 0) return [];
                var at = document.DataOffset(relative);
                var count = document.UInt32(at);
                if (count > 65_536 || count * 4L > document.payload.Length - at - 4)
                    throw new InvalidDataException(
                        $"DAO character MMH uint32 list {label} is out of range.");
                var result = new uint[count];
                for (var index = 0; index < result.Length; index++)
                    result[index] = document.UInt32(checked(at + 4 + index * 4));
                return result;
            }

            public float Single(uint label)
            {
                var field = RequireScalar(label, 8);
                return BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(Read(field.Offset, 4)));
            }

            public Vector3 Vector3(uint label)
            {
                var field = RequireVector(label, 12);
                var source = Read(field.Offset, 12);
                return new Vector3(ReadSingle(source, 0), ReadSingle(source, 4),
                    ReadSingle(source, 8));
            }

            public Quaternion Quaternion(uint label)
            {
                var field = RequireVector(label, 16);
                var source = Read(field.Offset, 16);
                return new Quaternion(ReadSingle(source, 0), ReadSingle(source, 4),
                    ReadSingle(source, 8), ReadSingle(source, 12));
            }

            internal int Int32At(int offset) => BinaryPrimitives.ReadInt32LittleEndian(
                Read(offset, 4));

            private Field RequireScalar(uint label, ushort type)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Flags != 0 || field.Type != type)
                    throw new InvalidDataException(
                        $"DAO character MMH scalar field {label} is absent or typed differently.");
                return field;
            }

            private Field RequireVector(uint label, int size)
            {
                if (!Definition.Fields.TryGetValue(label, out var field) ||
                    field.Flags != 0 || field.Type is not (10 or 12 or 13 or 15))
                    throw new InvalidDataException(
                        $"DAO character MMH vector field {label} is absent or typed differently.");
                _ = Read(field.Offset, size);
                return field;
            }

            private ReadOnlySpan<byte> Read(int offset, int length) =>
                document.payload.AsSpan(
                    document.DataOffset(checked(baseOffset + offset), length), length);

            private static float ReadSingle(ReadOnlySpan<byte> source, int offset) =>
                BitConverter.Int32BitsToSingle(
                    BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)));
        }
    }
}
