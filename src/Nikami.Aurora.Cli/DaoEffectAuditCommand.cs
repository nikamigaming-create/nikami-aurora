using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.Cli;

internal static class DaoEffectAuditCommand
{
    private const string ModelArchive = "packages/core/data/modelhierarchies.erf";
    private const string MaterialArchive = "packages/core/data/materialobjects.erf";
    private const string TextureArchive = "packages/core/textures/high/texturepack.erf";

    public static DaoEffectAuditResult Run(string root, IEnumerable<string> requested)
    {
        var fullRoot = Path.GetFullPath(root);
        var models = ReadOnlyErf.Open(Contained(fullRoot, ModelArchive));
        var materials = ReadOnlyErf.Open(Contained(fullRoot, MaterialArchive));
        var textures = ReadOnlyErf.Open(Contained(fullRoot, TextureArchive));
        var results = new List<DaoEffectAuditEntry>();
        foreach (var resRef in requested.Select(value =>
                     Path.GetFileNameWithoutExtension(value).ToLowerInvariant())
                 .Where(value => value.Length > 0)
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .Order(StringComparer.OrdinalIgnoreCase))
        {
            var modelMember = resRef + ".mmh";
            if (!models.TryRead(modelMember, out var model))
            {
                results.Add(new DaoEffectAuditEntry(resRef, false, "source-mmh-absent",
                    string.Empty, 0, null, 0, 0, 0, [], [], [], [], "unsupported"));
                continue;
            }
            var emitterInventoryReady =
                DragonAgeOriginsEffectGraphDecoder.TryInspectEmitterCount(
                    resRef, model, out var sourceEmitters, out var inventoryFailure);
            var emitterEvidenceReady =
                DragonAgeOriginsEffectGraphDecoder.TryInspectEmitterSemantics(
                    resRef, model, out var emitterEvidence, out var evidenceFailure);
            if (emitterInventoryReady && (!emitterEvidenceReady ||
                emitterEvidence.Count != sourceEmitters))
                throw new InvalidDataException(
                    $"Emitter semantic evidence is incomplete: {resRef}: {evidenceFailure}");
            if (DragonAgeOriginsEffectCatalog.TryResolve(resRef, out var curated))
            {
                var failure = emitterInventoryReady
                    ? ValidateCurated(curated, model, materials, textures)
                    : inventoryFailure;
                if (failure.Length == 0 && sourceEmitters !=
                    curated.Emitters.Count + curated.UnsupportedDistortionEmitters +
                    (curated.UnsupportedEmitterSemantics?.Count ?? 0))
                    failure = "curated-emitter-inventory-mismatch";
                var readability = Array.Empty<DragonAgeEffectReadabilityContract>();
                if (failure.Length == 0)
                {
                    try { readability = ValidateReadability(curated, textures); }
                    catch (InvalidDataException)
                    {
                        failure = "emitter-readability-contract-invalid";
                    }
                }
                results.Add(new DaoEffectAuditEntry(resRef, failure.Length == 0,
                    failure, Hex(model), curated.Emitters.Count,
                    emitterInventoryReady ? sourceEmitters : null,
                    curated.Emitters.Count(emitter => emitter.IndependentScaleAxes),
                    curated.UnsupportedDistortionEmitters,
                    curated.UnsupportedEmitterSemantics?.Count ?? 0,
                    curated.UnsupportedEmitterSemantics ?? [],
                    [],
                    emitterEvidence,
                    readability,
                    "curated-source-contract"));
                continue;
            }
            var materialEvidence = new Dictionary<string,
                DaoEffectMaterialSemanticEvidence>(StringComparer.OrdinalIgnoreCase);
            byte[]? ResolveMaterial(string member)
            {
                if (!materials.TryRead(member, out var bytes)) return null;
                materialEvidence.TryAdd(member, InspectMaterial(member, bytes));
                return bytes;
            }
            var ready = DragonAgeOriginsEffectGraphDecoder.TryDecode(
                resRef, model,
                ResolveMaterial,
                member => textures.TryRead(member, out var bytes) ? bytes : null,
                out var definition, out var reason);
            var decodedReadability = Array.Empty<DragonAgeEffectReadabilityContract>();
            if (ready)
            {
                try { decodedReadability = ValidateReadability(definition, textures); }
                catch (InvalidDataException)
                {
                    ready = false;
                    reason = "emitter-readability-contract-invalid";
                }
            }
            results.Add(new DaoEffectAuditEntry(resRef, ready, reason, Hex(model),
                ready ? definition.Emitters.Count : 0,
                emitterInventoryReady ? sourceEmitters : null,
                ready ? definition.Emitters.Count(emitter => emitter.IndependentScaleAxes) : 0,
                ready ? definition.UnsupportedDistortionEmitters : 0,
                ready ? definition.UnsupportedEmitterSemantics?.Count ?? 0 : 0,
                ready ? definition.UnsupportedEmitterSemantics ?? [] : [],
                materialEvidence.Values.OrderBy(value => value.Member,
                    StringComparer.OrdinalIgnoreCase).ToArray(),
                emitterEvidence,
                decodedReadability,
                ready ? "decoded-source-contract" : "unsupported"));
        }
        return new DaoEffectAuditResult(
            results.Count,
            results.Count(result => result.Supported),
            results.Count(result => !result.Supported),
            results);
    }

