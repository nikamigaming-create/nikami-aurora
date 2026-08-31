using System.Buffers.Binary;
using System.Text;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

internal sealed class ClassicGff32RootReader
{
    private const uint IntType = 5;
    private const uint ExoLocStringType = 12;

    private readonly byte[] data;
    private readonly uint fieldOffset;
    private readonly uint fieldCount;
    private readonly uint labelOffset;
    private readonly uint labelCount;
    private readonly uint fieldDataOffset;
    private readonly uint fieldIndicesOffset;
    private readonly uint rootFieldOffset;
    private readonly uint rootFieldCount;

    public ClassicGff32RootReader(byte[] data)
    {
        this.data = data;
        if (data.Length < 56 || Encoding.ASCII.GetString(data, 4, 4) != "V3.2")
            throw new InvalidDataException("classic-gff-v32-required");

        var structOffset = UInt(8);
        var structCount = UInt(12);
        fieldOffset = UInt(16);
        fieldCount = UInt(20);
        labelOffset = UInt(24);
        labelCount = UInt(28);
        fieldDataOffset = UInt(32);
        var fieldDataCount = UInt(36);
        fieldIndicesOffset = UInt(40);
        var fieldIndicesCount = UInt(44);
        var listIndicesOffset = UInt(48);
        var listIndicesCount = UInt(52);
        if (structCount == 0 || fieldCount > 1_000_000 || labelCount > 1_000_000 ||
            !Range(structOffset, checked(structCount * 12)) ||
            !Range(fieldOffset, checked(fieldCount * 12)) ||
            !Range(labelOffset, checked(labelCount * 16)) ||
            !Range(fieldDataOffset, fieldDataCount) ||
            !Range(fieldIndicesOffset, fieldIndicesCount) ||
            !Range(listIndicesOffset, listIndicesCount))
            throw new InvalidDataException("classic-gff-section-invalid");

        rootFieldOffset = UInt(structOffset + 4);
        rootFieldCount = UInt(structOffset + 8);
        if (rootFieldCount > fieldCount)
            throw new InvalidDataException("classic-gff-root-field-count-invalid");
    }

    public bool TryReadInt32(string label, out int value)
    {
        if (TryFind(label, out var type, out var raw) && type == IntType)
        {
            value = unchecked((int)raw);
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryReadLocStringReference(string label, out int value)
    {
        if (!TryFind(label, out var type, out var raw) || type != ExoLocStringType)
        {
            value = -1;
            return false;
        }

        var valueAt = checked(fieldDataOffset + raw);
        if (!Range(valueAt, 12))
        {
            value = -1;
            return false;
        }

        var rawValue = UInt(valueAt + 4);
        value = rawValue == uint.MaxValue ? -1 : checked((int)rawValue);
        return value >= 0;
    }

    private bool TryFind(string wantedLabel, out uint type, out uint raw)
    {
        for (uint ordinal = 0; ordinal < rootFieldCount; ordinal++)
        {
            uint fieldIndex;
            if (rootFieldCount == 1)
            {
                fieldIndex = rootFieldOffset;
            }
            else
            {
                var indexAt = checked(fieldIndicesOffset + rootFieldOffset + ordinal * 4);
                if (!Range(indexAt, 4)) break;
                fieldIndex = UInt(indexAt);
            }

            var fieldAt = checked(fieldOffset + fieldIndex * 12);
            if (fieldIndex >= fieldCount || !Range(fieldAt, 12)) break;
            var labelIndex = UInt(fieldAt + 4);
            if (labelIndex >= labelCount) continue;
            var labelAt = checked(labelOffset + labelIndex * 16);
            if (!Range(labelAt, 16)) break;
            var label = Encoding.ASCII.GetString(data, checked((int)labelAt), 16).TrimEnd('\0');
            if (!label.Equals(wantedLabel, StringComparison.Ordinal)) continue;
            type = UInt(fieldAt);
            raw = UInt(fieldAt + 8);
            return true;
        }

        type = 0;
        raw = 0;
        return false;
    }

    private uint UInt(uint offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(checked((int)offset), 4));

    private bool Range(uint offset, uint length) =>
        offset <= data.Length && length <= data.Length - offset;
}
