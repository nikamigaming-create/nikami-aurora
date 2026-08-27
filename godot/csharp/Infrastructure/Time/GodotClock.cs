using Godot;
using OpenDAO.Application.Abstractions;

namespace OpenDAO.Infrastructure.Time;

public sealed class GodotClock : IClock
{
    public double ElapsedSeconds => Godot.Time.GetTicksMsec() / 1000.0;
    public long ElapsedMilliseconds => unchecked((long)Godot.Time.GetTicksMsec());
}
