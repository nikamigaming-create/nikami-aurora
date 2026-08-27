namespace OpenDAO.Infrastructure.World;

using OpenDAO.Rendering;

internal static class WorldCollisionPolicy
{
    private static readonly string[] WalkableTokens =
    [
        "walk", "bridge", "ramp", "stair", "floor", "platform", "stage"
    ];

    private static readonly string[] MinimapOccluderTokens =
    [
        "ceiling", "roof", "canopy", "overhang", "skydome", "sky_dome", "skybox"
    ];

    internal static WorldDefinitionPolicy Terrain =>
        new(true, true, 1, WorldRenderLayers.GameplayAndMinimap);
    internal static WorldDefinitionPolicy VisualOnly =>
        new(true, false, 0, WorldRenderLayers.GameplayAndMinimap);

    internal static WorldDefinitionPolicy ForProp(string name, string path)
    {
        var semantic = (name + " " + Path.GetFileNameWithoutExtension(path)).ToLowerInvariant();
        // BioWare BLK meshes are textureless, invisible runtime blockers. They
        // must participate in physics (including SpringArm camera obstruction)
        // but must never be submitted to the renderer.
        if (semantic.StartsWith("blk_", StringComparison.Ordinal) ||
            semantic.Contains(" blk_", StringComparison.Ordinal))
            return new WorldDefinitionPolicy(false, true, 2, 0);
        if (IsCollisionProxy(semantic)) return new(false, true, 2, 0);
        var renderLayers = IsMinimapVisible(semantic)
            ? WorldRenderLayers.GameplayAndMinimap
            : WorldRenderLayers.Gameplay;
        return WalkableTokens.Any(semantic.Contains)
            ? new WorldDefinitionPolicy(true, true, 2, renderLayers)
            : new WorldDefinitionPolicy(true, false, 0, renderLayers);
    }

    internal static bool IsCollisionProxy(string value)
    {
        var semantic = value.ToLowerInvariant();
        return semantic.Contains("collision", StringComparison.Ordinal) ||
               semantic.Contains("ucx_", StringComparison.Ordinal) ||
               semantic.Contains("coll_", StringComparison.Ordinal) ||
               semantic.EndsWith("coll", StringComparison.Ordinal) ||
               semantic.Contains("coll.", StringComparison.Ordinal);
    }

    internal static bool IsMinimapVisible(string value) =>
        !MinimapOccluderTokens.Any(value.ToLowerInvariant().Contains);
}

internal readonly record struct WorldDefinitionPolicy(
    bool Render,
    bool Collision,
    uint CollisionLayer,
    uint RenderLayers);
