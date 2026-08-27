using Godot;

namespace OpenDAO.Infrastructure.World;

public sealed class GodotWorldLoadScheduler : IWorldLoadScheduler
{
    private const ulong WorkSliceMicroseconds = 4_000;
    private ulong sliceStarted;

    public int YieldCount { get; private set; }

    public double MaxWorkSliceMilliseconds { get; private set; }

    public void Reset()
    {
        YieldCount = 0;
        MaxWorkSliceMilliseconds = 0;
        sliceStarted = Godot.Time.GetTicksUsec();
    }

    public async Task YieldIfNeededAsync(Node owner, CancellationToken cancellationToken, bool force = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var elapsed = Godot.Time.GetTicksUsec() - sliceStarted;
        MaxWorkSliceMilliseconds = Math.Max(MaxWorkSliceMilliseconds, elapsed / 1_000.0);
        if (!force && elapsed < WorkSliceMicroseconds) return;
        YieldCount++;
        await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        cancellationToken.ThrowIfCancellationRequested();
        sliceStarted = Godot.Time.GetTicksUsec();
    }
}
