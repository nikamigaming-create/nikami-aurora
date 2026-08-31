using System.Numerics;

namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorDialogueShotKind
{
    Speaker,
    SpeakerTight
}

public sealed record KotorDialogueCameraShot(
    Vector3 Position,
    Vector3 Target,
    Vector3 Up,
    float VerticalFieldOfViewDegrees,
    KotorDialogueShotKind Kind);

/// <summary>
/// Profile-owned deterministic fallback for KOTOR DLG nodes that do not name
/// an authored static camera. Inputs are source-bound participant talk points;
/// the renderer only applies the resulting presentation transform.
/// </summary>
public static class KotorDialogueCameraComposer
{
    public static KotorDialogueCameraShot ComposeSpeakerShot(
        Vector3 listenerTalkPoint,
        Vector3 speakerTalkPoint,
        int cameraAngle,
        float verticalFieldOfViewDegrees)
    {
        if (!Finite(listenerTalkPoint) || !Finite(speakerTalkPoint))
            throw new ArgumentException("Dialogue talk points must be finite");
        if (!float.IsFinite(verticalFieldOfViewDegrees) ||
            verticalFieldOfViewDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(
                nameof(verticalFieldOfViewDegrees),
                "Dialogue FOV must be in (0, 180) degrees");

        var listenerToSpeaker = speakerTalkPoint - listenerTalkPoint;
        var distance = listenerToSpeaker.Length();
        if (distance < 0.01f)
            throw new ArgumentException(
                "Dialogue participants must have distinct talk points");

        var direction = listenerToSpeaker / distance;
        var side = Vector3.Cross(direction, -Vector3.UnitY);
        if (side.LengthSquared() < 0.000001f)
            throw new ArgumentException(
                "Dialogue participant axis must not be vertical");
        side = Vector3.Normalize(side);

        // CameraAngle=0 is the source's automatic dialogue framing request,
        // not a gameplay-camera handoff.  Keep the visible speaker in the
        // same close composition family as the explicit face angles; a
        // midpoint two-shot makes the first visible Trask beat read as a
        // distant first-person view when the participants start far apart.
        var tightSpeaker = cameraAngle is 0 or 1 or 2 or 4;
        Vector3 eye;
        if (tightSpeaker)
        {
            var forwardOffset = MathF.Min(0.325f * distance, 0.875f);
            var sideOffset = MathF.Min(0.11f * distance, 0.293f);
            eye = speakerTalkPoint - forwardOffset * direction +
                  sideOffset * side + 0.1f * Vector3.UnitY;
        }
        else
        {
            var center = 0.5f * (listenerTalkPoint + speakerTalkPoint);
            var offset = MathF.Min(0.25f * distance, 1.0f);
            eye = center - offset * direction + offset * side +
                  0.1f * Vector3.UnitY;
        }

        var targetHeight = tightSpeaker ? -0.1f : 0.1f;
        var targetSideOffset = (tightSpeaker ? 0.035f : 0.1f) * distance;
        var target = speakerTalkPoint - targetSideOffset * side +
                     targetHeight * Vector3.UnitY;
        return new KotorDialogueCameraShot(
            eye,
            target,
            Vector3.UnitY,
            verticalFieldOfViewDegrees,
            tightSpeaker ? KotorDialogueShotKind.SpeakerTight :
                KotorDialogueShotKind.Speaker);
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
