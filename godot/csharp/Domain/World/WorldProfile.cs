namespace Nikami.Aurora.GodotRuntime.Domain.World;

public sealed record WorldProfile(
    int Schema,
    string SourcePath,
    string AreaId,
    string DisplayName,
    string AreaFile,
    string AreaRoot,
    string SceneFile,
    string ActorFile,
    string ActorRoot,
    string TerrainMaterials,
    string GameRoot,
    string LayoutName,
    bool UseDaoStaticShader,
    bool UseDaoHslMatrix,
    string SourceKey,
    string TalktableFile);

public sealed record AuthoredNavigationGrid(
    string SourcePath,
    float BaseX,
    float BaseY,
    int Columns,
    int Rows,
    float CellSize,
    byte[] Accessibility)
{
    public bool IsWalkable(int column, int row) =>
        column >= 0 && column < Columns && row >= 0 && row < Rows &&
        Accessibility[row * Columns + column] == 1;

    public bool IsWalkableWorld(float godotX, float godotZ)
    {
        var column = (int)MathF.Round((godotX - BaseX) / CellSize);
        var row = (int)MathF.Round((-godotZ - BaseY) / CellSize);
        return IsWalkable(column, row);
    }
}

/// <summary>
/// Renderer-independent copy of the installed area's authored lighting inputs.
/// Probe matrices remain raw row-major coefficients so render backends can bind
/// them without leaking Godot types into the domain layer.
/// </summary>
public sealed record AuthoredLightingProfile(
    bool ProbeLoaded,
    float[] ProbeMatrixR,
    float[] ProbeMatrixG,
    float[] ProbeMatrixB,
    float[] SunColor,
    float[] CharacterSunColor,
    float[] SunDirection,
    float[] FogColor,
    float SunIntensity,
    string ProbeResource,
    string ProbeResourceSha256,
    AuthoredPointLightProfile[] PointLights,
    AuthoredPointLightProfile[] CharacterPointLights,
    AuthoredAtmosphereProfile? Atmosphere);

/// <summary>
/// Exact scalar/vector values exported from the installed ARE ATMO structure.
/// Renderer enhancements may consume these values, but may not replace them
/// with a profile-wide color grade.
/// </summary>
public sealed record AuthoredAtmosphereProfile(
    float FogIntensity,
    float FogCap,
    float FogZenith,
    float FogWaterIntensity,
    float FogWaterCap,
    float DistanceMultiplier,
    float AtmosphereAlpha,
    float[] AtmosphereSunColor,
    float Turbidity,
    float RayleighMultiplier,
    float MieMultiplier,
    float PhaseEccentricity,
    float CloudDensity,
    float CloudSharpness,
    float CloudDepth,
    float CloudRange1,
    float CloudRange2,
    float[] CloudColor,
    float MoonScale,
    float MoonAlpha,
    float MoonRotation,
    string SkyDome,
    int SourceFieldCount,
    string SourceFieldsSha256);

public sealed record AuthoredPointLightProfile(
    string Name,
    float X,
    float Y,
    float Z,
    float Red,
    float Green,
    float Blue,
    float Radius);

public sealed record WorldLoadResult(
    bool Succeeded,
    string Error,
    int Instances,
    int Actors,
    int DrawNodes,
    int CollisionShapes,
    int AuthoredBlockers,
    int AuthoredLights,
    AuthoredLightingProfile? Lighting,
    AuthoredNavigationGrid? Navigation,
    int CacheHits,
    int CacheMisses,
    int CooperativeYields,
    double MaxWorkSliceMilliseconds)
{
    public static WorldLoadResult Failed(string error) =>
        new(false, error, 0, 0, 0, 0, 0, 0, null, null, 0, 0, 0, 0);
    public static WorldLoadResult Complete(int instances, int actors, int drawNodes, int collisionShapes,
        int authoredBlockers, int authoredLights, AuthoredLightingProfile? lighting,
        AuthoredNavigationGrid? navigation, int cacheHits, int cacheMisses,
        int cooperativeYields,
        double maxWorkSliceMilliseconds) =>
        new(true, string.Empty, instances, actors, drawNodes, collisionShapes, authoredBlockers, authoredLights,
            lighting, navigation,
            cacheHits, cacheMisses, cooperativeYields, maxWorkSliceMilliseconds);
}
