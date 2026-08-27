// Matthew W, 2026-08-12

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

internal sealed class ScaleformGlyph
{
    public List<List<(int X, int Y, bool OnCurve)>> Contours { get; } = [];

    public int Advance { get; set; }

    public ushort Code { get; set; }
}

internal sealed class ScaleformFont
{
    private const int SwfUnitsPerEm = 20480;
    private const int TargetUnitsPerEm = 2048;

    private byte[] bytes = [];

    public string Name { get; private set; } = string.Empty;

    public List<ScaleformGlyph> Glyphs { get; } = [];

    public int Ascent { get; private set; } = 16384;

    public int Descent { get; private set; } = 4096;

    public static ScaleformFont? Extract(ErfArchive archive, string member, string wanted)
    {
        foreach (var font in ReadAll(archive.Read(member)))
        {
            if (font.Name.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }
        }

        return null;
    }

    public static List<ScaleformFont> ReadAll(byte[] stored)
    {
        var fonts = new List<ScaleformFont>();
        if (stored.Length < 9)
        {
            return fonts;
        }

        var declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(4, 4));
        byte[] body;
        if (stored[0] == 'C')
        {
            body = new byte[declared];
            stored.AsSpan(0, 8).CopyTo(body);
            using var source = new MemoryStream(stored, 8, stored.Length - 8);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            try
            {
                zlib.ReadExactly(body.AsSpan(8, declared - 8));
            }
            catch (EndOfStreamException)
            {
            }
        }
        else
        {
            body = stored.AsSpan(0, Math.Min(declared, stored.Length)).ToArray();
        }

        var cursor = 8;
        var rectBits = body[cursor] >> 3;
        cursor += (5 + (rectBits * 4) + 7) / 8;
        cursor += 4;

