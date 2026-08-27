using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenDAO.Infrastructure.Configuration;

public sealed record DaoPresentationConfiguration(
    int SchemaVersion,
    DaoGameplayCameraConfiguration GameplayCamera)
{
    public const int CurrentSchemaVersion = 1;
    private const string DefaultPath = "res://config/dao/presentation.json";

    public static DaoPresentationConfiguration Load(string path = DefaultPath)
    {
        var resolved = DaoRuntimePaths.ResolveSourcePath(path);
        if (!File.Exists(resolved))
            throw new FileNotFoundException(
                "DAO presentation configuration is missing", resolved);

        var configuration = JsonSerializer.Deserialize<DaoPresentationConfiguration>(
                File.ReadAllBytes(resolved),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
                })
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
    float SpringLengthMeters,
    float CollisionMarginMeters,
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
        if (!float.IsFinite(SpringLengthMeters) || SpringLengthMeters is < 0.8f or > 20)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera spring length must be in [0.8, 20] meters");
        if (!float.IsFinite(CollisionMarginMeters) || CollisionMarginMeters is < 0 or > 1)
            throw new InvalidDataException(
                "Configured DAO gameplay-camera collision margin must be in [0, 1] meters");
        if (CalibrationStatus is not (PendingRetailMatch or RetailAccepted))
            throw new InvalidDataException(
                $"Unsupported DAO gameplay-camera calibration status: {CalibrationStatus}");
    }
}
