// Matthew W, 2026-08-12

using Godot;
using System.Text.Json;

namespace OpenDAO.Launcher;

[Tool]
public partial class Launcher : Control
{
    private const string SettingsPath = "user://runtime-settings.json";

    private TextureRect launcherArtwork = null!;
    private LauncherTextureButton installationButton = null!;
    private LauncherTextureButton supportButton = null!;
    private LauncherTextureButton creditsButton = null!;
    private LauncherTextureButton settingsButton = null!;
    private LauncherTextureButton aboutButton = null!;
    private LauncherTextureButton documentationButton = null!;
    private LauncherTextureButton playButton = null!;
    private LauncherIconButton musicButton = null!;
    private LauncherIconButton minimizeButton = null!;
    private LauncherIconButton closeButton = null!;
    private Label status = null!;
    private AudioStreamPlayer clickAudio = null!;
    private FileDialog folderDialog = null!;
    private IntroSequenceController introSequence = null!;
    private OpenDAO.MainMenu.MainMenu startMenu = null!;
    private readonly WindowsBackgroundMusic backgroundMusic = new();
    private string configuredGameRoot = string.Empty;
    private InstallationScan? installationScan;
    private bool musicMuted;

    public override void _Ready()
    {
        // Every visual process, including diagnostics that return before the
        // title flow, must honor the same persisted physical display.
        var wantsWorldAutomation = RuntimeAutomation.WantsWorld();
        var wantsCharacterFlowCapture =
            System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1";
        if (!Engine.IsEditorHint() &&
            !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            var display = DisplayPreferences.Load();
            ApplyDisplayPreferences(display,
                screenOnly: wantsWorldAutomation || wantsCharacterFlowCapture);
        }

        if (!Engine.IsEditorHint() && OpenDAO.MainMenu.GfxHudSmoke.Requested)
        {
            OpenDAO.MainMenu.GfxHudSmoke.Run(GetTree());
            return;
        }
        if (!Engine.IsEditorHint() && OpenDAO.MainMenu.CharacterFlowSmoke.Requested)
        {
            OpenDAO.MainMenu.CharacterFlowSmoke.Run(GetTree());
            return;
        }
        if (!Engine.IsEditorHint() &&
            System.Environment.GetEnvironmentVariable("OPENDAO_LAUNCHER_CATALOG_SMOKE") == "1")
        {
            RunLauncherCatalogSmoke();
            return;
        }
        if (!Engine.IsEditorHint() &&
            System.Environment.GetEnvironmentVariable("OPENDAO_PORTABLE_PROFILE_SMOKE") == "1")
        {
            RunPortableProfileSmoke();
            return;
        }

        // The self-contained runtime owns its title flow. Do this before
        // binding the optional retail-launcher nodes and intro sequence.
        if (!Engine.IsEditorHint() &&
            System.Environment.GetEnvironmentVariable("OPENDAO_SMOKE_EXIT") != "1" &&
            !wantsWorldAutomation &&
            System.Environment.GetEnvironmentVariable("OPENDAO_RETAIL_LAUNCHER") != "1")
        {
            // No Dragon Age executable is selected or run. Installed content is read
            // only after New Game, Continue, or Explore Areas chooses a local route.
            startMenu = GetNode<OpenDAO.MainMenu.MainMenu>("StartMenu");
            if (!startMenu.BuildFromConfiguredInstallation([], out var titleArtworkError))
            {
                GD.PushWarning(titleArtworkError);
            }
            startMenu.Visible = true;
            if (System.Environment.GetEnvironmentVariable("OPENDAO_AREA_BROWSER") == "1")
            {
                System.Environment.SetEnvironmentVariable("OPENDAO_AREA_BROWSER", string.Empty);
                startMenu.OpenAreaBrowser();
            }
            else
            {
                startMenu.OpenStartMenu();
            }
            if (System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1")
                startMenu.BeginCharacterCreationAcceptance();
            return;
        }

        BindNodes();
        if (System.Environment.GetEnvironmentVariable("OPENDAO_SMOKE_EXIT") == "1")
        {
            GD.Print("OPENDAO_RUNTIME_SMOKE_PASS");
            GetTree().Quit();
            return;
        }
        if (Engine.IsEditorHint())
        {
            ApplyCaptions();
            var previewRoot = GameInstallation.ResolveConfiguredRoot();
            if (previewRoot.Length > 0)
            {
                BuildAssetLoader().Load(previewRoot, musicMuted, editorPreview: true);
            }

            return;
        }

        // Renderer and runtime validation must enter the world without waiting
        // for the interactive launcher. Keep this list synchronized with the
        // environment switches consumed by the C# world composition root.
        if (wantsWorldAutomation)
        {
            Callable.From(LoadAutomationWorld).CallDeferred();
            return;
        }

        ConnectSignals();
        ApplyCaptions();
        LoadInitialConfiguration();

        if (GameSession.IsGameProcess() && installationScan is not null)
        {
            BeginGame();
        }
    }

    private void LoadAutomationWorld()
    {
        if (System.Environment.GetEnvironmentVariable("OPENDAO_FRESH_GAME_ACCEPTANCE") == "1" &&
            !PrepareFreshGameAcceptance())
        {
            return;
        }
        GetTree().ChangeSceneToFile("res://dao_world.tscn");
    }

    private void RunLauncherCatalogSmoke()
    {
        // This runs the actual C# launcher catalog path without opening or
        // interacting with the browser. It deliberately never writes user://
        // state, so it is safe for unattended acceptance.
        var catalog = new OpenDAO.MainMenu.AreaCatalog();
        if (!catalog.Load())
        {
            GD.PushError("OPENDAO_LAUNCHER_CATALOG_SMOKE_FAIL catalog=" + catalog.Error);
            GetTree().Quit(55);
            return;
        }

        var travelMarkers = catalog.WorldMapMarkers.Where(marker => marker.CanTravel).ToArray();
        var invalid = travelMarkers.Where(marker =>
            marker.Arrivals.Count == 0 ||
            marker.Arrivals.Any(arrival => arrival.Destination.Key.Length == 0 ||
                !ProfileMatchesCatalogKey(arrival.Destination))).ToArray();
        var missingArtwork = catalog.WorldMapMarkers
            .Select(marker => marker.Map)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(map => !File.Exists(OpenDAO.MainMenu.AreaCatalog.ResolveWorldMapArtworkPath(map)))
            .ToArray();
        var passed = catalog.WorldMapMarkers.Count > 0 && travelMarkers.Length > 0 &&
            invalid.Length == 0 && missingArtwork.Length == 0;
        GD.Print("OPENDAO_LAUNCHER_CATALOG_SMOKE status=" + (passed ? "pass" : "fail") +
                 " markers=" + catalog.WorldMapMarkers.Count +
                 " travel=" + travelMarkers.Length +
                 " invalid_arrival_keys=" + invalid.Length +
                 " missing_art=" + missingArtwork.Length +
                 " catalog=" + OpenDAO.MainMenu.AreaCatalog.ResolvePath());
        GetTree().Quit(passed ? 0 : 55);
    }

    private void RunPortableProfileSmoke()
    {
        // Exercise the real selected-profile writer against a deliberately
        // relocated catalog/profile. This is headless-only acceptance code:
        // the caller supplies an isolated user-data directory, so no player
        // state is touched.
        var catalog = new OpenDAO.MainMenu.AreaCatalog();
        var sourceArea = catalog.Load()
            ? catalog.Areas.FirstOrDefault(area => area.Ready && File.Exists(area.ProfilePath))
            : null;
        if (sourceArea is null)
        {
            GD.PushError("OPENDAO_PORTABLE_PROFILE_SMOKE_FAIL catalog=" + catalog.Error);
            GetTree().Quit(56);
            return;
        }

        try
        {
            var fakeRoot = Path.Combine(ProjectSettings.GlobalizePath("user://"),
                "portable-profile-smoke", "dao-world");
            var sourceText = File.ReadAllText(sourceArea.ProfilePath);
            using var sourceDocument = JsonDocument.Parse(sourceText);
            var fileFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "area_file", "actor_file", "terrain_materials", "talktable_file",
            };
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in new[]
                     {
                         "area_file", "area_root", "actor_file", "actor_root",
                         "terrain_materials", "talktable_file",
                     })
            {
                if (sourceDocument.RootElement.TryGetProperty(field, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    values[field] = value.GetString()!;
                }
            }
            if (values.Count == 0)
            {
                throw new InvalidOperationException("source profile had no cache paths");
            }

            const string marker = "/dao-world/";
            foreach (var (field, value) in values)
            {
                var normalized = value.Replace('\\', '/');
                var markerIndex = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (markerIndex < 0)
                {
                    throw new InvalidOperationException("profile field is not DAO-world rooted: " + field);
                }
                var relative = normalized[(markerIndex + marker.Length)..]
                    .Replace('/', Path.DirectorySeparatorChar);
                var target = Path.Combine(fakeRoot, relative);
                if (fileFields.Contains(field))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.WriteAllText(target, "portable-profile-smoke");
                }
                else
                {
                    Directory.CreateDirectory(target);
                }
                var stale = Path.Combine("Z:", "opendao-portable-missing", "dao-world", relative)
                    .Replace('\\', '/');
                sourceText = sourceText.Replace(value, stale, StringComparison.Ordinal);
            }

            var profileRelative = sourceArea.ProfilePath.Replace('\\', '/');
            var profileMarker = profileRelative.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (profileMarker < 0)
            {
                throw new InvalidOperationException("catalog profile path is not DAO-world rooted");
            }
            var fakeProfile = Path.Combine(fakeRoot,
                profileRelative[(profileMarker + marker.Length)..].Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fakeProfile)!);
            File.WriteAllText(fakeProfile, sourceText);
            var relocated = sourceArea with { ProfilePath = fakeProfile, CatalogRoot = fakeRoot };
            var selected = Path.Combine(ProjectSettings.GlobalizePath("user://"),
                "portable-profile-smoke", "selected-area-profile.json");
            var written = OpenDAO.MainMenu.AreaCatalog.WriteProfileForLoading(relocated, selected, out var error);
            using var selectedDocument = written ? JsonDocument.Parse(File.ReadAllText(selected)) : null;
            var rebased = written && values.All(pair =>
                selectedDocument!.RootElement.TryGetProperty(pair.Key, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString()!.Replace('\\', '/').StartsWith(fakeRoot.Replace('\\', '/'),
                    StringComparison.OrdinalIgnoreCase));
            GD.Print("OPENDAO_PORTABLE_PROFILE_SMOKE status=" + (rebased ? "pass" : "fail") +
                     " fields=" + values.Count + " error=" + error);
            GetTree().Quit(rebased ? 0 : 56);
        }
        catch (Exception exception)
        {
            GD.PushError("OPENDAO_PORTABLE_PROFILE_SMOKE_FAIL " + exception.Message);
            GetTree().Quit(56);
        }
    }

    private static bool ProfileMatchesCatalogKey(OpenDAO.MainMenu.CatalogArea area)
    {
        if (area.ProfilePath.Length == 0 || !File.Exists(area.ProfilePath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(area.ProfilePath));
            return document.RootElement.TryGetProperty("source_key", out var sourceKey) &&
                sourceKey.ValueKind == JsonValueKind.String &&
                string.Equals(sourceKey.GetString(), area.Key, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool PrepareFreshGameAcceptance()
    {
        // This route exists solely for unattended acceptance. Every mutable
        // path must be explicit so it cannot erase or reuse a player's save.
        var selectedPath = System.Environment.GetEnvironmentVariable("OPENDAO_SELECTED_PROFILE") ?? string.Empty;
        var sessionPath = System.Environment.GetEnvironmentVariable("OPENDAO_PLAYER_SESSION") ?? string.Empty;
        var storyPath = System.Environment.GetEnvironmentVariable("DAOPEN_STORY_STATE") ?? string.Empty;
        var characterPath = System.Environment.GetEnvironmentVariable("OPENDAO_CHARACTER_PROFILE") ?? string.Empty;
        if (selectedPath.Length == 0 || sessionPath.Length == 0 || storyPath.Length == 0 ||
            characterPath.Length == 0)
        {
            GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE requires isolated character/selected/session/story paths");
            GetTree().Quit(54);
            return false;
        }

        var catalog = new OpenDAO.MainMenu.AreaCatalog();
        if (!catalog.Load())
        {
            GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE catalog: " + catalog.Error);
            GetTree().Quit(54);
            return false;
        }
        var golden = catalog.Areas.FirstOrDefault(area =>
            area.Id.Equals("arl100ar_redcliffe_village", StringComparison.OrdinalIgnoreCase) && area.Ready);
        if (golden is null || golden.ProfilePath.Length == 0 || !File.Exists(golden.ProfilePath))
        {
            GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE golden-area profile unavailable");
            GetTree().Quit(54);
            return false;
        }

        try
        {
            if (!OpenDAO.MainMenu.AreaCatalog.WriteProfileForLoading(golden, selectedPath,
                    out var profileError))
            {
                GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE profile: " + profileError);
                GetTree().Quit(54);
                return false;
            }
            var character = OpenDAO.MainMenu.CharacterProfile.Create(
                "Acceptance Warden", "human-noble", "human", "female", "warrior", "preset-1");
            if (!OpenDAO.MainMenu.CharacterProfileStore.Save(character, out var characterError))
            {
                GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE character: " + characterError);
                GetTree().Quit(54);
                return false;
            }
            // The caller owns these explicit temporary files. Removing them
            // gives the run a true New Game state without touching user://.
            if (File.Exists(sessionPath)) File.Delete(sessionPath);
            if (File.Exists(storyPath)) File.Delete(storyPath);
            System.Environment.SetEnvironmentVariable("OPENDAO_CONTINUE", "0");
            GD.Print("OPENDAO_FRESH_GAME_PREP area=" + golden.Id + " profile=" + selectedPath);
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("OPENDAO_FRESH_GAME_ACCEPTANCE preparation: " + exception.Message);
            GetTree().Quit(54);
            return false;
        }
    }

    private void ApplyCaptions()
    {
        installationButton.SetDisplayText("Select the game executable");
        installationButton.SetButtonEnabled(true);
        supportButton.SetDisplayText("Support");
        creditsButton.SetDisplayText("Credits");
        settingsButton.SetDisplayText("Select DAOrigins.exe");
        aboutButton.SetDisplayText("About");
        documentationButton.SetDisplayText("Documentation");
        playButton.SetDisplayText("Play");
        playButton.SetButtonEnabled(false);
        musicButton.SetFallbackGlyph("♪");
        musicButton.SetButtonEnabled(false);
        minimizeButton.SetFallbackGlyph("−");
        minimizeButton.SetButtonEnabled(true);
        closeButton.SetFallbackGlyph("×");
        closeButton.SetButtonEnabled(true);
    }

    public override void _ExitTree()
    {
        backgroundMusic.Dispose();
    }

    private void BindNodes()
    {
        launcherArtwork = GetNode<TextureRect>("LauncherArtwork");
        installationButton = GetNode<LauncherTextureButton>("InstallationButton");
        supportButton = GetNode<LauncherTextureButton>("SupportButton");
        creditsButton = GetNode<LauncherTextureButton>("CreditsButton");
        settingsButton = GetNode<LauncherTextureButton>("SettingsButton");
        aboutButton = GetNode<LauncherTextureButton>("AboutButton");
        documentationButton = GetNode<LauncherTextureButton>("DocumentationButton");
        playButton = GetNode<LauncherTextureButton>("PlayButton");
        musicButton = GetNode<LauncherIconButton>("MusicButton");
        minimizeButton = GetNode<LauncherIconButton>("MinimizeButton");
        closeButton = GetNode<LauncherIconButton>("CloseButton");
        status = GetNode<Label>("Status");
        clickAudio = GetNode<AudioStreamPlayer>("ClickAudio");
        folderDialog = GetNode<FileDialog>("FolderDialog");
        introSequence = GetNode<IntroSequenceController>("IntroSequence");
        startMenu = GetNode<OpenDAO.MainMenu.MainMenu>("StartMenu");
    }

    private void ConnectSignals()
    {
        installationButton.Pressed += ShowInstallationPicker;
        supportButton.Pressed += () => ShowPlaceholder("Support");
        creditsButton.Pressed += () => ShowPlaceholder("Credits");
        aboutButton.Pressed += () => ShowPlaceholder("About");
        settingsButton.Pressed += ShowInstallationPicker;
        documentationButton.Pressed += () => ShowPlaceholder("Documentation");
        playButton.Pressed += StartGame;
        musicButton.Pressed += ToggleMusic;
        minimizeButton.Pressed += MinimizeLauncher;
        closeButton.Pressed += CloseLauncher;
        folderDialog.FileSelected += ConfigureInstallation;
        introSequence.MoviesStarting += EnterGameDisplayMode;
        introSequence.SequenceCompleted += ShowStartMenu;

        folderDialog.FileMode = FileDialog.FileModeEnum.OpenFile;
        folderDialog.Access = FileDialog.AccessEnum.Filesystem;
        folderDialog.UseNativeDialog = true;
        folderDialog.Filters = ["*.exe ; Dragon Age executable"];
    }

    private LauncherAssetLoader BuildAssetLoader() => new(
        launcherArtwork,
        installationButton,
        supportButton,
        creditsButton,
        settingsButton,
        aboutButton,
        documentationButton,
        playButton,
        musicButton,
        minimizeButton,
        closeButton,
        clickAudio,
        backgroundMusic);

    private void LoadInitialConfiguration()
    {
        var environmentRoot = System.Environment.GetEnvironmentVariable(
            GameInstallation.RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentRoot))
        {
            if (!TryUseInstallation(environmentRoot, persist: false, out var environmentError))
            {
                SetUnconfigured(
                    $"The {GameInstallation.RootEnvironmentVariable} location is invalid: " +
                    environmentError);
            }

            return;
        }

        var store = new RuntimeSettingsStore(ProjectSettings.GlobalizePath(SettingsPath));
        if (!store.Exists)
        {
            var detected = GameInstallation.DefaultRoot();
            if (detected.Length > 0 && TryUseInstallation(detected, persist: true, out _))
            {
                return;
            }

            SetUnconfigured("Select DAOrigins.exe from your Dragon Age: Origins installation.");
            return;
        }

        try
        {
            var settings = store.Load();
            musicMuted = settings.MusicMuted;
            if (!TryUseInstallation(settings.GameRoot, persist: false, out var settingsError))
            {
                SetUnconfigured(
                    "The previously selected game location is no longer valid: " + settingsError);
            }
        }
        catch (Exception exception)
        {
            SetUnconfigured($"Could not load the saved game location: {exception.Message}");
        }
    }

    private void ShowInstallationPicker()
    {
        PlayClickSound();
        folderDialog.CurrentDir = ResolveDialogDirectory();
        folderDialog.PopupCenteredRatio(0.75f);
    }

    private string ResolveDialogDirectory()
    {
        if (Directory.Exists(configuredGameRoot))
        {
            var installed = Path.Combine(configuredGameRoot, "bin_ship");
            if (Directory.Exists(installed))
            {
                return installed;
            }
        }

        return GameInstallation.DefaultExecutableDirectories.FirstOrDefault(Directory.Exists)
            ?? GameInstallation.DefaultExecutableDirectories[0];
    }

    private void ConfigureInstallation(string selectedExecutable)
    {
        try
        {
            var resolvedRoot = InstallationLocator.RootFromExecutable(selectedExecutable);
            if (TryUseInstallation(resolvedRoot, persist: true, out var error))
            {
                return;
            }

            SetUnconfigured(error);
        }
        catch (Exception exception)
        {
            SetUnconfigured(exception.Message);
        }
    }

    private bool TryUseInstallation(string selectedDirectory, bool persist, out string error)
    {
        try
        {
            var normalizedRoot = GameInstallation.NormalizeRoot(selectedDirectory);
            GameInstallation.ValidateRoot(normalizedRoot);

            installationScan = InstallationLocator.Scan(normalizedRoot);
            configuredGameRoot = normalizedRoot;
            installationButton.SetDisplayText("Change game location");
            playButton.SetButtonEnabled(true);

            status.Text = BuildAssetLoader().Load(normalizedRoot, musicMuted);
            UpdateMusicTooltip();

            if (persist)
            {
                WriteSettings(normalizedRoot);
            }

            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private void WriteSettings(string normalizedRoot)
    {
        var store = new RuntimeSettingsStore(ProjectSettings.GlobalizePath(SettingsPath));
        store.Save(new RuntimeSettings(normalizedRoot, musicMuted));
    }

    private void StartGame()
    {
        PlayClickSound();
        try
        {
            GameInstallation.ValidateRoot(configuredGameRoot);
        }
        catch (Exception exception)
        {
            SetUnconfigured(exception.Message);
            return;
        }

        if (!GameSession.Launch(out var error))
        {
            SetUnconfigured("The game could not be started: " + error);
            return;
        }

        backgroundMusic.Dispose();
        GetTree().Quit();
    }

    private void BeginGame()
    {
        backgroundMusic.Dispose();
        HideLauncherControls();
        introSequence.Configure(IntroSequencePlan.Build(installationScan!));
        introSequence.StartSequence();
    }

    private void HideLauncherControls()
    {
        CanvasItem[] launcherControls =
        [
            launcherArtwork,
            installationButton,
            supportButton,
            creditsButton,
            settingsButton,
            aboutButton,
            documentationButton,
            playButton,
            musicButton,
            minimizeButton,
            closeButton,
            status
        ];
        foreach (var control in launcherControls)
        {
            control.Visible = false;
        }
    }

    private void EnterGameDisplayMode()
    {
        ApplyDisplayPreferences(DisplayPreferences.Load(), screenOnly: false);
    }

    private void ApplyDisplayPreferences(DisplayPreferences display, bool screenOnly)
    {
        var window = GetWindow();
        if (screenOnly)
        {
            display.ApplyScreen(window);
        }
        else
        {
            display.Apply(window);
        }

        GD.Print($"OPENDAO_DISPLAY status=applied requested={display.Screen} " +
                 $"actual={window.CurrentScreen} policy={(screenOnly ? "screen-only" : "full")}");

        // Windows may complete the native fullscreen/windowed transition after
        // the Godot call returns. Settle on both following frames, and do this
        // for every transition (startup, intro, and title menu), not just _Ready.
        SettleDisplayOnFollowingFrame(display, remainingFrames: 2);
    }

    private void SettleDisplayOnFollowingFrame(DisplayPreferences display, int remainingFrames)
    {
        Callable.From(() =>
        {
            if (!IsInsideTree())
            {
                return;
            }

            var window = GetWindow();
            display.ApplyScreen(window);
            GD.Print($"OPENDAO_DISPLAY status=settled requested={display.Screen} " +
                     $"actual={window.CurrentScreen} position={window.Position} size={window.Size} " +
                     $"remaining={remainingFrames - 1}");

            if (remainingFrames > 1)
            {
                SettleDisplayOnFollowingFrame(display, remainingFrames - 1);
            }
        }).CallDeferred();
    }

    private void ShowStartMenu()
    {
        EnterGameDisplayMode();
        try
        {
            if (!startMenu.Build(installationScan!.GuiArchive, []))
            {
                return;
            }
        }
        catch (Exception exception)
        {
            GD.PushError("OpenDAO title menu failed: " + exception.Message);
            return;
        }

        startMenu.Visible = true;
        startMenu.OpenStartMenu();
    }

    private void ShowPlaceholder(string feature)
    {
        PlayClickSound();
        status.Text = $"{feature} is a placeholder and is not implemented yet.";
    }

    private void ToggleMusic()
    {
        PlayClickSound();

        var nextMuted = !musicMuted;
        if (!backgroundMusic.SetMuted(nextMuted, out var error))
        {
            status.Text = $"Could not change the music state: {error}";
            return;
        }

        musicMuted = nextMuted;
        musicButton.SetAlternateState(musicMuted);
        UpdateMusicTooltip();

        if (!string.IsNullOrWhiteSpace(configuredGameRoot))
        {
            WriteSettings(configuredGameRoot);
        }
    }

    private void UpdateMusicTooltip()
    {
        musicButton.TooltipText = musicMuted ? "Play music" : "Mute music";
    }

    private void MinimizeLauncher()
    {
        PlayClickSound();
        GetWindow().Mode = Window.ModeEnum.Minimized;
    }

    private void CloseLauncher()
    {
        backgroundMusic.Dispose();
        GetTree().Quit();
    }

    private void PlayClickSound()
    {
        if (clickAudio.Stream is not null)
        {
            clickAudio.Play();
        }
    }

    private void SetUnconfigured(string message)
    {
        configuredGameRoot = string.Empty;
        installationScan = null;
        installationButton.SetDisplayText("Select the game executable");
        playButton.SetButtonEnabled(false);
        musicButton.SetButtonEnabled(false);
        backgroundMusic.Dispose();
        status.Text = message;
    }
}
