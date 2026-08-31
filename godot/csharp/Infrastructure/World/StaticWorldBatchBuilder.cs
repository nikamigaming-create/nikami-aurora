using Godot;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class StaticWorldBatchBuilder(
    IWorldLoadScheduler scheduler,
    IGodotModelPostprocessor modelPostprocessor) : IStaticWorldBatchBuilder
{
    public async Task<StaticBatchResult> BuildAsync(PackedScene packed, string name,
        IReadOnlyList<Transform3D> transforms, Node3D destination,
        bool renderGeometry, uint renderLayers, bool createCollision, uint collisionLayer,
        Func<Mesh, Material?>? materialFactory,
        CancellationToken cancellationToken)
    {
        if (transforms.Count == 0) return default;
        using var prototype = packed.Instantiate<Node3D>();
        modelPostprocessor.Prepare(prototype);
        if (!CanBatch(prototype))
            return await BuildIndividualAsync(packed, transforms, destination, renderGeometry,
                renderLayers, createCollision, collisionLayer, materialFactory, cancellationToken);

        var meshes = new List<MeshRecord>();
        CollectMeshes(prototype, Transform3D.Identity, meshes);
        var drawNodes = renderGeometry
            ? await BuildVisualBatches(name, meshes, transforms, destination, renderLayers,
                materialFactory, cancellationToken)
            : 0;
        var collisionShapes = createCollision
            ? await BuildCollisionBatch(name, meshes, transforms, destination, collisionLayer,
                cancellationToken)
            : 0;
        return new StaticBatchResult(transforms.Count, drawNodes, collisionShapes);
    }

    private async Task<int> BuildVisualBatches(string name, IReadOnlyList<MeshRecord> meshes,
        IReadOnlyList<Transform3D> transforms, Node3D destination,
        uint renderLayers, Func<Mesh, Material?>? materialFactory,
        CancellationToken cancellationToken)
    {
        var drawNodes = 0;
        foreach (var record in meshes.Where(record => record.Visible))
        {
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                Mesh = record.Mesh,
                InstanceCount = transforms.Count
            };
            for (var index = 0; index < transforms.Count; index++)
            {
                multiMesh.SetInstanceTransform(index, transforms[index] * record.RelativeTransform);
                await scheduler.YieldIfNeededAsync(destination, cancellationToken);
            }

            var batch = new MultiMeshInstance3D
            {
                Name = $"Batch_{Sanitize(name)}_{drawNodes}",
                Multimesh = multiMesh,
                CastShadow = record.CastShadow,
                Layers = renderLayers,
                MaterialOverride = materialFactory?.Invoke(record.Mesh)
            };
            batch.SetMeta("dao_static_batch", true);
            batch.SetMeta("dao_authored_instance_count", transforms.Count);
            destination.AddChild(batch);
            drawNodes++;
        }

        return drawNodes;
    }

    private async Task<int> BuildCollisionBatch(string name, IReadOnlyList<MeshRecord> meshes,
        IReadOnlyList<Transform3D> transforms, Node3D destination, uint collisionLayer,
        CancellationToken cancellationToken)
    {
        var body = new StaticBody3D
        {
            Name = $"Collision_{Sanitize(name)}",
            CollisionLayer = collisionLayer,
            CollisionMask = 0
        };
        var shapeCount = 0;
        foreach (var record in meshes)
        {
            if (record.Mesh.CreateTrimeshShape() is not ConcavePolygonShape3D shape) continue;
            shape.BackfaceCollision = true;
            foreach (var transform in transforms)
            {
                body.AddChild(new CollisionShape3D
                {
                    Shape = shape,
                    Transform = transform * record.RelativeTransform
                });
                shapeCount++;
                await scheduler.YieldIfNeededAsync(destination, cancellationToken);
            }
        }

        if (shapeCount == 0)
        {
            body.Free();
            return 0;
        }

        body.SetMeta("dao_static_collision_batch", true);
        body.SetMeta("dao_authored_instance_count", transforms.Count);
        destination.AddChild(body);
        return shapeCount;
    }

    private async Task<StaticBatchResult> BuildIndividualAsync(PackedScene packed,
        IReadOnlyList<Transform3D> transforms, Node3D destination, bool renderGeometry,
        uint renderLayers, bool createCollision, uint collisionLayer,
        Func<Mesh, Material?>? materialFactory,
        CancellationToken cancellationToken)
    {
        var collisionShapes = 0;
        foreach (var transform in transforms)
        {
            var node = packed.Instantiate<Node3D>();
            modelPostprocessor.Prepare(node);
            node.Transform = transform;
            destination.AddChild(node);
            ApplyRenderPolicy(node, renderGeometry, renderLayers, materialFactory);
            PlayDefaultAnimation(node);
            if (createCollision)
                collisionShapes += AddIndividualCollision(node, collisionLayer);
            await scheduler.YieldIfNeededAsync(destination, cancellationToken);
        }

        return new StaticBatchResult(transforms.Count, renderGeometry ? transforms.Count : 0,
            collisionShapes);
    }

    private static void ApplyRenderPolicy(Node root, bool renderGeometry, uint renderLayers,
        Func<Mesh, Material?>? materialFactory)
    {
        foreach (var geometry in root.FindChildren("*", "GeometryInstance3D", true, false)
                     .OfType<GeometryInstance3D>())
        {
            geometry.Visible = renderGeometry && geometry.Visible;
            if (WorldCollisionPolicy.IsCollisionProxy(geometry.Name))
                geometry.Visible = false;
            geometry.Layers = renderLayers;
            if (geometry is MeshInstance3D { Mesh: not null } mesh)
                mesh.MaterialOverride = materialFactory?.Invoke(mesh.Mesh);
        }
    }

    private static int AddIndividualCollision(Node root, uint collisionLayer)
    {
        var shapes = 0;
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
        {
            if (mesh.Mesh is null || mesh.Skin is not null) continue;
            mesh.CreateTrimeshCollision();
            foreach (var body in mesh.GetChildren().OfType<StaticBody3D>())
            {
                body.CollisionLayer = collisionLayer;
                body.CollisionMask = 0;
                foreach (var collision in body.FindChildren("*", "CollisionShape3D", true, false)
                             .OfType<CollisionShape3D>())
                {
                    if (collision.Shape is ConcavePolygonShape3D shape)
                        shape.BackfaceCollision = true;
                    shapes++;
                }
            }
        }

        return shapes;
    }

    private static bool CanBatch(Node root) =>
        root.FindChildren("*", "AnimationPlayer", true, false).Count == 0 &&
        root.FindChildren("*", "Skeleton3D", true, false).Count == 0;

    private static void CollectMeshes(Node node, Transform3D accumulated, ICollection<MeshRecord> records)
    {
        if (node is MeshInstance3D { Mesh: not null, Skin: null } mesh)
        {
            var visible = mesh.Visible && !WorldCollisionPolicy.IsCollisionProxy(mesh.Name);
            var batchMesh = visible ? SnapshotDrawMesh(mesh) : mesh.Mesh;
            records.Add(new MeshRecord(batchMesh, accumulated, visible, mesh.CastShadow));
        }
        foreach (var child in node.GetChildren())
        {
            var childTransform = child is Node3D child3D
                ? accumulated * child3D.Transform
                : accumulated;
            CollectMeshes(child, childTransform, records);
        }
    }

    private static Mesh SnapshotDrawMesh(MeshInstance3D source)
    {
        if (source.Mesh?.Duplicate() is not Mesh snapshot)
            throw new InvalidDataException(
                $"Visible static mesh could not be isolated for batching: {source.Name}");
        for (var surface = 0; surface < snapshot.GetSurfaceCount(); surface++)
        {
            var material = source.GetActiveMaterial(surface) ??
                           throw new InvalidDataException(
                               $"Visible static mesh surface has no bound material: " +
                               $"mesh={source.Name} surface={surface}");
            if (!material.HasMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta))
                throw new InvalidDataException(
                    $"Visible static mesh surface lost its source identity before batching: " +
                    $"mesh={source.Name} surface={surface} material={material.ResourceName} " +
                    $"material_type={material.GetType().Name} " +
                    $"stored_mesh_identity=" +
                    $"{(DaoCharacterMaterialPostprocessor.HasStoredWorldMaterialIdentity(source, surface) ? 1 : 0)}");
            // MeshInstance overrides carry the verified source identity. A
            // MultiMesh retains only its Mesh resource, so publish every
            // active material onto an isolated snapshot before batching.
            snapshot.SurfaceSetMaterial(surface, material);
            if (snapshot.SurfaceGetMaterial(surface) is not { } published ||
                !published.HasMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta))
                throw new InvalidDataException(
                    $"Visible static mesh surface identity did not publish to the batch snapshot: " +
                    $"mesh={source.Name} surface={surface} material={material.ResourceName}");
        }
        return snapshot;
    }

    private static void PlayDefaultAnimation(Node root)
    {
        foreach (var player in root.FindChildren("*", "AnimationPlayer", true, false)
                     .OfType<AnimationPlayer>())
            foreach (var name in player.GetAnimationList())
            {
                if (name == "RESET") continue;
                var animation = player.GetAnimation(name);
                if (animation is not null) animation.LoopMode = Animation.LoopModeEnum.Linear;
                player.Play(name);
                return;
            }
    }

    private static string Sanitize(string value) =>
        value.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Replace('.', '_');

    private sealed record MeshRecord(Mesh Mesh, Transform3D RelativeTransform, bool Visible,
        GeometryInstance3D.ShadowCastingSetting CastShadow);
}