        while (cursor + 2 <= body.Length)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(cursor, 2));
            cursor += 2;
            var code = packed >> 6;
            var length = packed & 0x3F;
            if (length == 0x3F)
            {
                if (cursor + 4 > body.Length)
                {
                    break;
                }

                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(cursor, 4));
                cursor += 4;
            }

            if (code == 0 || cursor + length > body.Length)
            {
                break;
            }

            if (code == 75)
            {
                var font = new ScaleformFont { bytes = body };
                if (font.ReadDefineFont3(cursor, cursor + length))
                {
                    fonts.Add(font);
                }
            }

            cursor += length;
        }

        return fonts;
    }

    private bool ReadDefineFont3(int start, int end)
    {
        var cursor = start + 2;
        var flags = bytes[cursor++];
        var hasLayout = (flags & 0x80) != 0;
        var wideOffsets = (flags & 0x08) != 0;
        cursor++;

        int nameLength = bytes[cursor++];
        if (cursor + nameLength > end)
        {
            return false;
        }

        Name = Encoding.UTF8.GetString(bytes, cursor, nameLength).TrimEnd('\0');
        cursor += nameLength;

        if (cursor + 2 > end)
        {
            return false;
        }

        int glyphCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
        cursor += 2;
        if (glyphCount == 0)
        {
            return false;
        }

        var tableStart = cursor;
        var offsets = new int[glyphCount + 1];
        for (var index = 0; index <= glyphCount; index++)
        {
            if (wideOffsets)
            {
                offsets[index] = (int)BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(tableStart + (index * 4), 4));
            }
            else
            {
                offsets[index] = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(tableStart + (index * 2), 2));
            }
        }

        for (var index = 0; index < glyphCount; index++)
        {
            var glyph = new ScaleformGlyph();
            var from = tableStart + offsets[index];
            var to = tableStart + offsets[index + 1];
            if (from < end && to <= end && from < to)
            {
                ReadGlyphShape(from, to, glyph);
            }

            Glyphs.Add(glyph);
        }

        cursor = tableStart + offsets[glyphCount];
        for (var index = 0; index < glyphCount && cursor + 2 <= end; index++)
        {
            Glyphs[index].Code = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
        }

        if (hasLayout && cursor + 6 <= end)
        {
            Ascent = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(cursor, 2));
            Descent = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(cursor + 2, 2));
            cursor += 6;
            for (var index = 0; index < glyphCount && cursor + 2 <= end; index++)
            {
                Glyphs[index].Advance = BinaryPrimitives.ReadInt16LittleEndian(
                    bytes.AsSpan(cursor, 2));
                cursor += 2;
            }
        }

        return true;
    }

    private void ReadGlyphShape(int start, int end, ScaleformGlyph glyph)
    {
        var reader = new ShapeBits(bytes, start);
        var fillBits = (int)reader.Read(4);
        var lineBits = (int)reader.Read(4);

        var x = 0;
        var y = 0;
        List<(int X, int Y, bool OnCurve)>? contour = null;

        void Close()
        {
            if (contour is { Count: >= 3 })
            {
                glyph.Contours.Add(contour);
            }

            contour = null;
        }

        for (var guard = 0; guard < 65536; guard++)
        {
            if (reader.BytePosition >= end)
            {
                break;
            }

            if (reader.Read(1) == 0)
            {
                var change = reader.Read(5);
                if (change == 0)
                {
                    break;
                }

                if ((change & 0x01) != 0)
                {
                    var count = (int)reader.Read(5);
                    Close();
                    x = reader.ReadSigned(count);
                    y = reader.ReadSigned(count);
                    contour = [(x, y, true)];
                }

                if ((change & 0x02) != 0)
                {
                    reader.Read(fillBits);
                }

                if ((change & 0x04) != 0)
                {
                    reader.Read(fillBits);
                }

                if ((change & 0x08) != 0)
                {
                    reader.Read(lineBits);
                }

                if ((change & 0x10) != 0)
                {
                    break;
                }
            }
            else if (reader.Read(1) != 0)
            {
                var count = (int)reader.Read(4) + 2;
                if (reader.Read(1) != 0)
                {
                    x += reader.ReadSigned(count);
                    y += reader.ReadSigned(count);
                }
                else if (reader.Read(1) != 0)
                {
                    y += reader.ReadSigned(count);
                }
                else
                {
                    x += reader.ReadSigned(count);
                }

                contour?.Add((x, y, true));
            }
            else
            {
                var count = (int)reader.Read(4) + 2;
                var controlX = x + reader.ReadSigned(count);
                var controlY = y + reader.ReadSigned(count);
                x = controlX + reader.ReadSigned(count);
                y = controlY + reader.ReadSigned(count);
                contour?.Add((controlX, controlY, false));
                contour?.Add((x, y, true));
            }
        }

        Close();
    }

    public byte[] ToTrueType()
    {
        var scale = (double)TargetUnitsPerEm / SwfUnitsPerEm;

        var glyphs = new List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)>
        {
            ([], TargetUnitsPerEm / 2, 0, 0, 0, 0, 0)
        };

        var maxPoints = 0;
        var maxContours = 0;

        foreach (var source in Glyphs)
        {
            var contours = new List<List<(int X, int Y, bool OnCurve)>>();
            foreach (var raw in source.Contours)
            {
                var mapped = raw
                    .Select(p => ((int)Math.Round(p.X * scale), (int)Math.Round(-p.Y * scale), p.OnCurve))
                    .ToList();
                contours.Add(mapped);
            }

            var built = BuildGlyph(contours, out var box);
            maxPoints = Math.Max(maxPoints, contours.Sum(c => c.Count));
            maxContours = Math.Max(maxContours, contours.Count);
            glyphs.Add((
                built,
                (int)Math.Round(source.Advance * scale),
                source.Code,
                box.XMin, box.YMin, box.XMax, box.YMax));
        }

        var glyf = new MemoryStream();
        var loca = new List<uint>();
        foreach (var glyph in glyphs)
        {
            loca.Add((uint)glyf.Length);
            glyf.Write(glyph.Outline);
            while (glyf.Length % 4 != 0)
            {
                glyf.WriteByte(0);
            }
        }

        loca.Add((uint)glyf.Length);

        var count = glyphs.Count;
        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Head(glyphs),
            ["hhea"] = Hhea(glyphs),
            ["maxp"] = Maxp(count, maxPoints, maxContours),
            ["hmtx"] = Hmtx(glyphs),
            ["cmap"] = Cmap(glyphs),
            ["loca"] = Loca(loca),
            ["glyf"] = glyf.ToArray(),
            ["name"] = NameTable(),
            ["post"] = Post(),
            ["OS/2"] = Os2(glyphs)
        };

        return Assemble(tables);
    }

    private static byte[] BuildGlyph(
        List<List<(int X, int Y, bool OnCurve)>> contours,
        out (short XMin, short YMin, short XMax, short YMax) box)
    {
        box = (0, 0, 0, 0);
        contours.RemoveAll(c => c.Count < 2);
        if (contours.Count == 0)
        {
            return [];
        }

        int xMin = int.MaxValue, yMin = int.MaxValue, xMax = int.MinValue, yMax = int.MinValue;
        foreach (var point in contours.SelectMany(c => c))
        {
            xMin = Math.Min(xMin, point.X);
            yMin = Math.Min(yMin, point.Y);
            xMax = Math.Max(xMax, point.X);
            yMax = Math.Max(yMax, point.Y);
        }

        box = ((short)xMin, (short)yMin, (short)xMax, (short)yMax);

        var output = new MemoryStream();
        WriteInt16(output, (short)contours.Count);
        WriteInt16(output, (short)xMin);
        WriteInt16(output, (short)yMin);
        WriteInt16(output, (short)xMax);
        WriteInt16(output, (short)yMax);

        var total = 0;
        foreach (var contour in contours)
        {
            total += contour.Count;
            WriteUInt16(output, (ushort)(total - 1));
        }

        WriteUInt16(output, 0);

        foreach (var point in contours.SelectMany(c => c))
        {
            output.WriteByte((byte)(point.OnCurve ? 0x01 : 0x00));
        }

        var previous = 0;
        foreach (var point in contours.SelectMany(c => c))
        {
            WriteInt16(output, (short)(point.X - previous));
            previous = point.X;
        }

        previous = 0;
        foreach (var point in contours.SelectMany(c => c))
        {
            WriteInt16(output, (short)(point.Y - previous));
            previous = point.Y;
        }

        return output.ToArray();
    }

    private byte[] Head(List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)> glyphs)
    {
        var stream = new MemoryStream();
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0x00010000);
        WriteUInt32(stream, 0);
        WriteUInt32(stream, 0x5F0F3CF5);
        WriteUInt16(stream, 0x000B);
        WriteUInt16(stream, TargetUnitsPerEm);
        for (var index = 0; index < 4; index++)
        {
            WriteUInt32(stream, 0);
        }

        WriteInt16(stream, glyphs.Min(g => g.XMin));
        WriteInt16(stream, glyphs.Min(g => g.YMin));
        WriteInt16(stream, glyphs.Max(g => g.XMax));
        WriteInt16(stream, glyphs.Max(g => g.YMax));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 8);
        WriteInt16(stream, 2);
        WriteInt16(stream, 1);
        WriteInt16(stream, 0);
        return stream.ToArray();
    }

    private byte[] Hhea(List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)> glyphs)
    {
        var scale = (double)TargetUnitsPerEm / SwfUnitsPerEm;
        var stream = new MemoryStream();
        WriteUInt32(stream, 0x00010000);
        WriteInt16(stream, (short)Math.Round(Ascent * scale));
        WriteInt16(stream, (short)-Math.Round(Descent * scale));
        WriteInt16(stream, 0);
        WriteUInt16(stream, (ushort)glyphs.Max(g => Math.Max(0, g.Advance)));
        WriteInt16(stream, glyphs.Min(g => g.XMin));
        WriteInt16(stream, 0);
        WriteInt16(stream, glyphs.Max(g => g.XMax));
        WriteInt16(stream, 1);
        WriteInt16(stream, 0);
        WriteInt16(stream, 0);
        for (var index = 0; index < 4; index++)
        {
            WriteInt16(stream, 0);
        }

        WriteInt16(stream, 0);
        WriteUInt16(stream, (ushort)glyphs.Count);
        return stream.ToArray();
    }

    private static byte[] Maxp(int count, int maxPoints, int maxContours)
    {
        var stream = new MemoryStream();
        WriteUInt32(stream, 0x00010000);
        WriteUInt16(stream, (ushort)count);
        WriteUInt16(stream, (ushort)Math.Max(maxPoints, 1));
        WriteUInt16(stream, (ushort)Math.Max(maxContours, 1));
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 2);
        for (var index = 0; index < 7; index++)
        {
            WriteUInt16(stream, 0);
        }

        return stream.ToArray();
    }

    private static byte[] Hmtx(List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)> glyphs)
    {
        var stream = new MemoryStream();
        foreach (var glyph in glyphs)
        {
            WriteUInt16(stream, (ushort)Math.Clamp(glyph.Advance, 0, ushort.MaxValue));
            WriteInt16(stream, glyph.XMin);
        }

        return stream.ToArray();
    }

    private static byte[] Cmap(List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)> glyphs)
    {
        var mapping = new SortedDictionary<ushort, ushort>();
        for (var index = 1; index < glyphs.Count; index++)
        {
            var code = glyphs[index].Code;
            if (code != 0)
            {
                mapping.TryAdd(code, (ushort)index);
            }
        }

        var segments = new List<(ushort Start, ushort End, ushort Glyph)>();
        foreach (var (code, glyph) in mapping)
        {
            if (segments.Count > 0)
            {
                var last = segments[^1];
                if (code == last.End + 1 && glyph == last.Glyph + (code - last.Start))
                {
                    segments[^1] = (last.Start, code, last.Glyph);
                    continue;
                }
            }

            segments.Add((code, code, glyph));
        }

        segments.Add((0xFFFF, 0xFFFF, 0));

        var subtable = new MemoryStream();
        var segmentCount = segments.Count;
        var searchRange = 2 * (int)Math.Pow(2, Math.Floor(Math.Log2(segmentCount)));
        WriteUInt16(subtable, 4);
        WriteUInt16(subtable, (ushort)(16 + (8 * segmentCount)));
        WriteUInt16(subtable, 0);
        WriteUInt16(subtable, (ushort)(segmentCount * 2));
        WriteUInt16(subtable, (ushort)searchRange);
        WriteUInt16(subtable, (ushort)Math.Log2(searchRange / 2));
        WriteUInt16(subtable, (ushort)((segmentCount * 2) - searchRange));

        foreach (var segment in segments)
        {
            WriteUInt16(subtable, segment.End);
        }

        WriteUInt16(subtable, 0);
        foreach (var segment in segments)
        {
            WriteUInt16(subtable, segment.Start);
        }

        foreach (var segment in segments)
        {
            WriteUInt16(subtable, segment.Start == 0xFFFF
                ? (ushort)1
                : (ushort)(segment.Glyph - segment.Start));
        }

        foreach (var _ in segments)
        {
            WriteUInt16(subtable, 0);
        }

        var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 3);
        WriteUInt16(stream, 1);
        WriteUInt32(stream, 12);
        stream.Write(subtable.ToArray());
        return stream.ToArray();
    }

    private static byte[] Loca(List<uint> offsets)
    {
        var stream = new MemoryStream();
        foreach (var offset in offsets)
        {
            WriteUInt32(stream, offset);
        }

        return stream.ToArray();
    }

    private byte[] NameTable()
    {
        var family = Name.Trim();
        string[] values = [family, "Regular", family, family.Replace(" ", string.Empty)];
        ushort[] ids = [1, 2, 4, 6];

        var storage = new MemoryStream();
        var records = new List<(ushort Id, ushort Offset, ushort Length)>();
        for (var index = 0; index < values.Length; index++)
        {
            var encoded = Encoding.BigEndianUnicode.GetBytes(values[index]);
            records.Add((ids[index], (ushort)storage.Length, (ushort)encoded.Length));
            storage.Write(encoded);
        }

        var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, (ushort)records.Count);
        WriteUInt16(stream, (ushort)(6 + (12 * records.Count)));
        foreach (var record in records)
        {
            WriteUInt16(stream, 3);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 0x0409);
            WriteUInt16(stream, record.Id);
            WriteUInt16(stream, record.Length);
            WriteUInt16(stream, record.Offset);
        }

        stream.Write(storage.ToArray());
        return stream.ToArray();
    }

    private static byte[] Post()
    {
        var stream = new MemoryStream();
        WriteUInt32(stream, 0x00030000);
        WriteUInt32(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt32(stream, 0);
        for (var index = 0; index < 4; index++)
        {
            WriteUInt32(stream, 0);
        }

        return stream.ToArray();
    }

    private byte[] Os2(List<(byte[] Outline, int Advance, ushort Code, short XMin, short YMin, short XMax, short YMax)> glyphs)
    {
        var scale = (double)TargetUnitsPerEm / SwfUnitsPerEm;
        var ascender = (short)Math.Round(Ascent * scale);
        var descender = (short)Math.Round(Descent * scale);
        var codes = glyphs.Skip(1).Select(g => g.Code).Where(c => c != 0).ToList();

        var stream = new MemoryStream();
        WriteUInt16(stream, 4);
        WriteInt16(stream, (short)(glyphs.Count > 1 ? glyphs.Skip(1).Average(g => g.Advance) : 0));
        WriteUInt16(stream, 400);
        WriteUInt16(stream, 5);
        WriteUInt16(stream, 0);
        WriteInt16(stream, 1331);
        WriteInt16(stream, 1433);
        WriteInt16(stream, 0);
        WriteInt16(stream, 287);
        WriteInt16(stream, 1331);
        WriteInt16(stream, 1433);
        WriteInt16(stream, 0);
        WriteInt16(stream, 983);
        WriteInt16(stream, 102);
        WriteInt16(stream, 530);
        WriteInt16(stream, 0);
        for (var index = 0; index < 10; index++)
        {
            stream.WriteByte(0);
        }

        for (var index = 0; index < 4; index++)
        {
            WriteUInt32(stream, index == 0 ? 1u : 0u);
        }

        stream.Write("OPDA"u8);
        WriteUInt16(stream, 0x0040);
        WriteUInt16(stream, codes.Count > 0 ? codes.Min() : (ushort)32);
        WriteUInt16(stream, codes.Count > 0 ? codes.Max() : (ushort)126);
        WriteInt16(stream, ascender);
        WriteInt16(stream, (short)-descender);
        WriteInt16(stream, 0);
        WriteUInt16(stream, (ushort)ascender);
        WriteUInt16(stream, (ushort)descender);
        WriteUInt32(stream, 1);
        WriteUInt32(stream, 0);
        WriteInt16(stream, (short)(TargetUnitsPerEm / 4));
        WriteInt16(stream, (short)(TargetUnitsPerEm / 2));
        WriteUInt16(stream, 32);
        WriteUInt16(stream, 32);
        WriteUInt16(stream, 2);
        return stream.ToArray();
    }

    private static byte[] Assemble(Dictionary<string, byte[]> tables)
    {
        var ordered = tables.OrderBy(t => t.Key, StringComparer.Ordinal).ToList();
        var count = ordered.Count;
        var searchRange = 16 * (int)Math.Pow(2, Math.Floor(Math.Log2(count)));

        var directoryLength = 12 + (16 * count);
        var offset = directoryLength;
        var placed = new List<(string Tag, byte[] Data, int Offset)>();
        foreach (var (tag, data) in ordered)
        {
            placed.Add((tag, data, offset));
            offset += data.Length;
            offset = (offset + 3) & ~3;
        }

        var file = new MemoryStream();
        WriteUInt32(file, 0x00010000);
        WriteUInt16(file, (ushort)count);
        WriteUInt16(file, (ushort)searchRange);
        WriteUInt16(file, (ushort)Math.Log2(searchRange / 16));
        WriteUInt16(file, (ushort)((16 * count) - searchRange));

        foreach (var (tag, data, at) in placed)
        {
            file.Write(Encoding.ASCII.GetBytes(tag));
            WriteUInt32(file, Checksum(data));
            WriteUInt32(file, (uint)at);
            WriteUInt32(file, (uint)data.Length);
        }

        foreach (var (_, data, at) in placed)
        {
            while (file.Length < at)
            {
                file.WriteByte(0);
            }

            file.Write(data);
        }

        while (file.Length % 4 != 0)
        {
            file.WriteByte(0);
        }

        return file.ToArray();
    }

    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (var index = 0; index + 3 < data.Length; index += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(index, 4));
        }

        var remainder = data.Length & ~3;
        if (remainder < data.Length)
        {
            uint tail = 0;
            for (var index = 0; index < 4; index++)
            {
                tail = (tail << 8) | (remainder + index < data.Length ? data[remainder + index] : 0u);
            }

            sum += tail;
        }

        return sum;
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void WriteUInt16(Stream stream, int value) => WriteUInt16(stream, (ushort)value);

    private static void WriteInt16(Stream stream, short value) =>
        WriteUInt16(stream, unchecked((ushort)value));

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private struct ShapeBits(byte[] source, int startByte)
    {
        private readonly byte[] source = source;
        private int bit = startByte * 8;

        public readonly int BytePosition => (bit + 7) / 8;

        public uint Read(int count)
        {
            uint value = 0;
            for (var index = 0; index < count; index++)
            {
                if (bit / 8 >= source.Length)
                {
                    return value;
                }

                value = (value << 1) | (uint)((source[bit / 8] >> (7 - (bit % 8))) & 1);
                bit++;
            }

            return value;
        }

        public int ReadSigned(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var value = Read(count);
            if (count < 32 && (value & (1u << (count - 1))) != 0)
            {
                value |= uint.MaxValue << count;
            }

            return unchecked((int)value);
        }
    }
}
