// Matthew W, 2026-08-12

using System.Text.Json;
using System.Text.Json.Nodes;
using Godot;
using OpenDAO.Infrastructure.Configuration;

namespace OpenDAO.MainMenu;

internal sealed record CatalogArea(
    string Id,
    string Key,
    string Layout,
    string Archive,
    bool Ready,
    string ProfilePath,
    int AnimatedActors,
    int Instances,
    string CatalogRoot)
{
    public string Searchable => $"{Id} {Key} {Layout} {Archive}".ToLowerInvariant();
}

/// <summary>
/// A pin authored in a DAO MAP resource.  Its 2-D position is deliberately
/// kept separate from the destination waypoint's 3-D arrival position.
/// </summary>
internal sealed record WorldMapArrival(
    CatalogArea Destination,
    string Waypoint,
    string Resolution)
{
    public bool IsUsable => Destination.Ready && Waypoint.Length > 0;
}

internal sealed record WorldMapMarker(
    string Id,
    string Map,
    string Tag,
    string AreaId,
    int Status,
    Vector2 Position,
    IReadOnlyList<WorldMapArrival> Arrivals)
{
    // The launcher is an exploration selector, not a story-state gate. A pin
    // becomes clickable only for an imported profile plus an authored arrival.
    // When the MAP source names an area with several wmw_* entrances, the UI
    // presents those real arrivals instead of guessing a spawn point.
    public bool CanTravel => Arrivals.Any(arrival => arrival.IsUsable);
    public bool RequiresArrivalChoice => Arrivals.Count > 1;
}

internal sealed class AreaCatalog
{
    public const string SelectedProfilePath = "user://selected-area-profile.json";

    private const string CatalogEnvironmentVariable = "OPENDAO_CATALOG";
    public const string PendingTransitionPath = "user://opendao-pending-transition.json";

    public IReadOnlyList<CatalogArea> Areas { get; private set; } = [];

    public IReadOnlyList<WorldMapMarker> WorldMapMarkers { get; private set; } = [];

    public string Error { get; private set; } = string.Empty;

    public static string ResolvePath()
    {
        var configured = OS.GetEnvironment(CatalogEnvironmentVariable);
        if (configured.Length > 0)
        {
            return GlobalizeIfGodotPath(configured);
        }

        // A portable package deliberately keeps imported DAO data beside the
        // executable rather than baking a user-owned, multi-gigabyte source
        // cache into the PCK.  The native launcher does not need to inject an
        // environment variable for the normal sidecar layout.
        var executable = OS.GetExecutablePath();
        var executableDirectory = Path.GetDirectoryName(executable);
        if (!string.IsNullOrWhiteSpace(executableDirectory))
        {
            foreach (var relative in new[]
            {
                Path.Combine("OpenDAOData", "cache", "dao-world", "runtime-catalog.json"),
                // Support an early sidecar layout during migration, but
                // prefer the cache root above so companion/interior assets
                // retain their established relative paths.
                Path.Combine("OpenDAOData", "dao-world", "runtime-catalog.json"),
            })
            {
                var sidecar = Path.Combine(executableDirectory, relative);
                if (File.Exists(sidecar))
                {
                    return sidecar;
                }
            }
        }

        return DaoRuntimePaths.Cache("runtime-catalog.json");
    }

    /// <summary>
    /// Returns the map-art sidecar that belongs to the selected imported
    /// catalog. This keeps map imagery and pin data coherent and works from a
    /// PCK where res:// cannot refer to arbitrary external PNG files.
    /// </summary>
    public static string ResolveWorldMapArtworkPath(string map) =>
        Path.Combine(Path.GetDirectoryName(ResolvePath()) ?? string.Empty,
            "worldmaps", map + ".png");

    private static string GlobalizeIfGodotPath(string path) =>
        path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    private static string SiblingCatalogPath(string catalogPath, string fileName)
    {
        // OPENDAO_CATALOG selects one coherent imported-data bundle.  The
        // world-map and transition documents must come from that same bundle,
        // not silently from the development cache beside the executable.
        var directory = Path.GetDirectoryName(catalogPath);
        return string.IsNullOrWhiteSpace(directory)
            ? fileName
            : Path.Combine(directory, fileName);
    }

    private static string ResolveBundlePath(string value, string catalogRoot)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var direct = GlobalizeIfGodotPath(value);
        if (File.Exists(direct) || Directory.Exists(direct))
        {
            return direct;
        }

