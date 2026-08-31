using System.Collections.ObjectModel;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public enum DragonAgeCharacterImportReadiness
{
    Missing,
    LegacyEvidence,
    FreshImport
}

public sealed record DragonAgeCharacterCreationAppearance(
    string Race,
    string Gender,
    string Preset,
    string MorphResource,
    string MorphSha256,
    string StandingRelativePath,
    string BedRelativePath,
    string ImportManifestRelativePath,
    string? LegacyStandingSha256,
    string? LegacyBedSha256)
{
    public string SelectionKey => $"{Race}:{Gender}:{Preset}";

    public bool HasLegacyEvidence =>
        LegacyStandingSha256 is not null && LegacyBedSha256 is not null;
}

public sealed record DragonAgeCharacterCreationImportManifest(
    string Schema,
    string ImporterId,
    string SelectionKey,
    string CatalogContainerRelativePath,
    string CatalogContainerSha256,
    string CatalogResource,
    string CatalogResourceSha256,
    string SourceContainerRelativePath,
    string SourceContainerSha256,
    string SourceResource,
    string SourcePayloadSha256,
    string StandingRelativePath,
    string StandingSha256,
    string BedRelativePath,
    string BedSha256);

public sealed record DragonAgeCharacterSelectionState(
    string SelectionKey,
    bool ActorVisible)
{
    public static DragonAgeCharacterSelectionState Empty { get; } = new(string.Empty, false);
}

/// <summary>
/// Source-bound Dragon Age: Origins character-creation identities. The first
/// four presets for each retail race/gender family are the 24 choices exposed
/// by Aurora's current creation UI. Converted payloads remain in ignored local
/// storage and must satisfy the import contract below before they are treated
/// as a fresh Aurora import.
/// </summary>
public static class DragonAgeOriginsCharacterCreationCatalog
{
    public const string ManifestSchema = "nikami-aurora-dao-character-import-v1";
    public const string ImporterId = "nikami-aurora-dao-character";
    public const string CatalogContainerRelativePath = "packages/core/data/misc.erf";
    public const string CatalogContainerSha256 =
        "3e645e7d1c04c2ec6c243e2e14b4ed71b504882a1f0012cb1b6b91be3031fca5";
    public const string CatalogResource = "chargenmorphcfg.xml";
    public const string CatalogResourceSha256 =
        "f8c97983502ee447a7c293e839fb22a418a0c63e8391bca9e711e0dd5482c541";
    public const string SourceContainerRelativePath = "packages/core/data/face.erf";
    public const string SourceContainerSha256 =
        "9c70de3c42d3a5bcc84c591469edb5144e775c05788a9b93665d4e30afe3114a";
    public const int ExpectedSelectionCount = 24;

    private sealed record Family(string Race, string Gender, string Prefix,
        string[] MorphHashes);

    private static readonly Family[] Families =
    [
        new("human", "female", "hf",
        [
            "7479b2d0d24e4ec5e71b082eac5f0c8ce7774ae030fabf29b8a06988f7361554",
            "825f47c8dea293cb212243873fe0ccb18da30c245f2fa639c62d399930596fb3",
            "c1d7a0efd8e49e293864b6cfdde5ae301c58811adb65cd653e827d74c527621f",
            "95c0436ad41a0d2753d44e67663b7dabc0ad15f4a07e76c704670370d816c55c"
        ]),
        new("human", "male", "hm",
        [
            "de699199dbcf48a258dd4049cb30fd04bad892cb2e4e95d0291d4b0e9fdb12e3",
            "ebb359d1ba26550efccaa6e32865ec159e25b2b3309dd1792850fc1cf0c43980",
            "3de8631b9e8c991c4e0d6c1854703e8099c959cd8b6d1d19d4138798e2dfea43",
            "59937dbc515b5191ae9e132263b44275fe43a5a2d05cfe3b22b736f2dfdda5d0"
        ]),
        new("elf", "female", "ef",
        [
            "3824bd11fd6ba7820be055b1a8b296a9faa9ee83f5b3a56d51bc9bfcfbb71a62",
            "4e5b5543cdccc4a03f9a34337790a8f608825f19a2b0d8f271bef2d855f3faec",
            "0ea48e526b5d7edc99318fd00aad7d3ad868d5f19d3e3cd1d112323ddc91ca65",
            "1414d567f130fb14c44776f92aa8d154cbdf2ba59b13f250be69f24cb9292fdf"
        ]),
        new("elf", "male", "em",
        [
            "5ee71ea686ab9a416007e6c76383d6ab0890d4e0c935febea400e15efa6a18c8",
            "07c5a851294651ac49a27c8b075622c93e8ee42be64f3c275c26f7de2928229c",
            "aade2e0bcc4584fc2fe8d95ac8a6c127a8a629916e4578893a095a8251206092",
            "11446fc707f17e07b5f85b175cca302a2910f2335151c1c5b9707004cd454b77"
        ]),
        new("dwarf", "female", "df",
        [
            "1112f92eed5373192b294a393100abb055bac494ea2f4c1bee19b3c02058aa7a",
            "78e6f687ce23777b532866b8f71c81e1651b2dae83d53c3eb106bd4487e132a3",
            "6693b13fbc4a0f02536fadbb3c1641ecf32c6be5585ef1a799417f93c391c0b6",
            "6b5aaafd0c296d59510641835ce2003345b167a2bb52079af5619a54ae733675"
        ]),
        new("dwarf", "male", "dm",
        [
            "5b6e18fd0159ea85753f6d94673489efb3318e674fc98b08d40e0a0394648d0a",
            "c8abd124da803fc090c812a25dd72ba3f85e042f2168e22823989fc3d550cb49",
            "07a01768ed62d066631d7434398d1d055983df4dcaf6f7f4e921af07085a8460",
            "8eab258b28ee7c2f9ed6953255756b49683fe8715ee6702af30f445451503e76"
        ])
    ];