    private static string ValidateCurated(DragonAgeEffectDefinition definition,
        byte[] model, ReadOnlyErf materials, ReadOnlyErf textures)
    {
        if (!Hex(model).Equals(definition.ModelHierarchySha256,
                StringComparison.OrdinalIgnoreCase))
            return "curated-mmh-hash-mismatch";
        foreach (var emitter in definition.Emitters)
        {
            if (!materials.TryRead(emitter.MaterialObject, out var material) ||
                !Hex(material).Equals(emitter.MaterialSha256,
                    StringComparison.OrdinalIgnoreCase))
                return "curated-material-hash-mismatch";
            if (!textures.TryRead(emitter.Texture, out var texture) ||
                !Hex(texture).Equals(emitter.TextureSha256,
                    StringComparison.OrdinalIgnoreCase))
                return "curated-texture-hash-mismatch";
        }
        return string.Empty;
    }

    private static DragonAgeEffectReadabilityContract[] ValidateReadability(
        DragonAgeEffectDefinition definition, ReadOnlyErf textures)
    {
        return definition.Emitters.Select(emitter =>
        {
            if (!textures.TryRead(emitter.Texture, out var texture))
                throw new InvalidDataException("DAO effect texture is absent.");
            var (width, height) = ReadDdsDimensions(texture);
            return DragonAgeOriginsEffectPresentationPolicy.Evaluate(
                emitter, width, height, presentationScale: 1,
                enhancedPresentation: true);
        }).ToArray();
    }

    private static (int Width, int Height) ReadDdsDimensions(byte[] payload)
    {
        if (payload.Length < 128 || !payload.AsSpan(0, 4).SequenceEqual("DDS "u8))
            throw new InvalidDataException("DAO effect texture is not a DDS payload.");
        var height = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12, 4));
        var width = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16, 4));
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
            throw new InvalidDataException("DAO effect DDS dimensions are invalid.");
        return (checked((int)width), checked((int)height));
    }

    private static string Contained(string root, string relative)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DAO audit archive escapes the installed root");
        return result;
    }

    private static string Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static DaoEffectMaterialSemanticEvidence InspectMaterial(
        string member, byte[] payload)
    {
        var root = XDocument.Parse(Encoding.UTF8.GetString(payload),
            LoadOptions.None).Root;
        var material = root?.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "Material")?.Attribute("Name")?.Value ?? string.Empty;
        var semantic = root?.Elements().SingleOrDefault(element =>
            element.Name.LocalName == "DefaultSemantic")?.Attribute("Name")?.Value ?? string.Empty;
        var textures = root?.Elements().Where(element =>
                element.Name.LocalName == "Texture")
            .Select(element =>
                $"{element.Attribute("Name")?.Value ?? string.Empty}=" +
                (element.Attribute("ResName")?.Value ?? string.Empty))
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        var parameters = root?.Elements().Where(element =>
                element.Name.LocalName is not ("Material" or "DefaultSemantic" or "Texture"))
            .Select(element => element.Name.LocalName + ":" +
                string.Join(',', element.Attributes().OrderBy(attribute =>
                    attribute.Name.LocalName, StringComparer.Ordinal).Select(attribute =>
                    $"{attribute.Name.LocalName}={attribute.Value}")))
            .Order(StringComparer.Ordinal)
            .ToArray() ?? [];
        return new DaoEffectMaterialSemanticEvidence(member, Hex(payload), material,
            semantic, textures, parameters);
    }
}

