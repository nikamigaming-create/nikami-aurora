using Godot;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public interface IWorldLoadScheduler
{
    int YieldCount { get; }

    double MaxWorkSliceMilliseconds { get; }
    void Reset();
    Task YieldIfNeededAsync(Node owner, CancellationToken cancellationToken, bool force = false);
}
