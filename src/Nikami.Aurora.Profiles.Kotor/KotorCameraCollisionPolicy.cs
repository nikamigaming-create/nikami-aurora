namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorCameraSurfaceOpacity
{
    SourceOpaque,
    SourceTransparent,
    Unsupported
}

/// <summary>
/// Profile-owned selection contract for source-room camera collision. Only
/// surfaces whose active material is proven opaque may enter a collision mesh;
/// unknown semantics fail instead of silently changing camera framing.
/// </summary>
public static class KotorCameraCollisionPolicy
{
    public static IReadOnlyList<int> RequireBlockingSurfaceIndices(
        IReadOnlyList<KotorCameraSurfaceOpacity> surfaces)
    {
        ArgumentNullException.ThrowIfNull(surfaces);
        var unsupported = surfaces
            .Select((opacity, index) => (opacity, index))
            .Where(item => item.opacity == KotorCameraSurfaceOpacity.Unsupported)
            .Select(item => item.index)
            .ToArray();
        if (unsupported.Length > 0)
            throw new InvalidDataException(
                "KOTOR camera-collision surface opacity is unsupported: " +
                string.Join(',', unsupported));
        return surfaces
            .Select((opacity, index) => (opacity, index))
            .Where(item => item.opacity == KotorCameraSurfaceOpacity.SourceOpaque)
            .Select(item => item.index)
            .ToArray();
    }
}
