using Godot;
using OpenDAO.Domain.World;

namespace OpenDAO.Application.Abstractions;

public interface IWorldArrivalResolver
{
    WorldArrival? Resolve(WorldProfile profile);
}

public sealed record WorldArrival(string Waypoint, Transform3D Transform, string Source);
