using Nikami.Aurora.Core;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public enum DragonAgePresentationTier
{
    Source,
    Enhanced
}

public sealed record DragonAgeAreaRenderPolicy(
    string Layout,
    DragonAgePresentationTier Tier,
    string RenderingMethod,
    bool ValidatedAtmosphere,
    bool EnhancedFeatures,
    string Status,
    RenderingQualityDecision QualityDecision);

public readonly record struct DragonAgePbrCoverage(
    int RenderableSurfaces,
    int BoundSurfaces,
    int IdentityReadySurfaces,
    int PbrReadySurfaces);

public enum DragonAgePbrContractKind
{
    ImportedGltf,
    SourceShader,
    EnhancedShader
}

/// <summary>
/// Layout-neutral selection policy for DAO presentation. Area identity is
/// carried into telemetry, but never selects renderer behavior.
/// </summary>
public static class DragonAgeOriginsRenderFidelityPolicy
{
    public static DragonAgePbrContractKind RequirePbrContract(string materialIdentity)
    {
        var status = RequireIdentityToken(materialIdentity, "pbr_status");
        return status.ToLowerInvariant() switch
        {
            "ready" => DragonAgePbrContractKind.ImportedGltf,
            "source-shader" => DragonAgePbrContractKind.SourceShader,
            "enhanced-shader" => DragonAgePbrContractKind.EnhancedShader,
            _ => throw new InvalidDataException(
                $"Unsupported DAO PBR contract status: {status}")
        };
    }

    public static string RequireIdentityToken(string materialIdentity, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(materialIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new ArgumentException("DAO material identity key is invalid.", nameof(key));
        var values = materialIdentity.Split(';',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(part => part.Length == 2 &&
                           part[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            .Select(part => part[1])
            .ToArray();
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0]))
            throw new InvalidDataException(
                $"DAO material identity must contain exactly one {key} token.");
        return values[0];
    }

    public static void RequirePbrCoverage(DragonAgePbrCoverage coverage)
    {
        if (coverage.RenderableSurfaces <= 0 ||
            coverage.BoundSurfaces != coverage.RenderableSurfaces ||
            coverage.IdentityReadySurfaces != coverage.RenderableSurfaces ||
            coverage.PbrReadySurfaces != coverage.RenderableSurfaces)
            throw new InvalidDataException(
                "DAO global PBR surface coverage is incomplete: " +
                $"renderable={coverage.RenderableSurfaces} " +
                $"bound={coverage.BoundSurfaces} " +
                $"identity={coverage.IdentityReadySurfaces} " +
                $"pbr={coverage.PbrReadySurfaces}");
    }

    public static DragonAgeAreaRenderPolicy Evaluate(
        string layout,
        string? requestedTier,
        string renderingMethod,
        bool validatedAtmosphere)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(renderingMethod);
        var backend = RenderingQualityPolicy.ParseBackend(renderingMethod);
        var applicationTier = RenderingQualityPolicy.ParseTier(requestedTier, backend);
        // Geometry presence alone does not prove reflection material semantics,
        // and a source light record does not prove an indirect-lighting policy.
        // Keep SSR/SDFGI fail-closed until those facts are harvested. The exact
        // ATMO contract is sufficient only for the authored cloud volume.
        var authorization = validatedAtmosphere
            ? SourceAuthorizedRenderFeature.Volumetrics
            : SourceAuthorizedRenderFeature.None;
        RenderingQualityDecision quality;
        try
        {
            quality = RenderingQualityPolicy.Resolve(new RenderingQualityRequest(
                applicationTier,
                backend,
                RenderingSelectionScope.Application,
                SelectionKey: null,
                backend == RenderingBackend.ForwardPlus
                    ? RenderingQualityPolicy.AllCapabilities
                    : EnhancedRenderingCapability.None,
                authorization));
        }
        catch (InvalidDataException error)
            when (applicationTier == RenderingPresentationTier.Enhanced &&
                  backend != RenderingBackend.ForwardPlus)
        {
            // Preserve the profile API's established exception contract while
            // Core remains the single selector and capability authority.
            throw new InvalidOperationException(error.Message, error);
        }
        var tier = applicationTier == RenderingPresentationTier.Enhanced
            ? DragonAgePresentationTier.Enhanced
            : DragonAgePresentationTier.Source;
        return new DragonAgeAreaRenderPolicy(
            layout,
            tier,
            renderingMethod,
            validatedAtmosphere,
            tier == DragonAgePresentationTier.Enhanced,
            validatedAtmosphere ? "ready" : "unsupported",
            quality);
    }
}
