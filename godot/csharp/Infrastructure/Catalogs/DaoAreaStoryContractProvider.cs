using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed record DaoPlaceableStoryContract(
    int Ordinal,
    string Template,
    string Tag,
    string EventScript,
    string TransitionArea,
    string TransitionWaypoint);

/// <summary>
/// Loads the disposable story slice corresponding exactly to an installed ARE.
/// The slice filename is the first 16 hex digits of its source key's SHA-256.
/// </summary>
public sealed class DaoAreaStoryContractProvider(
    IJsonStore store,
    IRuntimeEnvironment environment)
{
    public IReadOnlyDictionary<int, DaoPlaceableStoryContract> Load(WorldProfile profile)
    {
        var cacheRoot = environment.Get("NIKAMI_AURORA_DAO_CACHE_ROOT")?.Trim() ?? string.Empty;
        if (cacheRoot.Length == 0 || profile.SourceKey.Length == 0) return Empty();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.SourceKey)))
            .ToLowerInvariant()[..16];
        var document = store.Read(Path.Combine(cacheRoot, "story-areas", hash + ".json"));
        if (document?["schemaVersion"]?.GetValue<int>() != 1 ||
            document["provenance"]?["sourceKey"]?.GetValue<string>() is not { } sourceKey ||
            !sourceKey.Equals(profile.SourceKey, StringComparison.Ordinal) ||
            document["areaLinks"] is not JsonArray links) return Empty();
        var area = links.OfType<JsonObject>().SingleOrDefault(link =>
            Text(link, "key").Equals(profile.SourceKey, StringComparison.Ordinal));
        if (area?["placeablePlacements"] is not JsonArray placements) return Empty();

        var result = new Dictionary<int, DaoPlaceableStoryContract>();
        foreach (var placement in placements.OfType<JsonObject>())
        {
            var ordinal = placement["ordinal"]?.GetValue<int>() ?? -1;
            if (ordinal < 0 || result.ContainsKey(ordinal)) return Empty();
            result.Add(ordinal, new DaoPlaceableStoryContract(
                ordinal,
                Text(placement, "templateResRef"),
                Text(placement, "tag"),
                Text(placement, "eventScript"),
                NormalizeTransition(Text(placement, "transitionArea")),
                NormalizeTransition(Text(placement, "transitionWaypoint"))));
        }
        return result;
    }

    private static string Text(JsonObject source, string key) =>
        source[key]?.GetValue<string>()?.Trim().ToLowerInvariant() ?? string.Empty;
    private static string NormalizeTransition(string value) =>
        value.Equals("none", StringComparison.OrdinalIgnoreCase) ? string.Empty : value;
    private static IReadOnlyDictionary<int, DaoPlaceableStoryContract> Empty() =>
        new Dictionary<int, DaoPlaceableStoryContract>();
}
