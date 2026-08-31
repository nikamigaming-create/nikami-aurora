namespace Nikami.Aurora.GodotRuntime.Application.Abstractions;

public interface IClock
{
    double ElapsedSeconds { get; }
    long ElapsedMilliseconds { get; }
}
