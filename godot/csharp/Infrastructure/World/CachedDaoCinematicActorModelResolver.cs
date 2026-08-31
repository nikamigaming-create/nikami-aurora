using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

/// <summary>
/// Resolves actor-specific retail appearance exports without coupling authored
/// manifests to a machine-local cache path. Animation-bank models remain owned
/// by the authored manifests so an appearance override cannot discard clips.
/// </summary>
public sealed class CachedDaoCinematicActorModelResolver : ICinematicActorModelResolver
{
    private static readonly IReadOnlyDictionary<string, string> AuthoredActors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bec110cr_shianni"] =
                "actor-reexports/shianni/lak100d/actors/bec110cr_shianni.glb"
        };

    public string ResolveAppearanceModelPath(string actorResref)
    {
        if (!AuthoredActors.TryGetValue(actorResref, out var relativePath))
            return string.Empty;
        var path = DaoRuntimePaths.Cache(relativePath);
        if (!File.Exists(path))
        {
            GD.PushWarning($"OPENDAO_CINEMATIC_ACTOR_MODEL status=missing actor={actorResref} path={path}");
            return string.Empty;
        }
        GD.Print($"OPENDAO_CINEMATIC_ACTOR_MODEL status=ready actor={actorResref} " +
                 "source=retail-utc-export");
        return path;
    }
}
