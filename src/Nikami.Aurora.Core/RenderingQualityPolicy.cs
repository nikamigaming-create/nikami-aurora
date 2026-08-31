namespace Nikami.Aurora.Core;

public enum RenderingPresentationTier
{
    Source,
    Enhanced
}

public enum RenderingBackend
{
    ForwardPlus,
    Mobile,
    Compatibility
}

public enum RenderingSelectionScope
{
    Application,
    Profile,
    Area,
    Module,
    Layout
}

[Flags]
public enum EnhancedRenderingCapability
{
    None = 0,
    ForwardPlus = 1 << 0,
    AgxToneMapping = 1 << 1,
    HighResolutionShadows = 1 << 2,
    AnisotropicFiltering = 1 << 3,
    AmbientOcclusion = 1 << 4,
    ScreenSpaceIndirectLighting = 1 << 5,
    FourSampleMsaa = 1 << 6,
    Debanding = 1 << 7,
    ScreenSpaceReflections = 1 << 8,
    Sdfgi = 1 << 9,
    Volumetrics = 1 << 10
}

[Flags]
public enum SourceAuthorizedRenderFeature
{
    None = 0,
    Reflections = 1 << 0,
    IndirectLighting = 1 << 1,
    Volumetrics = 1 << 2
}

public enum ConditionalRenderFeatureStatus
{
    Enabled,
    SourceEvidenceRequired,
    CapabilityUnavailable,
    OwnedBySourceTier
}

public sealed record ConditionalRenderFeature(
    bool Enabled,
    ConditionalRenderFeatureStatus Status);

public enum EnhancedReflectionPolicy
{
    SourceTierOwned,
    SourceBoundProbesAndMapsOnly,
    SourceBoundProbesMapsAndScreenSpace
}

public sealed record EnhancedRenderingQualityValues(
    bool TemporalAntialiasing,
    int MultisampleAntialiasingSamples,
    bool Debanding,
    int AnisotropicFilteringSamples,
    bool TrilinearMipmapFiltering,
    int DirectionalShadowMapSize,
    int PositionalShadowAtlasSize,
    int SoftShadowFilterQuality,
    int SsaoQuality,
    bool SsaoHalfSize,
    float SsaoAdaptiveTarget,
    int SsilQuality,
    bool SsilHalfSize,
    float SsilAdaptiveTarget,
    bool ScreenSpaceReflectionHalfSize,
    bool GiHalfResolution,
    int SdfgiProbeRayCount,
    int SdfgiFramesToConverge,
    int SdfgiFramesToUpdateLights,
    int VolumetricFogFilter);

public sealed record RenderingQualityRequest(
    RenderingPresentationTier Tier,
    RenderingBackend Backend,
    RenderingSelectionScope SelectionScope,
    string? SelectionKey,
    EnhancedRenderingCapability AvailableCapabilities,
    SourceAuthorizedRenderFeature SourceAuthorizedFeatures);

