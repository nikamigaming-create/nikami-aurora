using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Application.Characters;

public sealed class CharacterProgression
{
    private readonly DragonAgeOriginsExperienceTable table;

    public CharacterProgression(DragonAgeOriginsExperienceTable table, int experience = 0)
    {
        this.table = table ?? throw new ArgumentNullException(nameof(table));
        if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
        Experience = experience;
    }

    public int Experience { get; private set; }
    public int Level => Math.Max(1, table.ResolveLevel(Experience));
    public int NextLevelExperience => table.MinimumExperienceFor(Level + 1);

    public event Action<int, int>? Changed;

    public void Award(int amount)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        SetExperience(checked(Experience + amount));
    }

    public bool ApplyCreatureProperty(int action, int property, float value, int valueType,
        out string reason)
    {
        if (!DragonAgeOriginsCreatureProperty.TryApplyExperience(action, property, value, valueType,
                Experience, out var updated, out reason)) return false;
        SetExperience(updated);
        return true;
    }

    private void SetExperience(int value)
    {
        if (value == Experience) return;
        Experience = value;
        Changed?.Invoke(Experience, Level);
    }
}
