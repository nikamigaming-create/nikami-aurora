using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Security.Cryptography;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

/// <summary>
/// Resolves the installed engine's global irradiance fallback when an area has
/// no local probe. Outdoor/sun-lit areas remain untouched; the core night probe
/// is selected only for the authored no-sun/no-character-sun contract used by
/// interiors such as den201d.
/// </summary>
public sealed class DaoAuthoredLightingResolver : IAuthoredLightingResolver
{
    private const string NightProbeResource = "night_probe_3654.mtx";

    public AuthoredLightingProfile? Resolve(WorldProfile world, AuthoredLightingProfile? authored)
    {
        if (authored is null || authored.ProbeLoaded || HasRgbEnergy(authored.SunColor) ||
            HasRgbEnergy(authored.CharacterSunColor)) return authored;
        var archive = Path.Combine(world.GameRoot, "packages", "core", "data", "lightprobedata.erf");
        var payload = ReadErfEntry(archive, NightProbeResource);
        var coefficients = payload is null ? null : ParseMatrices(payload);
        if (coefficients is null) return authored;
        return authored with
        {
            ProbeLoaded = true,
            ProbeMatrixR = coefficients[..16],
            ProbeMatrixG = coefficients[16..32],
            ProbeMatrixB = coefficients[32..48],
            ProbeResource = NightProbeResource,
            ProbeResourceSha256 = Convert.ToHexString(SHA256.HashData(payload!)).ToLowerInvariant()
        };
    }

    private static bool HasRgbEnergy(IReadOnlyList<float> values) =>
        Enumerable.Range(0, Math.Min(3, values.Count))
            .Any(index => Math.Abs(values[index]) > 0.000001f);

    private static float[]? ParseMatrices(byte[] payload)
    {
        var tokens = Encoding.ASCII.GetString(payload)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new float[48];
        for (var index = 0; index < result.Length; index++)
        {
            if (index >= tokens.Length ||
                !float.TryParse(tokens[index], NumberStyles.Float, CultureInfo.InvariantCulture,
                    out result[index])) return null;
        }
        return result;
    }

    private static byte[]? ReadErfEntry(string path, string wanted)
    {
        if (!File.Exists(path)) return null;
        var data = File.ReadAllBytes(path);
        if (data.Length < 32) return null;
        if (Encoding.ASCII.GetString(data, 0, 8) == "ERF V2.1")
        {
            var count = BitConverter.ToUInt32(data, 16);
            for (var index = 0u; index < count; index++)
            {
                var record = checked(32 + (int)index * 44);
                if (record + 44 > data.Length) return null;
                var name = ReadNullTerminatedAscii(data, record, 32);
                if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
                var offset = checked((int)BitConverter.ToUInt32(data, record + 32));
                var storedSize = checked((int)BitConverter.ToUInt32(data, record + 36));
                var decodedSize = checked((int)BitConverter.ToUInt32(data, record + 40));
                if (offset < 0 || storedSize < 0 || offset + storedSize > data.Length) return null;
                var payload = data.AsSpan(offset, storedSize).ToArray();
                if (storedSize == decodedSize) return payload;
                using var input = new MemoryStream(payload);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream(decodedSize);
                zlib.CopyTo(output);
                return output.Length == decodedSize ? output.ToArray() : null;
            }
            return null;
        }

        if (Encoding.Unicode.GetString(data, 0, 16).TrimEnd('\0') != "ERF V2.0") return null;
        var v20Count = BitConverter.ToUInt32(data, 16);
        for (var index = 0u; index < v20Count; index++)
        {
            var record = checked(32 + (int)index * 72);
            if (record + 72 > data.Length) return null;
            var name = Encoding.Unicode.GetString(data, record, 64).Split('\0', 2)[0];
            if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;
            var offset = checked((int)BitConverter.ToUInt32(data, record + 64));
            var size = checked((int)BitConverter.ToUInt32(data, record + 68));
            return offset >= 0 && size >= 0 && offset + size <= data.Length
                ? data.AsSpan(offset, size).ToArray()
                : null;
        }
        return null;
    }

    private static string ReadNullTerminatedAscii(byte[] data, int offset, int length)
    {
        var end = Array.IndexOf(data, (byte)0, offset, length);
        if (end < 0) end = offset + length;
        return Encoding.ASCII.GetString(data, offset, end - offset);
    }
}