public sealed record RenderingQualityDecision(
    RenderingPresentationTier Tier,
    RenderingBackend Backend,
    EnhancedRenderingCapability EnabledEnhancedCapabilities,
    ConditionalRenderFeature Reflections,
    ConditionalRenderFeature Sdfgi,
    ConditionalRenderFeature Volumetrics,
    EnhancedReflectionPolicy ReflectionPolicy,
    EnhancedRenderingQualityValues? QualityValues,
    string EvidenceIntent,
    string ParityClaim)
{
    public bool Enables(EnhancedRenderingCapability capability) =>
        (EnabledEnhancedCapabilities & capability) == capability;

    public string ToTelemetryMarker() =>
        "NIKAMI_AURORA_RENDER_QUALITY status=ready scope=application " +
        $"tier={Token(Tier)} backend={Token(Backend)} " +
        $"agx={Bit(EnhancedRenderingCapability.AgxToneMapping)} " +
        $"shadows={(QualityValues is null ? 0 : 1)} " +
        $"shadow_size={(QualityValues?.DirectionalShadowMapSize ?? 0)} " +
        $"anisotropy={(QualityValues?.AnisotropicFilteringSamples > 0 ? 1 : 0)} " +
        $"anisotropy_samples={(QualityValues?.AnisotropicFilteringSamples ?? 0)} " +
        $"ssao={Bit(EnhancedRenderingCapability.AmbientOcclusion)} " +
        $"ssil={Bit(EnhancedRenderingCapability.ScreenSpaceIndirectLighting)} " +
        $"msaa={(QualityValues?.MultisampleAntialiasingSamples ?? 0)}x " +
        $"taa={(QualityValues?.TemporalAntialiasing == true ? 1 : 0)} " +
        $"debanding={(QualityValues?.Debanding == true ? 1 : 0)} " +
        $"reflections={(Reflections.Enabled ? 1 : 0)} " +
        $"reflections_gate={Token(Reflections.Status)} " +
        $"reflection_policy={Token(ReflectionPolicy)} " +
        $"sdfgi={(Sdfgi.Enabled ? 1 : 0)} " +
        $"sdfgi_gate={Token(Sdfgi.Status)} " +
        $"volumetrics={(Volumetrics.Enabled ? 1 : 0)} " +
        $"volumetrics_gate={Token(Volumetrics.Status)} " +
        $"parity_claim={ParityClaim}";

    private int Bit(EnhancedRenderingCapability capability) => Enables(capability) ? 1 : 0;

    private static string Token(RenderingPresentationTier tier) => tier switch
    {
        RenderingPresentationTier.Source => "source",
        RenderingPresentationTier.Enhanced => "enhanced",
        _ => throw new InvalidDataException($"Unsupported presentation tier: {tier}")
    };

    private static string Token(RenderingBackend backend) => backend switch
    {
        RenderingBackend.ForwardPlus => "forward_plus",
        RenderingBackend.Mobile => "mobile",
        RenderingBackend.Compatibility => "gl_compatibility",
        _ => throw new InvalidDataException($"Unsupported rendering backend: {backend}")
    };

    private static string Token(ConditionalRenderFeatureStatus status) => status switch
    {
        ConditionalRenderFeatureStatus.Enabled => "enabled",
        ConditionalRenderFeatureStatus.SourceEvidenceRequired => "source_evidence_required",
        ConditionalRenderFeatureStatus.CapabilityUnavailable => "capability_unavailable",
        ConditionalRenderFeatureStatus.OwnedBySourceTier => "owned_by_source_tier",
        _ => throw new InvalidDataException($"Unsupported render feature status: {status}")
    };

    private static string Token(EnhancedReflectionPolicy policy) => policy switch
    {
        EnhancedReflectionPolicy.SourceTierOwned => "source_tier_owned",
        EnhancedReflectionPolicy.SourceBoundProbesAndMapsOnly => "source_bound_probes_maps",
        EnhancedReflectionPolicy.SourceBoundProbesMapsAndScreenSpace =>
            "source_bound_probes_maps_ssr",
        _ => throw new InvalidDataException($"Unsupported reflection policy: {policy}")
    };
}

/// <summary>
/// Resolves an application-wide rendering tier. Scene identity is deliberately
/// absent from the decision surface: areas, modules, and layouts may supply
/// source facts, but may never select a different presentation tier.
/// </summary>
public static class RenderingQualityPolicy
{
    public const string NoParityClaim = "none";
    public const string SourceComparisonEvidenceIntent = "source-comparison-candidate";
    public const string EnhancedEvidenceIntent = "enhanced-non-parity";

    public const EnhancedRenderingCapability RequiredEnhancedCapabilities =
        EnhancedRenderingCapability.ForwardPlus |
        EnhancedRenderingCapability.AgxToneMapping |
        EnhancedRenderingCapability.HighResolutionShadows |
        EnhancedRenderingCapability.AnisotropicFiltering |
        EnhancedRenderingCapability.AmbientOcclusion |
        EnhancedRenderingCapability.ScreenSpaceIndirectLighting |
        EnhancedRenderingCapability.FourSampleMsaa |
        EnhancedRenderingCapability.Debanding;

