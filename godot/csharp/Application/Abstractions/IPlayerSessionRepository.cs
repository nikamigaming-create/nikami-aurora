using Nikami.Aurora.GodotRuntime.Domain.Sessions;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IPlayerSessionRepository
{
    PlayerSession? Load();
    bool Save(PlayerSession session);
}
