// Matthew W, 2026-08-12

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

internal readonly record struct GfxMatrix(
    double ScaleX,
    double RotateSkew0,
    double RotateSkew1,
    double ScaleY,
    double TranslateX,
    double TranslateY)
{
    public static readonly GfxMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public GfxMatrix Concat(GfxMatrix parent) => new(
        (ScaleX * parent.ScaleX) + (RotateSkew0 * parent.RotateSkew1),
        (ScaleX * parent.RotateSkew0) + (RotateSkew0 * parent.ScaleY),
        (RotateSkew1 * parent.ScaleX) + (ScaleY * parent.RotateSkew1),
        (RotateSkew1 * parent.RotateSkew0) + (ScaleY * parent.ScaleY),
        (TranslateX * parent.ScaleX) + (TranslateY * parent.RotateSkew1) + parent.TranslateX,
        (TranslateX * parent.RotateSkew0) + (TranslateY * parent.ScaleY) + parent.TranslateY);
}

internal readonly record struct GfxRect(double XMin, double XMax, double YMin, double YMax)
{
    public double Width => XMax - XMin;

    public double Height => YMax - YMin;
}

internal sealed record GfxExternalImage(ushort CharacterId, int Width, int Height, string Name);

internal sealed record GfxBitmapFill(ushort ImageCharacterId, GfxMatrix Matrix);

internal sealed record GfxShape(ushort CharacterId, GfxRect Bounds, IReadOnlyList<GfxBitmapFill> Fills);

internal sealed class GfxOp
{
    public bool Remove { get; init; }

    public ushort Depth { get; init; }

    public int CharacterId { get; init; } = -1;

    public GfxMatrix? Matrix { get; init; }

    public float? Alpha { get; init; }

    public int? Blend { get; init; }

    public float? Ratio { get; init; }

    public ushort ClipDepth { get; init; }

    public string? Name { get; init; }
}

internal sealed class GfxFrameAction
{
    public bool Stop { get; set; }

    public bool Play { get; set; }

    public int GotoFrame { get; set; } = -1;

    public string GotoLabel { get; set; } = string.Empty;
}

internal sealed class GfxTimeline
{
    public ushort CharacterId { get; init; }

    public List<List<GfxOp>> Frames { get; } = [];

    public List<GfxFrameAction> Actions { get; } = [];

    public Dictionary<string, int> Labels { get; } = new(StringComparer.Ordinal);
}

internal sealed class GfxMovie
{
    private const int HeaderLength = 8;
    private const int BlendMultiply = 3;

    private byte[] bytes = [];

    public GfxRect Stage { get; private set; }

    public double FrameRate { get; private set; }

    public GfxTimeline Root { get; } = new();

    public Dictionary<ushort, GfxTimeline> Sprites { get; } = [];

    public Dictionary<ushort, GfxShape> Shapes { get; } = [];

    public Dictionary<ushort, GfxExternalImage> Images { get; } = [];

    public HashSet<string> ActionStrings { get; } = new(StringComparer.Ordinal);

    public static GfxMovie Read(byte[] stored)
    {
        if (stored.Length < HeaderLength)
        {
            throw new InvalidDataException("The GFx header is truncated.");
        }

        var signature = Encoding.ASCII.GetString(stored, 0, 3);
        var compressed = signature == "CFX";
        if (!compressed && signature != "FWS" && signature != "GFX")
        {
            throw new InvalidDataException($"Unsupported GFx signature '{signature}'.");
        }

        var declared = (int)BinaryPrimitives.ReadUInt32LittleEndian(stored.AsSpan(4, 4));
        var movie = new GfxMovie();
        if (compressed)
        {
            var logical = new byte[declared];
            stored.AsSpan(0, HeaderLength).CopyTo(logical);
            using var source = new MemoryStream(stored, HeaderLength, stored.Length - HeaderLength);
            using var zlib = new ZLibStream(source, CompressionMode.Decompress);
            zlib.ReadExactly(logical.AsSpan(HeaderLength, declared - HeaderLength));
            movie.bytes = logical;
        }
        else
        {
            movie.bytes = stored.AsSpan(0, declared).ToArray();
        }

        var cursor = HeaderLength;
        movie.Stage = ReadRect(movie.bytes, ref cursor);
        movie.FrameRate = BinaryPrimitives.ReadUInt16LittleEndian(
            movie.bytes.AsSpan(cursor, 2)) / 256.0;
        cursor += 4;
        movie.ReadTagStream(cursor, movie.bytes.Length, movie.Root);
        return movie;
    }

