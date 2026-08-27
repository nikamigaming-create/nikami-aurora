// Matthew W, 2026-08-12

using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace OpenDAO.Launcher;

public enum DisplayMode
{
    Windowed,
    BorderlessFullscreen,
    ExclusiveFullscreen
}

public sealed record DisplayPreferences(DisplayMode Mode, int Width, int Height, int Screen = 0)
{
    private const string SettingsPath = "user://display-settings.json";
    private const int MinimumWidth = 480;
    private const int MinimumHeight = 270;

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static DisplayPreferences Default => new(DisplayMode.BorderlessFullscreen, 1280, 720, 0);

    public static DisplayPreferences Load()
    {
        var path = ProjectSettings.GlobalizePath(SettingsPath);
        if (!File.Exists(path))
        {
            return Default;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<DisplayPreferences>(
                File.ReadAllText(path), Format);
            return stored is null ? Default : stored.Clamped();
        }
        catch (Exception exception)
        {
            GD.PushWarning("Display settings could not be read: " + exception.Message);
            return Default;
        }
    }

    public void Save()
    {
        var path = ProjectSettings.GlobalizePath(SettingsPath);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(Clamped(), Format));
        }
        catch (Exception exception)
        {
            GD.PushWarning("Display settings could not be written: " + exception.Message);
        }
    }

    public void Apply(Window window)
    {
        var wanted = Clamped();
        // Establish the target before changing mode, then reassert it after the
        // transition. On Windows a fullscreen/windowed mode change may recreate
        // or re-home the native window on the primary display.
        wanted.ApplyScreen(window, false);
        window.Borderless = false;
        window.Unresizable = false;

        switch (wanted.Mode)
        {
            case DisplayMode.ExclusiveFullscreen:
                window.Mode = Window.ModeEnum.ExclusiveFullscreen;
                wanted.ApplyScreen(window, false);
                return;
            case DisplayMode.BorderlessFullscreen:
                window.Mode = Window.ModeEnum.Fullscreen;
                wanted.ApplyScreen(window, false);
                return;
            default:
                window.Mode = Window.ModeEnum.Windowed;
                window.Size = new Vector2I(wanted.Width, wanted.Height);
                wanted.ApplyScreen(window);
                return;
        }
    }

    public void ApplyScreen(Window window, bool centre = true)
    {
        var wanted = Clamped();
        window.CurrentScreen = wanted.Screen;
        if (centre && window.Mode == Window.ModeEnum.Windowed)
        {
            Centre(window);
        }
    }

    private static void Centre(Window window)
    {
        var screen = DisplayServer.ScreenGetUsableRect(window.CurrentScreen);
        window.Position = screen.Position + ((screen.Size - window.Size) / 2);
    }

    private DisplayPreferences Clamped() => this with
    {
        Width = Math.Max(MinimumWidth, Width),
        Height = Math.Max(MinimumHeight, Height),
        Screen = Math.Clamp(Screen, 0, Math.Max(0, DisplayServer.GetScreenCount() - 1))
    };
}
