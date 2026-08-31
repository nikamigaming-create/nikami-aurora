using Godot;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public interface IAuthoredWorldBlockerBuilder
{
    int Build(IReadOnlyList<WorldBlockerPlacement> blockers, Node3D destination);
}
