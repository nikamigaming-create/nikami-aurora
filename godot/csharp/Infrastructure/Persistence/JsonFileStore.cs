using System.Text.Json;
using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Persistence;

public sealed class JsonFileStore(IRuntimeEnvironment environment) : IJsonStore
{
    public JsonObject? Read(string path)
    {
        var resolved = Resolve(path);
        if (resolved.Length == 0 || !File.Exists(resolved)) return null;
        try { return JsonNode.Parse(File.ReadAllText(resolved)) as JsonObject; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public bool Write(string path, JsonObject document)
    {
        var resolved = Resolve(path);
        if (resolved.Length == 0) return false;
        try
        {
            var directory = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temporary = resolved + ".tmp";
            File.WriteAllText(temporary,
                document.ToJsonString(RuntimeJsonOptions.Indented) + System.Environment.NewLine);
            File.Move(temporary, resolved, true);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public bool Exists(string path) => File.Exists(Resolve(path));

    private string Resolve(string path) => path.StartsWith("res://", StringComparison.Ordinal) ||
        path.StartsWith("user://", StringComparison.Ordinal)
        ? environment.GlobalizePath(path)
        : path;
}
