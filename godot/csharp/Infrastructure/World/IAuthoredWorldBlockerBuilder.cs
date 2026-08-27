using Godot;
using OpenDAO.Domain.World;

namespace OpenDAO.Infrastructure.World;

public interface IAuthoredWorldBlockerBuilder
{
    int Build(IReadOnlyList<WorldBlockerPlacement> blockers, Node3D destination);
}
