using OpenDAO.Domain.Characters;

namespace OpenDAO.Application.Abstractions;

public interface ICharacterModelResolver
{
    string Resolve(CharacterProfile character);
}
