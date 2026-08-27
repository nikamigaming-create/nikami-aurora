using Godot;
using OpenDAO.Infrastructure.World;

namespace OpenDAO.MainMenu;

[Tool]
public partial class CharacterCreationPanel : Control
{
    private static readonly Color Gold = new(0.93f, 0.82f, 0.55f);
    private static readonly Color Parchment = new(0.86f, 0.82f, 0.72f);
    private static readonly Vector2 AuthoredSize = new(1024, 768);

    private Control stage = null!;
    private Control identityStage = null!;
    private Control appearanceStage = null!;
    private Label status = null!;
    private Label descriptionTitle = null!;
    private Label descriptionBody = null!;
    private Label appearanceSummary = null!;
    private LineEdit nameInput = null!;
    private CharacterPreviewViewport preview = null!;
    private CharacterCreationArtwork artwork = null!;
    private AuthoredChoiceStrip genderStrip = null!;
    private AuthoredChoiceStrip raceStrip = null!;
    private AuthoredChoiceStrip classStrip = null!;
    private AuthoredChoiceStrip originStrip = null!;
    private AuthoredChoiceStrip appearanceStrip = null!;
    private Button playButton = null!;
    private readonly List<Button> navigationButtons = [];
    private FontFile? font;
    private bool built;
    private int artworkAssetCount;

    internal event Action<CharacterProfile>? StartRequested;
    public event Action? Cancelled;

    public override void _Ready()
    {
        stage = GetNode<Control>("Stage");
        identityStage = stage.GetNode<Control>("IdentityStage");
        appearanceStage = stage.GetNode<Control>("AppearanceStage");
        status = stage.GetNode<Label>("Status");
        preview = new CharacterPreviewViewport(
            new DaoCharacterMaterialPostprocessor())
        {
            Name = "LiveCharacterPreview"
        };
        var previewHost = stage.GetNode<Control>("PreviewHost");
        previewHost.AddChild(preview);
        preview.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized += LayoutAuthoredStage;
        LayoutAuthoredStage();
    }

    public void Build(string archivePath, FontFile? gameFont)
    {
        if (built) return;
        built = true;
        font = gameFont;
        artwork = CharacterCreationArtwork.Open(archivePath);
        stage.GetNode<TextureRect>("Background").Texture =
            artwork.Texture("dxt1_chargen_temp_back.dds");
        identityStage.AddChild(artwork.CreateScreen("RaceGender"));
        appearanceStage.AddChild(artwork.CreateScreen("SoundsetAppearance"));
        BuildIdentityStage();
        BuildAppearanceStage();
        ApplyFont(status, 14);

        var required = CharacterProfileRules.Genders.Select(value => value.Icon)
            .Concat(CharacterProfileRules.Classes.Select(value => value.Icon))
            .Concat(new[]
            {
                "cg_ico_race_female_human.dds", "cg_ico_race_male_human.dds",
                "cg_ico_race_female_elf.dds", "cg_ico_race_male_elf.dds",
                "cg_ico_race_female_dwarf.dds", "cg_ico_race_male_dwarf.dds",
                "cg_ico_origin_human_noble.dds", "cg_ico_origin_elf_city.dds",
                "cg_ico_origin_elf_dalish.dds", "cg_ico_origin_dwarf_common.dds",
                "cg_ico_origin_dwarf_noble.dds", "cg_ico_origin_mage.dds",
                "CharGen_I278.dds", "CharGen_I27D.dds", "CharGen_I280.dds",
                "CharGen_I2CC.dds", "Login_I62.dds", "dxt1_chargen_temp_back.dds"
            });
        artworkAssetCount = artwork.CountAvailable(required);
        GD.Print($"OPENDAO_CHARGEN_AUTHORED_UI assets={artworkAssetCount} " +
                 $"gender_icons={CharacterProfileRules.Genders.Count} race_icons=6 " +
                 $"class_icons={CharacterProfileRules.Classes.Count} origin_icons=6");
    }

    public void Open()
    {
        Visible = true;
        SetBusy(false);
        status.Text = string.Empty;
        identityStage.Visible = true;
        appearanceStage.Visible = false;
        RefreshOrigins();
        RefreshDescription();
        RefreshPreview();
    }

