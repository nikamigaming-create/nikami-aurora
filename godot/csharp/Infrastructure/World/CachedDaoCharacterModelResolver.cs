using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Application.Characters;
using OpenDAO.Domain.Characters;
using OpenDAO.Infrastructure.Configuration;

namespace OpenDAO.Infrastructure.World;

public sealed class CachedDaoCharacterModelResolver : ICharacterModelResolver
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> models = new(BuildModelIndex);

    public string Resolve(CharacterProfile character)
    {
        if (RetailCharacterAppearanceCatalog.Resolve(character.Race, character.Gender,
                character.Appearance) is { } retail)
        {
            var authored = DaoRuntimePaths.Cache(retail.StandingRelativePath);
            if (File.Exists(authored))
            {
                GD.Print($"OPENDAO_CHARACTER_APPEARANCE status=ready morph={retail.Morph} " +
                         "source=retail-quickplay-preset");
                return authored;
            }
            GD.PushWarning($"OPENDAO_CHARACTER_APPEARANCE status=missing morph={retail.Morph} " +
                           $"path={authored}");
        }
        var preset = int.TryParse(character.Appearance.AsSpan(character.Appearance.LastIndexOf('-') + 1),
            out var parsed)
            ? Math.Clamp(parsed, 1, 4)
            : 1;
        string[] candidates = (character.Race, character.Gender) switch
        {
            ("dwarf", "female") => [$"bdc100cr_amb_f_{preset}.glb", "bdn120cr_rica.glb"],
            ("dwarf", _) => [$"bdc100cr_amb_m_{preset}.glb", "bdn100cr_gorim.glb"],
            ("elf", "female") =>
            [
                new[] { "bec210cr_elf_servantf.glb", "bec100cr_elf_commoner_f.glb",
                    "den300cr_crowd_elf_fem_3.glb", "ntb100cr_elf_female_03.glb" }[preset - 1]
            ],
            ("elf", _) =>
            [
                new[] { "bec210cr_elf_servantm.glb", "bec100cr_homeless_elf_man.glb",
                    "den300cr_crowd_elf_male_3.glb", "ntb100cr_elf_male_03.glb" }[preset - 1]
            ],
            ("human", "female") =>
            [
                new[] { "arl110cr_villager_f_1.glb", "arl110cr_villager_f_2.glb",
                    "arl150cr_bella.glb", "den211cr_nigella.glb" }[preset - 1]
            ],
            _ =>
            [
                new[] { "arl100cr_tomas.glb", "arl100cr_militia_1.glb",
                    "arl100cr_murdock.glb", "arl100cr_watchman.glb" }[preset - 1]
            ]
        };

        foreach (var candidate in candidates)
        {
            var playable = DaoRuntimePaths.Cache("playable-characters",
                "lak100d", "actors", candidate);
            if (File.Exists(playable)) return playable;
            if (models.Value.TryGetValue(candidate, out var path)) return path;
        }
        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> BuildModelIndex()
    {
        var root = DaoRuntimePaths.Cache("areas");
        if (!Directory.Exists(root)) return new Dictionary<string, string>();
        return Directory.EnumerateFiles(root, "*.glb", SearchOption.AllDirectories)
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key is not null)
            .ToDictionary(group => group.Key!, group => group.First(), StringComparer.OrdinalIgnoreCase);
    }
}
