using System.Text.Json.Nodes;
using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.Sessions;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Persistence;

public sealed class PlayerSessionRepository(IJsonStore store, IRuntimeEnvironment environment)
    : IPlayerSessionRepository
{
    private const string DefaultPath = "user://opendao-player-session.json";
    private string Path => environment.Get("OPENDAO_PLAYER_SESSION") is { Length: > 0 } value
        ? value : DefaultPath;

    public PlayerSession? Load()
    {
        var document = store.Read(Path);
        if (document is null) return null;
        var schema = document["schema"]?.GetValue<string>();
        if (schema is not ("opendao-player-session-v1" or "opendao-player-session-v2") ||
            document["position"] is not JsonArray { Count: 3 } position) return null;
        var experience = schema == "opendao-player-session-v2"
            ? document["experience"]?.GetValue<int>() ?? -1
            : 0;
        if (experience < 0) return null;
        return new(document["sourceKey"]?.GetValue<string>() ?? string.Empty,
            document["areaId"]?.GetValue<string>() ?? string.Empty,
            new Vector3(Number(position[0]), Number(position[1]), Number(position[2])),
            document["yaw"]?.GetValue<float>() ?? 0, document["pitch"]?.GetValue<float>() ?? 0,
            document["savedAtUnix"]?.GetValue<long>() ?? 0, experience);
    }

    public bool Save(PlayerSession session) => store.Write(Path, new JsonObject
    {
        ["schema"] = "opendao-player-session-v2",
        ["sourceKey"] = session.SourceKey,
        ["areaId"] = session.AreaId,
        ["position"] = new JsonArray(session.Position.X, session.Position.Y, session.Position.Z),
        ["yaw"] = session.Yaw,
        ["pitch"] = session.Pitch,
        ["savedAtUnix"] = session.SavedAtUnix,
        ["experience"] = session.Experience,
    });

    private static float Number(JsonNode? node) => node?.GetValue<float>() ?? 0;
}