    public void SetStatus(string message, bool isError = true)
    {
        status.Text = message;
        status.AddThemeColorOverride("font_color", isError
            ? new Color(1.0f, 0.48f, 0.42f)
            : new Color(0.72f, 0.9f, 0.58f));
    }

    public void SetBusy(bool busy)
    {
        if (!built) return;
        foreach (var strip in new[] { genderStrip, raceStrip, classStrip, originStrip, appearanceStrip })
            strip.SetBusy(busy);
        foreach (var button in navigationButtons) button.Disabled = busy;
        nameInput.Editable = !busy;
        playButton.Text = busy ? "STARTING…" : "PLAY";
    }

    internal bool ConfigureForAcceptance(CharacterProfile profile)
    {
        genderStrip.SelectById(profile.Gender);
        RebuildRaceIcons();
        raceStrip.SelectById(profile.Race);
        classStrip.SelectById(profile.CharacterClass);
        RefreshOrigins();
        originStrip.SelectById(profile.Origin);
        appearanceStrip.SelectById(profile.Appearance);
        nameInput.Text = profile.Name;
        RefreshDescription();
        RefreshPreview();
        return artworkAssetCount >= 20 && preview.CurrentModelPath.Length > 0;
    }

    internal void AdvanceForAcceptance() => ShowAppearance();

    internal void SubmitForAcceptance() => RequestStart();

    internal int ArtworkQuadCount => artworkAssetCount;

    internal string PreviewModelPath => preview.CurrentModelPath;

    internal string CurrentStage => appearanceStage.Visible ? "appearance" : "identity";

    private void BuildIdentityStage()
    {
        AddHeader(identityStage, "CHOOSE YOUR HERO", "Select gender, race, class, and background.");
        genderStrip = AddChoiceRow(identityStage, "GENDER", 80);
        raceStrip = AddChoiceRow(identityStage, "RACE", 183);
        classStrip = AddChoiceRow(identityStage, "CLASS", 286);
        originStrip = AddChoiceRow(identityStage, "BACKGROUND", 389);

        genderStrip.SetChoices(CharacterProfileRules.Genders);
        RebuildRaceIcons();
        classStrip.SetChoices(CharacterProfileRules.Classes);
        RefreshOrigins();

        genderStrip.SelectionChanged += () =>
        {
            RebuildRaceIcons();
            RefreshDescription();
            RefreshPreview();
        };
        raceStrip.SelectionChanged += () =>
        {
            if (raceStrip.Selected.Id == "dwarf" && classStrip.Selected.Id == "mage")
                classStrip.SelectById("warrior");
            RefreshOrigins();
            RefreshDescription();
            RefreshPreview();
        };
        classStrip.SelectionChanged += () =>
        {
            if (classStrip.Selected.Id == "mage" && raceStrip.Selected.Id == "dwarf")
                raceStrip.SelectById("human");
            RefreshOrigins();
            RefreshDescription();
            RefreshPreview();
        };
        originStrip.SelectionChanged += RefreshDescription;

        AddDescriptionPanel(identityStage);
        AddNavigationButton(identityStage, "PREVIOUS",
            artwork.InstancePosition("RaceGender", "previous_mc"), () => Cancelled?.Invoke());
        AddNavigationButton(identityStage, "NEXT",
            artwork.InstancePosition("RaceGender", "next_mc"), ShowAppearance);
    }

