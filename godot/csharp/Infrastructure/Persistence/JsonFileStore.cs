using System.Text.Json;
using System.Text.Json.Nodes;
using OpenDAO.Application.Abstractions;

namespace OpenDAO.Infrastructure.Persistence;

public sealed class JsonFileStore(IRuntimeEnvironment environment) : IJsonStore
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

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
            File.WriteAllText(temporary, document.ToJsonString(WriteOptions) + System.Environment.NewLine);
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
