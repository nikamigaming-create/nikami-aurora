using System.Numerics;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeEffectReadabilityContract(
    int AgeKeys,
    bool IndependentScaleAxes,
    Vector2 MeshAspect,
    float MaximumAnimatedScale,
    float MaximumCardWidthMeters,
    float MaximumCardHeightMeters,
    int AtlasColumns,
    int AtlasRows,
    int AtlasFrames,
    int AtlasCellWidth,
    int AtlasCellHeight,
    float AnimationCyclesPerLifetime,
    float? ProximityFadeDistanceMeters,
    float VisibilityBoundsExtentMeters);

/// <summary>
/// Layout-neutral renderer safety and readability boundary for decoded DAO
/// particle cards. These limits reject corrupt transfer values; they do not
/// replace source scale, atlas, timing, or motion with authored defaults.
/// </summary>
public static class DragonAgeOriginsEffectPresentationPolicy
{
    public const float MaximumCardDimensionMeters = 128f;
    public const float MaximumVisibilityBoundsExtentMeters = 16384f;
    public const float MaximumFramesPerSecond = 1000f;
    public const int MaximumAtlasFrames = 4096;
    public const float MaximumAnimationCyclesPerLifetime = 4096f;
    public const float MinimumProximityFadeDistanceMeters = .05f;
    public const float MaximumProximityFadeDistanceMeters = 1.5f;