    private void ReadTagStream(int start, int end, GfxTimeline timeline)
    {
        var cursor = start;
        timeline.Frames.Add([]);
        timeline.Actions.Add(new GfxFrameAction());

        while (cursor < end)
        {
            var packed = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
            var code = packed >> 6;
            var length = packed & 0x3F;
            if (length == 0x3F)
            {
                length = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(cursor, 4));
                cursor += 4;
            }

            var payloadStart = cursor;
            var payloadEnd = payloadStart + length;
            if (payloadEnd > end)
            {
                break;
            }

            switch (code)
            {
                case 0:
                    return;
                case 1:
                    timeline.Frames.Add([]);
                    timeline.Actions.Add(new GfxFrameAction());
                    break;
                case 2:
                case 22:
                case 32:
                    ReadShape(payloadStart, payloadEnd, code);
                    break;
                case 12:
                    ReadFrameAction(payloadStart, payloadEnd, timeline);
                    ExtractStrings(payloadStart, payloadEnd);
                    break;
                case 43:
                    timeline.Labels[ReadString(payloadStart, payloadEnd, out _)] =
                        timeline.Frames.Count - 1;
                    break;
                case 26:
                    timeline.Frames[^1].Add(ReadPlaceObject2(payloadStart, payloadEnd));
                    break;
                case 28:
                    timeline.Frames[^1].Add(new GfxOp
                    {
                        Remove = true,
                        Depth = BinaryPrimitives.ReadUInt16LittleEndian(
                            bytes.AsSpan(payloadStart, 2))
                    });
                    break;
                case 39:
                    ReadSprite(payloadStart, payloadEnd);
                    break;
                case 59:
                    if (length >= 2)
                    {
                        ExtractStrings(payloadStart + 2, payloadEnd);
                    }

                    break;
                case 70:
                    timeline.Frames[^1].Add(ReadPlaceObject3(payloadStart, payloadEnd));
                    break;
                case 1001:
                    ReadExternalImage(payloadStart, payloadEnd);
                    break;
            }

            cursor = payloadEnd;
        }
    }

    private void ReadSprite(int payloadStart, int payloadEnd)
    {
        var characterId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(payloadStart, 2));
        var timeline = new GfxTimeline { CharacterId = characterId };
        ReadTagStream(payloadStart + 4, payloadEnd, timeline);
        while (timeline.Frames.Count > 1 && timeline.Frames[^1].Count == 0)
        {
            timeline.Frames.RemoveAt(timeline.Frames.Count - 1);
            timeline.Actions.RemoveAt(timeline.Actions.Count - 1);
        }

        Sprites.TryAdd(characterId, timeline);
    }

    private void ReadFrameAction(int payloadStart, int payloadEnd, GfxTimeline timeline)
    {
        var action = timeline.Actions[^1];
        var cursor = payloadStart;
        while (cursor < payloadEnd)
        {
            var code = bytes[cursor++];
            if (code == 0)
            {
                break;
            }

            var size = 0;
            if (code >= 0x80)
            {
                if (cursor + 2 > payloadEnd)
                {
                    break;
                }

                size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
                cursor += 2;
                if (cursor + size > payloadEnd)
                {
                    break;
                }
            }

            switch (code)
            {
                case 0x07:
                    action.Stop = true;
                    break;
                case 0x06:
                    action.Play = true;
                    break;
                case 0x81 when size >= 2:
                    action.GotoFrame = BinaryPrimitives.ReadUInt16LittleEndian(
                        bytes.AsSpan(cursor, 2));
                    break;
                case 0x8C:
                    action.GotoLabel = ReadString(cursor, cursor + size, out _);
                    break;
            }

            cursor += size;
        }
    }

    private void ReadShape(int payloadStart, int payloadEnd, int tagCode)
    {
        var cursor = payloadStart;
        var characterId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
        cursor += 2;
        var bounds = ReadRect(bytes, ref cursor);
        var hasAlpha = tagCode >= 32;
        var extended = tagCode >= 22;
        var fills = new List<GfxBitmapFill>();

        var fillCount = ReadStyleCount(ref cursor, extended);
        for (var index = 0; index < fillCount && cursor < payloadEnd; index++)
        {
            var type = bytes[cursor++];
            if (type == 0x00)
            {
                cursor += hasAlpha ? 4 : 3;
            }
            else if (type is 0x10 or 0x12 or 0x13)
            {
                ReadMatrix(bytes, ref cursor);
                var stops = bytes[cursor++] & 0x0F;
                cursor += stops * (1 + (hasAlpha ? 4 : 3));
                if (type == 0x13)
                {
                    cursor += 2;
                }
            }
            else if (type is >= 0x40 and <= 0x43)
            {
                var image = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
                cursor += 2;
                var matrix = ReadMatrix(bytes, ref cursor);
                if (image != 0xFFFF)
                {
                    fills.Add(new GfxBitmapFill(image, matrix));
                }
            }
            else
            {
                break;
            }
        }

        Shapes.TryAdd(characterId, new GfxShape(characterId, bounds, fills));
    }

    private void ReadExternalImage(int payloadStart, int payloadEnd)
    {
        var cursor = payloadStart;
        var characterId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 4, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor + 6, 2));
        cursor += 8;
        var firstField = bytes[cursor++];
        var firstLength = bytes[cursor++];
        string name;
        if (cursor + firstLength == payloadEnd)
        {
            name = Fixed(cursor, firstLength);
        }
        else
        {
            cursor--;
            cursor += firstField;
            var nameLength = bytes[cursor++];
            name = Fixed(cursor, nameLength);
        }

        Images.TryAdd(characterId, new GfxExternalImage(characterId, width, height, name));
    }

    private GfxOp ReadPlaceObject2(int payloadStart, int payloadEnd)
    {
        var cursor = payloadStart;
        var flags = bytes[cursor++];
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
        cursor += 2;
        return ReadPlacementBody(flags, 0, depth, cursor, payloadEnd);
    }

    private GfxOp ReadPlaceObject3(int payloadStart, int payloadEnd)
    {
        var cursor = payloadStart;
        var flags = bytes[cursor++];
        var extendedFlags = bytes[cursor++];
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
        cursor += 2;
        if ((extendedFlags & 0x08) != 0 || ((extendedFlags & 0x10) != 0 && (flags & 0x02) != 0))
        {
            ReadString(cursor, payloadEnd, out cursor);
        }

        return ReadPlacementBody(flags, extendedFlags, depth, cursor, payloadEnd);
    }

    private GfxOp ReadPlacementBody(
        byte flags,
        byte extendedFlags,
        ushort depth,
        int cursor,
        int payloadEnd)
    {
        var characterId = -1;
        GfxMatrix? matrix = null;
        float? alpha = null;
        float? ratio = null;
        int? blend = null;
        ushort clipDepth = 0;
        string? name = null;

        if ((flags & 0x02) != 0)
        {
            characterId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
        }

        if ((flags & 0x04) != 0)
        {
            matrix = ReadMatrix(bytes, ref cursor);
        }

        if ((flags & 0x08) != 0)
        {
            alpha = ReadColorTransformAlpha(ref cursor);
        }

        if ((flags & 0x10) != 0)
        {
            ratio = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2)) / 65535.0f;
            cursor += 2;
        }

        if ((flags & 0x20) != 0)
        {
            name = ReadString(cursor, payloadEnd, out cursor);
        }

        if ((flags & 0x40) != 0)
        {
            clipDepth = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
        }

        if ((extendedFlags & 0x01) != 0)
        {
            cursor = SkipFilterList(cursor, payloadEnd);
        }

        if ((extendedFlags & 0x02) != 0 && cursor < payloadEnd)
        {
            blend = bytes[cursor] == BlendMultiply ? BlendMultiply : 0;
        }

        return new GfxOp
        {
            Depth = depth,
            CharacterId = characterId,
            Matrix = matrix,
            Alpha = alpha,
            Ratio = ratio,
            Blend = blend,
            ClipDepth = clipDepth,
            Name = name
        };
    }

    private int SkipFilterList(int cursor, int payloadEnd)
    {
        if (cursor >= payloadEnd)
        {
            return cursor;
        }

        int count = bytes[cursor++];
        for (var index = 0; index < count && cursor < payloadEnd; index++)
        {
            var filter = bytes[cursor++];
            cursor += filter switch
            {
                0 => 1 + (bytes.Length > cursor ? (bytes[cursor] * 4) + 4 : 0),
                1 => 20,
                2 => 28,
                3 => 24,
                4 => 21,
                5 => 1 + (bytes.Length > cursor ? bytes[cursor] * bytes[cursor] : 0),
                6 => 20,
                7 => 36,
                _ => payloadEnd - cursor
            };
        }

        return Math.Min(cursor, payloadEnd);
    }

    private float ReadColorTransformAlpha(ref int cursor)
    {
        var reader = new BitReader(bytes, cursor);
        var hasAdd = reader.ReadUnsigned(1) != 0;
        var hasMultiply = reader.ReadUnsigned(1) != 0;
        var count = (int)reader.ReadUnsigned(4);
        var alpha = 1.0f;
        if (hasMultiply)
        {
            reader.ReadSigned(count);
            reader.ReadSigned(count);
            reader.ReadSigned(count);
            alpha = reader.ReadSigned(count) / 256.0f;
        }

        if (hasAdd)
        {
            for (var component = 0; component < 4; component++)
            {
                reader.ReadSigned(count);
            }
        }

        cursor = reader.AlignedBytePosition;
        return Math.Clamp(alpha, 0.0f, 1.0f);
    }

    private void ExtractStrings(int payloadStart, int payloadEnd)
    {
        var start = -1;
        for (var index = payloadStart; index <= payloadEnd; index++)
        {
            var printable = index < payloadEnd && bytes[index] >= 0x20 && bytes[index] <= 0x7E;
            if (printable)
            {
                start = start < 0 ? index : start;
                continue;
            }

            if (start >= 0 && index - start >= 3)
            {
                ActionStrings.Add(Encoding.ASCII.GetString(bytes, start, index - start));
            }

            start = -1;
        }
    }

    private int ReadStyleCount(ref int cursor, bool extended)
    {
        int count = bytes[cursor++];
        if (count == 0xFF && extended)
        {
            count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(cursor, 2));
            cursor += 2;
        }

        return count;
    }

    private string Fixed(int start, int length)
    {
        var stop = start;
        var limit = Math.Min(bytes.Length, start + length);
        while (stop < limit && bytes[stop] != 0)
        {
            stop++;
        }

        return Encoding.UTF8.GetString(bytes, start, stop - start);
    }

    private string ReadString(int start, int end, out int next)
    {
        var terminator = start;
        while (terminator < end && bytes[terminator] != 0)
        {
            terminator++;
        }

        next = terminator + 1;
        return Encoding.UTF8.GetString(bytes, start, terminator - start);
    }

    private static GfxRect ReadRect(byte[] bytes, ref int cursor)
    {
        var reader = new BitReader(bytes, cursor);
        var count = (int)reader.ReadUnsigned(5);
        var xMin = reader.ReadSigned(count) / 20.0;
        var xMax = reader.ReadSigned(count) / 20.0;
        var yMin = reader.ReadSigned(count) / 20.0;
        var yMax = reader.ReadSigned(count) / 20.0;
        cursor = reader.AlignedBytePosition;
        return new GfxRect(xMin, xMax, yMin, yMax);
    }

    private static GfxMatrix ReadMatrix(byte[] bytes, ref int cursor)
    {
        var reader = new BitReader(bytes, cursor);
        var scaleX = 1.0;
        var scaleY = 1.0;
        var rotate0 = 0.0;
        var rotate1 = 0.0;
        if (reader.ReadUnsigned(1) != 0)
        {
            var count = (int)reader.ReadUnsigned(5);
            scaleX = reader.ReadSigned(count) / 65536.0;
            scaleY = reader.ReadSigned(count) / 65536.0;
        }

        if (reader.ReadUnsigned(1) != 0)
        {
            var count = (int)reader.ReadUnsigned(5);
            rotate0 = reader.ReadSigned(count) / 65536.0;
            rotate1 = reader.ReadSigned(count) / 65536.0;
        }

        var translateBits = (int)reader.ReadUnsigned(5);
        var translateX = translateBits == 0 ? 0 : reader.ReadSigned(translateBits) / 20.0;
        var translateY = translateBits == 0 ? 0 : reader.ReadSigned(translateBits) / 20.0;
        cursor = reader.AlignedBytePosition;
        return new GfxMatrix(scaleX, rotate0, rotate1, scaleY, translateX, translateY);
    }

    private struct BitReader(byte[] bytes, int startByte)
    {
        private readonly byte[] bytes = bytes;
        private int bitPosition = startByte * 8;

        public readonly int AlignedBytePosition => (bitPosition + 7) / 8;

        public uint ReadUnsigned(int count)
        {
            uint value = 0;
            for (var index = 0; index < count; index++)
            {
                if (bitPosition / 8 >= bytes.Length)
                {
                    return value;
                }

                var current = bytes[bitPosition / 8];
                var shift = 7 - (bitPosition % 8);
                bitPosition++;
                value = (value << 1) | (uint)((current >> shift) & 1);
            }

            return value;
        }

        public int ReadSigned(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            var value = ReadUnsigned(count);
            if (count < 32 && (value & (1u << (count - 1))) != 0)
            {
                value |= uint.MaxValue << count;
            }

            return unchecked((int)value);
        }
    }
}
