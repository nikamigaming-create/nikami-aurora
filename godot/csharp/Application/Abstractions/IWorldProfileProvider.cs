using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IWorldProfileProvider
{
    string ResolveProfilePath();
    WorldProfile Load();
}
