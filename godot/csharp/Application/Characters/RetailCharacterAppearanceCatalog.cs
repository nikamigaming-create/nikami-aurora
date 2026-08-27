namespace OpenDAO.Application.Characters;

public sealed record RetailCharacterAppearance(
    string Morph,
    string StandingRelativePath,
    string BedRelativePath);

/// <summary>
/// Maps an authored character-creation selection to the installed retail
/// morph exports used consistently by preview, cinematics, and gameplay.
/// </summary>
public static class RetailCharacterAppearanceCatalog
{
    public static RetailCharacterAppearance? Resolve(string race, string gender,
        string appearance) => (race.ToLowerInvariant(), gender.ToLowerInvariant(),
        appearance.ToLowerInvariant()) switch
        {
            ("elf", "female", "preset-1") => new RetailCharacterAppearance(
                "ef_cps_p01.mop",
                "quickplay-characters/ef_cps_p01.glb",
                "quickplay-characters/ef_cps_p01-bed.glb"),
            ("elf", "female", "preset-2") => new RetailCharacterAppearance(
                "ef_cps_p02.mop",
                "quickplay-characters/ef_cps_p02.glb",
                "quickplay-characters/ef_cps_p02-bed.glb"),
            ("elf", "female", "preset-3") => new RetailCharacterAppearance(
                "ef_cps_p03.mop",
                "quickplay-characters/ef_cps_p03.glb",
                "quickplay-characters/ef_cps_p03-bed.glb"),
            ("elf", "female", "preset-4") => new RetailCharacterAppearance(
                "ef_cps_p04.mop",
                "quickplay-characters/ef_cps_p04.glb",
                "quickplay-characters/ef_cps_p04-bed.glb"),
            _ => null
        };
}
