using System.Text.Json.Nodes;
using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class GodotWorldArrivalResolver(IJsonStore store) : IWorldArrivalResolver
{
    public WorldArrival? Resolve(WorldProfile profile)
    {
        var pendingPath = ResolvePath("OPENDAO_PENDING_TRANSITION", "user://opendao-pending-transition.json");
        if (OS.GetEnvironment("OPENDAO_IGNORE_PENDING_TRANSITION") != "1" && File.Exists(pendingPath))
        {
            var pending = store.Read(pendingPath);
            var pendingArea = pending?["areaId"]?.GetValue<string>() ?? string.Empty;
            var waypoint = pending?["waypointTag"]?.GetValue<string>() ?? string.Empty;
            if (pendingArea.Equals(profile.AreaId, StringComparison.OrdinalIgnoreCase) && waypoint.Length > 0)
            {
                var resolved = ResolveWaypoint(profile, waypoint, "pending-transition");
                if (resolved is not null)
                {
                    File.Delete(pendingPath);
                    return resolved;
                }
            }
        }

        if (OS.GetEnvironment("OPENDAO_CONTINUE") == "1") return null;
        return ResolveDefaultStart(profile);
    }

    private WorldArrival? ResolveDefaultStart(WorldProfile profile)
    {
        var document = store.Read(profile.AreaFile);
        if (document?["waypoints"] is not JsonArray waypoints) return null;
        var tags = waypoints.OfType<JsonObject>()
            .Select(record => record["tag"]?.GetValue<string>()?.Trim() ?? string.Empty)
            .Where(tag => tag.Length > 0)
            .ToArray();
        var startTag = tags.FirstOrDefault(tag => tag.Equals("start",
                           StringComparison.OrdinalIgnoreCase)) ??
                       tags.FirstOrDefault(tag => tag.EndsWith("wp_start",
                           StringComparison.OrdinalIgnoreCase)) ??
                       tags.FirstOrDefault(tag => tag.EndsWith("_start",
                           StringComparison.OrdinalIgnoreCase)) ??
                       tags.FirstOrDefault();
        return startTag is null
            ? null
            : ResolveWaypoint(profile, startTag, "new-game-authored-start");
    }

    private WorldArrival? ResolveWaypoint(WorldProfile profile, string tag, string source)
    {
        var document = store.Read(profile.AreaFile);
        if (document?["waypoints"] is not JsonArray waypoints) return null;
        var record = waypoints.OfType<JsonObject>().FirstOrDefault(value =>
            (value["tag"]?.GetValue<string>() ?? string.Empty).Equals(tag,
                StringComparison.OrdinalIgnoreCase));
        if (record is null) return null;
        var position = ReadVector(record["position"] as JsonArray);
        var values = record["rotation"] as JsonArray;
        var rotation = values is { Count: >= 4 }
            ? new Quaternion(Number(values[0]), Number(values[1]), Number(values[2]), Number(values[3]))
            : Quaternion.Identity;
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var basis = conversion * new Basis(rotation) * conversion.Inverse();
        var transform = new Transform3D(basis,
            new Vector3(position.X, position.Z, -position.Y));
        GD.Print($"OPENDAO_AUTHORED_ARRIVAL source={source} waypoint={tag} position={transform.Origin}");
        return new WorldArrival(tag, transform, source);
    }

    private static string ResolvePath(string variable, string fallback)
    {
        var configured = OS.GetEnvironment(variable);
        var path = configured.Length > 0 ? configured : fallback;
        return path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;
    }

    private static Vector3 ReadVector(JsonArray? value) => value is { Count: >= 3 }
        ? new Vector3(Number(value[0]), Number(value[1]), Number(value[2]))
        : Vector3.Zero;

    private static float Number(JsonNode? value) => value?.GetValue<float>() ?? 0;
}
