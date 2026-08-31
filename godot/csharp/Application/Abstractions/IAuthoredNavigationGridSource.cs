using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IAuthoredNavigationGridSource
{
    AuthoredNavigationGrid? Load(WorldProfile profile);
}
