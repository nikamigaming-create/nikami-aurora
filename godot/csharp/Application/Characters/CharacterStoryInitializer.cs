using OpenDAO.Domain.Characters;
using OpenDAO.Domain.Common;
using OpenDAO.Domain.Story;

namespace OpenDAO.Application.Characters;

/// <summary>
/// Projects the selected character into the retail global class/race/gender plot.
/// Dialogue conditions consume these plot flags directly, just as the installed
/// game's DLG graphs do.
/// </summary>
public sealed class CharacterStoryInitializer(StoryState story)
{
    public const string ClassRaceGenderPlotGuid = "64F06DB1ED4B49F18DF326A0B1C2D06C";

    private static readonly IReadOnlyDictionary<string, int> ClassFlags =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["mage"] = 256,
            ["rogue"] = 257,
            ["warrior"] = 258,
        };

    private static readonly IReadOnlyDictionary<string, int> GenderFlags =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["female"] = 259,
            ["male"] = 260,
        };

    private static readonly IReadOnlyDictionary<string, int> RaceFlags =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["dwarf"] = 261,
            ["elf"] = 262,
            ["human"] = 263,
        };

    public OperationResult Initialize(CharacterProfile character)
    {
        if (!ClassFlags.TryGetValue(character.Class, out var classFlag))
            return OperationResult.Unsupported("character-story-class-unsupported",
                ("class", character.Class));
        if (!GenderFlags.TryGetValue(character.Gender, out var genderFlag))
            return OperationResult.Unsupported("character-story-gender-unsupported",
                ("gender", character.Gender));
        if (!RaceFlags.TryGetValue(character.Race, out var raceFlag))
            return OperationResult.Unsupported("character-story-race-unsupported",
                ("race", character.Race));

        SetExclusive(ClassFlags.Values, classFlag);
        SetExclusive(GenderFlags.Values, genderFlag);
        SetExclusive(RaceFlags.Values, raceFlag);
        return OperationResult.Complete(
            ("plotGuid", ClassRaceGenderPlotGuid),
            ("classFlag", classFlag),
            ("genderFlag", genderFlag),
            ("raceFlag", raceFlag),
            ("source", "installed-plt_gen00pt_class_race_gend.plo"));
    }

    private void SetExclusive(IEnumerable<int> flags, int selected)
    {
        foreach (var flag in flags)
            story.SetPlotFlag(ClassRaceGenderPlotGuid, flag, flag == selected);
    }
}
