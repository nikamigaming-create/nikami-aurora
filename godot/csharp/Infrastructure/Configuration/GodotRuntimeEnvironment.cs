using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

public sealed class GodotRuntimeEnvironment : IRuntimeEnvironment
{
    private readonly IReadOnlyDictionary<string, string> values = CaptureValues();

    public string Get(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return values.GetValueOrDefault(name, string.Empty);
    }

    public bool IsEnabled(string name) => string.Equals(Get(name), "1", StringComparison.Ordinal);
    public string GlobalizePath(string path) => ProjectSettings.GlobalizePath(path);
    public string ExecutableDirectory => Path.GetDirectoryName(OS.GetExecutablePath()) ?? string.Empty;

    private static IReadOnlyDictionary<string, string> CaptureValues()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in
                 System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
                result[name] = value.Trim();
        }
        return result;
    }
}
