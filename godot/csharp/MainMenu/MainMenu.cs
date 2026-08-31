// Matthew W, 2026-08-12

using Godot;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.GodotRuntime.Launcher;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

[Tool]
public partial class MainMenu : Control
{
    private const float StageWidth = 1024.0f;
    private const float StageHeight = 768.0f;
    private const float TextInset = 30.0f;
    private const float PlateHomeX = 561.1f;

    private const float RollDistance = 156.7f;
    private const float RollSeconds = 13.0f / 60.0f;
    private const float ShineDelay = 99.0f / 60.0f;
    private const float ShineSeconds = 91.0f / 60.0f;
    private const float ShineCycle = 405.0f / 60.0f;
    private const string WorldScene = "res://dao_world.tscn";

    private Control scenery = null!;
    private Control frame = null!;
    private Control items = null!;
    private Button newGameButton = null!;
    private Button continueButton = null!;
    private Button loadLevelButton = null!;
    private Button quitButton = null!;
    private TextureRect newGamePlate = null!;
    private TextureRect continuePlate = null!;
    private TextureRect loadLevelPlate = null!;
    private TextureRect quitPlate = null!;
    private Button optionsButton = null!;
    private TextureRect optionsPlate = null!;
    private VideoOptions options = null!;
    private TextureRect wordmark = null!;
    private readonly Dictionary<TextureRect, Tween> rolling = [];
    private readonly Dictionary<TextureRect, Tween> shining = [];
    private readonly List<(Control Node, Vector2 Base, Vector2 Anchor)> anchored = [];
    private float plateShift;
    private PanelContainer browser = null!;
    private OptionButton mapTabs = null!;
    private WorldMapCanvas mapCanvas = null!;
    private OptionButton arrivalChoices = null!;
    private Label status = null!;
    private Button loadButton = null!;
    private Button backButton = null!;
    private CharacterCreationPanel characterCreation = null!;

    private readonly AreaCatalog catalog = new();
    private readonly NewGameService newGame = new();
    private readonly List<string> mapNames = [];
    private WorldMapMarker? selectedMarker;
    private readonly Dictionary<Control, Variant> stashed = [];
    private Texture2D? editorPlate;
    private FontFile? editorFont;

    public event Action<string>? CommandRequested;

    public override void _Ready()
    {
        scenery = GetNode<Control>("Scenery");
        frame = GetNode<Control>("Frame");
        items = frame.GetNode<Control>("Items");
        newGameButton = items.GetNode<Button>("NewGameButton");
        continueButton = items.GetNode<Button>("ContinueButton");
        loadLevelButton = items.GetNode<Button>("LoadLevelButton");
        quitButton = items.GetNode<Button>("QuitButton");
        newGamePlate = items.GetNode<TextureRect>("NewGamePlate");
        continuePlate = items.GetNode<TextureRect>("ContinuePlate");
        loadLevelPlate = items.GetNode<TextureRect>("LoadLevelPlate");
        quitPlate = items.GetNode<TextureRect>("QuitPlate");
        optionsButton = items.GetNode<Button>("OptionsButton");
        optionsPlate = items.GetNode<TextureRect>("OptionsPlate");
        options = new VideoOptions(this);
        wordmark = frame.GetNode<TextureRect>("Wordmark");

        browser = frame.GetNode<PanelContainer>("AreaBrowser");
        mapTabs = browser.GetNode<OptionButton>("Layout/MapTabs");
        mapCanvas = browser.GetNode<WorldMapCanvas>("Layout/MapCanvas");
        arrivalChoices = browser.GetNode<OptionButton>("Layout/ArrivalChoices");
        status = browser.GetNode<Label>("Layout/Status");
        loadButton = browser.GetNode<Button>("Layout/Actions/LoadButton");
        backButton = browser.GetNode<Button>("Layout/Actions/BackButton");
        characterCreation = GetNode<CharacterCreationPanel>("CharacterCreation");
        mapTabs.AddThemeColorOverride("font_color", new Color(0.82f, 0.78f, 0.68f));
        mapTabs.AddThemeColorOverride("font_hover_color", new Color(1.0f, 0.92f, 0.65f));

        if (Engine.IsEditorHint())
        {
            ShowEditorPreview();
            return;
        }

        newGameButton.Pressed += OpenCharacterCreation;
        continueButton.Pressed += ContinueGame;
        loadLevelButton.Pressed += OpenBrowser;
        quitButton.Pressed += Quit;
        optionsButton.Pressed += OpenOptions;
        options.Ok.Pressed += AcceptOptions;
        options.Cancel.Pressed += CloseOptions;
        backButton.Pressed += CloseBrowser;
        loadButton.Pressed += LoadSelected;
        mapTabs.ItemSelected += SelectMap;
        arrivalChoices.ItemSelected += OnArrivalChoiceSelected;
        mapCanvas.MarkerSelected += Describe;
        characterCreation.StartRequested += StartNewGame;
        characterCreation.Cancelled += CloseCharacterCreation;

        CaptureAnchors();
        Resized += LayoutStage;
        GetViewport().SizeChanged += LayoutStage;
        LayoutStage();
        RefreshContinueState();
    }

