namespace Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

public sealed class RuntimeBundlePaths(string root = "")
{
    private static readonly string[] ProfilePathKeys =
        ["area_file", "area_root", "actor_file", "actor_root", "terrain_materials", "talktable_file"];
    private const string DaoWorldMarker = "/dao-world/";

    public string Root { get; } = root;

    public string Resolve(string value)
    {
        if (value.Length == 0 || File.Exists(value) || Directory.Exists(value) || Root.Length == 0) return value;
        var normalized = value.Replace('\\', '/');
        var marker = normalized.LastIndexOf(DaoWorldMarker, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return value;
        var relative = normalized[(marker + DaoWorldMarker.Length)..].Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.Combine(Root, relative);
        return File.Exists(candidate) || Directory.Exists(candidate) ? candidate : value;
    }

    public void Rebase(System.Text.Json.Nodes.JsonObject profile)
    {
        foreach (var key in ProfilePathKeys)
            if (profile[key] is not null) profile[key] = Resolve(profile[key]!.GetValue<string>());
    }
}
