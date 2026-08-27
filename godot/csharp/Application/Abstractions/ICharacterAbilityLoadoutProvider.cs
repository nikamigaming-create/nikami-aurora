using OpenDAO.Domain.Abilities;
using OpenDAO.Domain.Characters;

namespace OpenDAO.Application.Abstractions;

public interface ICharacterAbilityLoadoutProvider
{
    string Error { get; }
    CharacterAbilityLoadout? Resolve(CharacterProfile character);
}
