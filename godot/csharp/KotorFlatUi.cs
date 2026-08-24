using System.Security.Cryptography;
using Godot;
using Nikami.Aurora.Profiles.Kotor;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot
{
    private static readonly Color KotorBlue = new(0.0f, 0.65882355f, 0.98039216f);
    private static readonly Color KotorYellow = new(0.9843137f, 1.0f, 0.0f);
    private readonly Dictionary<string, Texture2D> flatUiTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (FontFile Font, int Size, int Baseline, int NativeSize)> flatUiFonts =
        new(StringComparer.OrdinalIgnoreCase);
    private KotorUiRecord? flatUiRecord;
    private string flatUiManifestDirectory = "";
    private Control? desktopHudRoot;
    private Control? inventoryScreen;
    private Control? inventoryReferenceSurface;
    private VBoxContainer? inventoryRows;
    private Label? inventoryDescription;
    private Label? inventoryCredits;
    private Label? inventoryVitality;
    private Label? inventoryDefense;
    private Button? inventoryUseButton;
    private readonly List<Button> inventoryRowButtons = [];
    private IReadOnlyList<KotorUiInventoryItemRecord> visibleInventoryItems = [];
    private int selectedInventoryIndex;
    private TextureProgressBar? loadingProgress;
    private TextureProgressBar? hudVitalityBar;
    private AudioStreamPlayer? loadingMusicPlayer;
    private Control? hudMinimapClip;
    private TextureRect? hudMinimapTexture;
    private TextureRect? hudMinimapArrow;
    private KotorUiMinimapRecord? hudMinimapRecord;
    private bool automatedInventoryOpened;

    private void ConfigureFlatReferenceViewportIfRequested()
    {
        var requested = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_FLAT_UI_REFERENCE_VIEWPORT");
        if (!string.Equals(requested, "800x600", StringComparison.Ordinal))
            return;
        var root = GetTree().Root;
        root.ContentScaleSize = new Vector2I(800, 600);
        root.ContentScaleAspect = Window.ContentScaleAspectEnum.Ignore;
        root.Size = new Vector2I(800, 600);
        GD.Print("NIKAMI_AURORA_FLAT_VIEWPORT status=configured size=800x600 aspect=ignore");
    }

    private void ConfigureFlatPresentation(KotorUiRecord ui, string manifestDirectory)
    {
        if (ui.Schema != "nikami-aurora-kotor-ui-v1")
            throw new InvalidDataException($"Unsupported KOTOR UI schema: {ui.Schema}");
        flatUiRecord = ui;
        flatUiManifestDirectory = manifestDirectory;
        flatUiTextures.Clear();
        flatUiFonts.Clear();
        foreach (var source in ui.Textures)
        {
            flatUiTextures.Add(source.Resref, LoadAndValidateUiTexture(source));
            if (source.BitmapFontPath is { Length: > 0 })
                flatUiFonts.Add(source.Resref, LoadAndValidateUiFont(source));
        }

        ConfigureRetailLoadingScreen(ui.Loading);
        if (!xrActive)
        {
            ConfigureRetailDesktopHud(ui.Hud);
            ConfigureRetailInventory(ui.Inventory);
        }
        GD.Print($"NIKAMI_AURORA_FLAT_UI status=ready schema={ui.Schema} " +
                 $"textures={ui.Textures.Count} " +
                 $"fonts={flatUiFonts.Count} " +
                 $"loading={ui.Loading.Layout.Resref} " +
                 $"hud={ui.Hud.Layout.Resref} inventory={ui.Inventory.Layout.Resref}");
    }

    private Texture2D LoadAndValidateUiTexture(KotorUiTextureRecord source)
    {
        var path = ResolveFlatUiBundlePath(source.Path);
        var payload = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (!actualHash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"KOTOR UI payload hash drifted: {source.Resref}");
        var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty() ||
            image.GetWidth() != source.Width || image.GetHeight() != source.Height)
            throw new InvalidDataException(
                $"KOTOR UI texture dimensions drifted: {source.Resref}");
        return ImageTexture.CreateFromImage(image);
    }

    private (FontFile Font, int Size, int Baseline, int NativeSize) LoadAndValidateUiFont(
        KotorUiTextureRecord source)
    {
        var path = ResolveFlatUiBundlePath(source.BitmapFontPath!);
        var payload = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (payload.Length != source.BitmapFontByteCount ||
            !actualHash.Equals(source.BitmapFontSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"KOTOR UI bitmap-font payload drifted: {source.Resref}");
        var font = new FontFile();
        var error = font.LoadBitmapFont(path);
        if (error != Error.Ok || source.BitmapFontSize <= 0 ||
            source.BitmapFontBaseline <= 0 ||
            source.BitmapFontNativeSize <= 0 ||
            source.BitmapFontGlyphCount <= 0)
            throw new InvalidDataException(
                $"KOTOR UI bitmap font could not be loaded: {source.Resref} ({error})");
        return (
            font,
            source.BitmapFontSize,
            source.BitmapFontBaseline,
            source.BitmapFontNativeSize);
    }

    private string ResolveFlatUiBundlePath(string relative)
    {
        var root = Path.GetFullPath(flatUiManifestDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            flatUiManifestDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"KOTOR UI path escapes the local import bundle: {relative}");
        return path;
    }

    private void ConfigureRetailLoadingScreen(KotorUiLoadingRecord source)
    {
        foreach (var child in loadingBackdrop.GetChildren())
            child.QueueFree();
        loadingBackdrop.Color = Colors.Black;
        var surface = CreateReferenceSurface(
            loadingBackdrop, source.Layout.Extent, "RetailLoadingSurface");
        AddTexture(surface, source.Background.Resref, source.Layout.Extent);

        var logoControl = RequireControl(source.Controls, "LBL_LOGO");
        AddTexture(surface, source.Logo.Resref, RequireExtent(logoControl));

        var progressControl = RequireControl(source.Controls, "PB_PROGRESS");
        loadingProgress = new TextureProgressBar
        {
            Name = "RetailLoadingProgress",
            MinValue = 0,
            MaxValue = 100,
            Value = 4,
            TextureProgress = Texture(source.Progress.Resref),
            FillMode = (int)TextureProgressBar.FillModeEnum.LeftToRight,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        Place(loadingProgress, RequireExtent(progressControl));
        surface.AddChild(loadingProgress);

        var loadingControl = RequireControl(source.Controls, "LBL_LOADING");
        status = CreateKotorLabel(
            source.LoadingText,
            loadingControl.Text,
            RequireExtent(loadingControl),
            16);
        surface.AddChild(status);

        var hintControl = RequireControl(source.Controls, "LBL_HINT");
        details = CreateKotorLabel(
            source.HintText,
            hintControl.Text,
            RequireExtent(hintControl),
            16,
            true);
        surface.AddChild(details);
        loadingMusicPlayer?.QueueFree();
        loadingMusicPlayer = new AudioStreamPlayer
        {
            Name = "RetailLoadingMusic",
            Stream = LoadAndValidateUiAudio(source.Music),
            VolumeDb = -3.0f
        };
        loadingBackdrop.AddChild(loadingMusicPlayer);
        loadingMusicPlayer.Play();
        GD.Print($"NIKAMI_AURORA_LOADING_UI status=ready " +
                 $"layout={source.Layout.Resref} " +
                 $"background={source.Background.Resref} " +
                 $"hintStrref={source.HintStrref} music={source.MusicResref} " +
                 $"font={hintControl.Text?.Font}");
    }

    private AudioStream LoadAndValidateUiAudio(KotorUiAudioRecord source)
    {
        var path = ResolveFlatUiBundlePath(source.Path);
        var payload = File.ReadAllBytes(path);
        var actualHash = Convert.ToHexString(SHA256.HashData(payload));
        if (payload.Length != source.ByteCount ||
            !actualHash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"KOTOR UI audio payload drifted: {source.Resref}");
        AudioStream stream = source.Format.ToLowerInvariant() switch
        {
            "wav" => AudioStreamWav.LoadFromBuffer(
                payload, new Godot.Collections.Dictionary()),
            "mp3" => AudioStreamMP3.LoadFromBuffer(payload),
            _ => throw new InvalidDataException(
                $"Unsupported KOTOR UI audio format: {source.Format}")
        };
        if (stream.GetLength() <= 0)
            throw new InvalidDataException(
                $"KOTOR UI audio decoded with no duration: {source.Resref}");
        GD.Print($"NIKAMI_AURORA_LOADING_AUDIO status=ready " +
                 $"resref={source.Resref} duration={stream.GetLength():F3}");
        return stream;
    }

    private void StopRetailLoadingMusic()
    {
        if (loadingMusicPlayer is null) return;
        loadingMusicPlayer.Stop();
        loadingMusicPlayer.QueueFree();
        loadingMusicPlayer = null;
    }

    private void ConfigureRetailDesktopHud(KotorUiHudRecord source)
    {
        desktopHudRoot?.QueueFree();
        desktopHudRoot = new Control
        {
            Name = "RetailDesktopHud",
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        desktopHudRoot.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlayLayer.AddChild(desktopHudRoot);
        var surface = CreateReferenceSurface(
            desktopHudRoot, source.Layout.Extent, "RetailHudSurface");
        ConfigureRetailMinimap(surface, source);

        var staticTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LBL_COMBATBG1", "LBL_MOULDING1", "LBL_MOULDING2",
            "LBL_MOULDING3", "LBL_MOULDING4", "LBL_MENUBG",
            "TB_SOLO", "TB_PAUSE", "BTN_MSG", "BTN_JOU", "BTN_MAP",
            "BTN_OPT", "BTN_CHAR", "BTN_ABI", "BTN_INV", "BTN_EQU",
            "LBL_BACK1", "LBL_MAPBORDER", "LBL_ARROW",
            "BTN_ACTION0", "BTN_ACTION1", "BTN_ACTION2", "BTN_ACTION3",
            "BTN_ACTION4", "BTN_ACTION5", "BTN_ACTIONUP0", "BTN_ACTIONUP1",
            "BTN_ACTIONUP2", "BTN_ACTIONUP3", "BTN_ACTIONUP4", "BTN_ACTIONUP5",
            "BTN_ACTIONDOWN0", "BTN_ACTIONDOWN1", "BTN_ACTIONDOWN2",
            "BTN_ACTIONDOWN3", "BTN_ACTIONDOWN4", "BTN_ACTIONDOWN5"
        };
        foreach (var control in source.Controls.Where(control => staticTags.Contains(control.Tag)))
        {
            var fill = control.Border?.Fill ?? "";
            if (!string.IsNullOrWhiteSpace(fill))
            {
                var texture = AddTexture(surface, fill, RequireExtent(control));
                if (control.Tag.Equals("LBL_ARROW", StringComparison.OrdinalIgnoreCase))
                {
                    hudMinimapArrow = texture;
                    hudMinimapArrow.PivotOffset = hudMinimapArrow.Size * 0.5f;
                }
            }
        }

        var portraitControl = RequireControl(source.Controls, "LBL_CHAR1");
        AddTexture(surface, source.Portrait.Resref, RequireExtent(portraitControl));
        hudVitalityBar = AddHudBar(
            surface, RequireControl(source.Controls, "PB_VIT1"), "redfill", 1.0);
        AddHudBar(surface, RequireControl(source.Controls, "PB_FORCE1"), "bluefill", 0.0);
        if (source.PartyPortraits.Count > 1)
        {
            AddTexture(
                surface,
                source.PartyPortraits[1].Resref,
                RequireExtent(RequireControl(source.Controls, "LBL_CHAR3")));
            AddHudBar(
                surface, RequireControl(source.Controls, "PB_VIT3"), "redfill", 1.0);
            AddHudBar(
                surface, RequireControl(source.Controls, "PB_FORCE3"), "bluefill", 0.0);
        }

        var inventoryControl = RequireControl(source.Controls, "BTN_INV");
        var inventoryHotspot = new Button
        {
            Name = "RetailInventoryHotspot",
            Flat = true,
            TooltipText = "Inventory",
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        Place(inventoryHotspot, RequireExtent(inventoryControl));
        inventoryHotspot.Pressed += ShowInventory;
        surface.AddChild(inventoryHotspot);
        GD.Print($"NIKAMI_AURORA_HUD status=ready layout={source.Layout.Resref} " +
                 $"reference=800x600 portraits={source.PartyPortraits.Count} " +
                 "playerVitality=profile force=empty");
    }

    private void ConfigureRetailMinimap(Control surface, KotorUiHudRecord source)
    {
        hudMinimapRecord = source.Minimap;
        var border = RequireExtent(RequireControl(source.Controls, "LBL_MAPBORDER"));
        var interior = new KotorUiExtent(
            border.Left + 4,
            border.Top + 4,
            border.Width - 8,
            border.Height - 9);
        hudMinimapClip = new Control
        {
            Name = "RetailMinimapClip",
            ClipContents = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        Place(hudMinimapClip, interior);
        surface.AddChild(hudMinimapClip);
        var black = new ColorRect
        {
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        black.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        hudMinimapClip.AddChild(black);
        hudMinimapTexture = new TextureRect
        {
            Name = "RetailMinimapOwnedTexture",
            Texture = Texture(source.Minimap.Texture.Resref),
            Size = new Vector2(
                source.Minimap.Texture.Width,
                source.Minimap.Texture.Height),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Keep,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        hudMinimapClip.AddChild(hudMinimapTexture);
    }

    private void UpdateRetailMinimap()
    {
        if (hudMinimapRecord is null || hudMinimapClip is null ||
            hudMinimapTexture is null || playerBody is null)
            return;
        var source = hudMinimapRecord;
        var world = ToNumerics(playerBody.GlobalPosition);
        var worldWidth = source.WorldPoint2[0] - source.WorldPoint1[0];
        var worldHeight = source.WorldPoint2[1] - source.WorldPoint1[1];
        if (Math.Abs(worldWidth) < 0.0001f || Math.Abs(worldHeight) < 0.0001f)
            return;
        var u = source.MapPoint1[0] +
                (world.X - source.WorldPoint1[0]) / worldWidth *
                (source.MapPoint2[0] - source.MapPoint1[0]);
        var v = source.MapPoint1[1] +
                (world.Y - source.WorldPoint1[1]) / worldHeight *
                (source.MapPoint2[1] - source.MapPoint1[1]);
        hudMinimapTexture.Position = hudMinimapClip.Size * 0.5f - new Vector2(
            u * source.Texture.Width,
            v * source.Texture.Height);
        if (hudMinimapArrow is not null)
            hudMinimapArrow.Rotation = -yaw;
    }

    private void ConfigureRetailInventory(KotorUiInventoryRecord source)
    {
        inventoryScreen?.QueueFree();
        inventoryScreen = new Control
        {
            Name = "RetailInventoryScreen",
            MouseFilter = Control.MouseFilterEnum.Stop,
            Visible = false
        };
        inventoryScreen.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlayLayer.AddChild(inventoryScreen);

        var dim = new ColorRect
        {
            Color = new Color(0, 0, 0, 0.86f),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        dim.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        inventoryScreen.AddChild(dim);
        inventoryReferenceSurface = CreateReferenceSurface(
            inventoryScreen, source.Layout.Extent, "RetailInventorySurface");
        AddTexture(inventoryReferenceSurface, source.Background.Resref, source.Layout.Extent);

        foreach (var control in source.TopControls.Where(control =>
                     control.Tag.StartsWith("LBLH_", StringComparison.OrdinalIgnoreCase)))
        {
            var selected = control.Tag.Equals(
                "LBLH_INV", StringComparison.OrdinalIgnoreCase);
            var fill = selected
                ? control.Highlight?.Fill
                : control.Border?.Fill;
            if (!string.IsNullOrWhiteSpace(fill))
                AddTexture(inventoryReferenceSurface, fill, RequireExtent(control));
        }

        foreach (var tag in new[] { "LBL_BGPORT", "LBL_BGSTATS" })
        {
            var control = RequireControl(source.Controls, tag);
            if (control.Border?.Fill is { Length: > 0 } fill)
                AddTexture(inventoryReferenceSurface, fill, RequireExtent(control));
        }

        var portraitControl = RequireControl(source.Controls, "LBL_PORT");
        AddTexture(inventoryReferenceSurface, source.Portrait.Resref, RequireExtent(portraitControl));
        var partyTags = new[] { "BTN_CHANGE1", "BTN_CHANGE2" };
        for (var index = 0; index < Math.Min(partyTags.Length, source.PartyPortraits.Count); index++)
        {
            AddTexture(
                inventoryReferenceSurface,
                source.PartyPortraits[index].Resref,
                RequireExtent(RequireControl(source.Controls, partyTags[index])));
        }

        AddSourceLabel(inventoryReferenceSurface, source.Controls, "LBL_INV", 16);
        AddSourceLabel(inventoryReferenceSurface, source.Controls, "LBL_CREDITS", 16);

        var creditsControl = RequireControl(source.Controls, "LBL_CREDITS_VALUE");
        inventoryCredits = CreateKotorLabel(
            "0", creditsControl.Text, RequireExtent(creditsControl), 16);
        inventoryReferenceSurface.AddChild(inventoryCredits);

        var vitControl = RequireControl(source.Controls, "LBL_VIT");
        inventoryVitality = CreateKotorLabel(
            "20/20", vitControl.Text, RequireExtent(vitControl), 16);
        inventoryReferenceSurface.AddChild(inventoryVitality);
        var defenseControl = RequireControl(source.Controls, "LBL_DEF");
        inventoryDefense = CreateKotorLabel(
            "10", defenseControl.Text, RequireExtent(defenseControl), 16);
        inventoryReferenceSurface.AddChild(inventoryDefense);

        var listControl = RequireControl(source.Controls, "LB_ITEMS");
        inventoryRows = new VBoxContainer
        {
            Name = "RetailInventoryRows"
        };
        inventoryRows.AddThemeConstantOverride("separation", 0);
        var prototype = listControl.Prototype ?? throw new InvalidDataException(
            "KOTOR inventory layout has no list prototype");
        var prototypeExtent = RequireExtent(prototype);
        inventoryRows.Position = new Vector2(prototypeExtent.Left, prototypeExtent.Top);
        inventoryRows.Size = new Vector2(prototypeExtent.Width, 250);
        inventoryReferenceSurface.AddChild(inventoryRows);

        var descriptionControl = RequireControl(source.Controls, "LB_DESCRIPTION");
        var descriptionPrototype = descriptionControl.Prototype ??
            throw new InvalidDataException(
                "KOTOR inventory description has no source prototype");
        var descriptionOuter = RequireExtent(descriptionControl);
        var descriptionInner = RequireExtent(descriptionPrototype) with
        {
            Height = descriptionOuter.Height - 8
        };
        inventoryDescription = CreateKotorLabel(
            "", descriptionPrototype.Text,
            descriptionInner, 10, true);
        inventoryReferenceSurface.AddChild(inventoryDescription);

        AddSourceButton(
            inventoryReferenceSurface, source.Controls, "BTN_QUESTITEMS", () => { }, 16);
        inventoryUseButton = AddSourceButton(
            inventoryReferenceSurface, source.Controls, "BTN_USEITEM", UseSelectedInventoryItem, 16);
        AddSourceButton(
            inventoryReferenceSurface, source.Controls, "BTN_EXIT", HideInventory, 16);
        GD.Print($"NIKAMI_AURORA_INVENTORY_UI status=ready " +
                 $"layout={source.Layout.Resref} items={source.Items.Count} " +
                 $"top={source.TopLayout.Resref} party={source.PartyPortraits.Count} " +
                 "interaction=mouse,keyboard state=profile-owned");
    }

    private void UpdateLoadingProgress(float normalized)
    {
        if (loadingProgress is not null)
            loadingProgress.Value = Mathf.Clamp(normalized, 0, 1) * 100.0;
    }

    private async Task<bool> CaptureLoadingPresentationIfRequested()
    {
        if (System.Environment.GetEnvironmentVariable(
                "NIKAMI_AURORA_CAPTURE_LOADING_SCREEN") != "1")
            return false;
        var capturePath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE");
        if (string.IsNullOrWhiteSpace(capturePath))
            throw new InvalidDataException(
                "Loading-screen capture requires NIKAMI_AURORA_CAPTURE");
        await ToSignal(
            RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
        var error = GetViewport().GetTexture().GetImage().SavePng(capturePath);
        captureCompleted = true;
        GD.Print($"NIKAMI_AURORA_LOADING_CAPTURE status={error} " +
                 $"path={capturePath} progress={loadingProgress?.Value:F1}");
        var exit = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_CAPTURE_EXIT") == "1";
        if (exit)
            RequestCleanExit(error == Error.Ok ? 0 : 1);
        return exit;
    }

    private void UpdateFlatUiVisibility()
    {
        if (desktopHudRoot is null) return;
        UpdateRetailMinimap();
        if (hudVitalityBar is not null && gameplaySimulation is not null)
        {
            var snapshot = gameplaySimulation.CaptureSnapshot();
            hudVitalityBar.Value = snapshot.PlayerCurrentVitality /
                                   (double)snapshot.PlayerMaximumVitality;
        }
        desktopHudRoot.Visible = moduleReady &&
                                 inventoryScreen?.Visible != true &&
                                 !dialoguePanel.Visible &&
                                 !loadingBackdrop.Visible;
    }

    private void ShowInventory()
    {
        if (!moduleReady || xrActive || inventoryScreen is null || dialoguePanel.Visible)
            return;
        RefreshInventory();
        inventoryScreen.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UpdateFlatUiVisibility();
        GD.Print($"NIKAMI_AURORA_INVENTORY_UI status=open " +
                 $"items={visibleInventoryItems.Count} selection={selectedInventoryIndex}");
    }

    private void HideInventory()
    {
        if (inventoryScreen is null || !inventoryScreen.Visible) return;
        inventoryScreen.Visible = false;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        UpdateFlatUiVisibility();
        GD.Print("NIKAMI_AURORA_INVENTORY_UI status=closed");
    }

    private bool HandleFlatUiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventKey key || !key.Pressed || key.Echo)
            return false;
        if (inventoryScreen?.Visible == true)
        {
            if (key.Keycode is Key.Escape or Key.I)
                HideInventory();
            else if (key.Keycode is Key.Up or Key.W)
                SelectInventoryRow(selectedInventoryIndex - 1);
            else if (key.Keycode is Key.Down or Key.S)
                SelectInventoryRow(selectedInventoryIndex + 1);
            else if (key.Keycode is Key.Enter or Key.Space)
                UseSelectedInventoryItem();
            return true;
        }
        if (key.Keycode == Key.I)
        {
            ShowInventory();
            return true;
        }
        return false;
    }

    private void RefreshInventory()
    {
        if (flatUiRecord is null || inventoryRows is null || gameplaySimulation is null)
            return;
        foreach (var child in inventoryRows.GetChildren())
            child.QueueFree();
        inventoryRowButtons.Clear();
        var snapshot = gameplaySimulation.CaptureSnapshot();
        if (inventoryCredits is not null)
            inventoryCredits.Text = snapshot.PlayerCredits.ToString();
        if (inventoryVitality is not null)
            inventoryVitality.Text =
                $"{snapshot.PlayerCurrentVitality}/{snapshot.PlayerMaximumVitality}";
        if (inventoryDefense is not null)
            inventoryDefense.Text = snapshot.PlayerDefense.ToString();
        visibleInventoryItems = flatUiRecord.Inventory.Items
            .Where(item => snapshot.PlayerInventory.ContainsKey(item.Resref))
            .ToArray();
        selectedInventoryIndex = visibleInventoryItems.Count == 0
            ? 0
            : Math.Clamp(selectedInventoryIndex, 0, visibleInventoryItems.Count - 1);

        for (var index = 0; index < visibleInventoryItems.Count; index++)
        {
            var item = visibleInventoryItems[index];
            var quantity = snapshot.PlayerInventory[item.Resref];
            var row = CreateInventoryRow(item, quantity, index);
            inventoryRows.AddChild(row);
            inventoryRowButtons.Add(row);
        }
        SelectInventoryRow(selectedInventoryIndex);
    }

    private Button CreateInventoryRow(
        KotorUiInventoryItemRecord item,
        int quantity,
        int index)
    {
        var prototypeText = flatUiRecord?.Inventory.Controls
            .Single(control => control.Tag.Equals(
                "LB_ITEMS", StringComparison.OrdinalIgnoreCase))
            .Prototype?.Text;
        var row = new Button
        {
            Name = $"InventoryRow{index}",
            CustomMinimumSize = new Vector2(245, 50),
            Flat = true,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        row.Pressed += () => SelectInventoryRow(index);
        var icon = new TextureRect
        {
            Texture = Texture(item.Icon.Resref),
            Position = new Vector2(3, 1),
            Size = new Vector2(48, 48),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        row.AddChild(icon);
        var name = new Label
        {
            Text = item.DisplayName,
            Position = new Vector2(57, 0),
            Size = new Vector2(180, 50),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ApplyKotorFont(name, prototypeText, 16);
        name.AddThemeColorOverride("font_color", KotorTextColor(prototypeText));
        row.AddChild(name);
        var count = new Label
        {
            Text = quantity > 1 ? quantity.ToString() : "",
            Position = new Vector2(205, 0),
            Size = new Vector2(32, 50),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ApplyKotorFont(count, prototypeText, 16);
        count.AddThemeColorOverride("font_color", KotorYellow);
        row.AddChild(count);
        return row;
    }

    private void SelectInventoryRow(int index)
    {
        if (visibleInventoryItems.Count == 0)
        {
            selectedInventoryIndex = 0;
            SetInventoryDescription("");
            if (inventoryUseButton is not null)
                inventoryUseButton.Disabled = true;
            return;
        }
        selectedInventoryIndex = Math.Clamp(index, 0, visibleInventoryItems.Count - 1);
        for (var rowIndex = 0; rowIndex < inventoryRowButtons.Count; rowIndex++)
            inventoryRowButtons[rowIndex].Modulate = rowIndex == selectedInventoryIndex
                ? new Color(0.78f, 1.0f, 1.0f)
                : Colors.White;
        var selected = visibleInventoryItems[selectedInventoryIndex];
        SetInventoryDescription(selected.Description);
        if (inventoryUseButton is not null)
        {
            var snapshot = gameplaySimulation?.CaptureSnapshot();
            inventoryUseButton.Disabled =
                selected.BaseItem != 55 ||
                snapshot is null ||
                snapshot.PlayerCurrentVitality >= snapshot.PlayerMaximumVitality;
        }
        inventoryRowButtons[selectedInventoryIndex].GrabFocus();
    }

    private void SetInventoryDescription(string text)
    {
        if (inventoryDescription is null || flatUiRecord is null)
            return;
        var source = flatUiRecord.Inventory.Controls
            .Single(control => control.Tag.Equals(
                "LB_DESCRIPTION", StringComparison.OrdinalIgnoreCase))
            .Prototype?.Text;
        if (source?.Font is { Length: > 0 } resref &&
            flatUiFonts.TryGetValue(resref, out var bitmap))
            text = WrapKotorText(
                text,
                bitmap.Font,
                bitmap.NativeSize,
                inventoryDescription.Size.X);
        inventoryDescription.Text = text;
    }

    private void UseSelectedInventoryItem()
    {
        if (visibleInventoryItems.Count == 0 || gameplaySimulation is null) return;
        var selected = visibleInventoryItems[selectedInventoryIndex];
        if (selected.BaseItem != 55)
        {
            GD.Print($"NIKAMI_AURORA_INVENTORY_UI status=unsupported-use " +
                     $"resref={selected.Resref} baseItem={selected.BaseItem}");
            return;
        }
        var transition = gameplaySimulation.UseMedpac(selected.Resref);
        ApplyGameplayTransition(transition);
        RefreshInventory();
        GD.Print($"NIKAMI_AURORA_INVENTORY_UI status=" +
                 $"{(transition.Events.Count == 0 ? "no-effect" : "used")} " +
                 $"resref={selected.Resref} action=consume");
    }

    private Control CreateReferenceSurface(
        Control parent,
        KotorUiExtent extent,
        string name)
    {
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var scale = Mathf.Min(
            viewportSize.X / extent.Width,
            viewportSize.Y / extent.Height);
        var surface = new Control
        {
            Name = name,
            Position = new Vector2(
                (viewportSize.X - extent.Width * scale) * 0.5f,
                (viewportSize.Y - extent.Height * scale) * 0.5f),
            Size = new Vector2(extent.Width, extent.Height),
            Scale = Vector2.One * scale,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        parent.AddChild(surface);
        return surface;
    }

    private TextureRect AddTexture(Control parent, string resref, KotorUiExtent extent)
    {
        var texture = new TextureRect
        {
            Name = $"Texture_{resref}_{parent.GetChildCount()}",
            Texture = Texture(resref),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        Place(texture, extent);
        parent.AddChild(texture);
        return texture;
    }

    private TextureProgressBar AddHudBar(
        Control parent,
        KotorUiControlRecord control,
        string textureResref,
        double value)
    {
        var bar = new TextureProgressBar
        {
            TextureProgress = Texture(textureResref),
            MinValue = 0,
            MaxValue = 1,
            Value = value,
            FillMode = (int)TextureProgressBar.FillModeEnum.BottomToTop,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        Place(bar, RequireExtent(control));
        parent.AddChild(bar);
        return bar;
    }

    private Label AddSourceLabel(
        Control parent,
        IReadOnlyList<KotorUiControlRecord> controls,
        string tag,
        int fontSize)
    {
        var source = RequireControl(controls, tag);
        var label = CreateKotorLabel(
            source.Text?.Resolved ?? source.Text?.Literal ?? "",
            source.Text,
            RequireExtent(source),
            fontSize);
        parent.AddChild(label);
        return label;
    }

    private Button AddSourceButton(
        Control parent,
        IReadOnlyList<KotorUiControlRecord> controls,
        string tag,
        Action pressed,
        int fontSize)
    {
        var source = RequireControl(controls, tag);
        var button = new Button
        {
            Name = tag,
            Text = source.Text?.Resolved ?? source.Text?.Literal ?? "",
            Flat = true,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand
        };
        ApplyKotorFont(button, source.Text, fontSize);
        button.AddThemeColorOverride("font_color", KotorTextColor(source.Text));
        button.AddThemeColorOverride("font_hover_color", KotorYellow);
        Place(button, RequireExtent(source));
        button.Pressed += pressed;
        parent.AddChild(button);
        return button;
    }

    private Label CreateKotorLabel(
        string text,
        KotorUiTextRecord? source,
        KotorUiExtent extent,
        int fontSize,
        bool wrap = false)
    {
        var label = new Label
        {
            Text = text,
            Position = new Vector2(extent.Left, extent.Top),
            Size = new Vector2(extent.Width, extent.Height),
            AutowrapMode = wrap
                ? TextServer.AutowrapMode.WordSmart
                : TextServer.AutowrapMode.Off,
            ClipText = true,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        ApplyLabelAlignment(label, source?.Alignment ?? 18);
        ApplyKotorFont(label, source, fontSize);
        label.Size = new Vector2(
            extent.Width / label.Scale.X,
            extent.Height / label.Scale.Y);
        if (wrap && source?.Font is { Length: > 0 } resref &&
            flatUiFonts.TryGetValue(resref, out var bitmap))
        {
            var layoutWidth = extent.Width / label.Scale.X;
            label.Text = WrapKotorText(
                text,
                bitmap.Font,
                bitmap.NativeSize,
                layoutWidth);
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            label.Size = new Vector2(
                layoutWidth,
                extent.Height / label.Scale.Y);
            GD.Print($"NIKAMI_AURORA_UI_TEXT status=wrapped font={resref} " +
                     $"lines={label.Text.Count(character => character == '\n') + 1} " +
                     $"layoutWidth={layoutWidth:F1} scale={label.Scale.X:F3} " +
                     $"measuredWidth={bitmap.Font.GetStringSize(text, HorizontalAlignment.Left, -1, bitmap.NativeSize).X:F1}");
        }
        label.AddThemeColorOverride("font_color", KotorTextColor(source));
        return label;
    }

    private static string WrapKotorText(
        string text,
        Font font,
        int fontSize,
        float maximumWidth)
    {
        var output = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var current = "";
            foreach (var word in paragraph.Split(
                         ' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = current.Length == 0
                    ? word
                    : $"{current} {word}";
                var width = font.GetStringSize(
                    candidate,
                    HorizontalAlignment.Left,
                    -1,
                    fontSize).X;
                if (current.Length > 0 && width > maximumWidth)
                {
                    output.Add(current);
                    current = word;
                }
                else
                {
                    current = candidate;
                }
            }
            output.Add(current);
        }
        return string.Join('\n', output);
    }

    private void ApplyKotorFont(
        Control control,
        KotorUiTextRecord? source,
        int fallbackSize)
    {
        if (source?.Font is { Length: > 0 } resref &&
            flatUiFonts.TryGetValue(resref, out var bitmap))
        {
            control.AddThemeFontOverride("font", bitmap.Font);
            if (control is Label && bitmap.Size < bitmap.NativeSize)
            {
                // Godot measures a bitmap label at the requested theme size but
                // draws its source glyph rectangles at their native size.  Keep
                // layout and drawing in the atlas coordinate space, then scale
                // the complete label to KOTOR's logical font height.
                control.AddThemeFontSizeOverride("font_size", bitmap.NativeSize);
                var scale = bitmap.Size / (float)bitmap.NativeSize;
                control.Scale = new Vector2(scale, scale);
            }
            else
            {
                control.AddThemeFontSizeOverride("font_size", bitmap.Size);
            }
            return;
        }
        control.AddThemeFontSizeOverride("font_size", fallbackSize);
    }

    private static void ApplyLabelAlignment(Label label, int alignment)
    {
        label.HorizontalAlignment = (alignment & 4) != 0
            ? HorizontalAlignment.Right
            : (alignment & 2) != 0
                ? HorizontalAlignment.Center
                : HorizontalAlignment.Left;
        label.VerticalAlignment = (alignment & 32) != 0
            ? VerticalAlignment.Bottom
            : (alignment & 16) != 0
                ? VerticalAlignment.Center
                : VerticalAlignment.Top;
    }

    private static Color KotorTextColor(KotorUiTextRecord? source) =>
        source?.Color is { Count: >= 3 } color
            ? new Color(color[0], color[1], color[2])
            : KotorBlue;

    private Texture2D Texture(string resref) =>
        flatUiTextures.TryGetValue(resref, out var texture)
            ? texture
            : throw new InvalidDataException($"KOTOR UI texture was not imported: {resref}");

    private static void Place(Control control, KotorUiExtent extent)
    {
        control.Position = new Vector2(extent.Left, extent.Top);
        control.Size = new Vector2(extent.Width, extent.Height);
    }

    private static KotorUiControlRecord RequireControl(
        IReadOnlyList<KotorUiControlRecord> controls,
        string tag) =>
        controls.Single(control => control.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase));

    private static KotorUiExtent RequireExtent(KotorUiControlRecord control) =>
        control.Extent ?? throw new InvalidDataException(
            $"KOTOR UI control has no extent: {control.Tag}");

    private sealed record KotorUiRecord(
        string Schema,
        KotorUiLoadingRecord Loading,
        KotorUiInventoryRecord Inventory,
        KotorUiHudRecord Hud,
        IReadOnlyList<KotorUiTextureRecord> Textures);
    private sealed record KotorUiLoadingRecord(
        KotorUiLayoutRecord Layout,
        IReadOnlyList<KotorUiControlRecord> Controls,
        KotorUiTextureRecord Background,
        KotorUiTextureRecord Logo,
        KotorUiTextureRecord Progress,
        string LoadingText,
        int LoadingStrref,
        string HintText,
        int HintStrref,
        string HintsSourceSha256,
        string MusicResref,
        KotorUiAudioRecord Music);
    private sealed record KotorUiInventoryRecord(
        KotorUiLayoutRecord Layout,
        IReadOnlyList<KotorUiControlRecord> Controls,
        KotorUiLayoutRecord TopLayout,
        IReadOnlyList<KotorUiControlRecord> TopControls,
        KotorUiTextureRecord Background,
        KotorUiTextureRecord Portrait,
        IReadOnlyList<KotorUiTextureRecord> PartyPortraits,
        string PartyPortraitsSourceSha256,
        IReadOnlyList<KotorUiInventoryItemRecord> Items);
    private sealed record KotorUiHudRecord(
        KotorUiLayoutRecord Layout,
        IReadOnlyList<KotorUiControlRecord> Controls,
        KotorUiTextureRecord Portrait,
        IReadOnlyList<KotorUiTextureRecord> PartyPortraits,
        KotorUiMinimapRecord Minimap);
    private sealed record KotorUiMinimapRecord(
        KotorUiTextureRecord Texture,
        IReadOnlyList<float> MapPoint1,
        IReadOnlyList<float> MapPoint2,
        IReadOnlyList<float> WorldPoint1,
        IReadOnlyList<float> WorldPoint2,
        int ResolutionX,
        int Zoom,
        int NorthAxis);
    private sealed record KotorUiAudioRecord(
        string Resref,
        string Path,
        string Format,
        string SourceSha256,
        int SourceByteCount,
        string PayloadSha256,
        int ByteCount);
    private sealed record KotorUiInventoryItemRecord(
        string Resref,
        string DisplayName,
        string Description,
        int Cost,
        int BaseItem,
        int EquipableSlots,
        KotorUiTextureRecord Icon,
        string UtiSha256);
    private sealed record KotorUiLayoutRecord(
        string Resref,
        string SourceSha256,
        int SourceByteCount,
        KotorUiExtent Extent,
        KotorUiSurfaceRecord? Border);
    private sealed record KotorUiControlRecord(
        string Tag,
        int Type,
        KotorUiExtent? Extent,
        KotorUiSurfaceRecord? Border,
        KotorUiSurfaceRecord? Highlight,
        KotorUiSurfaceRecord? Progress,
        KotorUiTextRecord? Text,
        bool StartFromLeft,
        int CurrentValue,
        int MaxValue,
        KotorUiControlRecord? Prototype,
        KotorUiControlRecord? Scrollbar);
    private sealed record KotorUiExtent(int Left, int Top, int Width, int Height);
    private sealed record KotorUiSurfaceRecord(
        string Corner,
        string Edge,
        string Fill,
        int FillStyle,
        int Dimension,
        int InnerOffset,
        IReadOnlyList<float>? Color,
        bool Pulsing);
    private sealed record KotorUiTextRecord(
        int Alignment,
        IReadOnlyList<float>? Color,
        string Font,
        string Literal,
        uint Strref,
        string Resolved,
        bool Pulsing);
    private sealed record KotorUiTextureRecord(
        string Resref,
        string Path,
        int Width,
        int Height,
        string SourceSha256,
        int SourceByteCount,
        string SourceType,
        string SourceTxi,
        string PayloadSha256,
        int ByteCount,
        string? BitmapFontPath,
        string? BitmapFontSha256,
        int BitmapFontByteCount,
        int BitmapFontSize,
        int BitmapFontBaseline,
        int BitmapFontNativeSize,
        int BitmapFontGlyphCount);
}
