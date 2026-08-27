using OpenDAO.Domain.Characters;

namespace OpenDAO.Application.Abstractions;

public sealed record CharacterCinematicModels(string StandingModelPath, string BedModelPath);

public interface ICharacterCinematicModelResolver
{
    CharacterCinematicModels Resolve(CharacterProfile character, string standingModelPath);
}
