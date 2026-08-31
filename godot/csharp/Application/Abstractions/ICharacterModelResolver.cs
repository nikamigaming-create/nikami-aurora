using Nikami.Aurora.GodotRuntime.Domain.Characters;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface ICharacterModelResolver
{
    string Resolve(CharacterProfile character);
}
