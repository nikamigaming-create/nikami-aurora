using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IWorldProfileProvider
{
    string ResolveProfilePath();
    WorldProfile Load();
}
