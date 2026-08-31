using Nikami.Aurora.GodotRuntime.Domain.Characters;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public sealed record CharacterCinematicModels(string StandingModelPath, string BedModelPath);

public interface ICharacterCinematicModelResolver
{
    CharacterCinematicModels Resolve(CharacterProfile character, string standingModelPath);
}
