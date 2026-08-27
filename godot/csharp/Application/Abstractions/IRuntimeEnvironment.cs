namespace OpenDAO.Application.Abstractions;

public interface IRuntimeEnvironment
{
    string Get(string name);
    bool IsEnabled(string name);
    string GlobalizePath(string path);
    string ExecutableDirectory { get; }
}
