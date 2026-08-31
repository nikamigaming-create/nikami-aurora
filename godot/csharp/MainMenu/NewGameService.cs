using Godot;
using System.Text.Json;
using Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

internal static class RuntimeSavePaths
{
    public static string SelectedProfile => Resolve("OPENDAO_SELECTED_PROFILE", AreaCatalog.SelectedProfilePath);
    public static string PlayerSession => Resolve("OPENDAO_PLAYER_SESSION", "user://opendao-player-session.json");
    public static string StoryState => Resolve("DAOPEN_STORY_STATE", "user://opendao-story-state.json");
    public static string PendingTransition => Resolve("OPENDAO_PENDING_TRANSITION", AreaCatalog.PendingTransitionPath);

    private static string Resolve(string environmentVariable, string fallback)
    {
        var configured = OS.GetEnvironment(environmentVariable);
        var path = configured.Length > 0 ? configured : fallback;
        return path.StartsWith("user://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;
    }
}

internal sealed class NewGameService
{
    public bool Prepare(CharacterProfile character, out string error)
    {
        error = string.Empty;
        if (!CharacterProfileRules.Validate(character, out error))
        {
            return false;
        }

        var origin = CharacterProfileRules.OriginFor(character.Origin);
        if (origin is null)
        {
            error = "The selected origin has no authored campaign start";
            return false;
        }

        var catalog = new AreaCatalog();
        if (!catalog.Load())
        {
            error = catalog.Error;
            return false;
        }

        var startArea = catalog.Areas.FirstOrDefault(area => area.Ready &&
            area.Id.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase) &&
            area.Archive.EndsWith(origin.Archive, StringComparison.OrdinalIgnoreCase));
        if (startArea is null)
        {
            error = $"The authored {origin.Label} start is not installed ({origin.AreaId}).";
            return false;
        }

        if (!CharacterProfileStore.Save(character, out error) ||
            !AreaCatalog.WriteProfileForLoading(startArea, RuntimeSavePaths.SelectedProfile, out error))
        {
            return false;
        }

        try
        {
            DeleteIfPresent(RuntimeSavePaths.PlayerSession);
            DeleteIfPresent(RuntimeSavePaths.StoryState);
            DeleteIfPresent(RuntimeSavePaths.PendingTransition);
            WriteOriginArrival(origin, RuntimeSavePaths.PendingTransition);
        }
        catch (Exception exception)
        {
            error = "Could not clear the previous campaign: " + exception.Message;
            return false;
        }

        OS.SetEnvironment("OPENDAO_CONTINUE", "0");
        OS.SetEnvironment("OPENDAO_AREA_BROWSER", "");
        OS.SetEnvironment("OPENDAO_IGNORE_PENDING_TRANSITION", "");
        OS.SetEnvironment("OPENDAO_MAP_EXPLORE", "");
        return true;
    }

    private static void WriteOriginArrival(CharacterOrigin origin, string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
        var pending = new
        {
            schema = "opendao-pending-transition-v1",
            areaId = origin.AreaId,
            waypointTag = origin.Waypoint,
            provenance = new
            {
                source = "dao-background-defaults",
                origin = origin.Id,
                archive = origin.Archive
            }
        };
        File.WriteAllText(path,
            JsonSerializer.Serialize(pending, RuntimeJsonOptions.Indented) +
            System.Environment.NewLine);
    }

    public static bool CanContinue(out string reason)
    {
        if (!File.Exists(RuntimeSavePaths.PlayerSession))
        {
            reason = "No saved game yet";
            return false;
        }
        if (!File.Exists(RuntimeSavePaths.SelectedProfile))
        {
            reason = "The saved area's local profile is missing";
            return false;
        }

        reason = CharacterProfileStore.TryLoad(out var character, out _)
            ? $"Continue as {character.Name}"
            : "Continue legacy Warden save";
        return true;
    }

    public static void ConfigureContinue()
    {
        OS.SetEnvironment("OPENDAO_CONTINUE", "1");
        OS.SetEnvironment("OPENDAO_AREA_BROWSER", "");
        OS.SetEnvironment("OPENDAO_IGNORE_PENDING_TRANSITION", "");
        OS.SetEnvironment("OPENDAO_MAP_EXPLORE", "");
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
