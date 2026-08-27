using OpenDAO.Domain.Characters;

namespace OpenDAO.Application.Abstractions;

public interface ICharacterProfileProvider
{
    string ProfilePath { get; }
    CharacterProfile Load();
}
