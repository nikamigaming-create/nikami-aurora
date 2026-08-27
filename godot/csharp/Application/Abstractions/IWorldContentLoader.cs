using Godot;
using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IWorldContentLoader
{
    Task<WorldLoadResult> LoadAsync(WorldProfile profile, Node3D destination, CancellationToken cancellationToken);
}
