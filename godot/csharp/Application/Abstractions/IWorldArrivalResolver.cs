using Godot;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IWorldArrivalResolver
{
    WorldArrival? Resolve(WorldProfile profile);
}

public sealed record WorldArrival(string Waypoint, Transform3D Transform, string Source);
