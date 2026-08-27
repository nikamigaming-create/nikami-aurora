// Matthew W, 2026-08-12

using Godot;

namespace OpenDAO.Launcher;

internal sealed class LauncherAssetLoader(
    TextureRect launcherArtwork,
    LauncherTextureButton installationButton,
    LauncherTextureButton supportButton,
    LauncherTextureButton creditsButton,
    LauncherTextureButton settingsButton,
    LauncherTextureButton aboutButton,
    LauncherTextureButton documentationButton,
    LauncherTextureButton playButton,
    LauncherIconButton musicButton,
    LauncherIconButton minimizeButton,
    LauncherIconButton closeButton,
    AudioStreamPlayer clickAudio,
    WindowsBackgroundMusic backgroundMusic)
{
    public string Load(string gameRoot, bool musicMuted, bool editorPreview = false)
    {
        var launcherDirectory = Path.Combine(gameRoot, "data", "launcher");
        var warnings = new List<string>();

        try
        {
            launcherArtwork.Texture = LoadExternalTexture(
                Path.Combine(launcherDirectory, "background.bmp"));
        }
        catch (Exception exception)
        {
            launcherArtwork.Texture = null;
            warnings.Add(exception.Message);
        }

        LoadTextureSet(installationButton, launcherDirectory, "bu1", warnings);
        LoadTextureSet(playButton, launcherDirectory, "bu7", warnings);
        LoadTextureSet(supportButton, launcherDirectory, "bu2", warnings);
        LoadTextureSet(creditsButton, launcherDirectory, "bu3", warnings);
        LoadTextureSet(settingsButton, launcherDirectory, "bu4", warnings);
        LoadTextureSet(aboutButton, launcherDirectory, "bu5", warnings);
        LoadTextureSet(documentationButton, launcherDirectory, "bu6", warnings);

        LoadIconTextures(minimizeButton, launcherDirectory, "min", null, warnings);
        LoadIconTextures(closeButton, launcherDirectory, "close", null, warnings);
        LoadIconTextures(musicButton, launcherDirectory, "mute", "muted", warnings);

        if (editorPreview)
        {
            return warnings.Count == 0
                ? string.Empty
                : "Some launcher artwork is unavailable; using the built-in fallback.";
        }

        var clickPath = Path.Combine(launcherDirectory, "click.wav");
        if (File.Exists(clickPath))
        {
            clickAudio.Stream = AudioStreamWav.LoadFromFile(clickPath);
        }

        var musicPath = Path.Combine(launcherDirectory, "background.wma");
        if (backgroundMusic.TryPlayLooping(musicPath, musicMuted, out var musicError))
        {
            musicButton.SetButtonEnabled(true);
            musicButton.SetAlternateState(musicMuted);
            musicButton.TooltipText = musicMuted ? "Play music" : "Mute music";
        }
        else
        {
            musicButton.SetButtonEnabled(false);
            warnings.Add(musicError);
        }

        if (warnings.Count == 0)
        {
            return string.Empty;
        }

        GD.PushWarning("Launcher artwork fallback is active: " + string.Join(" | ", warnings));
        return "Some launcher artwork is unavailable; using the built-in fallback.";
    }

    private static void LoadTextureSet(
        LauncherTextureButton button,
        string launcherDirectory,
        string prefix,
        ICollection<string> warnings)
    {
        if (!button.LoadTextureSet(launcherDirectory, prefix, out var error))
        {
            warnings.Add(error);
        }
    }

    private static void LoadIconTextures(
        LauncherIconButton button,
        string launcherDirectory,
        string prefix,
        string? alternatePrefix,
        ICollection<string> warnings)
    {
        if (!button.LoadTexturePair(launcherDirectory, prefix, alternatePrefix, out var error))
        {
            warnings.Add(error);
        }
    }

    private static Texture2D LoadExternalTexture(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("Launcher artwork was not found.", path);
        }

        var image = Image.LoadFromFile(path);
        if (image.IsEmpty())
        {
            throw new InvalidDataException($"Launcher artwork could not be decoded: {path}");
        }

        return ImageTexture.CreateFromImage(image);
    }
}
