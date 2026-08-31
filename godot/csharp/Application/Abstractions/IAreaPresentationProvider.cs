using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IAreaPresentationProvider
{
    AreaPresentationResult Resolve(WorldProfile profile);
}
