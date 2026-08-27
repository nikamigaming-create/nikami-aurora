using Godot;

namespace OpenDAO.Infrastructure.World;

public interface IGodotModelPostprocessor
{
    /// <summary>
    /// Identifies every resource that affects the packed result. Model caches
    /// include this value so a shader or post-processing change cannot revive
    /// stale material parameters from an otherwise unchanged GLB.
    /// </summary>
    string CacheFingerprint { get; }

    void Process(Node3D model, GltfState source);

    /// <summary>
    /// Reconnects runtime-only state after a PackedScene is loaded from memory
    /// or disk. Serialized caches retain shader/palette resources, but area
    /// probe and nearest-light bindings belong to the current world session.
    /// </summary>
    void Prepare(PackedScene scene);

    /// <summary>
    /// Applies session-local bindings to the actual scene instance. PackedScene
    /// subresources may be duplicated on instantiate, so binding only a temporary
    /// cache instance is not sufficient or deterministic.
    /// </summary>
    void Prepare(Node root);
}
