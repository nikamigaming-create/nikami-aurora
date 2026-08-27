using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface ICharacterLightingBinder
{
    void Apply(AuthoredLightingProfile lighting, float focusX, float focusY, float focusZ);
}
