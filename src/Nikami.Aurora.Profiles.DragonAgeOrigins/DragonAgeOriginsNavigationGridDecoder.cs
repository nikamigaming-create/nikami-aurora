using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeNavigationGridContract(
    string PayloadSha256,
    string AccessibilitySha256,
    float BaseX,
    float BaseY,
    int Columns,
    int Rows,
    float CellSize,
    int WalkableCells,
    int NonWalkableCells)
{
    public bool Ready => Columns > 0 && Rows > 0 && CellSize > 0 && WalkableCells > 0;
}

/// <summary>
/// Strict, source-bound decoder for the DAO AREAGRID_AREA contract used by
/// installed PC ARL files. It exposes navigation as a boot prerequisite only;
/// it does not claim that an arrival point, camera line of sight, or transition
/// has been exercised at runtime.
/// </summary>
public static class DragonAgeOriginsNavigationGridDecoder
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

    public static DragonAgeNavigationGridContract Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 28 ||
            Encoding.ASCII.GetString(payload[..16]) != "GFF V4.0PC  ARL ")
            throw new InvalidDataException("DAO navigation requires a PC GFF V4.0 ARL payload.");

        var structCount = ReadUInt32(payload, 20);
        var dataStart = checked((int)ReadUInt32(payload, 24));
        var structs = ReadStructs(payload, structCount);
        if (dataStart < 28 || dataStart > payload.Length)
            throw new InvalidDataException("DAO navigation data offset is outside the payload.");

        var pathfinding = Field(structs, 0, AreaPathfinding);
        var pathfindingStruct = InlineStruct(pathfinding, structs.Count,
            "ENV_AREA_PATHFINDING_EXPORT");
        var grid = Field(structs, pathfindingStruct.StructIndex, AreaGrid);
        var gridStruct = InlineStruct(grid, structs.Count, "AREAGRID_AREA");
        var baseOffset = checked(pathfindingStruct.BaseOffset + gridStruct.BaseOffset);

        var columns = ReadInt32(payload, DataPosition(payload, dataStart, baseOffset,
            Field(structs, gridStruct.StructIndex, GridColumns).Offset, sizeof(int)));
        var rows = ReadInt32(payload, DataPosition(payload, dataStart, baseOffset,
            Field(structs, gridStruct.StructIndex, GridRows).Offset, sizeof(int)));
        var cellSize = ReadSingle(payload, DataPosition(payload, dataStart, baseOffset,
            Field(structs, gridStruct.StructIndex, GridCellSize).Offset, sizeof(float)));

        var baseField = Field(structs, gridStruct.StructIndex, GridBasePosition);
        var basePosition = DataPosition(payload, dataStart, baseOffset, baseField.Offset,
            sizeof(float) * 2);
        var baseX = ReadSingle(payload, basePosition);
        var baseY = ReadSingle(payload, basePosition + sizeof(float));
        if (columns <= 0 || rows <= 0 || !float.IsFinite(cellSize) || cellSize <= 0 ||
            !float.IsFinite(baseX) || !float.IsFinite(baseY))
            throw new InvalidDataException("DAO navigation dimensions or coordinate basis are invalid.");

        var expected = checked(columns * rows);
        var dataField = Field(structs, gridStruct.StructIndex, GridData);
        if ((dataField.Flags & ListFlag) == 0)
            throw new InvalidDataException("DAO navigation accessibility is not a primitive list.");
        var referencePosition = DataPosition(payload, dataStart, baseOffset, dataField.Offset,
            sizeof(int));
        var listReference = ReadInt32(payload, referencePosition);
        if (listReference < 0)
            throw new InvalidDataException("DAO navigation accessibility list is absent.");
        var listPosition = checked(dataStart + listReference);
        RequireRange(payload, listPosition, sizeof(int));
        var count = ReadInt32(payload, listPosition);
        if (count != expected)
            throw new InvalidDataException("DAO navigation accessibility count does not match its grid.");
        var accessibilityPosition = checked(listPosition + sizeof(int));
        RequireRange(payload, accessibilityPosition, count);
        var accessibility = payload.Slice(accessibilityPosition, count);
        var walkable = 0;
        foreach (var value in accessibility)
            if (value == 1) walkable++;

        return new DragonAgeNavigationGridContract(
            Hex(SHA256.HashData(payload)), Hex(SHA256.HashData(accessibility)),
            baseX, baseY, columns, rows, cellSize, walkable, count - walkable);
    }

    private static IReadOnlyList<StructDefinition> ReadStructs(
        ReadOnlySpan<byte> payload, uint count)
    {
        if (count == 0 || count > 256 || 28L + count * 16 > payload.Length)
            throw new InvalidDataException("DAO navigation structure table is invalid.");
        var result = new List<StructDefinition>((int)count);
        for (var index = 0; index < count; index++)
        {
            var position = checked(28 + index * 16);
            var fieldCount = ReadUInt32(payload, position + 4);
            var fieldOffset = ReadUInt32(payload, position + 8);
            if (fieldCount > 4096 || fieldOffset + fieldCount * 12L > payload.Length)
                throw new InvalidDataException("DAO navigation field table is invalid.");
            var fields = new List<FieldDefinition>((int)fieldCount);
            for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                var fieldPosition = checked((int)fieldOffset + fieldIndex * 12);
                fields.Add(new FieldDefinition(ReadUInt32(payload, fieldPosition),
                    ReadUInt16(payload, fieldPosition + 4),
                    ReadUInt16(payload, fieldPosition + 6),
                    checked((int)ReadUInt32(payload, fieldPosition + 8))));
            }
            result.Add(new StructDefinition(fields));
        }
        return result;
    }

    private static FieldDefinition Field(IReadOnlyList<StructDefinition> structs,
        int structIndex, uint label)
    {
        if ((uint)structIndex >= (uint)structs.Count)
            throw new InvalidDataException("DAO navigation structure reference is invalid.");
        return structs[structIndex].Fields.FirstOrDefault(field => field.Label == label) ??
               throw new InvalidDataException($"DAO navigation field {label} is absent.");
    }

    private static StructReference InlineStruct(FieldDefinition field, int structCount,
        string name)
    {
        if ((field.Flags & StructFlag) == 0 || field.Type >= structCount)
            throw new InvalidDataException($"DAO navigation {name} is not an inline structure.");
        return new StructReference(field.Type, field.Offset);
    }

    private static int DataPosition(ReadOnlySpan<byte> payload, int dataStart,
        int baseOffset, int fieldOffset, int length)
    {
        var position = checked(dataStart + baseOffset + fieldOffset);
        RequireRange(payload, position, length);
        return position;
    }

    private static void RequireRange(ReadOnlySpan<byte> payload, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > payload.Length)
            throw new InvalidDataException("DAO navigation field extends beyond the payload.");
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> payload, int offset)
    {
        RequireRange(payload, offset, sizeof(ushort));
        return BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> payload, int offset)
    {
        RequireRange(payload, offset, sizeof(uint));
        return BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(offset, sizeof(uint)));
    }

    private static int ReadInt32(ReadOnlySpan<byte> payload, int offset)
    {
        RequireRange(payload, offset, sizeof(int));
        return BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, sizeof(int)));
    }

    private static float ReadSingle(ReadOnlySpan<byte> payload, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(payload, offset));

    private static string Hex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(bytes).ToLowerInvariant();

    private sealed record StructDefinition(IReadOnlyList<FieldDefinition> Fields);
    private sealed record FieldDefinition(uint Label, ushort Type, ushort Flags, int Offset);
    private readonly record struct StructReference(int StructIndex, int BaseOffset);
}
