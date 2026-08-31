using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Application.Characters;

/// <summary>
/// Maps an authored character-creation selection to the installed retail
/// morph exports used consistently by preview, cinematics, and gameplay.
/// </summary>
public static class RetailCharacterAppearanceCatalog
{
    public static IReadOnlyList<DragonAgeCharacterCreationAppearance> Appearances =>
        DragonAgeOriginsCharacterCreationCatalog.Appearances;

    public static DragonAgeCharacterCreationAppearance? Resolve(
        string race, string gender, string appearance) =>
        DragonAgeOriginsCharacterCreationCatalog.Resolve(race, gender, appearance);
}