    public const EnhancedRenderingCapability SamplingQualityCapabilities =
        EnhancedRenderingCapability.HighResolutionShadows |
        EnhancedRenderingCapability.AnisotropicFiltering |
        EnhancedRenderingCapability.FourSampleMsaa |
        EnhancedRenderingCapability.Debanding;

    public const EnhancedRenderingCapability AllCapabilities =
        RequiredEnhancedCapabilities |
        EnhancedRenderingCapability.ScreenSpaceReflections |
        EnhancedRenderingCapability.Sdfgi |
        EnhancedRenderingCapability.Volumetrics;

    public const SourceAuthorizedRenderFeature AllSourceAuthorizedFeatures =
        SourceAuthorizedRenderFeature.Reflections |
        SourceAuthorizedRenderFeature.IndirectLighting |
        SourceAuthorizedRenderFeature.Volumetrics;

    /// <summary>
    /// Concrete Godot Forward+ quality target. Enum-valued quality fields use
    /// Godot RenderingServer values; sample counts and map sizes use physical
    /// units. Individual Environment resources still decide whether optional effects are active.
    /// TAA is intentionally off because it can ghost particles and skinned
    /// meshes; 4x MSAA is the application-wide antialiasing baseline instead.
    /// </summary>
    public static EnhancedRenderingQualityValues FullBlastValues { get; } = new(
        TemporalAntialiasing: false,
        MultisampleAntialiasingSamples: 4,
        Debanding: true,
        AnisotropicFilteringSamples: 16,
        TrilinearMipmapFiltering: true,
        DirectionalShadowMapSize: 8192,
        PositionalShadowAtlasSize: 8192,
        SoftShadowFilterQuality: 5,
        SsaoQuality: 4,
        SsaoHalfSize: false,
        SsaoAdaptiveTarget: 1.0f,
        SsilQuality: 4,
        SsilHalfSize: false,
        SsilAdaptiveTarget: 1.0f,
        ScreenSpaceReflectionHalfSize: false,
        GiHalfResolution: false,
        SdfgiProbeRayCount: 5,
        SdfgiFramesToConverge: 5,
        SdfgiFramesToUpdateLights: 0,
        VolumetricFogFilter: 2);