    private void BuildAppearanceStage()
    {
        AddHeader(appearanceStage, "CUSTOMIZE APPEARANCE", "Choose a preset and name your character.");
        appearanceStrip = AddChoiceRow(appearanceStage, "PRESET", 112, 620, 490);
        appearanceStrip.SetChoices(CharacterProfileRules.Appearances);
        appearanceStrip.SelectionChanged += () =>
        {
            RefreshPreview();
            RefreshAppearanceSummary();
        };

        var nameLabel = AddLabel(appearanceStage, "NAME", new Vector2(617, 330), new Vector2(92, 34), 18, Gold);
        nameLabel.VerticalAlignment = VerticalAlignment.Center;
        nameInput = new LineEdit
        {
            Name = "NameInput",
            Text = "Warden",
            MaxLength = 32,
            Position = new Vector2(713, 330),
            Size = new Vector2(280, 38),
            PlaceholderText = "Warden",
            SelectAllOnFocus = true
        };
        ApplyFont(nameInput, 18);
        var field = new StyleBoxFlat
        {
            BgColor = new Color(0.025f, 0.02f, 0.015f, 0.92f),
            BorderColor = new Color(0.72f, 0.58f, 0.3f),
            BorderWidthLeft = 1,
            BorderWidthTop = 1,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ContentMarginLeft = 10,
            ContentMarginRight = 10
        };
        nameInput.AddThemeStyleboxOverride("normal", field);
        nameInput.AddThemeStyleboxOverride("focus", field);
        nameInput.AddThemeColorOverride("font_color", Parchment);
        nameInput.TextChanged += _ => RefreshAppearanceSummary();
        appearanceStage.AddChild(nameInput);

        AddTexture(appearanceStage, "CharGen_I2CC.dds", new Vector2(600, 445), new Vector2(400, 193));
        appearanceSummary = AddLabel(appearanceStage, string.Empty, new Vector2(640, 505),
            new Vector2(320, 82), 17, Parchment);
        appearanceSummary.HorizontalAlignment = HorizontalAlignment.Center;
        appearanceSummary.VerticalAlignment = VerticalAlignment.Center;
        appearanceSummary.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        AddNavigationButton(appearanceStage, "PREVIOUS",
            artwork.InstancePosition("SoundsetAppearance", "previous_mc"), ShowIdentity);
        playButton = AddNavigationButton(appearanceStage, "PLAY",
            artwork.InstancePosition("SoundsetAppearance", "next_mc"), RequestStart);
        RefreshAppearanceSummary();
    }

    private AuthoredChoiceStrip AddChoiceRow(Control parent, string title, float y, float stripX = 717,
        float labelX = 590)
    {
        var rowLabel = AddLabel(parent, title, new Vector2(labelX, y + 25), new Vector2(120, 40), 16, Gold);
        rowLabel.HorizontalAlignment = HorizontalAlignment.Right;
        var strip = new AuthoredChoiceStrip(artwork, font)
        {
            Name = title[..1] + title[1..].ToLowerInvariant() + "Choices",
            Position = new Vector2(stripX, y),
            Size = new Vector2(390, 92)
        };
        parent.AddChild(strip);
        return strip;
    }

    private void AddHeader(Control parent, string title, string subtitle)
    {
        var header = AddLabel(parent, title, new Vector2(617, 22), new Vector2(380, 38), 27, Gold);
        header.HorizontalAlignment = HorizontalAlignment.Center;
        var hint = AddLabel(parent, subtitle, new Vector2(617, 55), new Vector2(380, 28), 14, Parchment);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
    }

    private void AddDescriptionPanel(Control parent)
    {
        AddTexture(parent, "CharGen_I2CC.dds", new Vector2(600, 475), new Vector2(400, 193));
        descriptionTitle = AddLabel(parent, string.Empty, new Vector2(640, 499), new Vector2(320, 32), 21, Gold);
        descriptionTitle.HorizontalAlignment = HorizontalAlignment.Center;
        descriptionBody = AddLabel(parent, string.Empty, new Vector2(638, 535), new Vector2(324, 102), 14, Parchment);
        descriptionBody.HorizontalAlignment = HorizontalAlignment.Center;
        descriptionBody.VerticalAlignment = VerticalAlignment.Center;
        descriptionBody.AutowrapMode = TextServer.AutowrapMode.WordSmart;
    }

    private Button AddNavigationButton(Control parent, string text, Vector2 position, Action pressed)
    {
        var button = new Button
        {
            Name = parent.Name + text[..1] + text[1..].ToLowerInvariant() + "Button",
            Text = text,
            Position = position,
            Size = new Vector2(257, 46),
            ZIndex = 20,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand
        };
        var plate = new StyleBoxTexture { Texture = artwork.Texture("Login_I62.dds") };
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled", "hover_pressed" })
            button.AddThemeStyleboxOverride(state, plate);
        ApplyFont(button, 17);
        button.AddThemeColorOverride("font_color", Parchment);
        button.AddThemeColorOverride("font_hover_color", Colors.White);
        button.AddThemeColorOverride("font_pressed_color", Gold);
        button.AddThemeColorOverride("font_outline_color", Colors.Black);
        button.AddThemeConstantOverride("outline_size", 3);
        button.Pressed += pressed;
        parent.AddChild(button);
        navigationButtons.Add(button);
        return button;
    }

