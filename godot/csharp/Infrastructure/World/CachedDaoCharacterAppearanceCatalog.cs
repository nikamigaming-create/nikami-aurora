using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public enum DaoCharacterAppearanceAvailability
{
    UnsupportedSelection,
    MissingImport,
    InvalidImport,
    LegacyEvidence,
    FreshImport
}

public sealed record DaoCharacterAppearanceResolution(
    DaoCharacterAppearanceAvailability Availability,
    DragonAgeCharacterCreationAppearance? Appearance,
    string StandingPath,
    string BedPath,
    string ManifestPath,
    string Failure)
{
    public bool IsReady => Availability is
        DaoCharacterAppearanceAvailability.LegacyEvidence or
        DaoCharacterAppearanceAvailability.FreshImport;

    public string Provenance => Availability switch
    {
        DaoCharacterAppearanceAvailability.FreshImport => "fresh-import",
        DaoCharacterAppearanceAvailability.LegacyEvidence => "legacy-evidence",
        _ => "unsupported"
    };
}

/// <summary>
/// Resolves one authored selection to one standing/bed pair. It never searches
/// area actor exports and therefore cannot substitute an unrelated NPC.
/// </summary>
public static class CachedDaoCharacterAppearanceCatalog
{
    private sealed record HashKey(string Path, long Length, long LastWriteTicks);

    private static readonly ConcurrentDictionary<HashKey, string> HashCache = new();
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private static int coveragePublished;

    public static DaoCharacterAppearanceResolution Resolve(
        string race, string gender, string appearance)
    {
        PublishCoverageOnce();
        return ResolveCore(race, gender, appearance);
    }

    private static DaoCharacterAppearanceResolution ResolveCore(
        string race, string gender, string preset)
    {
        var authored = RetailCharacterAppearanceCatalog.Resolve(race, gender, preset);
        if (authored is null)
            return new DaoCharacterAppearanceResolution(
                DaoCharacterAppearanceAvailability.UnsupportedSelection,
                null, string.Empty, string.Empty, string.Empty,
                "selection-not-in-source-catalog");

        var standingPath = DaoRuntimePaths.Cache(authored.StandingRelativePath);
        var bedPath = DaoRuntimePaths.Cache(authored.BedRelativePath);
        var manifestPath = DaoRuntimePaths.Cache(authored.ImportManifestRelativePath);
        if (!File.Exists(standingPath) || !File.Exists(bedPath))
            return new DaoCharacterAppearanceResolution(
                DaoCharacterAppearanceAvailability.MissingImport,
                authored, standingPath, bedPath, manifestPath,
                !File.Exists(standingPath) && !File.Exists(bedPath)
                    ? "standing-and-bed-payloads-missing"
                    : !File.Exists(standingPath)
                        ? "standing-payload-missing"
                        : "bed-payload-missing");

        try
        {
            var standingHash = Hash(standingPath);
            var bedHash = Hash(bedPath);
            DragonAgeCharacterCreationImportManifest? manifest = null;
            if (File.Exists(manifestPath))
            {
                var info = new FileInfo(manifestPath);
                if (info.Length is <= 0 or > 128 * 1024)
                    throw new InvalidDataException(
                        $"Character import manifest size is invalid: {manifestPath}");
                manifest = JsonSerializer.Deserialize<DragonAgeCharacterCreationImportManifest>(
                    File.ReadAllText(manifestPath), ManifestJson) ??
                    throw new InvalidDataException(
                        $"Character import manifest is empty: {manifestPath}");
            }

            var readiness = DragonAgeOriginsCharacterCreationCatalog.ClassifyImport(
                authored, manifest, standingHash, bedHash);
            return readiness switch
            {
                DragonAgeCharacterImportReadiness.FreshImport =>
                    new DaoCharacterAppearanceResolution(
                        DaoCharacterAppearanceAvailability.FreshImport,
                        authored, standingPath, bedPath, manifestPath, string.Empty),
                DragonAgeCharacterImportReadiness.LegacyEvidence =>
                    new DaoCharacterAppearanceResolution(
                        DaoCharacterAppearanceAvailability.LegacyEvidence,
                        authored, standingPath, bedPath, manifestPath,
                        "private-cache-compatibility-only"),
                _ => new DaoCharacterAppearanceResolution(
                    DaoCharacterAppearanceAvailability.MissingImport,
                    authored, standingPath, bedPath, manifestPath,
                    "source-bound-import-manifest-missing")
            };
        }
        catch (Exception exception) when (exception is IOException or
                                          UnauthorizedAccessException or
                                          JsonException or
                                          InvalidDataException)
        {
            return new DaoCharacterAppearanceResolution(
                DaoCharacterAppearanceAvailability.InvalidImport,
                authored, standingPath, bedPath, manifestPath,
                NormalizeFailure(exception.Message));
        }
    }

    private static void PublishCoverageOnce()
    {
        if (Interlocked.Exchange(ref coveragePublished, 1) != 0) return;
        var resolutions = RetailCharacterAppearanceCatalog.Appearances
            .Select(value => ResolveCore(value.Race, value.Gender, value.Preset))
            .ToArray();
        var fresh = resolutions.Count(value => value.Availability ==
                                               DaoCharacterAppearanceAvailability.FreshImport);
        var legacy = resolutions.Count(value => value.Availability ==
                                                DaoCharacterAppearanceAvailability.LegacyEvidence);
        var missing = resolutions.Count(value => value.Availability ==
                                                 DaoCharacterAppearanceAvailability.MissingImport);
        var invalid = resolutions.Count(value => value.Availability ==
                                                 DaoCharacterAppearanceAvailability.InvalidImport);
        var sourceProven = RetailCharacterAppearanceCatalog.Appearances.Count;
        var runtimeReady = fresh + legacy ==
                           DragonAgeOriginsCharacterCreationCatalog.ExpectedSelectionCount;
        var releaseReady = fresh == DragonAgeOriginsCharacterCreationCatalog.ExpectedSelectionCount;
        GD.Print("OPENDAO_CHARGEN_IDENTITY_COVERAGE status=" +
                 (runtimeReady ? "ready" : "partial") +
                 $" selections={DragonAgeOriginsCharacterCreationCatalog.ExpectedSelectionCount} " +
                 $"catalog_proven={sourceProven} fresh_import_ready={fresh} " +
                 $"legacy_evidence_ready={legacy} missing={missing} invalid={invalid} " +
                 "npc_substitutions=0 identity_join=preview-cinematic-gameplay " +
                 "pbr=global-postprocessor " +
                 $"runtime_ready={(runtimeReady ? 1 : 0)} " +
                 $"fresh_import={(fresh == sourceProven ? 1 : 0)} " +
                 $"release_ready={(releaseReady ? 1 : 0)} parity_claim=none");
    }

    private static string Hash(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0)
            throw new InvalidDataException("Character payload is absent or empty: " + path);
        var key = new HashKey(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
        return HashCache.GetOrAdd(key, static value =>
        {
            using var stream = new FileStream(value.Path, FileMode.Open, System.IO.FileAccess.Read,
                FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        });
    }

    private static string NormalizeFailure(string value) => string.Join('-',
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Replace('=', '-')
        .Replace('"', '\'');
}
