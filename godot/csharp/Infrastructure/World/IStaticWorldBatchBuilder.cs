using Godot;

namespace OpenDAO.Infrastructure.World;

public interface IStaticWorldBatchBuilder
{
    Task<StaticBatchResult> BuildAsync(PackedScene packed, string name,
        IReadOnlyList<Transform3D> transforms, Node3D destination,
        bool renderGeometry, uint renderLayers, bool createCollision, uint collisionLayer,
        Func<Mesh, Material?>? materialFactory,
        CancellationToken cancellationToken);
}

public readonly record struct StaticBatchResult(int Instances, int DrawNodes, int CollisionShapes);
