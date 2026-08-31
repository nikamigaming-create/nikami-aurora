using Nikami.Aurora.GodotRuntime.Domain.Abilities;
using Nikami.Aurora.GodotRuntime.Domain.Characters;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface ICharacterAbilityLoadoutProvider
{
    string Error { get; }
    CharacterAbilityLoadout? Resolve(CharacterProfile character);
}
