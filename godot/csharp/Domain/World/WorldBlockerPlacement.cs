using System.Numerics;

namespace Nikami.Aurora.GodotRuntime.Domain.World;

public sealed record WorldBlockerPlacement(
    string Template,
    string Tag,
    Vector3 Position,
    Quaternion Rotation,
    WorldBlockerKind Kind);

public enum WorldBlockerKind
{
    Door,
    InvisibleWide,
    InvisibleStandard
}
