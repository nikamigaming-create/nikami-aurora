using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace OpenDAO.MainMenu;

internal sealed record CharacterChoice(
    string Id,
    string Label,
    string Icon,
    string Description = "");

internal sealed record CharacterOrigin(
    string Id,
    string Label,
    string Icon,
    string Description,
    string AreaId,
    string Archive,
    string Waypoint,
    string OpeningCutscene);

internal static class CharacterProfileRules
{
    public static readonly IReadOnlyList<CharacterChoice> Races =
    [
        new("human", "Human", "cg_ico_race_female_human.dds",
            "Humans are the most numerous and politically powerful people in Ferelden."),
        new("elf", "Elf", "cg_ico_race_female_elf.dds",
            "Elves live as second-class citizens or keep the old ways among the Dalish clans."),
        new("dwarf", "Dwarf", "cg_ico_race_female_dwarf.dds",
            "Dwarves are a tough, practical people whose lives are shaped by caste and tradition."),
    ];

    public static readonly IReadOnlyList<CharacterChoice> Genders =
    [
        new("female", "Female", "cg_ico_gender_female.dds"),
        new("male", "Male", "cg_ico_gender_male.dds"),
    ];

    public static readonly IReadOnlyList<CharacterChoice> Classes =
    [
        new("warrior", "Warrior", "classico_warrior.dds",
            "Warriors are powerful fighters who rely on strength, discipline, and heavy arms."),
        new("mage", "Mage", "classico_mage.dds",
            "Mages command raw magical power, but live under the watch of the templars."),
        new("rogue", "Rogue", "classico_rogue.dds",
            "Rogues win through speed, precision, stealth, and an eye for opportunity."),
    ];

