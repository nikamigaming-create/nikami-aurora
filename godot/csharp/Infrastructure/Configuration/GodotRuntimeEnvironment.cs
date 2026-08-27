using Godot;
using OpenDAO.Application.Abstractions;

namespace OpenDAO.Infrastructure.Configuration;

public sealed class GodotRuntimeEnvironment : IRuntimeEnvironment
{
    public string Get(string name) => System.Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;
    public bool IsEnabled(string name) => string.Equals(Get(name), "1", StringComparison.Ordinal);
    public string GlobalizePath(string path) => ProjectSettings.GlobalizePath(path);
    public string ExecutableDirectory => Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty;
}
