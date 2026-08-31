using System.Text.Json;
using System.Text.Json.Serialization;
using Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

public sealed record DaoPresentationConfiguration(
    int SchemaVersion,
    DaoGameplayCameraConfiguration GameplayCamera)
{
    public const int CurrentSchemaVersion = 2;
    private const string DefaultPath = "res://config/dao/presentation.json";

    public static DaoPresentationConfiguration Load(string path = DefaultPath)
    {
        var resolved = DaoRuntimePaths.ResolveSourcePath(path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                "DAO presentation configuration is missing", resolved);

        var configuration = JsonSerializer.Deserialize<DaoPresentationConfiguration>(
                File.ReadAllBytes(resolved),
                RuntimeJsonOptions.StrictCaseInsensitive)
            ?? throw new InvalidDataException(
                "DAO presentation configuration is empty");
        return configuration.Validate();
    }

    public DaoPresentationConfiguration Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported DAO presentation configuration schema: {SchemaVersion}");
        ArgumentNullException.ThrowIfNull(GameplayCamera);
        GameplayCamera.Validate();
        return this;
    }
}

public sealed record DaoGameplayCameraConfiguration(
    float FieldOfViewDegrees,
    float NearPlaneMeters,
    float FarPlaneMeters,
    float PitchDegrees,
    float PivotHeightMeters,
    float SpringLengthMeters,
    float CollisionMarginMeters,
    float EnhancedPivotHeightMeters,
    float EnhancedCompressedPivotHeightMeters,
    float EnhancedCompressedPivotLateralMeters,
    float EnhancedCollisionProbeRadiusMeters,
    float EnhancedMinimumAvatarClearanceMeters,
    float EnhancedAvatarClearanceHysteresisMeters,
    float EnhancedAvatarBodyTransparency,
    float EnhancedAvatarHeadTransparency,
    string CalibrationStatus)
{
    public const string PendingRetailMatch = "pending-retail-match";
    public const string RetailAccepted = "retail-accepted";

    internal void Validate()
    {
        if (!float.IsFinite(FieldOfViewDegrees) || FieldOfViewDegrees is < 20 or > 100)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera FOV must be in [20, 100] degrees");
        if (!float.IsFinite(NearPlaneMeters) || NearPlaneMeters is <= 0 or > 1)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera near plane must be in (0, 1] meters");
        if (!float.IsFinite(FarPlaneMeters) || FarPlaneMeters <= NearPlaneMeters)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera far plane must exceed its near plane");
        if (!float.IsFinite(PitchDegrees) || PitchDegrees is < -85 or > 0)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera pitch must be in [-85, 0] degrees");
        if (!float.IsFinite(PivotHeightMeters) || PivotHeightMeters is < 0.2f or > 3)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera pivot height must be in [0.2, 3] meters");
        if (!float.IsFinite(SpringLengthMeters) || SpringLengthMeters is < 0.8f or > 20)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera spring length must be in [0.8, 20] meters");
        if (!float.IsFinite(CollisionMarginMeters) || CollisionMarginMeters is < 0 or > 1)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera collision margin must be in [0, 1] meters");
        if (!float.IsFinite(EnhancedPivotHeightMeters) ||
            EnhancedPivotHeightMeters is < 0.2f or > 3)
            throw new InvalidDataException(
                "Configured enhanced DAO gameplay-camera pivot height must be in [0.2, 3] meters");
        if (!float.IsFinite(EnhancedCompressedPivotHeightMeters) ||
            EnhancedCompressedPivotHeightMeters is < 0.2f or > 3)
            throw new InvalidDataException(
                "Configured enhanced compressed-pivot height must be in [0.2, 3] meters");
        if (!float.IsFinite(EnhancedCompressedPivotLateralMeters) ||
            EnhancedCompressedPivotLateralMeters is < -2 or > 2)
            throw new InvalidDataException(
                "Configured enhanced compressed-pivot lateral offset must be in [-2, 2] meters");
        if (!float.IsFinite(EnhancedCollisionProbeRadiusMeters) ||
            EnhancedCollisionProbeRadiusMeters is < 0.05f or > 1)
            throw new InvalidDataException(
                "Configured enhanced DAO camera probe radius must be in [0.05, 1] meters");
        if (!float.IsFinite(EnhancedMinimumAvatarClearanceMeters) ||
            EnhancedMinimumAvatarClearanceMeters < 0.2f ||
            EnhancedMinimumAvatarClearanceMeters > SpringLengthMeters)
            throw new InvalidDataException(
                "Configured enhanced DAO avatar clearance must be in [0.2, spring length] meters");
        if (!float.IsFinite(EnhancedAvatarClearanceHysteresisMeters) ||
            EnhancedAvatarClearanceHysteresisMeters is < 0 or > 1)
            throw new InvalidDataException(
                "Configured enhanced DAO avatar-clearance hysteresis must be in [0, 1] meters");
        if (!float.IsFinite(EnhancedAvatarBodyTransparency) ||
            EnhancedAvatarBodyTransparency is < 0 or > 0.85f)
            throw new InvalidDataException(
                "Configured enhanced DAO body transparency must be in [0, 0.85]");
        if (!float.IsFinite(EnhancedAvatarHeadTransparency) ||
            EnhancedAvatarHeadTransparency is < 0 or > 1)
            throw new InvalidDataException(
                "Configured enhanced DAO head transparency must be in [0, 1]");
        if (CalibrationStatus is not (PendingRetailMatch or RetailAccepted))
            throw new InvalidDataException(
                $"Unsupported DAO gameplay-camera calibration status: {CalibrationStatus}");
    }
}
