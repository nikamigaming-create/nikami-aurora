using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace OpenDAO.Infrastructure.Archives;

/// <summary>
/// Minimal, read-only DAO ERF/RIM reader used by the runtime. It supports the
/// unencrypted V2.0 and V2.1 containers observed in owned installations and
/// fails closed on unknown layouts.
/// </summary>
public sealed class ErfArchive
{
    private const int HeaderSize = 32;
    private const int V20EntrySize = 72;
    private const int V21EntrySize = 44;
    private const int MaximumEntries = 1_000_000;
    private const int MaximumMemberBytes = 512 * 1024 * 1024;

    private sealed record Entry(long Offset, int StoredSize, int DecodedSize, bool Compressed);

    private readonly Dictionary<string, Entry> entries;

    private ErfArchive(string path, string version, Dictionary<string, Entry> entries)
    {
        Path = path;
        Version = version;
        this.entries = entries;
    }

    public string Path { get; }

    public string Version { get; }

    public bool IsEncrypted => false;

    public static bool IsEncryptedArchive(string path)
    {
        _ = Open(path);
        return false;
    }

    public static ErfArchive Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess);
        if (stream.Length < HeaderSize)
        {
            throw new InvalidDataException("ERF header is truncated: " + fullPath);
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.ReadExactly(header);
        if (header[..8].SequenceEqual("ERF V2.1"u8))
        {
            return ReadV21(fullPath, stream, header);
        }

        var utf16Signature = Encoding.Unicode.GetString(header[..16]).TrimEnd('\0');
        if (utf16Signature.Equals("ERF V2.0", StringComparison.Ordinal))
        {
            return ReadV20(fullPath, stream, header);
        }

        throw new InvalidDataException(
            $"Unsupported or encrypted ERF container '{utf16Signature}': {fullPath}");
    }

    public bool Contains(string member) => entries.ContainsKey(NormalizeMember(member));

    public IEnumerable<string> MemberNames() => entries.Keys.Order(StringComparer.OrdinalIgnoreCase);

    public long SizeOf(string member) => GetEntry(member).DecodedSize;

    public byte[] Read(string member)
    {
        var entry = GetEntry(member);
        var stored = new byte[entry.StoredSize];
        using (var stream = new FileStream(
            Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.RandomAccess))
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(stored);
        }

        if (!entry.Compressed)
        {
            return stored;
        }

        var decoded = new byte[entry.DecodedSize];
        using var source = new MemoryStream(stored, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress, leaveOpen: false);
        inflater.ReadExactly(decoded);
        if (inflater.ReadByte() != -1)
        {
            throw new InvalidDataException("ERF member expands beyond its declared size: " + member);
        }
        return decoded;
    }

    private Entry GetEntry(string member)
    {
        var normalized = NormalizeMember(member);
        return entries.TryGetValue(normalized, out var entry)
            ? entry
            : throw new KeyNotFoundException($"ERF member was not found: {member}");
    }

    private static ErfArchive ReadV20(string path, FileStream stream, ReadOnlySpan<byte> header)
    {
        var count = ReadCount(header);
        ValidateTable(stream.Length, count, V20EntrySize, path);
        var entries = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[V20EntrySize];
        stream.Position = HeaderSize;
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(buffer);
            var name = Encoding.Unicode.GetString(buffer, 0, 64).Split('\0', 2)[0];
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(64, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(68, 4));
            AddEntry(entries, name, offset, size, size, stream.Length, path);
        }
        return new ErfArchive(path, "2.0", entries);
    }

    private static ErfArchive ReadV21(string path, FileStream stream, ReadOnlySpan<byte> header)
    {
        var count = ReadCount(header);
        ValidateTable(stream.Length, count, V21EntrySize, path);
        var entries = new Dictionary<string, Entry>(count, StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[V21EntrySize];
        stream.Position = HeaderSize;
        for (var index = 0; index < count; index++)
        {
            stream.ReadExactly(buffer);
            var name = Encoding.ASCII.GetString(buffer, 0, 32).Split('\0', 2)[0];
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(32, 4));
            var storedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(36, 4));
            var decodedSize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(40, 4));
            AddEntry(entries, name, offset, storedSize, decodedSize, stream.Length, path);
        }
        return new ErfArchive(path, "2.1", entries);
    }

    private static int ReadCount(ReadOnlySpan<byte> header)
    {
        var raw = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(16, 4));
        if (raw > MaximumEntries)
        {
            throw new InvalidDataException($"ERF entry count is unreasonable: {raw}");
        }
        return checked((int)raw);
    }

    private static void ValidateTable(long fileLength, int count, int entrySize, string path)
    {
        var tableEnd = checked(HeaderSize + (long)count * entrySize);
        if (tableEnd > fileLength)
        {
            throw new InvalidDataException("ERF entry table is truncated: " + path);
        }
    }

    private static void AddEntry(
        IDictionary<string, Entry> entries,
        string rawName,
        uint rawOffset,
        uint rawStoredSize,
        uint rawDecodedSize,
        long fileLength,
        string path)
    {
        var name = NormalizeMember(rawName);
        if (name.Length == 0)
        {
            throw new InvalidDataException("ERF contains an unnamed member: " + path);
        }
        if (rawStoredSize > MaximumMemberBytes || rawDecodedSize > MaximumMemberBytes)
        {
            throw new InvalidDataException("ERF member is too large for the runtime: " + name);
        }

        var offset = (long)rawOffset;
        var storedSize = checked((int)rawStoredSize);
        var decodedSize = checked((int)rawDecodedSize);
        if (offset < HeaderSize || offset > fileLength - storedSize)
        {
            throw new InvalidDataException("ERF member range is outside the archive: " + name);
        }
        if (decodedSize == 0 && storedSize != 0)
        {
            throw new InvalidDataException("ERF member has an invalid decoded size: " + name);
        }
        if (!entries.TryAdd(name, new Entry(offset, storedSize, decodedSize, storedSize != decodedSize)))
        {
            throw new InvalidDataException("ERF contains a duplicate member: " + name);
        }
    }

    private static string NormalizeMember(string member)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(member);
        return member.Trim().Replace('\\', '/').TrimStart('/');
    }
}
