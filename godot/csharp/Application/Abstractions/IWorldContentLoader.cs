using Godot;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IWorldContentLoader
{
    Task<WorldLoadResult> LoadAsync(WorldProfile profile, Node3D destination, CancellationToken cancellationToken);
}
