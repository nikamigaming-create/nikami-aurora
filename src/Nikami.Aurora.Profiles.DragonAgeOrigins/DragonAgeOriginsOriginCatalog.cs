using System.Collections.ObjectModel;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeOriginRoute(
    string Id,
    string Label,
    string Race,
    bool MageOnly,
    string Icon,
    string Description,
    string AreaId,
    string Archive,
    string Waypoint,
    string OpeningCutscene,
    string OpeningDialogue = "");

/// <summary>
/// Profile-owned identities for the six retail origin routes. The shared Godot
/// host consumes these records without knowing DAO area, archive, waypoint, or
/// cinematic identifiers.
/// </summary>
public static class DragonAgeOriginsOriginCatalog
{
    private static readonly IReadOnlyList<DragonAgeOriginRoute> routes =
        Array.AsReadOnly<DragonAgeOriginRoute>(
        [
            new("human-noble", "Human Noble", "human", false,
                "cg_ico_origin_human_noble.dds",
                "Born to wealth and power second only to royalty, you find your training in both diplomacy and battle put to the test as your brother leads the bulk of your family's forces to war in the south.",
                "bhn100ar_castle_cousland", "al_bhn01al_castle_cousland.rim",
                "bhn100wp_start", "bhn100cs_intro"),
            new("city-elf", "City Elf", "elf", false,
                "cg_ico_origin_elf_city.dds",
                "You have always lived under the heavy thumb of your human overlords, but when a local lord claiming his “privilege” with the bride shatters your wedding day, the simmering racial tensions explode in a rain of vengeance.",
                "bec110ar_players_house", "al_bec01al_alienage.rim",
                "bec110wp_start", "start_wake", "bec110cr_shianni"),
            new("dalish-elf", "Dalish Elf", "elf", false,
                "cg_ico_origin_elf_dalish.dds",
                "Proud of your role as one of the few “true elves,” you have always assumed you would spend your life with your tribe... until a chance encounter with a relic of your people's past threatens to tear you away from everything you have ever known.",
                "bed100ar_forest_clearing", "al_bed100ar_forest_clearing.rim",
                "bed100wp_start", "bed100cs_intro"),
            new("dwarf-commoner", "Dwarf Commoner", "dwarf", false,
                "cg_ico_origin_dwarf_common.dds",
                "Born casteless in a land where rank is everything, bound as the lackey and thug of a local crime lord, you have spent your life invisible... until chance thrusts you into the spotlight, where you can finally prove whether you will be defined by your actions or your birth.",
                "bdc200ar_slums", "al_bdc02al_slums.rim", "start", "bdccs_intro"),
            new("dwarf-noble", "Dwarf Noble", "dwarf", false,
                "cg_ico_origin_dwarf_noble.dds",
                "As the favored child of the dwarven king, you proudly take up your first military command... only to learn that the deadly intrigues of family and sycophants may pose a greater danger than even the battlefield.",
                "bdn120ar_royal_palace", "al_bdn120ar_royal_palace.rim",
                "start", "bdncs_intro"),
            new("circle-mage", "Magi", "human|elf", true,
                "cg_ico_origin_mage.dds",
                "Wielding a power as dangerous as it is potent, you know that magic is a curse for those lacking the will to control it. You anxiously await your Harrowing, the one chance to prove yourself against the demons lurking without and within. Succeed, or be slaughtered by the knights who ward against your kind.",
                "bhm500ar_tower_harrowing", "al_bhm500ar_tower_harrowing.rim",
                "bhm400wp_start", "bhm500cs_harrowing")
        ]);

    private static readonly IReadOnlyDictionary<string, DragonAgeOriginRoute> byId =
        new ReadOnlyDictionary<string, DragonAgeOriginRoute>(
            routes.ToDictionary(value => value.Id, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<DragonAgeOriginRoute> Routes => routes;

    public static DragonAgeOriginRoute? Resolve(string id) =>
        string.IsNullOrWhiteSpace(id) ? null : byId.GetValueOrDefault(id.Trim());

    public static IReadOnlyList<DragonAgeOriginRoute> For(string race, string characterClass)
    {
        if (string.IsNullOrWhiteSpace(race) || string.IsNullOrWhiteSpace(characterClass)) return [];
        var normalizedRace = race.Trim().ToLowerInvariant();
        var mage = characterClass.Trim().Equals("mage", StringComparison.OrdinalIgnoreCase);
        return routes.Where(route => route.MageOnly == mage &&
                                     route.Race.Split('|').Contains(normalizedRace,
                                         StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }
}
