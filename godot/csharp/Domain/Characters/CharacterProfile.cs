namespace OpenDAO.Domain.Characters;

public sealed record CharacterProfile(string Name, string Origin, string Race, string Gender,
    string Class, string Appearance)
{
    public static CharacterProfile Default { get; } =
        new("Warden", "human-noble", "human", "female", "warrior", "preset-1");
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "Warden" : Name.Trim()[..Math.Min(32, Name.Trim().Length)];
}
