using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Application.Characters;
using OpenDAO.Domain.Characters;
using OpenDAO.Infrastructure.Configuration;

namespace OpenDAO.Infrastructure.World;

public sealed class CachedDaoCharacterCinematicModelResolver : ICharacterCinematicModelResolver
{
    public CharacterCinematicModels Resolve(CharacterProfile character, string standingModelPath)
    {
        if (string.IsNullOrWhiteSpace(standingModelPath))
            return new CharacterCinematicModels(string.Empty, string.Empty);
        var retail = RetailCharacterAppearanceCatalog.Resolve(character.Race, character.Gender,
            character.Appearance);
        var bedPath = retail is not null
            ? DaoRuntimePaths.Cache(retail.BedRelativePath)
            : DaoRuntimePaths.Cache("playable-character-bed", "lak100d", "actors",
                Path.GetFileName(standingModelPath));
        if (!File.Exists(bedPath))
        {
            GD.PushWarning($"OPENDAO_CINEMATIC_BED_MODEL status=missing character={character.Name} " +
                           $"path={bedPath}");
            bedPath = string.Empty;
        }
        else
            GD.Print($"OPENDAO_CINEMATIC_BED_MODEL status=ready character={character.Name} " +
                     $"gender={character.Gender} source=" +
                     $"{(retail is null ? "selected-character-cinematic-export" : "retail-quickplay-preset")}");
        return new CharacterCinematicModels(standingModelPath, bedPath);
    }
}
