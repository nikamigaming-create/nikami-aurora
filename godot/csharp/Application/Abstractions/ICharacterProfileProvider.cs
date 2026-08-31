using Nikami.Aurora.GodotRuntime.Domain.Characters;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface ICharacterProfileProvider
{
    string ProfilePath { get; }
    CharacterProfile Load();
}
