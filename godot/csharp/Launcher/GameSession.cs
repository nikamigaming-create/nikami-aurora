// Matthew W, 2026-08-12

using Godot;

namespace Nikami.Aurora.GodotRuntime.Launcher;

internal static class GameSession
{
    private const string Flag = "game";

    public static bool IsGameProcess() => OS.GetCmdlineUserArgs().Contains(Flag);

    public static bool Launch(out string error)
    {
        error = string.Empty;
        try
        {
            var arguments = new List<string>();
            if (OS.HasFeature("editor"))
            {
                arguments.Add("--path");
                arguments.Add(ProjectSettings.GlobalizePath("res://").TrimEnd('/', '\\'));
            }

            arguments.Add("++");
            arguments.Add(Flag);

            var pid = OS.CreateProcess(OS.GetExecutablePath(), arguments.ToArray());
            if (pid <= 0)
            {
                error = "the game process could not be started";
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}
