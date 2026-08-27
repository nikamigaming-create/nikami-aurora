namespace OpenDAO.Application.Abstractions;

public interface IClock
{
    double ElapsedSeconds { get; }
    long ElapsedMilliseconds { get; }
}
