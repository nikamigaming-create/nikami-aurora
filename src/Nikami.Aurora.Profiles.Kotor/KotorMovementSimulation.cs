using System.Numerics;

namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorLocomotionMode
{
    Idle,
    Walk,
    Run
}

public readonly record struct KotorMovementIntent(float Right, float Forward, bool Sprint)
{
    public static KotorMovementIntent FromAxes(
        float right, float forward, bool sprint, float deadZone = 0.2f)
    {
        if (!float.IsFinite(deadZone) || deadZone < 0 || deadZone >= 1)
            throw new ArgumentOutOfRangeException(nameof(deadZone));
        var raw = new Vector2(right, forward);
        var magnitude = raw.Length();
        if (magnitude <= deadZone)
            return new KotorMovementIntent(0, 0, sprint);
        var normalized = raw / magnitude;
        var remappedMagnitude = Math.Clamp((magnitude - deadZone) / (1.0f - deadZone), 0, 1);
        var remapped = normalized * remappedMagnitude;
        return new KotorMovementIntent(remapped.X, remapped.Y, sprint);
    }

    public Vector2 Direction
    {
        get
        {
            var direction = new Vector2(Right, Forward);
            return direction.LengthSquared() > 1.0f ? Vector2.Normalize(direction) : direction;
        }
    }
}

public readonly record struct KotorNavigationTriangle(Vector3 A, Vector3 B, Vector3 C);

public readonly record struct KotorDoorObstacle(Vector3 Position, bool Open, float Radius = 0.65f);

public readonly record struct KotorMovementResult(
    Vector3 Position,
    bool Accepted,
    bool Moved,
    KotorLocomotionMode Mode);

public sealed record KotorMovementConfiguration(
    float WalkSpeed,
    float RunSpeed,
    float BarycentricTolerance = 0.002f)
{
    public KotorMovementConfiguration Validate()
    {
        if (!float.IsFinite(WalkSpeed) || WalkSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(WalkSpeed));
        if (!float.IsFinite(RunSpeed) || RunSpeed <= 0)
            throw new ArgumentOutOfRangeException(nameof(RunSpeed));
        if (!float.IsFinite(BarycentricTolerance) || BarycentricTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(BarycentricTolerance));
        return this;
    }
}

public sealed class KotorMovementSimulation
{
    private readonly IReadOnlyList<KotorNavigationTriangle> navigation;
    private readonly KotorMovementConfiguration configuration;

    public KotorMovementSimulation(
        IReadOnlyList<KotorNavigationTriangle> navigation,
        KotorMovementConfiguration configuration)
    {
        this.navigation = navigation.Count > 0
            ? navigation
            : throw new ArgumentException("Navigation cannot be empty", nameof(navigation));
        this.configuration = configuration.Validate();
    }

    public KotorMovementResult Step(
        Vector3 position,
        float facingRadians,
        KotorMovementIntent intent,
        float deltaSeconds,
        IReadOnlyList<KotorDoorObstacle> doors)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaSeconds));
        var local = intent.Direction;
        if (local.LengthSquared() <= 0.000001f || deltaSeconds == 0)
            return new KotorMovementResult(position, true, false, KotorLocomotionMode.Idle);

        var sin = MathF.Sin(facingRadians);
        var cos = MathF.Cos(facingRadians);
        var right = new Vector2(cos, sin);
        var forward = new Vector2(-sin, cos);
        var worldDirection = Vector2.Normalize(right * local.X + forward * local.Y);
        var mode = intent.Sprint ? KotorLocomotionMode.Run : KotorLocomotionMode.Walk;
        var speed = intent.Sprint ? configuration.RunSpeed : configuration.WalkSpeed;
        var displacement = new Vector3(
            worldDirection.X * speed * deltaSeconds,
            worldDirection.Y * speed * deltaSeconds,
            0);
        return TryDisplace(position, displacement, doors, mode);
    }

    public KotorMovementResult TryDisplace(
        Vector3 position,
        Vector3 displacement,
        IReadOnlyList<KotorDoorObstacle> doors,
        KotorLocomotionMode mode = KotorLocomotionMode.Walk)
    {
        if (!float.IsFinite(displacement.X) || !float.IsFinite(displacement.Y) ||
            !float.IsFinite(displacement.Z))
            throw new ArgumentOutOfRangeException(nameof(displacement));
        if (displacement.LengthSquared() <= 0.000001f)
            return new KotorMovementResult(position, true, false, KotorLocomotionMode.Idle);
        var candidate = position + displacement;

        foreach (var door in doors)
        {
            if (door.Open) continue;
            var offset = new Vector2(candidate.X - door.Position.X, candidate.Y - door.Position.Y);
            if (offset.LengthSquared() < door.Radius * door.Radius)
                return new KotorMovementResult(position, false, false, KotorLocomotionMode.Idle);
        }

        if (!TryProjectToWalkmesh(candidate, out var ground))
            return new KotorMovementResult(position, false, false, KotorLocomotionMode.Idle);
        candidate.Z = ground;
        return new KotorMovementResult(candidate, true, true, mode);
    }

    public bool TryProjectToWalkmesh(Vector3 position, out float ground)
    {
        ground = 0;
        var bestDistance = float.PositiveInfinity;
        var found = false;
        foreach (var triangle in navigation)
        {
            var a = new Vector2(triangle.A.X, triangle.A.Y);
            var b = new Vector2(triangle.B.X, triangle.B.Y);
            var c = new Vector2(triangle.C.X, triangle.C.Y);
            var point = new Vector2(position.X, position.Y);
            var denominator = (b.Y - c.Y) * (a.X - c.X) +
                              (c.X - b.X) * (a.Y - c.Y);
            if (MathF.Abs(denominator) < 0.000001f) continue;
            var weightA = ((b.Y - c.Y) * (point.X - c.X) +
                           (c.X - b.X) * (point.Y - c.Y)) / denominator;
            var weightB = ((c.Y - a.Y) * (point.X - c.X) +
                           (a.X - c.X) * (point.Y - c.Y)) / denominator;
            var weightC = 1.0f - weightA - weightB;
            var tolerance = configuration.BarycentricTolerance;
            if (weightA < -tolerance || weightB < -tolerance || weightC < -tolerance)
                continue;
            var candidateGround = weightA * triangle.A.Z +
                                  weightB * triangle.B.Z +
                                  weightC * triangle.C.Z;
            var distance = MathF.Abs(candidateGround - position.Z);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            ground = candidateGround;
            found = true;
        }
        return found;
    }
}