    public static readonly IReadOnlyList<CharacterChoice> Appearances =
    [
        new("preset-1", "Preset 1", "CharGen_IFD.dds"),
        new("preset-2", "Preset 2", "CharGen_IFD.dds"),
        new("preset-3", "Preset 3", "CharGen_IFD.dds"),
        new("preset-4", "Preset 4", "CharGen_IFD.dds"),
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<CharacterOrigin>> MartialOrigins =
        new Dictionary<string, IReadOnlyList<CharacterOrigin>>(StringComparer.OrdinalIgnoreCase)
        {
            ["human"] =
            [
                new("human-noble", "Human Noble", "cg_ico_origin_human_noble.dds",
                    "Born to wealth and power second only to royalty, you find your training in both diplomacy and battle put to the test as your brother leads the bulk of your family's forces to war in the south.",
                    "bhn100ar_castle_cousland", "al_bhn01al_castle_cousland.rim", "bhn100wp_start",
                    "bhn100cs_intro")
            ],
            ["elf"] =
            [
                new("city-elf", "City Elf", "cg_ico_origin_elf_city.dds",
                    "You have always lived under the heavy thumb of your human overlords, but when a local lord claiming his “privilege” with the bride shatters your wedding day, the simmering racial tensions explode in a rain of vengeance.",
                    "bec110ar_players_house", "al_bec01al_alienage.rim", "bec110wp_start", "start_wake"),
                new("dalish-elf", "Dalish Elf", "cg_ico_origin_elf_dalish.dds",
                    "Proud of your role as one of the few “true elves,” you have always assumed you would spend your life with your tribe... until a chance encounter with a relic of your people's past threatens to tear you away from everything you have ever known.",
                    "bed100ar_forest_clearing", "al_bed100ar_forest_clearing.rim", "bed100wp_start",
                    "bed100cs_intro")
            ],
            ["dwarf"] =
            [
                new("dwarf-commoner", "Dwarf Commoner", "cg_ico_origin_dwarf_common.dds",
                    "Born casteless in a land where rank is everything, bound as the lackey and thug of a local crime lord, you have spent your life invisible... until chance thrusts you into the spotlight, where you can finally prove whether you will be defined by your actions or your birth.",
                    "bdc200ar_slums", "al_bdc02al_slums.rim", "start", "bdccs_intro"),
                new("dwarf-noble", "Dwarf Noble", "cg_ico_origin_dwarf_noble.dds",
                    "As the favored child of the dwarven king, you proudly take up your first military command... only to learn that the deadly intrigues of family and sycophants may pose a greater danger than even the battlefield.",
                    "bdn120ar_royal_palace", "al_bdn120ar_royal_palace.rim", "start", "bdncs_intro")
            ],
        };

    private static readonly IReadOnlyList<CharacterOrigin> MageOrigins =
    [
        new("circle-mage", "Magi", "cg_ico_origin_mage.dds",
            "Wielding a power as dangerous as it is potent, you know that magic is a curse for those lacking the will to control it. You anxiously await your Harrowing, the one chance to prove yourself against the demons lurking without and within. Succeed, or be slaughtered by the knights who ward against your kind.",
            "bhm500ar_tower_harrowing", "al_bhm500ar_tower_harrowing.rim", "bhm400wp_start",
            "bhm500cs_harrowing")
    ];

    public static IReadOnlyList<CharacterOrigin> OriginsFor(string race, string characterClass)
    {
        if (characterClass.Equals("mage", StringComparison.OrdinalIgnoreCase))
        {
            return race.Equals("dwarf", StringComparison.OrdinalIgnoreCase) ? [] : MageOrigins;
        }

        return MartialOrigins.GetValueOrDefault(race, []);
    }

    public static CharacterOrigin? OriginFor(string id) =>
        MartialOrigins.Values.SelectMany(value => value).Concat(MageOrigins)
            .FirstOrDefault(origin => origin.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static string RaceIcon(string race, string gender) =>
        $"cg_ico_race_{gender.ToLowerInvariant()}_{race.ToLowerInvariant()}.dds";

    public static bool Validate(CharacterProfile profile, out string error)
    {
        error = string.Empty;
        if (!profile.Schema.Equals(CharacterProfile.SchemaName, StringComparison.Ordinal))
        {
            error = "Unsupported character profile";
        }
        else if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name.Length > 32 ||
                 profile.Name.Any(char.IsControl))
        {
            error = "Enter a character name (1–32 characters)";
        }
        else if (!Contains(Races, profile.Race) || !Contains(Genders, profile.Gender) ||
                 !Contains(Classes, profile.CharacterClass) || !Contains(Appearances, profile.Appearance))
        {
            error = "Choose a valid race, gender, class, and appearance";
        }
        else if (!Contains(OriginsFor(profile.Race, profile.CharacterClass), profile.Origin))
        {
            error = "That origin is not available for the selected race and class";
        }

        return error.Length == 0;
    }

    public static string LabelFor(IReadOnlyList<CharacterChoice> choices, string id) =>
        choices.FirstOrDefault(choice => choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Label ?? id;

    private static bool Contains(IReadOnlyList<CharacterChoice> choices, string id) =>
        choices.Any(choice => choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static bool Contains(IReadOnlyList<CharacterOrigin> choices, string id) =>
        choices.Any(choice => choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

internal sealed record CharacterProfile(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("origin")] string Origin,
    [property: JsonPropertyName("race")] string Race,
    [property: JsonPropertyName("gender")] string Gender,
    [property: JsonPropertyName("class")] string CharacterClass,
    [property: JsonPropertyName("appearance")] string Appearance,
    [property: JsonPropertyName("createdAtUnix")] long CreatedAtUnix)
{
    public const string SchemaName = "opendao-character-v1";

    public static CharacterProfile Create(string name, string origin, string race, string gender,
        string characterClass, string appearance) => new(
        SchemaName,
        name.Trim(),
        origin.ToLowerInvariant(),
        race.ToLowerInvariant(),
        gender.ToLowerInvariant(),
        characterClass.ToLowerInvariant(),
        appearance.ToLowerInvariant(),
        DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public static CharacterProfile LegacyDefault() =>
        Create("Warden", "human-noble", "human", "female", "warrior", "preset-1");
}

internal static class CharacterProfileStore
{
    public const string DefaultPath = "user://opendao-character.json";
    private const string PathEnvironmentVariable = "OPENDAO_CHARACTER_PROFILE";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string ResolvePath()
    {
        var configured = OS.GetEnvironment(PathEnvironmentVariable);
        var path = configured.Length > 0 ? configured : DefaultPath;
        return path.StartsWith("user://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;
    }

    public static bool Save(CharacterProfile profile, out string error)
    {
        error = string.Empty;
        if (!CharacterProfileRules.Validate(profile, out error))
        {
            return false;
        }

        var path = ResolvePath();
        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(temporaryPath,
                JsonSerializer.Serialize(profile, JsonOptions) + System.Environment.NewLine);
            File.Move(temporaryPath, path, true);
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not save the character: " + exception.Message;
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
            return false;
        }
    }

    public static bool TryLoad(out CharacterProfile profile, out string error)
    {
        profile = CharacterProfile.LegacyDefault();
        error = string.Empty;
        var path = ResolvePath();
        if (!File.Exists(path))
        {
            error = "No character has been created";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CharacterProfile>(File.ReadAllText(path));
            if (parsed is null || !CharacterProfileRules.Validate(parsed, out error))
            {
                return false;
            }
            profile = parsed;
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not read the character: " + exception.Message;
            return false;
        }
    }
}
