using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IAreaPresentationProvider
{
    AreaPresentationResult Resolve(WorldProfile profile);
}