    public static DragonAgeEffectReadabilityContract Evaluate(
        DragonAgeEffectEmitter source,
        int textureWidth,
        int textureHeight,
        float presentationScale,
        bool enhancedPresentation)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!float.IsFinite(presentationScale) || presentationScale <= 0 ||
            presentationScale > 16)
            throw new InvalidDataException("DAO emitter presentation scale is invalid.");
        if (source.IndependentScaleAxes && source.ScaleAspect is not null)
            throw new InvalidDataException(
                "DAO independent-axis emitter also declares a constant aspect.");
        if (!source.IndependentScaleAxes && source.AgeMap is not null &&
            source.ScaleAspect is null)
            throw new InvalidDataException(
                "DAO decoded constant-axis emitter has no source aspect.");

        var ageMap = ResolveAgeMap(source);
        var previousTime = -1f;
        foreach (var key in ageMap)
        {
            if (!float.IsFinite(key.Time) || key.Time < 0 || key.Time > 1 ||
                key.Time < previousTime || !Finite(key.Scale) ||
                key.Scale.X < 0 || key.Scale.Y < 0 || !Finite(key.Color))
                throw new InvalidDataException("DAO emitter age-map readability is invalid.");
            previousTime = key.Time;
        }

        var meshAspect = source.IndependentScaleAxes
            ? Vector2.One
            : source.ScaleAspect ?? Vector2.One;
        if (!Finite(meshAspect) || meshAspect.X <= .000001f ||
            meshAspect.Y <= .000001f ||
            Math.Max(meshAspect.X, meshAspect.Y) > 1.0001f)
            throw new InvalidDataException("DAO emitter mesh aspect is invalid.");
        if (source.IndependentScaleAxes && ageMap.Any(key =>
                key.Scale.X <= .000001f || key.Scale.Y <= .000001f))
            throw new InvalidDataException(
                "DAO independent-axis emitter has an empty or zero-crossing scale axis.");

        var maximumScale = ageMap.Max(key => Math.Max(key.Scale.X, key.Scale.Y)) *
                           presentationScale;
        var maximumCardWidth = maximumScale * meshAspect.X;
        var maximumCardHeight = maximumScale * meshAspect.Y;
        if (!float.IsFinite(maximumScale) || maximumScale <= .000001f ||
            !float.IsFinite(maximumCardWidth) || !float.IsFinite(maximumCardHeight) ||
            maximumCardWidth <= .000001f || maximumCardHeight <= .000001f ||
            maximumCardWidth > MaximumCardDimensionMeters ||
            maximumCardHeight > MaximumCardDimensionMeters)
            throw new InvalidDataException(
                "DAO emitter card dimensions exceed the renderer readability contract.");

        if (source.Columns <= 0 || source.Rows <= 0 ||
            source.Columns > MaximumAtlasFrames || source.Rows > MaximumAtlasFrames)
            throw new InvalidDataException("DAO effect atlas grid is invalid.");
        var atlasFrames = checked(source.Columns * source.Rows);
        if (atlasFrames > MaximumAtlasFrames || textureWidth <= 0 || textureHeight <= 0 ||
            textureWidth % source.Columns != 0 || textureHeight % source.Rows != 0)
            throw new InvalidDataException(
                "DAO effect atlas does not divide into its source grid.");
        var cellWidth = textureWidth / source.Columns;
        var cellHeight = textureHeight / source.Rows;
        if (cellWidth <= 0 || cellHeight <= 0)
            throw new InvalidDataException("DAO effect atlas cells are empty.");
        if (!float.IsFinite(source.FramesPerSecond) || source.FramesPerSecond < 0 ||
            source.FramesPerSecond > MaximumFramesPerSecond ||
            !float.IsFinite(source.Lifetime) || source.Lifetime <= 0 ||
            !float.IsFinite(source.LifetimeRange) || source.LifetimeRange < 0 ||
            !float.IsFinite(source.Velocity) || source.Velocity < 0 ||
            !float.IsFinite(source.VelocityRange) || source.VelocityRange < 0 ||
            !Finite(source.VolumeExtents))
            throw new InvalidDataException("DAO effect atlas timing is invalid.");
        var animationCycles = source.FramesPerSecond * source.Lifetime / atlasFrames;
        if (!float.IsFinite(animationCycles) || animationCycles < 0 ||
            animationCycles > MaximumAnimationCyclesPerLifetime)
            throw new InvalidDataException(
                "DAO effect atlas cycles exceed the renderer readability contract.");

        var maximumTravel = (source.Velocity + source.VelocityRange) *
                            (source.Lifetime + source.LifetimeRange);
        var sourceExtent = Math.Max(Math.Abs(source.VolumeExtents.X),
            Math.Max(Math.Abs(source.VolumeExtents.Y), Math.Abs(source.VolumeExtents.Z)));
        var visibilityExtent = Math.Max(2, maximumTravel + sourceExtent + maximumScale);
        if (!float.IsFinite(maximumTravel) || maximumTravel < 0 ||
            !float.IsFinite(sourceExtent) || !float.IsFinite(visibilityExtent) ||
            visibilityExtent > MaximumVisibilityBoundsExtentMeters)
            throw new InvalidDataException(
                "DAO effect visibility bounds exceed the renderer safety contract.");

        float? proximityFade = enhancedPresentation && !source.IndependentScaleAxes
            ? Math.Clamp(maximumScale * .5f,
                MinimumProximityFadeDistanceMeters,
                MaximumProximityFadeDistanceMeters)
            : null;
        return new DragonAgeEffectReadabilityContract(
            ageMap.Count, source.IndependentScaleAxes, meshAspect,
            maximumScale, maximumCardWidth, maximumCardHeight,
            source.Columns, source.Rows, atlasFrames, cellWidth, cellHeight,
            animationCycles, proximityFade, visibilityExtent);
    }

    public static IReadOnlyList<DragonAgeEffectAgeKey> ResolveAgeMap(
        DragonAgeEffectEmitter source) => source.AgeMap is { Count: >= 2 }
        ? source.AgeMap
        :
        [
            new DragonAgeEffectAgeKey(0, new Vector2(source.SizeStart), source.ColorStart),
            new DragonAgeEffectAgeKey(source.MiddleTime,
                new Vector2(source.SizeMiddle), source.ColorMiddle),
            new DragonAgeEffectAgeKey(1, new Vector2(source.SizeEnd), source.ColorEnd)
        ];

    private static bool Finite(Vector2 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y);

    private static bool Finite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
