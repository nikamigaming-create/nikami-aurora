// Matthew W, 2026-08-12

using Godot;

namespace Nikami.Aurora.GodotRuntime.Launcher;

[Tool]
public partial class LauncherIconButton : Button
{
    private TextureRect artwork = null!;
    private Label fallbackGlyph = null!;

    private Texture2D? normalTexture;
    private Texture2D? hoverTexture;
    private Texture2D? alternateNormalTexture;
    private Texture2D? alternateHoverTexture;
    private bool alternateState;
    private bool pointerInside;
    private bool pointerDown;

    public override void _Ready()
    {
        artwork = GetNode<TextureRect>("Artwork");
        fallbackGlyph = GetNode<Label>("FallbackGlyph");

        MouseEntered += HandleMouseEntered;
        MouseExited += HandleMouseExited;
        ButtonDown += HandleButtonDown;
        ButtonUp += HandleButtonUp;
        FocusEntered += ApplyVisualState;
        FocusExited += ApplyVisualState;

        ApplyVisualState();
    }

    public void SetFallbackGlyph(string glyph)
    {
        fallbackGlyph.Text = glyph;
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

    public void SetAlternateState(bool enabled)
    {
        alternateState = enabled;
        ApplyVisualState();
    }

    public bool LoadTexturePair(
        string launcherDirectory,
        string prefix,
        string? alternatePrefix,
        out string error)
    {
        try
        {
            var loadedNormal = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_up.bmp"));
            var loadedHover = LoadTexture(Path.Combine(launcherDirectory, $"{prefix}_hi.bmp"));
            Texture2D? loadedAlternateNormal = null;
            Texture2D? loadedAlternateHover = null;

            if (!string.IsNullOrWhiteSpace(alternatePrefix))
            {
                loadedAlternateNormal = LoadTexture(
                    Path.Combine(launcherDirectory, $"{alternatePrefix}_up.bmp"));
                loadedAlternateHover = LoadTexture(
                    Path.Combine(launcherDirectory, $"{alternatePrefix}_hi.bmp"));
            }

            normalTexture = loadedNormal;
            hoverTexture = loadedHover;
            alternateNormalTexture = loadedAlternateNormal;
            alternateHoverTexture = loadedAlternateHover;
            ApplyVisualState();

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            ClearTexturePair();
            error = exception.Message;
            return false;
        }
    }

    public void ClearTexturePair()
    {
        normalTexture = null;
        hoverTexture = null;
        alternateNormalTexture = null;
        alternateHoverTexture = null;

        if (IsNodeReady())
        {
            ApplyVisualState();
        }
    }

    private static Texture2D LoadTexture(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Launcher icon texture was not found.", path);
        }

        var image = Image.LoadFromFile(path);
        if (image.IsEmpty())
        {
            throw new InvalidDataException($"Launcher icon could not be decoded: {path}");
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

        var highlighted = pointerInside || pointerDown || HasFocus();
        var texture = alternateState
            ? highlighted
                ? alternateHoverTexture ?? alternateNormalTexture
                : alternateNormalTexture
            : highlighted
                ? hoverTexture ?? normalTexture
                : normalTexture;

        artwork.Texture = texture;
        artwork.Visible = texture is not null;
        fallbackGlyph.Visible = texture is null;
        fallbackGlyph.Modulate = Disabled
            ? new Color("586060")
            : highlighted
                ? Colors.White
                : new Color("a9b49a");
        fallbackGlyph.Position = pointerDown ? Vector2.Down : Vector2.Zero;
    }
}
