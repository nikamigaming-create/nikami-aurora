using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

public sealed class WorldProfileProvider(IJsonStore store, IRuntimeEnvironment environment) : IWorldProfileProvider
{
    private const string DefaultProfile = "res://profiles/local.json";
    private const string SelectedProfile = "user://selected-area-profile.json";

    public string ResolveProfilePath()
    {
        var configured = environment.Get("OPENDAO_PROFILE");
        if (configured.Length > 0) return configured;
        var selected = environment.Get("OPENDAO_SELECTED_PROFILE");
        if (selected.Length > 0) return selected;
        if (store.Exists(SelectedProfile)) return SelectedProfile;
        return DefaultProfile;
    }

    public WorldProfile Load()
    {
        var path = ResolveProfilePath();
        var document = store.Read(path) ?? throw new InvalidOperationException($"Invalid world profile: {path}");
        var bundleRoot = FindBundleRoot();
        new RuntimeBundlePaths(bundleRoot).Rebase(document);
        return new(
            Int(document, "schema", 1), path,
            Text(document, "area_id", Text(document, "area", "unknown")),
            Text(document, "display_name", Text(document, "area_id", Text(document, "area", "DAO Area"))),
            Text(document, "area_file"), Text(document, "area_root"), Text(document, "scene_file"),
            Text(document, "actor_file"), Text(document, "actor_root"), Text(document, "terrain_materials"),
            Text(document, "game_root"), Text(document, "area"),
            Flag(document, "use_dao_static_shader"), Flag(document, "use_dao_hslmatrix"),
            Text(document, "source_key"), Text(document, "talktable_file"));
    }

    private string FindBundleRoot()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData", "cache", "dao-world"),
            Path.Combine(environment.ExecutableDirectory, "Nikami.Aurora.GodotRuntimeData", "dao-world")
        }) if (Directory.Exists(candidate)) return candidate;
        return string.Empty;
    }

    private static string Text(JsonObject value, string key, string fallback = "") =>
        value[key]?.GetValue<string>() ?? fallback;
    private static int Int(JsonObject value, string key, int fallback) =>
        value[key]?.GetValue<int>() ?? fallback;
    private static bool Flag(JsonObject value, string key) => value[key]?.GetValue<bool>() ?? false;
}