    private TextureRect AddTexture(Control parent, string resource, Vector2 position, Vector2 size)
    {
        var texture = new TextureRect
        {
            Name = resource.Replace('.', '_'),
            Texture = artwork.Texture(resource),
            Position = position,
            Size = size,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        };
        parent.AddChild(texture);
        return texture;
    }

    private Label AddLabel(Control parent, string text, Vector2 position, Vector2 size, int fontSize, Color color)
    {
        var label = new Label { Text = text, Position = position, Size = size, MouseFilter = MouseFilterEnum.Ignore };
        ApplyFont(label, fontSize);
        label.AddThemeColorOverride("font_color", color);
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 3);
        parent.AddChild(label);
        return label;
    }

    private void ApplyFont(Control control, int size)
    {
        if (font is not null) control.AddThemeFontOverride("font", font);
        control.AddThemeFontSizeOverride("font_size", size);
    }

    private void RebuildRaceIcons()
    {
        if (raceStrip is null) return;
        var selected = raceStrip.Selected.Id;
        var choices = CharacterProfileRules.Races.Select(choice => choice with
        {
            Icon = CharacterProfileRules.RaceIcon(choice.Id, genderStrip.Selected.Id)
        });
        raceStrip.SetChoices(choices, selected.Length == 0 ? "human" : selected);
    }

    private void RefreshOrigins()
    {
        if (originStrip is null || raceStrip is null || classStrip is null) return;
        var selected = originStrip.Selected.Id;
        var origins = CharacterProfileRules.OriginsFor(raceStrip.Selected.Id, classStrip.Selected.Id)
            .Select(origin => new CharacterChoice(origin.Id, origin.Label, origin.Icon, origin.Description));
        originStrip.SetChoices(origins, selected);
    }

    private void RefreshDescription()
    {
        if (descriptionTitle is null || originStrip is null || originStrip.Selected.Id.Length == 0) return;
        descriptionTitle.Text = originStrip.Selected.Label.ToUpperInvariant();
        descriptionBody.Text = originStrip.Selected.Description;
    }

    private void RefreshAppearanceSummary()
    {
        if (appearanceSummary is null || nameInput is null) return;
        var chosenName = string.IsNullOrWhiteSpace(nameInput.Text) ? "Your Warden" : nameInput.Text.Trim();
        appearanceSummary.Text = $"{chosenName}\n{raceStrip.Selected.Label} {classStrip.Selected.Label}  •  " +
                                 originStrip.Selected.Label;
    }

    private void RefreshPreview()
    {
        if (preview is null || !IsInstanceValid(preview) || appearanceStrip is null) return;
        preview.ShowCharacter(raceStrip.Selected.Id, genderStrip.Selected.Id, appearanceStrip.Selected.Id);
    }

    private void ShowIdentity()
    {
        appearanceStage.Visible = false;
        identityStage.Visible = true;
        status.Text = string.Empty;
    }

    private void ShowAppearance()
    {
        identityStage.Visible = false;
        appearanceStage.Visible = true;
        status.Text = string.Empty;
        RefreshAppearanceSummary();
        nameInput.GrabFocus();
        nameInput.SelectAll();
    }

    private void RequestStart()
    {
        var profile = CharacterProfile.Create(
            nameInput.Text,
            originStrip.Selected.Id,
            raceStrip.Selected.Id,
            genderStrip.Selected.Id,
            classStrip.Selected.Id,
            appearanceStrip.Selected.Id);
        if (!CharacterProfileRules.Validate(profile, out var error))
        {
            SetStatus(error);
            return;
        }
        StartRequested?.Invoke(profile);
    }

    private void LayoutAuthoredStage()
    {
        if (stage is null) return;
        var scale = Math.Min(Size.X / AuthoredSize.X, Size.Y / AuthoredSize.Y);
        if (!float.IsFinite(scale) || scale <= 0) scale = 1;
        stage.Scale = Vector2.One * scale;
        stage.Position = (Size - AuthoredSize * scale) * 0.5f;
    }
}
