// Matthew W, 2026-08-12

using Godot;
using Nikami.Aurora.GodotRuntime.Launcher;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

internal sealed class VideoOptions
{
    private static readonly (int Width, int Height, string Ratio)[] Resolutions =
    [
        (800, 600, "4:3"), (1024, 768, "4:3"), (1152, 864, "4:3"), (1280, 1024, "5:4"),
        (1280, 960, "4:3"), (1440, 1080, "4:3"), (1600, 1200, "4:3"), (1920, 1440, "4:3"),
        (1280, 800, "16:10"), (1600, 1024, ""), (1680, 1050, "16:10"), (1920, 1200, "16:10"),
        (1176, 664, ""), (1280, 768, ""), (1280, 720, "16:9"), (1360, 768, ""),
        (1600, 900, "16:9"), (1920, 1080, "16:9"), (2560, 1440, "16:9"), (1366, 768, "")
    ];

    private static readonly (DisplayMode Mode, string Caption)[] Modes =
    [
        (DisplayMode.ExclusiveFullscreen, "Full Screen"),
        (DisplayMode.BorderlessFullscreen, "Windowed Full Screen"),
        (DisplayMode.Windowed, "Windowed")
    ];

    private readonly Control root;
    private readonly OptionButton resolution;
    private readonly OptionButton display;

    public VideoOptions(Control owner)
    {
        root = owner.GetNode<Control>("Options");
        resolution = root.GetNode<OptionButton>("ResolutionPicker");
        display = root.GetNode<OptionButton>("DisplayPicker");

        foreach (var (width, height, ratio) in Resolutions)
        {
            resolution.AddItem(ratio.Length > 0
                ? $"{width} x {height} ({ratio})"
                : $"{width} x {height}");
        }

        foreach (var (_, caption) in Modes)
        {
            display.AddItem(caption);
        }
    }

    public Control Root => root;

    public bool Visible
    {
        get => root.Visible;
        set => root.Visible = value;
    }

    public Button Ok => root.GetNode<Button>("OkButton");

    public Button Cancel => root.GetNode<Button>("CancelButton");

    public void Style(FontFile? font)
    {
        foreach (var picker in new OptionButton[] { resolution, display })
        {
            StylePopup(picker, font);
            picker.Flat = true;
            picker.Alignment = HorizontalAlignment.Center;
            picker.AddThemeStyleboxOverride("normal", new StyleBoxEmpty());
            picker.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
            picker.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
            picker.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            picker.AddThemeColorOverride("font_color", new Color(0.808f, 0.722f, 0.455f));
            picker.AddThemeColorOverride("font_hover_color", new Color(1, 1, 0.8f));
            picker.AddThemeFontSizeOverride("font_size", 12);
            picker.AddThemeIconOverride("arrow", new ImageTexture());
        }

        foreach (var button in new[] { Ok, Cancel })
        {
            button.Flat = true;
            var inset = new StyleBoxEmpty();
            inset.ContentMarginLeft = 15.4f;
            inset.ContentMarginRight = 16.7f;
            button.AddThemeStyleboxOverride("normal", inset);
            button.AddThemeStyleboxOverride("hover", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("pressed", new StyleBoxEmpty());
            button.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
            button.AddThemeColorOverride("font_color", new Color(0.808f, 0.722f, 0.455f));
            button.AddThemeColorOverride("font_hover_color", new Color(1, 1, 0.8f));
            button.AddThemeFontSizeOverride("font_size", 18);
            button.Alignment = button == Ok ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }

        if (font is null)
        {
            return;
        }

        foreach (var node in Labels())
        {
            node.AddThemeFontOverride("font", font);
        }
    }

    private void StylePopup(OptionButton picker, FontFile? font)
    {
        var arrow = root.GetNodeOrNull<TextureRect>(
            picker.Name == "ResolutionPicker" ? "ResolutionArrow" : "DisplayArrow");
        var popup = picker.GetPopup();

        var panel = new StyleBoxFlat
        {
            BgColor = new Color(0.055f, 0.045f, 0.035f, 0.97f),
            BorderColor = new Color(0.55f, 0.47f, 0.28f, 0.85f),
            ContentMarginLeft = 10.0f,
            ContentMarginRight = 10.0f,
            ContentMarginTop = 6.0f,
            ContentMarginBottom = 6.0f
        };
        panel.SetBorderWidthAll(1);

        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", new StyleBoxFlat
        {
            BgColor = new Color(0.24f, 0.2f, 0.11f, 0.9f)
        });
        popup.AddThemeColorOverride("font_color", new Color(0.808f, 0.722f, 0.455f));
        popup.AddThemeColorOverride("font_hover_color", new Color(1, 1, 0.8f));
        popup.AddThemeConstantOverride("v_separation", 4);
        popup.AddThemeFontSizeOverride("font_size", 12);
        if (font is not null)
        {
            popup.AddThemeFontOverride("font", font);
        }

        if (arrow is null)
        {
            return;
        }

        popup.AboutToPopup += () => arrow.FlipV = true;
        popup.PopupHide += () => arrow.FlipV = false;
    }

    public void ShowCurrent()
    {
        var current = DisplayPreferences.Load();

        var match = Array.FindIndex(
            Resolutions, r => r.Width == current.Width && r.Height == current.Height);
        resolution.Selected = match >= 0 ? match : Resolutions.Length - 3;

        var mode = Array.FindIndex(Modes, m => m.Mode == current.Mode);
        display.Selected = mode >= 0 ? mode : 1;
    }

    public DisplayPreferences Chosen()
    {
        var picked = Resolutions[Math.Clamp(resolution.Selected, 0, Resolutions.Length - 1)];
        var mode = Modes[Math.Clamp(display.Selected, 0, Modes.Length - 1)].Mode;
        return new DisplayPreferences(mode, picked.Width, picked.Height);
    }

    private IEnumerable<Control> Labels()
    {
        yield return resolution;
        yield return display;
        yield return Ok;
        yield return Cancel;
        foreach (var name in new[] { "Title", "TabLabel", "HeaderLabel", "ResolutionLabel", "DisplayLabel" })
        {
            if (root.GetNodeOrNull<Label>(name) is { } label)
            {
                yield return label;
            }
        }
    }
}
