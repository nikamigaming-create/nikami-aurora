namespace Nikami.Aurora.Profiles.Kotor;

public sealed record KotorLevelThreshold(int Level, int MinimumExperience);

/// <summary>
/// Profile-owned interpretation of Odyssey exptable.2da. The importer owns the
/// binary format; runtime code consumes only the neutral level thresholds.
/// </summary>
public sealed class KotorExperienceTable
{
    private readonly KotorLevelThreshold[] thresholds;

    public KotorExperienceTable(
        string sourceSha256,
        IEnumerable<KotorLevelThreshold> source)
    {
        if (sourceSha256.Length != 64 || !sourceSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Odyssey experience table has no valid source SHA-256.");
        SourceSha256 = sourceSha256.ToUpperInvariant();
        thresholds = source.OrderBy(value => value.Level).ToArray();
        if (thresholds.Length == 0 || thresholds[0] != new KotorLevelThreshold(1, 0) ||
            thresholds.Any(value => value.Level < 1 || value.MinimumExperience < 0) ||
            thresholds.Select(value => value.Level).Distinct().Count() != thresholds.Length ||
            thresholds.Zip(thresholds.Skip(1)).Any(pair =>
                pair.Second.Level != pair.First.Level + 1 ||
                pair.Second.MinimumExperience <= pair.First.MinimumExperience))
            throw new InvalidDataException("Odyssey experience thresholds are inconsistent.");
    }

    public string SourceSha256 { get; }
    public IReadOnlyList<KotorLevelThreshold> Thresholds => thresholds;

    public int ResolveLevel(int experience)
    {
        if (experience < 0) throw new ArgumentOutOfRangeException(nameof(experience));
        return thresholds.Last(value => value.MinimumExperience <= experience).Level;
    }

    public int? MinimumExperienceFor(int level) =>
        thresholds.FirstOrDefault(value => value.Level == level)?.MinimumExperience;
}
