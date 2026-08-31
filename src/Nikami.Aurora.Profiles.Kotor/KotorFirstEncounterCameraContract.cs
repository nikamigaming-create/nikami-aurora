namespace Nikami.Aurora.Profiles.Kotor;

public sealed record KotorEncounterCameraBeat(
    int CameraId,
    string SubjectTag,
    float SubjectRadius,
    float MinimumViewportMargin,
    float MinimumProjectedHeight,
    float MaximumProjectedHeight);

/// <summary>
/// Source-event targets for the three authored end_room3 cuts. Camera 26 is
/// k_pend_camera's party-entry establishing view. Cameras 19 and 20 present
/// k_pend_cut1_1's attacks on end_soldier2 from opposite corridor ends.
/// </summary>
public static class KotorFirstEncounterCameraContract
{
    public static IReadOnlyList<KotorEncounterCameraBeat> Beats { get; } =
    [
        new(26, "PLAYER", 0.62f, 0.01f, 0.05f, 0.45f),
        new(19, "end_soldier2", 0.62f, 0.01f, 0.18f, 0.78f),
        new(20, "end_soldier2", 0.62f, 0.01f, 0.04f, 0.35f)
    ];

    public static KotorEncounterCameraBeat Require(int cameraId) =>
        Beats.SingleOrDefault(beat => beat.CameraId == cameraId)
        ?? throw new ArgumentOutOfRangeException(
            nameof(cameraId), cameraId,
            "No source-bound first-encounter camera beat is defined");
}