    public static RenderingBackend ParseBackend(string renderingMethod)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingMethod);
        return renderingMethod.Trim().ToLowerInvariant() switch
        {
            "forward_plus" => RenderingBackend.ForwardPlus,
            "mobile" => RenderingBackend.Mobile,
            "gl_compatibility" => RenderingBackend.Compatibility,
            _ => throw new InvalidDataException(
                $"Unsupported rendering backend: {renderingMethod.Trim()}.")
        };
    }

    public static RenderingPresentationTier ParseTier(
        string? requestedTier,
        RenderingBackend backend)
    {
        var token = requestedTier?.Trim().ToLowerInvariant() ?? string.Empty;
        return token switch
        {
            "source" => RenderingPresentationTier.Source,
            "enhanced" => RenderingPresentationTier.Enhanced,
            "" when backend == RenderingBackend.ForwardPlus =>
                RenderingPresentationTier.Enhanced,
            "" => RenderingPresentationTier.Source,
            _ => throw new InvalidDataException($"Unsupported presentation tier: {token}.")
        };
    }

    public static RenderingQualityDecision Resolve(RenderingQualityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.Tier))
            throw new InvalidDataException($"Unsupported presentation tier: {request.Tier}.");
        if (!Enum.IsDefined(request.Backend))
            throw new InvalidDataException($"Unsupported rendering backend: {request.Backend}.");
        var unknownCapabilities = request.AvailableCapabilities & ~AllCapabilities;
        if (unknownCapabilities != EnhancedRenderingCapability.None)
            throw new InvalidDataException($"Unknown rendering capabilities: {unknownCapabilities}.");
        var unknownAuthorizations = request.SourceAuthorizedFeatures &
                                    ~AllSourceAuthorizedFeatures;
        if (unknownAuthorizations != SourceAuthorizedRenderFeature.None)
            throw new InvalidDataException($"Unknown source render authorization: {unknownAuthorizations}.");
        if (request.SelectionScope != RenderingSelectionScope.Application ||
            !string.IsNullOrWhiteSpace(request.SelectionKey))
            throw new InvalidDataException(
                "Rendering presentation must be selected once at application scope; " +
                "profile, area, module, and layout keys are not valid selectors.");

        if (request.Tier == RenderingPresentationTier.Source)
        {
            var sourceQualityValues = request.Backend == RenderingBackend.ForwardPlus &&
                                      (request.AvailableCapabilities &
                                       SamplingQualityCapabilities) ==
                                      SamplingQualityCapabilities
                ? FullBlastValues
                : null;
            return new RenderingQualityDecision(
                request.Tier,
                request.Backend,
                EnhancedRenderingCapability.None,
                new ConditionalRenderFeature(false,
                    ConditionalRenderFeatureStatus.OwnedBySourceTier),
                new ConditionalRenderFeature(false,
                    ConditionalRenderFeatureStatus.OwnedBySourceTier),
                new ConditionalRenderFeature(false,
                    ConditionalRenderFeatureStatus.OwnedBySourceTier),
                EnhancedReflectionPolicy.SourceTierOwned,
                sourceQualityValues,
                SourceComparisonEvidenceIntent,
                NoParityClaim);
        }

        if (request.Tier != RenderingPresentationTier.Enhanced)
            throw new InvalidDataException($"Unsupported presentation tier: {request.Tier}");
        if (request.Backend != RenderingBackend.ForwardPlus)
            throw new InvalidDataException(
                $"Enhanced presentation requires Forward+; active backend is {request.Backend}.");

        var missing = RequiredEnhancedCapabilities & ~request.AvailableCapabilities;
        if (missing != EnhancedRenderingCapability.None)
            throw new InvalidDataException(
                $"Enhanced presentation is missing required capabilities: {missing}.");

        var reflections = ResolveConditionalFeature(
            EnhancedRenderingCapability.ScreenSpaceReflections,
            SourceAuthorizedRenderFeature.Reflections,
            request);
        var sdfgi = ResolveConditionalFeature(
            EnhancedRenderingCapability.Sdfgi,
            SourceAuthorizedRenderFeature.IndirectLighting,
            request);
        var volumetrics = ResolveConditionalFeature(
            EnhancedRenderingCapability.Volumetrics,
            SourceAuthorizedRenderFeature.Volumetrics,
            request);
        var enabled = RequiredEnhancedCapabilities;
        if (reflections.Enabled) enabled |= EnhancedRenderingCapability.ScreenSpaceReflections;
        if (sdfgi.Enabled) enabled |= EnhancedRenderingCapability.Sdfgi;
        if (volumetrics.Enabled) enabled |= EnhancedRenderingCapability.Volumetrics;

        return new RenderingQualityDecision(
            request.Tier,
            request.Backend,
            enabled,
            reflections,
            sdfgi,
            volumetrics,
            reflections.Enabled
                ? EnhancedReflectionPolicy.SourceBoundProbesMapsAndScreenSpace
                : EnhancedReflectionPolicy.SourceBoundProbesAndMapsOnly,
            FullBlastValues,
            EnhancedEvidenceIntent,
            NoParityClaim);
    }

    private static ConditionalRenderFeature ResolveConditionalFeature(
        EnhancedRenderingCapability capability,
        SourceAuthorizedRenderFeature authorization,
        RenderingQualityRequest request)
    {
        if ((request.AvailableCapabilities & capability) != capability)
            return new ConditionalRenderFeature(false,
                ConditionalRenderFeatureStatus.CapabilityUnavailable);
        if ((request.SourceAuthorizedFeatures & authorization) != authorization)
            return new ConditionalRenderFeature(false,
                ConditionalRenderFeatureStatus.SourceEvidenceRequired);
        return new ConditionalRenderFeature(true, ConditionalRenderFeatureStatus.Enabled);
    }
}
