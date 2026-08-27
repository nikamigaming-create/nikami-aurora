using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.Characters;

namespace OpenDAO.Infrastructure.Configuration;

public sealed class CharacterProfileProvider(IJsonStore store, IRuntimeEnvironment environment)
    : ICharacterProfileProvider
{
    private const string DefaultPath = "user://opendao-character.json";
    public string ProfilePath => environment.Get("OPENDAO_CHARACTER_PROFILE") is { Length: > 0 } value
        ? value : DefaultPath;

    public CharacterProfile Load()
    {
        var document = store.Read(ProfilePath);
        if (document?["schema"]?.GetValue<string>() != "opendao-character-v1") return CharacterProfile.Default;
        var name = document["name"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(name)) return CharacterProfile.Default;
        return new(name,
            document["origin"]?.GetValue<string>() ?? "human-noble",
            document["race"]?.GetValue<string>() ?? "human",
            document["gender"]?.GetValue<string>() ?? "female",
            document["class"]?.GetValue<string>() ?? "warrior",
            document["appearance"]?.GetValue<string>() ?? "preset-1");
    }
}
