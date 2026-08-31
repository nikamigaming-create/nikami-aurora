using System.Buffers.Binary;
using System.Text;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Acceptance;

internal static partial class Program
{
    private static void DragonAgeNavigationGridDecoderIsSourceBound()
    {
        var payload = SyntheticNavigationGrid([1, 0, 1, 0, 0, 1]);
        var decoded = DragonAgeOriginsNavigationGridDecoder.Decode(payload);
        Expect(decoded.Ready && decoded.Columns == 2 && decoded.Rows == 3 &&
               Math.Abs(decoded.CellSize - .5f) < .00001f &&
               Math.Abs(decoded.BaseX - 10) < .00001f &&
               Math.Abs(decoded.BaseY - 20) < .00001f &&
               decoded.WalkableCells == 3 && decoded.NonWalkableCells == 3 &&
               decoded.PayloadSha256.Length == 64 && decoded.AccessibilitySha256.Length == 64,
            "DAO navigation grid lost its exact dimensions, basis, or accessibility contract");

        var blocked = DragonAgeOriginsNavigationGridDecoder.Decode(
            SyntheticNavigationGrid([0, 0, 0, 0, 0, 0]));
        Expect(!blocked.Ready && blocked.WalkableCells == 0,
            "DAO navigation grid treated an all-blocked source as a boot-ready prerequisite");

        var malformedCount = payload.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(malformedCount.AsSpan(184, 4), 5);
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsNavigationGridDecoder.Decode(malformedCount),
            "DAO navigation grid accepted an accessibility count that disagrees with dimensions");

        var wrongHeader = payload.ToArray();
        wrongHeader[15] = (byte)'X';
        ExpectThrows<InvalidDataException>(
            () => DragonAgeOriginsNavigationGridDecoder.Decode(wrongHeader),
            "DAO navigation grid accepted an unknown source header");
    }

    private static byte[] SyntheticNavigationGrid(byte[] accessibility)
    {
        if (accessibility.Length != 6) throw new ArgumentException("fixture must contain six cells");
        var payload = new byte[194];
        Encoding.ASCII.GetBytes("GFF V4.0PC  ARL ").CopyTo(payload, 0);
        WriteUInt32(payload, 20, 3);
        WriteUInt32(payload, 24, 160);
        WriteStruct(payload, 28, 1, 76);
        WriteStruct(payload, 44, 1, 88);
        WriteStruct(payload, 60, 5, 100);
        WriteField(payload, 76, 3020, 1, 0x4000, 0);
        WriteField(payload, 88, 3110, 2, 0x4000, 0);
        WriteField(payload, 100, 3086, 0, 0, 0);
        WriteField(payload, 112, 3087, 0, 0, 4);
        WriteField(payload, 124, 3088, 0, 0, 8);
        WriteField(payload, 136, 3090, 0, 0, 12);
        WriteField(payload, 148, 3092, 0, 0x8000, 20);
        WriteInt32(payload, 160, 2);
        WriteInt32(payload, 164, 3);
        WriteSingle(payload, 168, .5f);
        WriteSingle(payload, 172, 10);
        WriteSingle(payload, 176, 20);
        WriteInt32(payload, 180, 24);
        WriteInt32(payload, 184, accessibility.Length);
        accessibility.CopyTo(payload, 188);
        return payload;
    }

    private static void WriteStruct(byte[] payload, int offset, uint fields, uint fieldOffset)
    {
        WriteUInt32(payload, offset + 4, fields);
        WriteUInt32(payload, offset + 8, fieldOffset);
    }

    private static void WriteField(byte[] payload, int offset, uint label, ushort type,
        ushort flags, uint valueOffset)
    {
        WriteUInt32(payload, offset, label);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 4, 2), type);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(offset + 6, 2), flags);
        WriteUInt32(payload, offset + 8, valueOffset);
    }

    private static void WriteUInt32(byte[] payload, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(offset, 4), value);

    private static void WriteInt32(byte[] payload, int offset, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(offset, 4), value);

    private static void WriteSingle(byte[] payload, int offset, float value) =>
        WriteInt32(payload, offset, BitConverter.SingleToInt32Bits(value));
}