    private void CaptureAnchors()
    {
        anchored.Clear();
        foreach (var (node, anchor) in new (Control, Vector2)[]
                 {
                     (wordmark, new Vector2(1.0f, 0.5f)),
                     (frame.GetNode<Control>("Subtitle"), new Vector2(1.0f, 0.5f)),
                     (newGamePlate, new Vector2(1.0f, 0.5f)),
                     (continuePlate, new Vector2(1.0f, 0.5f)),
                     (loadLevelPlate, new Vector2(1.0f, 0.5f)),
                     (optionsPlate, new Vector2(1.0f, 0.5f)),
                     (quitPlate, new Vector2(1.0f, 0.5f)),
                     (newGameButton, new Vector2(1.0f, 0.5f)),
                     (continueButton, new Vector2(1.0f, 0.5f)),
                     (loadLevelButton, new Vector2(1.0f, 0.5f)),
                     (optionsButton, new Vector2(1.0f, 0.5f)),
                     (quitButton, new Vector2(1.0f, 0.5f)),
                     (frame.GetNode<Control>("BottomBar"), new Vector2(0.0f, 1.0f))
                 })
        {
            anchored.Add((node, node.Position, anchor));
        }
    }

    public bool Build(string archivePath, string[] disabledCommands)
    {
        var loader = new MainMenuAssetLoader(this);
        loader.Load(archivePath);
        StyleItems(loader, disabledCommands);
        characterCreation.Build(archivePath, loader.Font);
        return true;
    }

    public bool BuildFromConfiguredInstallation(string[] disabledCommands, out string error)
    {
        error = string.Empty;
        try
        {
            var gameRoot = GameInstallation.ResolveConfiguredRoot();
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                gameRoot = GameRootFromSelectedProfile();
            }

            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                error = "No configured Dragon Age installation was found for title artwork.";
                return false;
            }

            var archivePath = Path.Combine(gameRoot, "packages", "core", "data", "guiexport.erf");
            if (!File.Exists(archivePath))
            {
                error = "Title artwork archive was not found: " + archivePath;
                return false;
            }