    private static readonly IReadOnlyDictionary<string, (string Standing, string Bed)>
        LegacyEvidence = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["hf_cps_p01"] = (
                "166f5d2bb40b75a044cbc740083101fe7105d6b279c6696709eef5972d87ba7c",
                "166f5d2bb40b75a044cbc740083101fe7105d6b279c6696709eef5972d87ba7c"),
            ["hf_cps_p02"] = (
                "a0fc8d3735481043eca3b798efe1a496a0b49e7cfab2ee9f9b07446aeee560ca",
                "a0fc8d3735481043eca3b798efe1a496a0b49e7cfab2ee9f9b07446aeee560ca"),
            ["hf_cps_p03"] = (
                "9f34abe651bed8d866c25d7e5635b9d54de52bc38cf434e57b8cc26bd7baeae8",
                "9f34abe651bed8d866c25d7e5635b9d54de52bc38cf434e57b8cc26bd7baeae8"),
            ["hf_cps_p04"] = (
                "5b8b3bfa72b3bfe9827ccb46944e6e5c099fd05b4bfccd23742045c059528c36",
                "5b8b3bfa72b3bfe9827ccb46944e6e5c099fd05b4bfccd23742045c059528c36"),
            ["hm_cps_p01"] = (
                "5d9b23040cd8644d98072abeff81ce298a51b9e5e9b1179c81cd76d28e9bfeb4",
                "5d9b23040cd8644d98072abeff81ce298a51b9e5e9b1179c81cd76d28e9bfeb4"),
            ["hm_cps_p02"] = (
                "7d019292755e159b195297c887a9740098b602d3e58d5ae0910e568f8fc83571",
                "7d019292755e159b195297c887a9740098b602d3e58d5ae0910e568f8fc83571"),
            ["hm_cps_p03"] = (
                "4e78a851541fdfcbcb0de722047781da307bf439ffb6d1a45a0d3622c5f26abf",
                "4e78a851541fdfcbcb0de722047781da307bf439ffb6d1a45a0d3622c5f26abf"),
            ["hm_cps_p04"] = (
                "cb64f3b3b85a101329e8ca48eda35e8bab158e0ccdfc10210bcbd7e47b705a90",
                "cb64f3b3b85a101329e8ca48eda35e8bab158e0ccdfc10210bcbd7e47b705a90"),
            ["ef_cps_p01"] = (
                "0f59b095bd1484f2c71a55c8a71bd9936841a1a64f47f5ebe33eb573ea9531ae",
                "0f59b095bd1484f2c71a55c8a71bd9936841a1a64f47f5ebe33eb573ea9531ae"),
            ["ef_cps_p02"] = (
                "030314344bd17a477f8fb4ae3f312a4a957334bab166f11de72bf38d1a0665f9",
                "7e4dfa069f081b5cc238f737a8c5a6f89df037c495a4c3fca9a2a569bd2f9dc8"),
            ["ef_cps_p03"] = (
                "f176cfd80ccd923b061df8fb0f6066e883b1f0ed0d1942d6aecbb262ccde2de6",
                "d0f8d1f3abccb85c8645d4d88c64339b2334f34e2095ba8f4fcc1ef0b9d5f834"),
            ["ef_cps_p04"] = (
                "27e28ff7af6a31ce611e7411211e75a0ec8d2d91aa8d8d56f3e1e9e8129c7e6e",
                "27e28ff7af6a31ce611e7411211e75a0ec8d2d91aa8d8d56f3e1e9e8129c7e6e"),
            ["em_cps_p01"] = (
                "96078e640eaaf8b22ef1f59418e532630a2b7d17fa1fdc6c0b93c034ae3cd099",
                "96078e640eaaf8b22ef1f59418e532630a2b7d17fa1fdc6c0b93c034ae3cd099"),
            ["em_cps_p02"] = (
                "1b2a5dbff51e38ad79755c055542498d57cc6ea72f575e2a4d7b4deca27d2a2f",
                "1b2a5dbff51e38ad79755c055542498d57cc6ea72f575e2a4d7b4deca27d2a2f"),
            ["em_cps_p03"] = (
                "0cd44ceeabd270f8b18dba611a18cdf32e6131512cb4d80087f85ee20c1b4487",
                "0cd44ceeabd270f8b18dba611a18cdf32e6131512cb4d80087f85ee20c1b4487"),
            ["em_cps_p04"] = (
                "cb9532f056478a7333fa90001e38026b0427ec21f3acbce9f31c74377729a651",
                "cb9532f056478a7333fa90001e38026b0427ec21f3acbce9f31c74377729a651"),
            ["df_cps_p01"] = (
                "9c59525caf88bfd1795e90938e1d418690d0487849210481d4e8e8bf98211ee8",
                "9c59525caf88bfd1795e90938e1d418690d0487849210481d4e8e8bf98211ee8"),
            ["df_cps_p02"] = (
                "c799e38df0964161b53e761ee1395ce645a2a293eca61790a690387f46671fd7",
                "c799e38df0964161b53e761ee1395ce645a2a293eca61790a690387f46671fd7"),
            ["df_cps_p03"] = (
                "5c663162176f558a3279da4b4be5c7c8ef427b93c1b7f19f8b223320cefb4925",
                "5c663162176f558a3279da4b4be5c7c8ef427b93c1b7f19f8b223320cefb4925"),
            ["df_cps_p04"] = (
                "1efda7b622895dddb1406d09de4e045f186c95089d7c665ca17857a06da4007f",
                "1efda7b622895dddb1406d09de4e045f186c95089d7c665ca17857a06da4007f"),
            ["dm_cps_p01"] = (
                "3d4a97a438efecaa7817174cdf8f18650dc4966794edb675965fa8326bcf1e40",
                "3d4a97a438efecaa7817174cdf8f18650dc4966794edb675965fa8326bcf1e40"),
            ["dm_cps_p02"] = (
                "efe84fcce6e79f6da016cd23bfbaee59db4876c695ae8917ebbe42664dbd4e33",
                "efe84fcce6e79f6da016cd23bfbaee59db4876c695ae8917ebbe42664dbd4e33"),
            ["dm_cps_p03"] = (
                "28234b40fa464506eb411ae1b331c69791dbe9783dc612300df7307f8d4c65b3",
                "28234b40fa464506eb411ae1b331c69791dbe9783dc612300df7307f8d4c65b3"),
            ["dm_cps_p04"] = (
                "324282b51b7317e2f49028f4b09dc5034ad694dbe5811aaf368156cbee79fb2a",
                "324282b51b7317e2f49028f4b09dc5034ad694dbe5811aaf368156cbee79fb2a")
        };

    private static readonly IReadOnlyList<DragonAgeCharacterCreationAppearance> appearances =
        BuildAppearances();
    private static readonly IReadOnlyDictionary<string, DragonAgeCharacterCreationAppearance> byKey =
        new ReadOnlyDictionary<string, DragonAgeCharacterCreationAppearance>(
            appearances.ToDictionary(value => value.SelectionKey, StringComparer.Ordinal));

    public static IReadOnlyList<DragonAgeCharacterCreationAppearance> Appearances => appearances;

    public static DragonAgeCharacterCreationAppearance? Resolve(
        string race, string gender, string preset)
    {
        if (string.IsNullOrWhiteSpace(race) || string.IsNullOrWhiteSpace(gender) ||
            string.IsNullOrWhiteSpace(preset)) return null;
        var key = $"{race.Trim().ToLowerInvariant()}:" +
                  $"{gender.Trim().ToLowerInvariant()}:" +
                  preset.Trim().ToLowerInvariant();
        return byKey.GetValueOrDefault(key);
    }

    public static DragonAgeCharacterSelectionState TransitionSelection(
        DragonAgeCharacterCreationAppearance? appearance,
        DragonAgeCharacterImportReadiness readiness) =>
        appearance is not null && readiness is
            DragonAgeCharacterImportReadiness.LegacyEvidence or
            DragonAgeCharacterImportReadiness.FreshImport
            ? new DragonAgeCharacterSelectionState(appearance.SelectionKey, true)
            : DragonAgeCharacterSelectionState.Empty;

    public static bool CanStart(
        DragonAgeCharacterSelectionState state,
        DragonAgeCharacterCreationAppearance appearance) =>
        state.ActorVisible && state.SelectionKey == appearance.SelectionKey;

    public static DragonAgeCharacterImportReadiness ClassifyImport(
        DragonAgeCharacterCreationAppearance appearance,
        DragonAgeCharacterCreationImportManifest? manifest,
        string? standingSha256,
        string? bedSha256)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        if (!IsSha256(standingSha256) || !IsSha256(bedSha256))
            return DragonAgeCharacterImportReadiness.Missing;

        if (manifest is not null)
        {
            ValidateManifest(appearance, manifest);
            if (!string.Equals(standingSha256, manifest.StandingSha256,
                    StringComparison.Ordinal) ||
                !string.Equals(bedSha256, manifest.BedSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"Imported character payload hash disagrees with its manifest: " +
                    appearance.SelectionKey);
            return DragonAgeCharacterImportReadiness.FreshImport;
        }

        return appearance.HasLegacyEvidence &&
               string.Equals(standingSha256, appearance.LegacyStandingSha256,
                   StringComparison.Ordinal) &&
               string.Equals(bedSha256, appearance.LegacyBedSha256,
                   StringComparison.Ordinal)
            ? DragonAgeCharacterImportReadiness.LegacyEvidence
            : DragonAgeCharacterImportReadiness.Missing;
    }

    public static void ValidateManifest(
        DragonAgeCharacterCreationAppearance appearance,
        DragonAgeCharacterCreationImportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        ArgumentNullException.ThrowIfNull(manifest);
        var valid = manifest.Schema == ManifestSchema &&
                    manifest.ImporterId == ImporterId &&
                    manifest.SelectionKey == appearance.SelectionKey &&
                    manifest.CatalogContainerRelativePath == CatalogContainerRelativePath &&
                    manifest.CatalogContainerSha256 == CatalogContainerSha256 &&
                    manifest.CatalogResource == CatalogResource &&
                    manifest.CatalogResourceSha256 == CatalogResourceSha256 &&
                    manifest.SourceContainerRelativePath == SourceContainerRelativePath &&
                    manifest.SourceContainerSha256 == SourceContainerSha256 &&
                    manifest.SourceResource == appearance.MorphResource &&
                    manifest.SourcePayloadSha256 == appearance.MorphSha256 &&
                    manifest.StandingRelativePath == appearance.StandingRelativePath &&
                    manifest.BedRelativePath == appearance.BedRelativePath &&
                    IsSha256(manifest.StandingSha256) &&
                    IsSha256(manifest.BedSha256);
        if (!valid)
            throw new InvalidDataException(
                $"Character import manifest identity is invalid: {appearance.SelectionKey}");
    }

    private static IReadOnlyList<DragonAgeCharacterCreationAppearance> BuildAppearances()
    {
        var result = new List<DragonAgeCharacterCreationAppearance>(ExpectedSelectionCount);
        foreach (var family in Families)
            for (var index = 0; index < family.MorphHashes.Length; index++)
            {
                var number = index + 1;
                var stem = $"{family.Prefix}_cps_p{number:00}";
                LegacyEvidence.TryGetValue(stem, out var legacy);
                result.Add(new DragonAgeCharacterCreationAppearance(
                    family.Race,
                    family.Gender,
                    $"preset-{number}",
                    stem + ".mop",
                    family.MorphHashes[index],
                    $"quickplay-characters/{stem}.glb",
                    $"quickplay-characters/{stem}-bed.glb",
                    $"quickplay-characters/{stem}.import.json",
                    legacy.Standing,
                    legacy.Bed));
            }

        if (result.Count != ExpectedSelectionCount ||
            result.Select(value => value.SelectionKey).Distinct(StringComparer.Ordinal).Count() !=
            ExpectedSelectionCount ||
            result.Any(value => !IsSha256(value.MorphSha256) ||
                                value.HasLegacyEvidence !=
                                (value.LegacyStandingSha256 is not null &&
                                 value.LegacyBedSha256 is not null)))
            throw new InvalidDataException(
                "Dragon Age character-creation catalog is internally inconsistent.");
        return result.AsReadOnly();
    }

    private static bool IsSha256(string? value) => value is { Length: 64 } &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
