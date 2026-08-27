using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IAuthoredNavigationGridSource
{
    AuthoredNavigationGrid? Load(WorldProfile profile);
}