        // Imported profiles and reports historically persisted absolute paths.
        // Re-root only the DAO-world suffix when the sidecar was moved as a
        // bundle; unrelated source-game paths remain untouched.
        var normalized = value.Replace('\\', '/');
        const string marker = "/dao-world/";
        var markerIndex = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0 && !string.IsNullOrWhiteSpace(catalogRoot))
        {
            var relative = normalized[(markerIndex + marker.Length)..]
                .Replace('/', Path.DirectorySeparatorChar);
            var rebased = Path.Combine(catalogRoot, relative);
            if (File.Exists(rebased) || Directory.Exists(rebased))
            {
                return rebased;
            }
        }

        var local = Path.Combine(catalogRoot, value);
        return File.Exists(local) || Directory.Exists(local) ? local : direct;
    }

    public bool Load()
    {
        Areas = [];
        WorldMapMarkers = [];
        Error = string.Empty;

        var path = ResolvePath();
        if (!File.Exists(path))
        {
            Error = "Catalog missing: run scripts/Import-DAO-All.ps1";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("areas", out var areas) ||
                areas.ValueKind != JsonValueKind.Array)
            {
                Error = "Catalog has no areas: " + path;
                return false;
            }

            var catalogRoot = Path.GetDirectoryName(path) ?? string.Empty;
            var parsed = new List<CatalogArea>();
            foreach (var entry in areas.EnumerateArray())
            {
                var id = Text(entry, "id");
                if (id.Length == 0)
                {
                    continue;
                }

                var validation = entry.TryGetProperty("validation", out var found) &&
                    found.ValueKind == JsonValueKind.Object
                        ? found
                        : default;

                parsed.Add(new CatalogArea(
                    id,
                    Text(entry, "key"),
                    Text(entry, "layout"),
                    Text(entry, "archive"),
                    entry.TryGetProperty("ready", out var ready) &&
                        ready.ValueKind == JsonValueKind.True,
                    ResolveBundlePath(Text(entry, "profilePath"), catalogRoot),
                    Number(validation, "animatedActors"),
                    Number(validation, "instances"),
                    catalogRoot));
            }

            Areas = parsed;
            if (parsed.Count == 0)
            {
                Error = "Catalog has no areas: " + path;
                return false;
            }

            LoadWorldMap(parsed, path);
            return true;
        }
        catch (Exception exception)
        {
            Error = "Catalog could not be read: " + exception.Message;
            return false;
        }
    }

    private void LoadWorldMap(IReadOnlyList<CatalogArea> areas, string catalogPath)
    {
        var mapPath = SiblingCatalogPath(catalogPath, "world-map-catalog.json");
        var graphPath = SiblingCatalogPath(catalogPath, "transition-graph.json");
        if (!File.Exists(mapPath) || !File.Exists(graphPath))
        {
            Error = "Campaign MAP data missing: run scripts/Import-DAO-All.ps1";
            return;
        }

        try
        {
            // The broad runtime catalog can contain the same area id in more
            // than one archive/add-in.  MAP records carry areaKey precisely
            // to disambiguate them; only legacy key-less records can fall
            // back to a deterministic id choice.
            var areasByKey = new Dictionary<string, CatalogArea>(StringComparer.OrdinalIgnoreCase);
            var areasById = new Dictionary<string, CatalogArea>(StringComparer.OrdinalIgnoreCase);
            foreach (var area in areas)
            {
                if (area.Key.Length > 0)
                {
                    areasByKey[area.Key] = area;
                }
                if (!areasById.TryGetValue(area.Id, out var current) ||
                    (!current.Ready && area.Ready) ||
                    (current.Ready == area.Ready &&
                     string.Compare(area.Key, current.Key, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    areasById[area.Id] = area;
                }
            }
            using var mapDocument = JsonDocument.Parse(File.ReadAllText(mapPath));
            using var graphDocument = JsonDocument.Parse(File.ReadAllText(graphPath));
            var arrivals = new Dictionary<string, List<WorldMapArrival>>(
                StringComparer.OrdinalIgnoreCase);
            void AddArrival(JsonElement record, string resolution)
            {
                var id = Text(record, "id");
                var areaId = Text(record, "area");
                var areaKey = Text(record, "areaKey");
                var waypoint = Text(record, "waypoint");
                CatalogArea? area = null;
                if (areaKey.Length > 0)
                {
                    // Never route a qualified MAP record through a same-id
                    // variant. A missing exact key remains unavailable.
                    areasByKey.TryGetValue(areaKey, out area);
                }
                else
                {
                    areasById.TryGetValue(areaId, out area);
                }
                if (id.Length == 0 || waypoint.Length == 0 || area is null || !area.Ready ||
                    (record.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.False))
                {
                    return;
                }

                if (!arrivals.TryGetValue(id, out var values))
                {
                    values = [];
                    arrivals[id] = values;
                }
                if (!values.Any(existing => existing.Destination.Key.Equals(area.Key,
                        StringComparison.OrdinalIgnoreCase) && existing.Waypoint.Equals(waypoint,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    values.Add(new WorldMapArrival(area, waypoint, resolution));
                }
            }
            if (graphDocument.RootElement.TryGetProperty("worldMapDestinations", out var records) &&
                records.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in records.EnumerateArray())
                {
                    AddArrival(record, "exact-authored-arrival");
                }
            }
            if (graphDocument.RootElement.TryGetProperty("worldMapArrivalChoices", out var choiceRecords) &&
                choiceRecords.ValueKind == JsonValueKind.Array)
            {
                foreach (var record in choiceRecords.EnumerateArray())
                {
                    AddArrival(record, "source-authored-arrival-choice");
                }
            }

            var markers = new List<WorldMapMarker>();
            if (mapDocument.RootElement.TryGetProperty("maps", out var maps) &&
                maps.ValueKind == JsonValueKind.Array)
            {
                foreach (var map in maps.EnumerateArray())
                {
                    var mapTag = Text(map, "tag");
                    if (!map.TryGetProperty("locations", out var locations) ||
                        locations.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var location in locations.EnumerateArray())
                    {
                        var id = Text(location, "id");
                        var position = Position(location, "position");
                        if (id.Length == 0 || position is null)
                        {
                            continue;
                        }

                        var markerArrivals = arrivals.TryGetValue(id, out var found)
                            ? found.ToArray()
                            : [];
                        markers.Add(new WorldMapMarker(
                            id, mapTag, Text(location, "tag"), Text(location, "area"),
                            Number(location, "status"), position.Value,
                            markerArrivals));
                    }
                }
            }

            WorldMapMarkers = markers;
            if (markers.Count == 0)
            {
                Error = "Campaign MAP data has no pins";
            }
        }
        catch (Exception exception)
        {
            WorldMapMarkers = [];
            Error = "Campaign MAP data could not be read: " + exception.Message;
        }
    }

    public static bool SelectForLoading(CatalogArea area, out string error) =>
        WriteProfileForLoading(area, ProjectSettings.GlobalizePath(SelectedProfilePath), out error);

    /// <summary>
    /// Materializes a selected profile with sidecar-relative paths repaired.
    /// This is intentionally the only path that writes a selected profile, so
    /// the launcher and isolated acceptance route cannot disagree about where
    /// imported GLBs, terrain manifests, and talktables live.
    /// </summary>
    public static bool WriteProfileForLoading(CatalogArea area, string destinationPath, out string error)
    {
        error = string.Empty;
        if (!area.Ready)
        {
            error = area.Id + " has not been imported";
            return false;
        }

        if (area.ProfilePath.Length == 0 || !File.Exists(area.ProfilePath))
        {
            error = "Profile missing: " + area.ProfilePath;
            return false;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(area.ProfilePath));
            if (node is not JsonObject profile)
            {
                error = "Profile is not a JSON object: " + area.ProfilePath;
                return false;
            }
            RebaseProfilePaths(profile, area.CatalogRoot);
            var parent = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                Directory.CreateDirectory(parent);
            }
            File.WriteAllText(destinationPath,
                profile.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + System.Environment.NewLine);
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not write the local game profile: " + exception.Message;
            return false;
        }
    }

    public static bool SelectForTravel(WorldMapMarker marker, WorldMapArrival arrival, out string error)
    {
        error = string.Empty;
        if (!marker.CanTravel || !arrival.IsUsable || !marker.Arrivals.Contains(arrival))
        {
            error = marker.AreaId + " has no validated imported MAP arrival";
            return false;
        }

        if (!SelectForLoading(arrival.Destination, out error))
        {
            return false;
        }

        try
        {
            var pending = new
            {
                schema = "opendao-pending-transition-v1",
                areaId = arrival.Destination.Id.ToLowerInvariant(),
                areaKey = arrival.Destination.Key.ToLowerInvariant(),
                waypointTag = arrival.Waypoint.ToLowerInvariant(),
                provenance = new
                {
                    source = "launcher-authored-world-map-exploration",
                    map = marker.Map,
                    locationTag = marker.Tag,
                    locationId = marker.Id,
                    destinationAreaKey = arrival.Destination.Key,
                    arrivalResolution = arrival.Resolution,
                },
            };
            File.WriteAllText(ProjectSettings.GlobalizePath(PendingTransitionPath),
                JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return true;
        }
        catch (Exception exception)
        {
            error = "Could not prepare MAP arrival: " + exception.Message;
            return false;
        }
    }

    private static void RebaseProfilePaths(JsonNode? node, string catalogRoot)
    {
        if (node is JsonObject objectNode)
        {
            foreach (var property in objectNode.ToList())
            {
                if (property.Value is JsonValue value && value.TryGetValue<string>(out var text) &&
                    !string.IsNullOrWhiteSpace(text))
                {
                    objectNode[property.Key] = ResolveBundlePath(text, catalogRoot);
                    continue;
                }
                RebaseProfilePaths(property.Value, catalogRoot);
            }
            return;
        }

        if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                RebaseProfilePaths(value, catalogRoot);
            }
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Number(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static Vector2? Position(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = value.EnumerateArray().ToArray();
        return values.Length == 2 && values[0].TryGetSingle(out var x) && values[1].TryGetSingle(out var y)
            ? new Vector2(x, y)
            : null;
    }
}
