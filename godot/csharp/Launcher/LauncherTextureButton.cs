// Matthew W, 2026-08-12

using Godot;

namespace Nikami.Aurora.GodotRuntime.Launcher;

[Tool]
public partial class LauncherTextureButton : Button
{
    private ColorRect fallback = null!;
    private TextureRect artwork = null!;
    private Label caption = null!;

    private Texture2D? normalTexture;
    private Texture2D? hoverTexture;
    private Texture2D? pressedTexture;
    private Texture2D? disabledTexture;
    private bool pointerInside;
    private bool pointerDown;

    public override void _Ready()
    {
        fallback = GetNode<ColorRect>("Fallback");
        artwork = GetNode<TextureRect>("Artwork");
        caption = GetNode<Label>("Caption");

        MouseEntered += HandleMouseEntered;
        MouseExited += HandleMouseExited;
        ButtonDown += HandleButtonDown;
        ButtonUp += HandleButtonUp;
        FocusEntered += ApplyVisualState;
        FocusExited += ApplyVisualState;

        ApplyVisualState();
    }

    public void SetDisplayText(string text)
    {
        caption.Text = text;
    }

    public void SetButtonEnabled(bool enabled)
    {
        Disabled = !enabled;
        MouseDefaultCursorShape = enabled
            ? CursorShape.PointingHand
            : CursorShape.Arrow;
        pointerDown = false;
        ApplyVisualState();
    }

    public bool LoadTextureSet(string launcherDirectory, string prefix, out string error)
    {
        try
        {
            var loadedNormal = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_up.bmp"));
            var loadedHover = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_hi.bmp"));
            var loadedPressed = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_dn.bmp"));
            var loadedDisabled = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_ds.bmp"));

            normalTexture = loadedNormal;
            hoverTexture = loadedHover;
            pressedTexture = loadedPressed;
            disabledTexture = loadedDisabled;
            ApplyVisualState();

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            ClearTextureSet();
            error = exception.Message;
            return false;
        }
    }

    public void ClearTextureSet()
    {
        normalTexture = null;
        hoverTexture = null;
        pressedTexture = null;
        disabledTexture = null;

        if (IsNodeReady())
        {
            ApplyVisualState();
        }
    }

    private static Texture2D LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Launcher texture was not found.", path);
        }

        var image = Image.LoadFromFile(path);
        if (image.IsEmpty())
        {
            throw new InvalidDataException($"Launcher texture could not be decoded: {path}");
        }

        return ImageTexture.CreateFromImage(image);
    }

    private void HandleMouseEntered()
    {
        pointerInside = true;
        ApplyVisualState();
    }

    private void HandleMouseExited()
    {
        pointerInside = false;
        pointerDown = false;
        ApplyVisualState();
    }

    private void HandleButtonDown()
    {
        pointerDown = true;
        ApplyVisualState();
    }

    private void HandleButtonUp()
    {
        pointerDown = false;
        ApplyVisualState();
    }

    private void ApplyVisualState()
    {
        if (!IsNodeReady())
        {
            return;
        }

        var texture = Disabled
            ? disabledTexture
            : pointerDown
                ? pressedTexture
                : pointerInside || HasFocus()
                    ? hoverTexture
                    : normalTexture;

        artwork.Texture = texture;
        artwork.Visible = texture is not null;
        fallback.Visible = texture is null;

        fallback.Color = Disabled
            ? new Color("121719c8")
            : pointerDown
                ? new Color("839071b8")
                : pointerInside || HasFocus()
                    ? new Color("596653a8")
                    : new Color("0b1011b8");

        caption.Modulate = Disabled
            ? new Color("788080")
            : pointerInside || pointerDown || HasFocus()
                ? Colors.White
                : new Color("cddaf8");
        caption.Position = pointerDown ? Vector2.Down : Vector2.Zero;
    }
}