internal sealed record DaoEffectAuditResult(
    int Definitions,
    int SupportedDefinitions,
    int UnsupportedDefinitions,
    IReadOnlyList<DaoEffectAuditEntry> Results);

internal sealed record DaoEffectAuditEntry(
    string ResRef,
    bool Supported,
    string Reason,
    string ModelHierarchySha256,
    int Emitters,
    int? SourceEmitters,
    int IndependentScaleEmitters,
    int UnsupportedDistortionEmitters,
    int UnsupportedSemanticEmitters,
    IReadOnlyList<string> UnsupportedEmitterReasons,
    IReadOnlyList<DaoEffectMaterialSemanticEvidence> MaterialSemanticEvidence,
    IReadOnlyList<DragonAgeEffectEmitterSemanticEvidence> EmitterSemanticEvidence,
    IReadOnlyList<DragonAgeEffectReadabilityContract> EmitterReadability,
    string ContractKind);

internal sealed record DaoEffectMaterialSemanticEvidence(
    string Member,
    string Sha256,
    string Material,
    string Semantic,
    IReadOnlyList<string> Textures,
    IReadOnlyList<string> Parameters);

internal sealed class ReadOnlyErf
{
    private const int MaximumEntries = 1_000_000;
    private const int MaximumMemberBytes = 512 * 1024 * 1024;
    private sealed record Entry(long Offset, int Stored, int Decoded);
    private readonly string path;
    private readonly Dictionary<string, Entry> entries;

    private ReadOnlyErf(string path, Dictionary<string, Entry> entries)
    {
        this.path = path;
        this.entries = entries;
    }

    public static ReadOnlyErf Open(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> header = stackalloc byte[32];
        stream.ReadExactly(header);
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header[16..20]);
        if (count > MaximumEntries) throw new InvalidDataException("ERF entry count is invalid");
        if (header[..8].SequenceEqual("ERF V2.1"u8))
        {
            var row = new byte[44];
            for (var index = 0; index < count; index++)
            {
                stream.ReadExactly(row);
                Add(result, Encoding.ASCII.GetString(row, 0, 32).Split('\0', 2)[0],
                    BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(32, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(36, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(40, 4)),
                    stream.Length);
            }
        }
        else if (Encoding.Unicode.GetString(header[..16]).TrimEnd('\0') == "ERF V2.0")
        {
            var row = new byte[72];
            for (var index = 0; index < count; index++)
            {
                stream.ReadExactly(row);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(68, 4));
                Add(result, Encoding.Unicode.GetString(row, 0, 64).Split('\0', 2)[0],
                    BinaryPrimitives.ReadUInt32LittleEndian(row.AsSpan(64, 4)), size, size,
                    stream.Length);
            }
        }
        else
        {
            throw new InvalidDataException("DAO audit requires ERF V2.0 or V2.1");
        }
        return new ReadOnlyErf(path, result);
    }

    public bool TryRead(string member, out byte[] bytes)
    {
        if (!entries.TryGetValue(member.Replace('\\', '/').TrimStart('/'), out var entry))
        {
            bytes = [];
            return false;
        }
        var stored = new byte[entry.Stored];
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = entry.Offset;
            stream.ReadExactly(stored);
        }
        if (entry.Stored == entry.Decoded)
        {
            bytes = stored;
            return true;
        }
        bytes = new byte[entry.Decoded];
        using var source = new MemoryStream(stored, writable: false);
        using var inflater = new ZLibStream(source, CompressionMode.Decompress);
        inflater.ReadExactly(bytes);
        if (inflater.ReadByte() != -1)
            throw new InvalidDataException("ERF member exceeds its decoded size");
        return true;
    }

    public bool Contains(string member) =>
        entries.ContainsKey(member.Replace('\\', '/').TrimStart('/'));

    private static void Add(IDictionary<string, Entry> entries, string name,
        uint offset, uint stored, uint decoded, long length)
    {
        if (name.Length == 0 || stored > MaximumMemberBytes || decoded > MaximumMemberBytes ||
            offset < 32 || offset > length - stored || decoded == 0 && stored != 0 ||
            !entries.TryAdd(name.Replace('\\', '/'),
                new Entry(offset, checked((int)stored), checked((int)decoded))))
            throw new InvalidDataException("ERF entry table is invalid");
    }
}
