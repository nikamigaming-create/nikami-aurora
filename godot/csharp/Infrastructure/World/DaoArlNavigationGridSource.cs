using System.Buffers.Binary;
using System.Text;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class DaoArlNavigationGridSource : IAuthoredNavigationGridSource
{
    private const uint AreaPathfinding = 3020;
    private const uint AreaGrid = 3110;
    private const uint GridColumns = 3086;
    private const uint GridRows = 3087;
    private const uint GridCellSize = 3088;
    private const uint GridBasePosition = 3090;
    private const uint GridData = 3092;
    private const ushort StructFlag = 0x4000;
    private const ushort ListFlag = 0x8000;

    public AuthoredNavigationGrid? Load(WorldProfile profile)
    {
        if (profile.GameRoot.Length == 0 || profile.LayoutName.Length == 0) return null;
        var layout = profile.LayoutName.ToLowerInvariant();
        var path = Path.Combine(profile.GameRoot, "packages", "core", "env", layout + ".arl");
        if (!File.Exists(path)) return null;

        var data = File.ReadAllBytes(path);
        if (data.Length < 28 || Encoding.ASCII.GetString(data, 0, 16) != "GFF V4.0PC  ARL ")
            throw new InvalidDataException($"Unsupported DAO ARL navigation source: {path}");
        var structCount = ReadUInt32(data, 20);
        var dataOffset = ReadUInt32(data, 24);
        var dataStart = checked((int)dataOffset);
        var structs = ReadStructs(data, structCount);

        var area = Field(structs, 0, AreaPathfinding);
        var areaGrid = InlineStruct(area, structs.Count, "ENV_AREA_PATHFINDING_EXPORT");
        var gridField = Field(structs, areaGrid.StructIndex, AreaGrid);
        var navigation = InlineStruct(gridField, structs.Count, "AREAGRID_AREA");
        var baseOffset = areaGrid.BaseOffset + navigation.BaseOffset;

        var columns = ReadInt32(data, dataStart + baseOffset +
                                      Field(structs, navigation.StructIndex, GridColumns).Offset);
        var rows = ReadInt32(data, dataStart + baseOffset +
                                   Field(structs, navigation.StructIndex, GridRows).Offset);
        var cellSize = ReadSingle(data, dataStart + baseOffset +
                                        Field(structs, navigation.StructIndex, GridCellSize).Offset);
        var baseField = Field(structs, navigation.StructIndex, GridBasePosition);
        var basePosition = dataStart + baseOffset + baseField.Offset;
        var baseX = ReadSingle(data, basePosition);
        var baseY = ReadSingle(data, basePosition + 4);
        var gridData = Field(structs, navigation.StructIndex, GridData);
        if ((gridData.Flags & ListFlag) == 0)
            throw new InvalidDataException("DAO AREAGRID_DATA is not a primitive list");
        var listReference = ReadInt32(data, dataStart + baseOffset + gridData.Offset);
        if (listReference < 0) return null;
        var listPosition = checked(dataStart + listReference);
        var count = ReadInt32(data, listPosition);
        var expected = checked(columns * rows);
        if (columns <= 0 || rows <= 0 || cellSize <= 0 || count != expected ||
            listPosition + 4 + count > data.Length)
            throw new InvalidDataException($"Invalid DAO navigation grid in {path}");
        var accessibility = data.AsSpan(listPosition + 4, count).ToArray();
        return new AuthoredNavigationGrid(path, baseX, baseY, columns, rows, cellSize, accessibility);
    }

    private static List<StructDefinition> ReadStructs(byte[] data, uint count)
    {
        if (count == 0 || count > 256 || 28L + count * 16 > data.Length)
            throw new InvalidDataException("Invalid DAO ARL structure table");
        var result = new List<StructDefinition>((int)count);
        for (var index = 0; index < count; index++)
        {
            var position = 28 + index * 16;
            var fieldCount = ReadUInt32(data, position + 4);
            var fieldOffset = ReadUInt32(data, position + 8);
            if (fieldCount > 4096 || fieldOffset + fieldCount * 12 > data.Length)
                throw new InvalidDataException("Invalid DAO ARL field table");
            var fields = new List<FieldDefinition>((int)fieldCount);
            for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                var fieldPosition = checked((int)fieldOffset + fieldIndex * 12);
                fields.Add(new FieldDefinition(ReadUInt32(data, fieldPosition),
                    ReadUInt16(data, fieldPosition + 4), ReadUInt16(data, fieldPosition + 6),
                    checked((int)ReadUInt32(data, fieldPosition + 8))));
            }
            result.Add(new StructDefinition(fields));
        }
        return result;
    }

    private static FieldDefinition Field(IReadOnlyList<StructDefinition> structs, int index, uint label) =>
        structs[index].Fields.FirstOrDefault(field => field.Label == label) ??
        throw new InvalidDataException($"DAO ARL field {label} is missing");

    private static StructReference InlineStruct(FieldDefinition field, int structCount, string name)
    {
        if ((field.Flags & StructFlag) == 0 || field.Type >= structCount)
            throw new InvalidDataException($"DAO ARL {name} is not an inline structure");
        return new StructReference(field.Type, checked((int)field.Offset));
    }

    private static ushort ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    private static uint ReadUInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    private static int ReadInt32(byte[] data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
    private static float ReadSingle(byte[] data, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(data, offset));

    private sealed record StructDefinition(IReadOnlyList<FieldDefinition> Fields);
    private sealed record FieldDefinition(uint Label, ushort Type, ushort Flags, int Offset);
    private readonly record struct StructReference(int StructIndex, int BaseOffset);
}