            return Build(archivePath, disabledCommands);
        }
        catch (Exception exception)
        {
            error = "Title artwork could not be loaded: " + exception.Message;
            return false;
        }
    }

    private static string GameRootFromSelectedProfile()
    {
        var profilePath = ProjectSettings.GlobalizePath("user://selected-area-profile.json");
        if (!File.Exists(profilePath))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            return document.RootElement.TryGetProperty("game_root", out var value)
                ? value.GetString()?.Trim() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    public override void _Notification(int what)
    {
        if (!Engine.IsEditorHint())
        {
            return;
        }

        if (what == NotificationEditorPreSave)
        {
            StashEditorPreview();
        }
        else if (what == NotificationEditorPostSave)
        {
            RestoreEditorPreview();
        }
    }

    private void ShowEditorPreview()
    {
        var gameRoot = GameInstallation.ResolveConfiguredRoot();
        if (gameRoot.Length == 0)
        {
            return;
        }

        var archivePath = System.IO.Path.Combine(
            gameRoot, "packages", "core", "data", "guiexport.erf");
        if (!System.IO.File.Exists(archivePath))
        {
            return;
        }

        try
        {
            var loader = new MainMenuAssetLoader(this);
            loader.Load(archivePath);
            editorPlate = loader.Plate;
            editorFont = loader.Font;
            ApplyEditorItemStyling();
        }
        catch (Exception exception)
        {
            GD.PushWarning("Title screen editor preview unavailable: " + exception.Message);
        }
    }

    private void ApplyEditorItemStyling()
    {
        foreach (var button in MenuButtons())
        {
            button.Alignment = HorizontalAlignment.Right;
            button.AddThemeStyleboxOverride("normal", Blank());
            button.AddThemeColorOverride("font_color", new Color(0.82f, 0.78f, 0.7f));
            button.AddThemeFontSizeOverride("font_size", 22);
            if (editorFont is not null)
            {
                button.AddThemeFontOverride("font", editorFont);
            }
        }

        foreach (var plate in MenuPlates())
        {
            plate.Texture = editorPlate;
            plate.Modulate = new Color(1, 1, 1, 1);
        }
    }

    private void StashEditorPreview()
    {
        stashed.Clear();
        foreach (var rect in Textures(this))
        {
            stashed[rect] = rect.Get("texture");
            rect.Set("texture", default(Variant));
        }

        foreach (var button in MenuButtons())
        {
            if (button is null)
            {
                continue;
            }

            foreach (var name in new[] { "normal", "hover" })
            {
                button.RemoveThemeStyleboxOverride(name);
            }

            button.RemoveThemeColorOverride("font_color");
            button.RemoveThemeFontSizeOverride("font_size");
            button.RemoveThemeFontOverride("font");
        }

        foreach (var plate in MenuPlates())
        {
            if (plate is not null)
            {
                plate.Modulate = new Color(1, 1, 1, 0);
            }
        }
    }

    private void RestoreEditorPreview()
    {
        foreach (var (rect, texture) in stashed)
        {
            if (IsInstanceValid(rect))
            {
                rect.Set("texture", texture);
            }
        }

        stashed.Clear();
        if (newGameButton is not null && continueButton is not null && loadLevelButton is not null &&
            optionsButton is not null && quitButton is not null)
        {
            ApplyEditorItemStyling();
        }
    }

    private static IEnumerable<Control> Textures(Node root)
    {
        foreach (var child in root.GetChildren())
        {
            if (child is TextureRect or NinePatchRect)
            {
                yield return (Control)child;
            }

            foreach (var nested in Textures(child))
            {
                yield return nested;
            }
        }
    }

    private void StyleItems(MainMenuAssetLoader loader, string[] disabledCommands)
    {
        var shine = wordmark.Material is ShaderMaterial wordmarkMaterial
            ? wordmarkMaterial.Shader
            : null;

        foreach (var (button, plate) in new[]
                 {
                     (newGameButton, newGamePlate),
                     (continueButton, continuePlate),
                     (loadLevelButton, loadLevelPlate),
                     (optionsButton, optionsPlate),
                     (quitButton, quitPlate)
                 })
        {
            plate.Texture = loader.Plate;
            plate.Modulate = new Color(1, 1, 1, 0);
            plate.Position = new Vector2(PlateHome() + RollDistance, plate.Position.Y);
            if (shine is not null)
            {
                plate.Material = new ShaderMaterial { Shader = shine };
                plate.Material.Set("shader_parameter/sweep", -0.4f);
                plate.Material.Set("shader_parameter/band", 0.1f);
                plate.Material.Set("shader_parameter/strength", 0.75f);
            }

            var target = plate;
            button.MouseEntered += () => RollIn(target);
            button.MouseExited += () => RollOut(target);

            button.FocusMode = FocusModeEnum.None;
            button.MouseDefaultCursorShape = CursorShape.PointingHand;
            button.Alignment = HorizontalAlignment.Right;
            button.ClipText = true;

            button.AddThemeStyleboxOverride("normal", Blank());
            button.AddThemeStyleboxOverride("disabled", Blank());
            button.AddThemeStyleboxOverride("hover", Blank());
            button.AddThemeStyleboxOverride("focus", Blank());
            button.AddThemeStyleboxOverride("pressed", Blank());
            button.AddThemeStyleboxOverride("hover_pressed", Blank());

            button.AddThemeColorOverride("font_color", new Color(0.82f, 0.78f, 0.7f));
            button.AddThemeColorOverride("font_disabled_color", new Color(0.42f, 0.4f, 0.36f));
            button.AddThemeColorOverride("font_hover_color", new Color(1, 0.95f, 0.82f));
            button.AddThemeColorOverride("font_pressed_color", Colors.White);
            button.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.95f));
            button.AddThemeConstantOverride("outline_size", 3);
            button.AddThemeFontSizeOverride("font_size", 22);
            if (loader.Font is not null)
            {
                button.AddThemeFontOverride("font", loader.Font);
            }
        }

        if (loader.Font is not null)
        {
            foreach (var target in new Control[]
                     { browser, mapTabs, mapCanvas, arrivalChoices, status, loadButton, backButton })
            {
                target.AddThemeFontOverride("font", loader.Font);
            }

            browser.GetNode<Label>("Layout/Title").AddThemeFontOverride("font", loader.Font);
            browser.GetNode<Label>("Layout/Hint").AddThemeFontOverride("font", loader.Font);
        }

        newGameButton.Disabled = disabledCommands.Contains("new_game");
        continueButton.SetMeta("command_disabled", disabledCommands.Contains("load_game"));
        RefreshContinueState();
        options.Style(loader.Font);
    }

    private Button[] MenuButtons() =>
        [newGameButton, continueButton, loadLevelButton, optionsButton, quitButton];

    private TextureRect[] MenuPlates() =>
        [newGamePlate, continuePlate, loadLevelPlate, optionsPlate, quitPlate];

    public void OpenStartMenu()
    {
        browser.Visible = false;
        characterCreation.Visible = false;
        options.Visible = false;
        frame.Visible = true;
        items.Visible = true;
        RefreshContinueState();
    }

    private void RefreshContinueState()
    {
        if (continueButton is null)
        {
            return;
        }
        var canContinue = NewGameService.CanContinue(out var reason);
        var commandDisabled = continueButton.HasMeta("command_disabled") &&
            continueButton.GetMeta("command_disabled").AsBool();
        continueButton.Disabled = !canContinue || commandDisabled;
        continueButton.TooltipText = reason;
    }

    private void OpenCharacterCreation()
    {
        browser.Visible = false;
        options.Visible = false;
        frame.Visible = false;
        characterCreation.Open();
    }

    internal void BeginCharacterCreationAcceptance() =>
        Callable.From(RunCharacterCreationAcceptance).CallDeferred();

    internal void BeginCharacterPreviewMatrixAcceptance() =>
        Callable.From(RunCharacterPreviewMatrixAcceptance).CallDeferred();

    private async void RunCharacterPreviewMatrixAcceptance()
    {
        try
        {
            var outputRoot = OS.GetEnvironment(
                "OPENDAO_CHARACTER_PREVIEW_MATRIX_OUTPUT");
            if (string.IsNullOrWhiteSpace(outputRoot))
                throw new InvalidDataException(
                    "OPENDAO_CHARACTER_PREVIEW_MATRIX_OUTPUT is required.");
            outputRoot = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(outputRoot);
            OpenCharacterCreation();
            await WaitForAcceptanceFrames(
                "OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES", 8);

            var selectionKeys = new HashSet<string>(StringComparer.Ordinal);
            var morphs = new HashSet<string>(StringComparer.Ordinal);
            var payloadHashes = new HashSet<string>(StringComparer.Ordinal);
            var imageHashes = new HashSet<string>(StringComparer.Ordinal);
            var captured = 0;
            foreach (var appearance in
                     DragonAgeOriginsCharacterCreationCatalog.Appearances)
            {
                var resolution = CachedDaoCharacterAppearanceCatalog.Resolve(
                    appearance.Race, appearance.Gender, appearance.Preset);
                if (resolution.Availability !=
                        DaoCharacterAppearanceAvailability.LegacyEvidence ||
                    resolution.Appearance?.SelectionKey != appearance.SelectionKey ||
                    appearance.LegacyStandingSha256 is not { Length: 64 } payloadHash)
                    throw new InvalidDataException(
                        "Character preview did not resolve exact legacy evidence: " +
                        appearance.SelectionKey);
                var origin = appearance.Race switch
                {
                    "human" => "human-noble",
                    "elf" => "city-elf",
                    "dwarf" => "dwarf-commoner",
                    _ => throw new InvalidDataException(
                        "Unexpected character race: " + appearance.Race)
                };
                var profile = CharacterProfile.Create(
                    "Evidence Warden",
                    origin,
                    appearance.Race,
                    appearance.Gender,
                    "warrior",
                    appearance.Preset);
                if (!ConfigureAndValidatePreview(profile, appearance))
                    throw new InvalidDataException(
                        "Character preview rejected source identity: " +
                        appearance.SelectionKey);
                await WaitForAcceptanceFrames(
                    "OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES", 8);

                var image = characterCreation.CapturePreviewImage();
                var visual = InspectPreview(image);
                if (!visual.Passed)
                    throw new InvalidDataException(
                        $"Character preview visual gate failed: {appearance.SelectionKey} " +
                        visual.Failure);
                var fileStem = appearance.SelectionKey.Replace(':', '-');
                var path = Path.Combine(outputRoot, fileStem + ".png");
                if (image.SavePng(path) != Error.Ok)
                    throw new IOException("Character preview capture failed: " + path);
                var imageHash = Convert.ToHexString(
                    SHA256.HashData(image.GetData())).ToLowerInvariant();
                if (!selectionKeys.Add(appearance.SelectionKey) ||
                    !morphs.Add(appearance.MorphResource) ||
                    !payloadHashes.Add(payloadHash) ||
                    !imageHashes.Add(imageHash))
                    throw new InvalidDataException(
                        "Character preview identity or rendered image is duplicated: " +
                        appearance.SelectionKey);

                if (appearance.Preset == "preset-4")
                {
                    var representative = Path.Combine(outputRoot,
                        $"representative-{appearance.Race}-{appearance.Gender}.png");
                    if (GetViewport().GetTexture().GetImage().SavePng(representative) !=
                        Error.Ok)
                        throw new IOException(
                            "Representative character capture failed: " + representative);
                }
                captured++;
                GD.Print("OPENDAO_CHARACTER_PREVIEW_MATRIX_ITEM status=pass " +
                         $"selection={appearance.SelectionKey} " +
                         $"morph={appearance.MorphResource} " +
                         $"morph_sha256={appearance.MorphSha256} " +
                         $"payload_sha256={payloadHash} image_sha256={imageHash} " +
                         $"provenance={resolution.Provenance} visible_pixels={visual.VisiblePixels} " +
                         $"bounds={visual.Bounds} clipped=0 facing=root-yaw-180 " +
                         $"npc_substitution=0 pbr=global-postprocessor capture={path}");
            }

            var passed = captured ==
                             DragonAgeOriginsCharacterCreationCatalog.ExpectedSelectionCount &&
                         selectionKeys.Count == captured && morphs.Count == captured &&
                         payloadHashes.Count == captured && imageHashes.Count == captured;
            GD.Print("OPENDAO_CHARACTER_PREVIEW_MATRIX_ACCEPTANCE status=" +
                     (passed ? "pass" : "fail") +
                     $" captured={captured} selections={selectionKeys.Count} " +
                     $"morphs={morphs.Count} payload_hashes={payloadHashes.Count} " +
                     $"distinct_images={imageHashes.Count} legacy_evidence_ready={captured} " +
                     "fresh_import_ready=0 release_ready=0 parity_claim=none " +
                     "npc_substitutions=0 pbr=global-postprocessor " +
                     $"output={outputRoot}");
            GetTree().Quit(passed ? 0 : 59);
        }
        catch (Exception exception)
        {
            GD.PushError("OPENDAO_CHARACTER_PREVIEW_MATRIX_ACCEPTANCE status=fail " +
                         exception);
            GetTree().Quit(59);
        }
    }

    private bool ConfigureAndValidatePreview(
        CharacterProfile profile,
        DragonAgeCharacterCreationAppearance appearance) =>
        characterCreation.ConfigureForAcceptance(profile) &&
        Path.GetFullPath(characterCreation.PreviewModelPath).Equals(
            Path.GetFullPath(CachedDaoCharacterAppearanceCatalog.Resolve(
                appearance.Race, appearance.Gender, appearance.Preset).StandingPath),
            StringComparison.OrdinalIgnoreCase);

    private static PreviewVisualEvidence InspectPreview(Image image)
    {
        if (image.IsEmpty())
            return new PreviewVisualEvidence(false, "empty-image", 0, "none");
        var minimumX = image.GetWidth();
        var minimumY = image.GetHeight();
        var maximumX = -1;
        var maximumY = -1;
        var visible = 0;
        for (var y = 0; y < image.GetHeight(); y++)
            for (var x = 0; x < image.GetWidth(); x++)
            {
                var pixel = image.GetPixel(x, y);
                var luminance = pixel.R * .2126f + pixel.G * .7152f + pixel.B * .0722f;
                if (pixel.A <= .02f || luminance <= .005f) continue;
                visible++;
                minimumX = Math.Min(minimumX, x);
                minimumY = Math.Min(minimumY, y);
                maximumX = Math.Max(maximumX, x);
                maximumY = Math.Max(maximumY, y);
            }
        if (visible < image.GetWidth() * image.GetHeight() * .01f)
            return new PreviewVisualEvidence(false, "blank-or-near-black", visible,
                "none");
        var margin = 2;
        if (minimumX <= margin || minimumY <= margin ||
            maximumX >= image.GetWidth() - 1 - margin ||
            maximumY >= image.GetHeight() - 1 - margin)
            return new PreviewVisualEvidence(false, "visible-mesh-touches-frame", visible,
                $"{minimumX},{minimumY},{maximumX},{maximumY}");
        return new PreviewVisualEvidence(true, string.Empty, visible,
            $"{minimumX},{minimumY},{maximumX},{maximumY}");
    }

    private sealed record PreviewVisualEvidence(
        bool Passed, string Failure, int VisiblePixels, string Bounds);

    private async void RunCharacterCreationAcceptance()
    {
        try
        {
            await WaitForAcceptanceFrames("OPENDAO_ACCEPTANCE_MENU_HOLD_FRAMES", 2);
            var menuCapturePath = OS.GetEnvironment("OPENDAO_MAIN_MENU_CAPTURE");
            var menuCapture = menuCapturePath.Length == 0
                ? Error.Ok
                : GetViewport().GetTexture().GetImage().SavePng(menuCapturePath);
            GD.Print($"OPENDAO_MAIN_MENU_CAPTURE status={(menuCapture == Error.Ok ? "pass" : "fail")} " +
                     $"capture={menuCapturePath}");
            if (menuCapture != Error.Ok)
            {
                GetTree().Quit(59);
                return;
            }

            OpenCharacterCreation();
            await WaitForAcceptanceFrames("OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES", 16);

            var defaultCapturePath = OS.GetEnvironment("OPENDAO_CHARACTER_DEFAULT_CAPTURE");
            var defaultCapture = defaultCapturePath.Length == 0
                ? Error.Ok
                : GetViewport().GetTexture().GetImage().SavePng(defaultCapturePath);
            var defaultPassed = characterCreation.PreviewModelPath.Length > 0 && defaultCapture == Error.Ok;
            GD.Print($"OPENDAO_CHARACTER_DEFAULT_FRAMING_CAPTURE " +
                     $"status={(defaultPassed ? "pass" : "fail")} " +
                     $"preview={characterCreation.PreviewModelPath} capture={defaultCapturePath}");
            if (!defaultPassed)
            {
                GetTree().Quit(59);
                return;
            }

            var requestedOrigin = OS.GetEnvironment("OPENDAO_ACCEPTANCE_ORIGIN");
            if (requestedOrigin.Length == 0) requestedOrigin = "city-elf";
            var requestedClass = OS.GetEnvironment("OPENDAO_ACCEPTANCE_CLASS").ToLowerInvariant();
            var preferredClass = CharacterProfileRules.Classes.Any(value =>
                value.Id.Equals(requestedClass, StringComparison.OrdinalIgnoreCase))
                ? requestedClass
                : requestedOrigin switch
                {
                    "circle-mage" => "mage",
                    "city-elf" or "dalish-elf" or "dwarf-commoner" => "rogue",
                    _ => "warrior"
                };
            var identity = CharacterProfileRules.Races
                .Select(race => (Race: race.Id, Class: preferredClass))
                .FirstOrDefault(value => CharacterProfileRules.OriginsFor(value.Race, value.Class)
                    .Any(origin => origin.Id.Equals(requestedOrigin, StringComparison.OrdinalIgnoreCase)));
            if (identity == default)
                throw new InvalidDataException("Acceptance origin is not legal: " + requestedOrigin);
            var requestedGender = OS.GetEnvironment("OPENDAO_ACCEPTANCE_GENDER").ToLowerInvariant();
            if (requestedGender is not ("male" or "female")) requestedGender = "female";
            var requestedName = OS.GetEnvironment("OPENDAO_ACCEPTANCE_NAME");
            if (string.IsNullOrWhiteSpace(requestedName)) requestedName = "Automation Warden";
            var requestedAppearance = OS.GetEnvironment("OPENDAO_ACCEPTANCE_APPEARANCE").ToLowerInvariant();
            if (!CharacterProfileRules.Appearances.Any(value =>
                    value.Id.Equals(requestedAppearance, StringComparison.OrdinalIgnoreCase)))
                requestedAppearance = "preset-3";
            var expected = CharacterProfile.Create(requestedName, requestedOrigin,
                identity.Race, requestedGender, identity.Class, requestedAppearance);
            var configured = characterCreation.ConfigureForAcceptance(expected);
            await WaitForAcceptanceFrames("OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES", 16);

            var capturePath = OS.GetEnvironment("OPENDAO_CHARACTER_CREATION_CAPTURE");
            var capture = capturePath.Length == 0
                ? Error.Ok
                : GetViewport().GetTexture().GetImage().SavePng(capturePath);
            var passed = configured && capture == Error.Ok;
            GD.Print($"OPENDAO_CHARACTER_CREATION_ACCEPTANCE status={(passed ? "pass" : "fail")} " +
                     $"stage={characterCreation.CurrentStage} authored_assets={characterCreation.ArtworkQuadCount} " +
                     $"preview={characterCreation.PreviewModelPath} capture={capturePath}");
            if (!passed)
            {
                GetTree().Quit(59);
                return;
            }

            characterCreation.AdvanceForAcceptance();
            await WaitForAcceptanceFrames("OPENDAO_ACCEPTANCE_CHARACTER_HOLD_FRAMES", 16);
            var appearanceCapturePath = OS.GetEnvironment("OPENDAO_CHARACTER_APPEARANCE_CAPTURE");
            var appearanceCapture = appearanceCapturePath.Length == 0
                ? Error.Ok
                : GetViewport().GetTexture().GetImage().SavePng(appearanceCapturePath);
            var appearancePassed = characterCreation.CurrentStage == "appearance" &&
                                   appearanceCapture == Error.Ok;
            GD.Print($"OPENDAO_CHARACTER_APPEARANCE_ACCEPTANCE " +
                     $"status={(appearancePassed ? "pass" : "fail")} " +
                     $"stage={characterCreation.CurrentStage} capture={appearanceCapturePath}");
            if (!appearancePassed)
            {
                GetTree().Quit(59);
                return;
            }
            characterCreation.SubmitForAcceptance();
        }
        catch (Exception exception)
        {
            GD.PushError("OPENDAO_CHARACTER_CREATION_ACCEPTANCE_FAIL " + exception);
            GetTree().Quit(59);
        }
    }

    private async Task WaitForAcceptanceFrames(string variable, int fallback)
    {
        var frameCount = int.TryParse(OS.GetEnvironment(variable), out var configured)
            ? Math.Max(configured, 0)
            : fallback;
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    private void CloseCharacterCreation()
    {
        characterCreation.Visible = false;
        frame.Visible = true;
        items.Visible = true;
        RefreshContinueState();
    }

    private void StartNewGame(CharacterProfile profile)
    {
        characterCreation.SetBusy(true);
        characterCreation.SetStatus("Preparing your campaign…", false);
        if (!newGame.Prepare(profile, out var error))
        {
            characterCreation.SetBusy(false);
            characterCreation.SetStatus(error);
            return;
        }

        characterCreation.SetStatus($"Entering the world as {profile.Name}…", false);
        var sceneError = GetTree().ChangeSceneToFile(WorldScene);
        if (sceneError != Error.Ok)
        {
            characterCreation.SetBusy(false);
            characterCreation.SetStatus("Could not open the game world: " + sceneError);
        }
    }

    private void ContinueGame()
    {
        if (!NewGameService.CanContinue(out var reason))
        {
            RefreshContinueState();
            continueButton.TooltipText = reason;
            return;
        }
        NewGameService.ConfigureContinue();
        GetTree().ChangeSceneToFile(WorldScene);
    }

    private void OpenOptions()
    {
        options.ShowCurrent();
        options.Visible = true;
        frame.Visible = false;
    }

    private void CloseOptions()
    {
        options.Visible = false;
        frame.Visible = true;
    }

    private void AcceptOptions()
    {
        var chosen = options.Chosen();
        chosen.Save();
        chosen.Apply(GetWindow());
        CloseOptions();
    }

    private void OpenBrowser()
    {
        browser.Visible = true;
        items.Visible = false;
        if (catalog.Areas.Count == 0 && !catalog.Load())
        {
            ShowMapFallback(catalog.Error);
            return;
        }

        if (catalog.WorldMapMarkers.Count == 0)
        {
            ShowMapFallback(catalog.Error.Length > 0 ? catalog.Error : "No authored MAP pins are available");
            return;
        }

        PopulateMapTabs();
    }

    /// <summary>
    /// Opens the imported-area picker without requiring the retail launcher or
    /// a Dragon Age executable. This is the entry point used by Nikami.Aurora.GodotRuntime's
    /// compatibility-runtime shortcut.
    /// </summary>
    public void OpenAreaBrowser()
    {
        OpenBrowser();
    }

    private void CloseBrowser()
    {
        browser.Visible = false;
        items.Visible = true;
    }

    private void PopulateMapTabs()
    {
        mapTabs.Clear();
        mapNames.Clear();
        var maps = catalog.WorldMapMarkers
            .Select(marker => marker.Map)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(map => map, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var map in maps)
        {
            var available = catalog.WorldMapMarkers.Count(marker =>
                marker.Map.Equals(map, StringComparison.OrdinalIgnoreCase) && marker.CanTravel);
            var total = catalog.WorldMapMarkers.Count(marker =>
                marker.Map.Equals(map, StringComparison.OrdinalIgnoreCase));
            mapTabs.AddItem($"{DisplayMapName(map)}  •  {available}/{total} routes");
            mapNames.Add(map);
        }

        var preferred = Array.FindIndex(maps, map => map.Equals("wide_open_world", StringComparison.OrdinalIgnoreCase));
        mapTabs.Select(preferred >= 0 ? preferred : 0);
        SelectMap(mapTabs.Selected);
    }

    private void ShowMapFallback(string error)
    {
        mapTabs.Clear();
        mapNames.Clear();
        arrivalChoices.Clear();
        arrivalChoices.Visible = false;
        mapTabs.AddItem("FERELDEN  •  route data unavailable");
        mapNames.Add("wide_open_world");
        mapTabs.Select(0);
        mapCanvas.SetMarkers("wide_open_world", [], null);
        status.Text = error;
        loadButton.Disabled = true;
    }

    private void SelectMap(long index)
    {
        if (index < 0 || index >= mapNames.Count)
        {
            return;
        }

        var map = mapNames[(int)index];
        var markers = catalog.WorldMapMarkers
            .Where(marker => marker.Map.Equals(map, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        selectedMarker = markers.FirstOrDefault(marker => marker.CanTravel);
        mapCanvas.SetMarkers(map, markers, selectedMarker);
        if (selectedMarker is not null)
        {
            Describe(selectedMarker);
        }
        else
        {
            status.Text = "No validated imported arrival points on this authored MAP";
            loadButton.Disabled = true;
            arrivalChoices.Clear();
            arrivalChoices.Visible = false;
        }
    }

    private void Describe(WorldMapMarker marker)
    {
        var changed = selectedMarker is null || !selectedMarker.Equals(marker);
        selectedMarker = marker;
        ConfigureArrivalChoices(marker, changed);
        DescribeSelectedArrival(marker);
    }

    private void ConfigureArrivalChoices(WorldMapMarker marker, bool reset)
    {
        if (!marker.RequiresArrivalChoice)
        {
            arrivalChoices.Clear();
            arrivalChoices.Visible = false;
            return;
        }

        if (reset || arrivalChoices.ItemCount != marker.Arrivals.Count)
        {
            arrivalChoices.Clear();
            foreach (var arrival in marker.Arrivals)
            {
                arrivalChoices.AddItem($"{arrival.Waypoint}  •  {arrival.Destination.Id}");
            }
            arrivalChoices.Select(0);
        }
        arrivalChoices.Visible = true;
    }

    private void OnArrivalChoiceSelected(long _)
    {
        if (selectedMarker is not null)
        {
            DescribeSelectedArrival(selectedMarker);
        }
    }

    private WorldMapArrival? SelectedArrival(WorldMapMarker marker)
    {
        if (!marker.CanTravel)
        {
            return null;
        }
        if (!marker.RequiresArrivalChoice)
        {
            return marker.Arrivals.FirstOrDefault(arrival => arrival.IsUsable);
        }
        var selected = (int)arrivalChoices.Selected;
        return selected >= 0 && selected < marker.Arrivals.Count && marker.Arrivals[selected].IsUsable
            ? marker.Arrivals[selected]
            : marker.Arrivals.FirstOrDefault(arrival => arrival.IsUsable);
    }

    private void DescribeSelectedArrival(WorldMapMarker marker)
    {
        var arrival = SelectedArrival(marker);
        if (arrival is null)
        {
            status.Text = $"{marker.AreaId}  •  unavailable: no validated imported authored arrival";
            loadButton.Disabled = true;
            return;
        }

        var arrivalText = marker.RequiresArrivalChoice
            ? $"{marker.Arrivals.Count} authored entrances  •  selected: {arrival.Waypoint}"
            : $"authored arrival: {arrival.Waypoint}";
        status.Text = $"{marker.AreaId}  •  {arrivalText}  •  {arrival.Destination.AnimatedActors} actor models" +
            (marker.Status == 1 ? "  •  campaign-inactive, available for exploration" : string.Empty);
        loadButton.Disabled = false;
    }

    private void LoadSelected()
    {
        if (selectedMarker is null)
        {
            return;
        }

        var arrival = SelectedArrival(selectedMarker);
        if (arrival is null)
        {
            status.Text = selectedMarker.AreaId + " has no validated imported MAP arrival";
            return;
        }
        if (!AreaCatalog.SelectForTravel(selectedMarker, arrival, out var error))
        {
            status.Text = error;
            return;
        }

        // The C# world runtime consumes the pending MAP waypoint before restoring a saved
        // session. Esc sets this guard while returning to the picker, so clear
        // it when a new map destination is deliberately chosen.
        OS.SetEnvironment("OPENDAO_IGNORE_PENDING_TRANSITION", "");
        OS.SetEnvironment("OPENDAO_CONTINUE", "1");
        OS.SetEnvironment("OPENDAO_MAP_EXPLORE", "1");
        status.Text = "Traveling to " + selectedMarker.AreaId;
        GetTree().ChangeSceneToFile(WorldScene);
    }

    private static string DisplayMapName(string map) => map.Replace('_', ' ').ToUpperInvariant();

    private void Quit()
    {
        CommandRequested?.Invoke("quit");
        GetTree().Quit();
    }

    private float PlateHome() => PlateHomeX + plateShift;

    private void RollIn(TextureRect plate)
    {
        rolling.GetValueOrDefault(plate)?.Kill();
        shining.GetValueOrDefault(plate)?.Kill();

        var home = PlateHome();
        var roll = CreateTween().SetParallel();
        roll.TweenProperty(plate, "position:x", home, RollSeconds);
        roll.TweenProperty(plate, "modulate:a", 1.0f, RollSeconds);
        rolling[plate] = roll;

        if (plate.Material is null)
        {
            return;
        }

        var sweep = CreateTween().SetLoops();
        sweep.TweenInterval(ShineDelay);
        sweep.TweenProperty(plate.Material, "shader_parameter/sweep", 1.4f, ShineSeconds)
            .From(-0.4f);
        sweep.TweenInterval(Math.Max(0.1f, ShineCycle - ShineDelay - ShineSeconds));
        shining[plate] = sweep;
    }

    private void RollOut(TextureRect plate)
    {
        rolling.GetValueOrDefault(plate)?.Kill();
        shining.GetValueOrDefault(plate)?.Kill();
        shining.Remove(plate);

        var roll = CreateTween().SetParallel();
        roll.TweenProperty(plate, "position:x", PlateHome() + RollDistance, RollSeconds);
        roll.TweenProperty(plate, "modulate:a", 0.0f, RollSeconds);
        rolling[plate] = roll;
    }

    private static StyleBoxEmpty Blank()
    {
        var style = new StyleBoxEmpty();
        Inset(style);
        return style;
    }

    private static void Inset(StyleBox style)
    {
        style.ContentMarginLeft = TextInset;
        style.ContentMarginRight = TextInset;
    }

    private void LayoutStage()
    {
        if (scenery is null || frame is null)
        {
            return;
        }

        var available = Size;
        if (available.X <= 0 || available.Y <= 0)
        {
            return;
        }

        var cover = Math.Max(available.X / StageWidth, available.Y / StageHeight);
        scenery.Scale = new Vector2(cover, cover);
        scenery.Position = new Vector2(
            (available.X - (StageWidth * cover)) * 0.5f,
            (available.Y - (StageHeight * cover)) * 0.5f);

        frame.Scale = Vector2.One;
        frame.Position = Vector2.Zero;
        frame.Size = available;

        options.Root.Scale = Vector2.One;
        options.Root.Position = new Vector2(
            (available.X - StageWidth) * 0.5f, (available.Y - StageHeight) * 0.5f);

        var shift = available - new Vector2(StageWidth, StageHeight);
        plateShift = shift.X;
        foreach (var (node, home, anchor) in anchored)
        {
            node.Position = home + (shift * anchor);
        }
    }
}
