namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorModuleContentMode
{
    GenericWorld,
    EndarOpening
}

public readonly record struct KotorModuleVisualInventory(
    int AuthoredRooms,
    int VisualRooms,
    int AuthoredMaterialSurfaces,
    int ConfiguredMaterialSurfaces,
    int AuthoredEmitters,
    int MaterializedEmitters,
    int EnvironmentMaps,
    int BoundEnvironmentMaps,
    int MissingSourceAssets,
    int ReportedMissingSourceAssets,
    int UnsupportedSourceSemantics);

public readonly record struct KotorPbrCoverage(
    int RenderableSurfaces,
    int SourceUnshadedSurfaces,
    int PbrSurfaces,
    bool EnhancedPresentation)
{
    public int PbrEligibleSurfaces => RenderableSurfaces - SourceUnshadedSurfaces;
}

public readonly record struct KotorCreaturePresentationInventory(
    int SourceCreatures,
    int RenderedCreatures,
    int UnsupportedCreatures,
    int SourceModelParts,
    int MaterializedModelParts,
    int EquippedWeapons,
    int MaterializedEquippedWeapons,
    int WeaponAdditiveSurfaces,
    int ConfiguredWeaponAdditiveSurfaces,
    int UnsupportedEffectNodes);

/// <summary>
/// Profile-owned boundary between reusable Odyssey module presentation and the
/// source-specific Endar Spire opening route. Generic modules receive the same
/// room material, lightmap, environment-map, emitter, and tier policy without
/// acquiring Endar dialogue, camera, or automation assumptions.
/// </summary>
public static class KotorModulePresentationPolicy
{
    public const string EndarModuleId = "end_m01aa";
    public const string GenericWorldMode = "generic-world";
    public const string EndarOpeningMode = "endar-opening";
    public const string MissingSourceAssetPolicy =
        "source-absence-report-no-fabrication-v1";
    public const float EnhancedAdditiveGlowMultiplier = 1.8f;

    public static float AdditiveGlowMultiplier(bool enhancedPresentation) =>
        enhancedPresentation ? EnhancedAdditiveGlowMultiplier : 1.0f;

    public static string RequireModuleId(string moduleId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var normalized = moduleId.Trim().ToLowerInvariant();
        if (normalized.Length > 16 || normalized.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException(
                $"Unsupported KOTOR module identifier: {moduleId}");
        return normalized;
    }

    public static KotorModuleContentMode RequireContentMode(
        string moduleId,
        string contentMode,
        bool hasFirstEncounter)
    {
        var module = RequireModuleId(moduleId);
        var mode = contentMode switch
        {
            GenericWorldMode => KotorModuleContentMode.GenericWorld,
            EndarOpeningMode => KotorModuleContentMode.EndarOpening,
            _ => throw new InvalidDataException(
                $"Unsupported KOTOR module content mode: {contentMode}")
        };
        var isEndar = module.Equals(
            EndarModuleId, StringComparison.OrdinalIgnoreCase);
        if (isEndar != (mode == KotorModuleContentMode.EndarOpening) ||
            hasFirstEncounter != isEndar)
            throw new InvalidDataException(
                $"KOTOR module content identity is inconsistent: " +
                $"module={module} mode={contentMode} firstEncounter={hasFirstEncounter}");
        return mode;
    }

    public static void RequireVisualInventory(KotorModuleVisualInventory inventory)
    {
        if (inventory.AuthoredRooms <= 0 || inventory.VisualRooms <= 0 ||
            inventory.VisualRooms > inventory.AuthoredRooms)
            throw new InvalidDataException(
                "KOTOR module room presentation inventory is incomplete");
        if (inventory.AuthoredMaterialSurfaces <= 0 ||
            inventory.ConfiguredMaterialSurfaces != inventory.AuthoredMaterialSurfaces)
            throw new InvalidDataException(
                "KOTOR module material-surface coverage is incomplete");
        if (inventory.AuthoredEmitters < 0 ||
            inventory.MaterializedEmitters != inventory.AuthoredEmitters)
            throw new InvalidDataException(
                "KOTOR module emitter coverage is incomplete");
        if (inventory.EnvironmentMaps < 0 ||
            inventory.BoundEnvironmentMaps != inventory.EnvironmentMaps)
            throw new InvalidDataException(
                "KOTOR module environment-map coverage is incomplete");
        if (inventory.MissingSourceAssets < 0 ||
            inventory.ReportedMissingSourceAssets != inventory.MissingSourceAssets)
            throw new InvalidDataException(
                "KOTOR module missing-source-asset reporting is incomplete");
        if (inventory.UnsupportedSourceSemantics != 0)
            throw new InvalidDataException(
                "KOTOR module contains unsupported source presentation semantics");
    }

    public static void RequirePbrCoverage(KotorPbrCoverage coverage)
    {
        if (coverage.RenderableSurfaces <= 0 ||
            coverage.SourceUnshadedSurfaces < 0 ||
            coverage.SourceUnshadedSurfaces > coverage.RenderableSurfaces ||
            coverage.PbrSurfaces < 0)
            throw new InvalidDataException("KOTOR PBR surface inventory is invalid");
        var expected = coverage.EnhancedPresentation
            ? coverage.PbrEligibleSurfaces
            : 0;
        if (coverage.PbrSurfaces != expected)
            throw new InvalidDataException(
                "KOTOR global PBR surface coverage is incomplete: " +
                $"renderable={coverage.RenderableSurfaces} " +
                $"source_unshaded={coverage.SourceUnshadedSurfaces} " +
                $"eligible={coverage.PbrEligibleSurfaces} " +
                $"pbr={coverage.PbrSurfaces} " +
                $"tier={(coverage.EnhancedPresentation ? "enhanced" : "source")}");
    }

    public static void RequireCreaturePresentation(
        KotorCreaturePresentationInventory inventory)
    {
        if (inventory.SourceCreatures < 0 || inventory.RenderedCreatures < 0 ||
            inventory.UnsupportedCreatures < 0 ||
            inventory.RenderedCreatures + inventory.UnsupportedCreatures !=
                inventory.SourceCreatures ||
            inventory.UnsupportedCreatures != 0)
            throw new InvalidDataException(
                "KOTOR source-creature render coverage is incomplete");
        if (inventory.SourceModelParts < inventory.SourceCreatures ||
            inventory.MaterializedModelParts != inventory.SourceModelParts)
            throw new InvalidDataException(
                "KOTOR source-creature model-part coverage is incomplete");
        if (inventory.EquippedWeapons < 0 ||
            inventory.MaterializedEquippedWeapons != inventory.EquippedWeapons)
            throw new InvalidDataException(
                "KOTOR equipped-weapon model coverage is incomplete");
        if (inventory.WeaponAdditiveSurfaces < 0 ||
            inventory.ConfiguredWeaponAdditiveSurfaces !=
                inventory.WeaponAdditiveSurfaces)
            throw new InvalidDataException(
                "KOTOR equipped-weapon additive coverage is incomplete");
        if (inventory.UnsupportedEffectNodes != 0)
            throw new InvalidDataException(
                "KOTOR creature assembly contains unsupported effect nodes");
    }

    public static void RequireEndarAutomation(
        KotorModuleContentMode mode,
        bool requested)
    {
        if (requested && mode != KotorModuleContentMode.EndarOpening)
            throw new InvalidDataException(
                "Endar story/camera automation was requested for a generic KOTOR module");
    }
}
