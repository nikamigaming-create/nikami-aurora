using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IAuthoredLightingResolver
{
    AuthoredLightingProfile? Resolve(WorldProfile world, AuthoredLightingProfile? authored);
}
