using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IAuthoredLightingResolver
{
    AuthoredLightingProfile? Resolve(WorldProfile world, AuthoredLightingProfile? authored);
}
