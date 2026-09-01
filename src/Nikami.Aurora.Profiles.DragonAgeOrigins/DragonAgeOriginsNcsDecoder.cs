using System.Buffers.Binary;
using System.Text;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeNcsInstruction(
    int Address,
    byte Opcode,
    byte ValueType,
    IReadOnlyList<object> Arguments);

public sealed record DragonAgeNcsDecodeResult(
    bool Succeeded,
    string Error,
    IReadOnlyList<DragonAgeNcsInstruction> Instructions)
{
    public static DragonAgeNcsDecodeResult Fail(string error) => new(false, error, []);
}

/// <summary>
/// Bounded decoder for the installed NCS V1.0 big-endian instruction stream.
/// It performs no gameplay behavior and rejects unknown layouts explicitly.
/// </summary>
public static class DragonAgeOriginsNcsDecoder
{
    private static readonly HashSet<byte> KnownOpcodes =
    [
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0a, 0x0b,
        0x0c, 0x0d, 0x0e, 0x0f, 0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16,
        0x17, 0x18, 0x19, 0x1a, 0x1b, 0x1d, 0x1e, 0x1f, 0x20, 0x21, 0x22,
        0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2a, 0x2b, 0x2c, 0x2d,
        0x30, 0x32, 0x37, 0x39
    ];
    private static readonly HashSet<byte> Signed32Opcodes =
        [0x1b, 0x1d, 0x1e, 0x1f, 0x23, 0x24, 0x25, 0x28, 0x29];
    private static readonly HashSet<byte> StackArgumentOpcodes =
        [0x01, 0x03, 0x26, 0x27, 0x30, 0x32, 0x37, 0x39];

    public static DragonAgeNcsDecodeResult Decode(ReadOnlySpan<byte> source,
        int maximumInstructions = 1_000_000)
    {
        if (maximumInstructions <= 0) throw new ArgumentOutOfRangeException(nameof(maximumInstructions));
        if (source.Length < 13 || !source[..8].SequenceEqual("NCS V1.0"u8) || source[8] != 0x42)
            return DragonAgeNcsDecodeResult.Fail("not-ncs-v1.0");
        var declaredSize = BinaryPrimitives.ReadUInt32BigEndian(source[9..13]);
        if (declaredSize != source.Length)
            return DragonAgeNcsDecodeResult.Fail("ncs-size-mismatch");

        var instructions = new List<DragonAgeNcsInstruction>();
        var offset = 13;
        while (offset < source.Length)
        {
            if (instructions.Count >= maximumInstructions)
                return DragonAgeNcsDecodeResult.Fail("instruction-budget-exhausted");
            if (!Available(source, offset, 2))
                return DragonAgeNcsDecodeResult.Fail("truncated-instruction");
            var address = offset;
            var opcode = source[offset++];
            var valueType = source[offset++];
            if (!KnownOpcodes.Contains(opcode))
                return DragonAgeNcsDecodeResult.Fail($"unknown-opcode:0x{opcode:x2}");
            var arguments = new List<object>();

            if (opcode == 0x04)
            {
                if (valueType is 3 or 6)
                {
                    if (!Available(source, offset, 4))
                        return DragonAgeNcsDecodeResult.Fail("truncated-const");
                    arguments.Add(BinaryPrimitives.ReadInt32BigEndian(source[offset..]));
                    offset += 4;
                }
                else if (valueType == 4)
                {
                    if (!Available(source, offset, 4))
                        return DragonAgeNcsDecodeResult.Fail("truncated-const");
                    var bits = BinaryPrimitives.ReadInt32BigEndian(source[offset..]);
                    arguments.Add(BitConverter.Int32BitsToSingle(bits));
                    offset += 4;
                }
                else if (valueType is 5 or 96)
                {
                    if (!Available(source, offset, 2))
                        return DragonAgeNcsDecodeResult.Fail("truncated-string-length");
                    var length = BinaryPrimitives.ReadUInt16BigEndian(source[offset..]);
                    offset += 2;
                    if (!Available(source, offset, length))
                        return DragonAgeNcsDecodeResult.Fail("truncated-string");
                    arguments.Add(Encoding.UTF8.GetString(source.Slice(offset, length)));
                    offset += length;
                }
                else return DragonAgeNcsDecodeResult.Fail($"unsupported-const-type:{valueType}");
            }
            else if (opcode == 0x05)
            {
                if (!Available(source, offset, 3))
                    return DragonAgeNcsDecodeResult.Fail("truncated-action");
                arguments.Add((int)BinaryPrimitives.ReadUInt16BigEndian(source[offset..]));
                arguments.Add((int)source[offset + 2]);
                offset += 3;
            }
            else if (Signed32Opcodes.Contains(opcode))
            {
                if (!Available(source, offset, 4))
                    return DragonAgeNcsDecodeResult.Fail("truncated-direct-arg");
                arguments.Add(BinaryPrimitives.ReadInt32BigEndian(source[offset..]));
                offset += 4;
            }
            else if (StackArgumentOpcodes.Contains(opcode))
            {
                if (!Available(source, offset, 6))
                    return DragonAgeNcsDecodeResult.Fail("truncated-stack-args");
                arguments.Add(BinaryPrimitives.ReadInt32BigEndian(source[offset..]));
                arguments.Add((int)BinaryPrimitives.ReadInt16BigEndian(source[(offset + 4)..]));
                offset += 6;
            }
            else if (opcode == 0x21)
            {
                if (!Available(source, offset, 6))
                    return DragonAgeNcsDecodeResult.Fail("truncated-destruct");
                arguments.Add((int)BinaryPrimitives.ReadInt16BigEndian(source[offset..]));
                arguments.Add((int)BinaryPrimitives.ReadInt16BigEndian(source[(offset + 2)..]));
                arguments.Add((int)BinaryPrimitives.ReadInt16BigEndian(source[(offset + 4)..]));
                offset += 6;
            }
            else if (opcode == 0x2c)
            {
                if (!Available(source, offset, 8))
                    return DragonAgeNcsDecodeResult.Fail("truncated-storestate");
                arguments.Add(BinaryPrimitives.ReadUInt32BigEndian(source[offset..]));
                arguments.Add(BinaryPrimitives.ReadUInt32BigEndian(source[(offset + 4)..]));
                offset += 8;
            }
            else if (opcode is 0x0b or 0x0c && valueType == 36)
            {
                if (!Available(source, offset, 2))
                    return DragonAgeNcsDecodeResult.Fail("truncated-struct-compare");
                arguments.Add((int)BinaryPrimitives.ReadUInt16BigEndian(source[offset..]));
                offset += 2;
            }
            instructions.Add(new DragonAgeNcsInstruction(address, opcode, valueType, arguments));
        }
        return new DragonAgeNcsDecodeResult(true, string.Empty, instructions);
    }

    private static bool Available(ReadOnlySpan<byte> source, int offset, int count) =>
        offset >= 0 && count >= 0 && offset <= source.Length - count;
}
