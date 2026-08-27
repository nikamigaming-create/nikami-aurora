using OpenDAO.Domain.Sessions;

namespace OpenDAO.Application.Abstractions;

public interface IPlayerSessionRepository
{
    PlayerSession? Load();
    bool Save(PlayerSession session);
}
