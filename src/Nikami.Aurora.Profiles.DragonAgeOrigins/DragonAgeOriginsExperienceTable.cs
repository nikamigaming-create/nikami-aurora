namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeLevelThreshold(int Level, int MinimumExperience);

/// <summary>
/// Profile-owned interpretation of the installed exptable.gda rows. Parsing
/// the GDA container remains an adapter concern; level semantics live here.
/// </summary>
public sealed class DragonAgeOriginsExperienceTable
{
    public const string TableName = "exptable";
    public const long LevelColumnHash = 1727777078;
    public const long MinimumExperienceColumnHash = 2700095129;

    private readonly DragonAgeLevelThreshold[] thresholds;

    public DragonAgeOriginsExperienceTable(IEnumerable<DragonAgeLevelThreshold> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        thresholds = source.OrderBy(value => value.Level).ToArray();
        if (thresholds.Length == 0 || thresholds[0].Level != 0 ||
            thresholds.Any(value => value.Level < 0 || value.MinimumExperience < 0) ||
            thresholds.Select(value => value.Level).Distinct().Count() != thresholds.Length ||
            thresholds.Zip(thresholds.Skip(1), (left, right) =>
                    right.Level == left.Level + 1 &&
                    right.MinimumExperience >= left.MinimumExperience)
                .Any(valid => !valid))
            throw new InvalidDataException("DAO experience thresholds are inconsistent.");
    }

    public IReadOnlyList<DragonAgeLevelThreshold> Thresholds => thresholds;

    public int ResolveLevel(int experience)
    {
        if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
        return thresholds.Last(value => value.MinimumExperience <= experience).Level;
    }

    public int MinimumExperienceFor(int level) =>
        thresholds.FirstOrDefault(value => value.Level == level)?.MinimumExperience ??
        throw new ArgumentOutOfRangeException(nameof(level));
}
