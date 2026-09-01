using System.Numerics;
using Nikami.Aurora.Core;

namespace Nikami.Aurora.Profiles.Kotor;

/// <summary>
/// Renderer-neutral coefficients for the KOTOR room lightmap transfer. The
/// source tier publishes the complete lightmapped surface through emission so
/// Godot's dynamic Lambert term cannot light the same texel a second time.
/// </summary>
public readonly record struct KotorLightmapTransfer(
    string Formula,
    float DynamicLightAlbedoWeight,
    float BakedEmissionWeight,
    float DynamicAmbientEmissionWeight)
{
    public bool DynamicLightsEnabled => DynamicLightAlbedoWeight > 0;

    public Vector3 ComputeEmission(
        Vector3 surfaceColor,
        Vector3 lightmapColor,
        Vector3 dynamicAmbient)
    {
        if (!Finite(surfaceColor) || !Finite(lightmapColor) || !Finite(dynamicAmbient))
            throw new ArgumentException("KOTOR lightmap transfer inputs must be finite");
        var baked = Vector3.Clamp(lightmapColor, Vector3.Zero, Vector3.One) *
                    BakedEmissionWeight;
        var ambient = Vector3.Max(dynamicAmbient, Vector3.Zero) *
                      DynamicAmbientEmissionWeight;
        return Vector3.Multiply(surfaceColor, Vector3.Max(baked, ambient));
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

/// <summary>
/// Source-bound conventions shared by the owned KOTOR importer and renderer.
/// </summary>
public static class KotorEnvironmentMaterialPolicy
{
    public const string Schema = "nikami-aurora-kotor-environment-map-v2";
    public const string MaterialMarker = "__aurora_envmap_";
    public const string NormalScaleMarker = "__aurora_normal_scale_";
    public const string AdditiveMarker = "__aurora_additive";
    public const string DecalMarker = "__aurora_decal";
    public const string CycleMarker = "__aurora_cycle_";
    // Odyssey decals retain depth testing but never publish depth. Priority 1
    // draws them after ordinary priority-0 transparent room surfaces without
    // disabling occlusion by nearer geometry.
    public const int SourceDecalRenderPriority = 1;
    public const string RowTransform = "flip-top-bottom";
    public const string SampleBasis = "godot-to-odyssey:x,-z,y";

    // Odyssey's authored diffuse alpha is the reflection mask. The reflected
    // cube is added over authored diffuse; replacing diffuse with the cube turns
    // fully masked droids and hull panels black wherever the cube is dark.
    // Enhanced adds response only inside partially reflective mask values.
    public const float SourceReflectionStrength = 0.0f;
    public const float EnhancedReflectionStrength = 0.35f;
    public const float SourceMaximumReflectionWeight = 1.0f;
    public const float EnhancedMaximumReflectionWeight = 0.90f;

    public const float SourceDynamicLightAlbedoWeight = 0.0f;
    public const float SourceBakedEmissionWeight = 1.0f;
    // Lightmapped room surfaces already contain their complete authored light
    // response. Area dynamic ambient belongs to unlightmapped/dynamic geometry;
    // flooring every baked RGB channel here destroys colored K1/K2 lightmaps.
    public const float SourceDynamicAmbientEmissionWeight = 0.0f;
    // Retain the authored baked contrast as the dominant signal. Dynamic
    // response is deliberately restrained: higher fill flattened the Taris
    // sign/window highlights and turned the source walls/floor uniform grey.
    public const float EnhancedDynamicLightAlbedoWeight = 0.12f;
    public const float EnhancedBakedEmissionWeight = 1.0f;
    public const float EnhancedDynamicAmbientEmissionWeight = 0.15f;
    public const float SourceDielectricSpecular = 0.0f;
    public const float EnhancedDielectricSpecular = 0.5f;
    public const float SourceFallbackRoughness = 1.0f;
    public const float EnhancedFallbackRoughness = 0.68f;
    public const SourceAuthorizedRenderFeature EnhancedAuthorizedRenderFeatures =
        SourceAuthorizedRenderFeature.Reflections;

    private static readonly KotorLightmapTransfer SourceLightmapTransfer = new(
        "surface-times-clamped-lightmap",
        SourceDynamicLightAlbedoWeight,
        SourceBakedEmissionWeight,
        SourceDynamicAmbientEmissionWeight);

    private static readonly KotorLightmapTransfer EnhancedLightmapTransfer = new(
        "baked-preserving-bounded-dynamic",
        EnhancedDynamicLightAlbedoWeight,
        EnhancedBakedEmissionWeight,
        EnhancedDynamicAmbientEmissionWeight);

    public static IReadOnlyList<string> FaceOrder { get; } =
    [
        "positive-x", "negative-x", "positive-y",
        "negative-y", "positive-z", "negative-z"
    ];

    public static float ReflectionStrength(bool enhanced) =>
        enhanced ? EnhancedReflectionStrength : SourceReflectionStrength;

    public static float MaximumReflectionWeight(bool enhanced) => enhanced
        ? EnhancedMaximumReflectionWeight
        : SourceMaximumReflectionWeight;

    public static KotorLightmapTransfer LightmapTransfer(bool enhanced) =>
        enhanced ? EnhancedLightmapTransfer : SourceLightmapTransfer;

    public static float DielectricSpecular(bool enhanced) => enhanced
        ? EnhancedDielectricSpecular
        : SourceDielectricSpecular;

    public static float FallbackRoughness(bool enhanced) => enhanced
        ? EnhancedFallbackRoughness
        : SourceFallbackRoughness;

    public static bool IsSourceDecal(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        return materialName.Contains(DecalMarker, StringComparison.OrdinalIgnoreCase);
    }

    public static Vector3 ToOdysseySampleDirection(Vector3 godotDirection) =>
        new(godotDirection.X, -godotDirection.Z, godotDirection.Y);

    public static string? EnvironmentMapResref(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        var marker = materialName.IndexOf(
            MaterialMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;
        var start = marker + MaterialMarker.Length;
        var end = materialName.IndexOf("__aurora_", start,
            StringComparison.OrdinalIgnoreCase);
        var resref = (end < 0 ? materialName[start..] : materialName[start..end]).Trim();
        if (resref.Length is < 1 or > 16 ||
            resref.Any(character => !char.IsLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException(
                $"Environment-map material marker is invalid: {materialName}");
        return resref;
    }

    public static float? AuthoredNormalScale(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        var marker = materialName.IndexOf(
            NormalScaleMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;
        var start = marker + NormalScaleMarker.Length;
        var end = materialName.IndexOf("__aurora_", start,
            StringComparison.OrdinalIgnoreCase);
        var encoded = (end < 0 ? materialName[start..] : materialName[start..end]).Trim();
        if (!float.TryParse(
                encoded,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var scale) ||
            !float.IsFinite(scale))
            throw new InvalidDataException(
                $"Normal-scale material marker is invalid: {materialName}");
        return scale;
    }

    public static KotorCycleTexture? CycleTexture(string materialName)
    {
        ArgumentNullException.ThrowIfNull(materialName);
        var marker = materialName.IndexOf(
            CycleMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            return null;
        var start = marker + CycleMarker.Length;
        var end = materialName.IndexOf("__aurora_", start,
            StringComparison.OrdinalIgnoreCase);
        var encoded = end < 0 ? materialName[start..] : materialName[start..end];
        var parts = encoded.Split('_');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var columns) ||
            !int.TryParse(parts[1], out var rows) ||
            !float.TryParse(
                parts[2],
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var fps) ||
            columns <= 0 || rows <= 0 || columns * rows > 256 ||
            !float.IsFinite(fps) || fps <= 0)
            throw new InvalidDataException(
                $"Cycle-texture material marker is invalid: {materialName}");
        return new KotorCycleTexture(columns, rows, fps);
    }
}

public readonly record struct KotorCycleTexture(int Columns, int Rows, float FramesPerSecond);
