using System.Numerics;

namespace Nikami.Aurora.Core;

[Flags]
public enum CinematicFramingFailure
{
    None = 0,
    BehindCamera = 1 << 0,
    OutsideViewport = 1 << 1,
    SubjectTooSmall = 1 << 2,
    SubjectTooLarge = 1 << 3,
    Occluded = 1 << 4
}

/// <summary>
/// Renderer-neutral inputs for proving that an intended cinematic subject is
/// actually presented by a camera. Coordinates may use any handedness as long
/// as the supplied forward and up vectors use the same basis.
/// </summary>
public sealed record CinematicFramingSample(
    Vector3 CameraPosition,
    Vector3 CameraForward,
    Vector3 CameraUp,
    float VerticalFieldOfViewDegrees,
    float AspectRatio,
    float NearPlane,
    Vector3 SubjectCenter,
    float SubjectRadius,
    bool LineOfSightClear);

public sealed record CinematicFramingRequirements(
    float MinimumViewportMargin,
    float MinimumProjectedHeight,
    float MaximumProjectedHeight);

public sealed record CinematicFramingResult(
    CinematicFramingFailure Failures,
    Vector2 NormalizedViewportCenter,
    float ProjectedHeight,
    float CameraDepth)
{
    public bool Accepted => Failures == CinematicFramingFailure.None;
}

/// <summary>
/// Objective framing gate shared by game profiles. It proves subject depth,
/// full-bounds viewport containment, meaningful projected size, and caller-
/// supplied line of sight without making a claim about image grading.
/// </summary>
public static class CinematicFramingGate
{
    public static CinematicFramingResult Evaluate(
        CinematicFramingSample sample,
        CinematicFramingRequirements requirements)
    {
        Validate(sample, requirements);

        var forward = Vector3.Normalize(sample.CameraForward);
        var projectedUp = sample.CameraUp - forward * Vector3.Dot(sample.CameraUp, forward);
        if (projectedUp.LengthSquared() < 0.000001f)
            throw new ArgumentException("Camera forward and up vectors must not be parallel", nameof(sample));
        var up = Vector3.Normalize(projectedUp);
        var right = Vector3.Normalize(Vector3.Cross(forward, up));
        up = Vector3.Normalize(Vector3.Cross(right, forward));

        var offset = sample.SubjectCenter - sample.CameraPosition;
        var depth = Vector3.Dot(offset, forward);
        var failures = CinematicFramingFailure.None;
        if (depth - sample.SubjectRadius <= sample.NearPlane)
            failures |= CinematicFramingFailure.BehindCamera;

        var safeDepth = MathF.Max(depth, sample.NearPlane + sample.SubjectRadius);
        var tangent = MathF.Tan(sample.VerticalFieldOfViewDegrees * MathF.PI / 360.0f);
        var verticalHalfExtent = safeDepth * tangent;
        var horizontalHalfExtent = verticalHalfExtent * sample.AspectRatio;
        var center = new Vector2(
            Vector3.Dot(offset, right) / horizontalHalfExtent,
            Vector3.Dot(offset, up) / verticalHalfExtent);
        var radius = new Vector2(
            sample.SubjectRadius / horizontalHalfExtent,
            sample.SubjectRadius / verticalHalfExtent);
        var viewportLimit = 1.0f - requirements.MinimumViewportMargin * 2.0f;
        if (MathF.Abs(center.X) + radius.X > viewportLimit ||
            MathF.Abs(center.Y) + radius.Y > viewportLimit)
            failures |= CinematicFramingFailure.OutsideViewport;

        // The vertical normalized-device-coordinate diameter is two radii and
        // the viewport height is also two units, so radius.Y is the fraction
        // of total viewport height occupied by the subject diameter.
        var projectedHeight = radius.Y;
        if (projectedHeight < requirements.MinimumProjectedHeight)
            failures |= CinematicFramingFailure.SubjectTooSmall;
        if (projectedHeight > requirements.MaximumProjectedHeight)
            failures |= CinematicFramingFailure.SubjectTooLarge;
        if (!sample.LineOfSightClear)
            failures |= CinematicFramingFailure.Occluded;

        return new CinematicFramingResult(failures, center, projectedHeight, depth);
    }

    private static void Validate(
        CinematicFramingSample sample,
        CinematicFramingRequirements requirements)
    {
        if (!Finite(sample.CameraPosition) || !Finite(sample.CameraForward) ||
            !Finite(sample.CameraUp) || !Finite(sample.SubjectCenter))
            throw new ArgumentException("Cinematic framing vectors must be finite", nameof(sample));
        if (sample.CameraForward.LengthSquared() < 0.000001f ||
            sample.CameraUp.LengthSquared() < 0.000001f)
            throw new ArgumentException("Camera forward and up vectors must be non-zero", nameof(sample));
        if (!float.IsFinite(sample.VerticalFieldOfViewDegrees) ||
            sample.VerticalFieldOfViewDegrees is <= 0 or >= 180)
            throw new ArgumentOutOfRangeException(nameof(sample), "Vertical FOV must be in (0, 180) degrees");
        if (!float.IsFinite(sample.AspectRatio) || sample.AspectRatio <= 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "Aspect ratio must be positive");
        if (!float.IsFinite(sample.NearPlane) || sample.NearPlane < 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "Near plane must be non-negative");
        if (!float.IsFinite(sample.SubjectRadius) || sample.SubjectRadius <= 0)
            throw new ArgumentOutOfRangeException(nameof(sample), "Subject radius must be positive");
        if (!float.IsFinite(requirements.MinimumViewportMargin) ||
            requirements.MinimumViewportMargin is < 0 or >= 0.5f)
            throw new ArgumentOutOfRangeException(nameof(requirements),
                "Viewport margin must be in [0, 0.5)");
        if (!float.IsFinite(requirements.MinimumProjectedHeight) ||
            !float.IsFinite(requirements.MaximumProjectedHeight) ||
            requirements.MinimumProjectedHeight <= 0 ||
            requirements.MaximumProjectedHeight <= requirements.MinimumProjectedHeight ||
            requirements.MaximumProjectedHeight > 1)
            throw new ArgumentOutOfRangeException(nameof(requirements),
                "Projected-height bounds must satisfy 0 < minimum < maximum <= 1");
    }

    private static bool Finite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
