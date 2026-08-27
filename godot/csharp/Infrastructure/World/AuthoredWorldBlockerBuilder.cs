using Godot;
using OpenDAO.Domain.World;
using NumericsQuaternion = System.Numerics.Quaternion;
using NumericsVector3 = System.Numerics.Vector3;

namespace OpenDAO.Infrastructure.World;

public sealed class AuthoredWorldBlockerBuilder : IAuthoredWorldBlockerBuilder
{
    private const uint AuthoredBoundaryLayer = 2;

    public int Build(IReadOnlyList<WorldBlockerPlacement> blockers, Node3D destination)
    {
        foreach (var blocker in blockers)
        {
            var size = SizeFor(blocker.Kind);
            var body = new StaticBody3D
            {
                Name = $"AuthoredBlocker_{Sanitize(blocker.Tag)}",
                CollisionLayer = AuthoredBoundaryLayer,
                CollisionMask = 0,
                Transform = ToGodotTransform(blocker.Position, blocker.Rotation)
            };
            body.SetMeta("dao_authored_blocker", true);
            body.SetMeta("dao_template", blocker.Template);
            body.AddChild(new CollisionShape3D
            {
                Position = Vector3.Up * (size.Y * 0.5f),
                Shape = new BoxShape3D { Size = size }
            });
            destination.AddChild(body);
        }

        return blockers.Count;
    }

    private static Vector3 SizeFor(WorldBlockerKind kind) => kind switch
    {
        WorldBlockerKind.InvisibleWide => new Vector3(5.5f, 3.0f, 0.45f),
        WorldBlockerKind.Door => new Vector3(2.2f, 3.0f, 0.4f),
        _ => new Vector3(1.2f, 2.6f, 0.4f)
    };

    private static Transform3D ToGodotTransform(NumericsVector3 position, NumericsQuaternion rotation)
    {
        var sourceRotation = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var basis = conversion * new Basis(sourceRotation) * conversion.Inverse();
        return new Transform3D(basis, new Vector3(position.X, position.Z, -position.Y));
    }

    private static string Sanitize(string value) => value.Replace('/', '_').Replace(':', '_');
}
