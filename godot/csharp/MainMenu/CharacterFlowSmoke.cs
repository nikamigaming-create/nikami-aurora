using System.Text.Json;
using Godot;

namespace OpenDAO.MainMenu;

internal static class CharacterFlowSmoke
{
    public const string EnvironmentVariable = "OPENDAO_CHARACTER_FLOW_SMOKE";

    public static bool Requested => OS.GetEnvironment(EnvironmentVariable) == "1";

    public static void Run(SceneTree tree)
    {
        var requiredOverrides = new[]
        {
            "OPENDAO_CHARACTER_PROFILE", "OPENDAO_SELECTED_PROFILE", "OPENDAO_PLAYER_SESSION",
            "DAOPEN_STORY_STATE", "OPENDAO_PENDING_TRANSITION",
        };
        var missing = requiredOverrides.Where(name => OS.GetEnvironment(name).Length == 0).ToArray();
        if (missing.Length > 0)
        {
            Finish(tree, false, "missing-isolated-paths:" + string.Join(',', missing));
            return;
        }

        try
        {
            var service = new NewGameService();
            var combinations = new (string Origin, string Race, string Class)[]
            {
                ("human-noble", "human", "warrior"), ("human-noble", "human", "rogue"),
                ("city-elf", "elf", "warrior"), ("city-elf", "elf", "rogue"),
                ("dalish-elf", "elf", "warrior"), ("dalish-elf", "elf", "rogue"),
                ("dwarf-commoner", "dwarf", "warrior"), ("dwarf-commoner", "dwarf", "rogue"),
                ("dwarf-noble", "dwarf", "warrior"), ("dwarf-noble", "dwarf", "rogue"),
                ("circle-mage", "human", "mage"), ("circle-mage", "elf", "mage")
            };
            for (var index = 0; index < combinations.Length; index++)
            {
                SeedStaleState(RuntimeSavePaths.PlayerSession);
                SeedStaleState(RuntimeSavePaths.StoryState);
                SeedStaleState(RuntimeSavePaths.PendingTransition);
                var choice = combinations[index];
                var character = CharacterProfile.Create(
                    $"Route Warden {index + 1}", choice.Origin, choice.Race,
                    index % 2 == 0 ? "female" : "male", choice.Class, "preset-3");
                if (!service.Prepare(character, out var prepareError))
                {
                    Finish(tree, false, $"prepare:{choice.Origin}:{choice.Class}:{prepareError}");
                    return;
                }
                if (!CharacterProfileStore.TryLoad(out var loaded, out var loadError) || loaded != character)
                {
                    Finish(tree, false, $"character-roundtrip:{choice.Origin}:{choice.Class}:{loadError}");
                    return;
                }
                using var selected = JsonDocument.Parse(File.ReadAllText(RuntimeSavePaths.SelectedProfile));
                var source = selected.RootElement.TryGetProperty("source_key", out var sourceKey)
                    ? sourceKey.GetString() ?? string.Empty
                    : string.Empty;
                var origin = CharacterProfileRules.OriginFor(character.Origin);
                using var pending = JsonDocument.Parse(File.ReadAllText(RuntimeSavePaths.PendingTransition));
                var pendingArea = pending.RootElement.GetProperty("areaId").GetString() ?? string.Empty;
                var pendingWaypoint = pending.RootElement.GetProperty("waypointTag").GetString() ?? string.Empty;
                var passed = origin is not null &&
                    source.Contains(origin.AreaId, StringComparison.OrdinalIgnoreCase) &&
                    pendingArea.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase) &&
                    pendingWaypoint.Equals(origin.Waypoint, StringComparison.OrdinalIgnoreCase) &&
                    !File.Exists(RuntimeSavePaths.PlayerSession) &&
                    !File.Exists(RuntimeSavePaths.StoryState) &&
                    OS.GetEnvironment("OPENDAO_CONTINUE") == "0";
                if (!passed)
                {
                    Finish(tree, false, $"fresh-state-contract:{choice.Origin}:{choice.Class}");
                    return;
                }
            }
            GD.Print($"OPENDAO_ORIGIN_ROUTE_MATRIX status=pass combinations={combinations.Length} origins=6");
            Finish(tree, true, string.Empty);
        }
        catch (Exception exception)
        {
            Finish(tree, false, exception.Message);
        }
    }

    private static void SeedStaleState(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }
        File.WriteAllText(path, "stale");
    }

    private static void Finish(SceneTree tree, bool passed, string error)
    {
        GD.Print($"OPENDAO_CHARACTER_FLOW_SMOKE status={(passed ? "pass" : "fail")}" +
                 (error.Length > 0 ? " error=" + error : string.Empty));
        tree.Quit(passed ? 0 : 57);
    }
}
