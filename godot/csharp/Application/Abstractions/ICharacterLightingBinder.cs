using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface ICharacterLightingBinder
{
    void Apply(AuthoredLightingProfile lighting, float focusX, float focusY, float focusZ);
}
