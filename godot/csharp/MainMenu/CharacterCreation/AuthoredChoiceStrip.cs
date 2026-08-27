using Godot;

namespace OpenDAO.MainMenu;

internal sealed partial class AuthoredChoiceStrip : Control
{
    private readonly List<Button> buttons = [];
    private readonly List<CharacterChoice> choices = [];
    private readonly CharacterCreationArtwork artwork;
    private readonly Font? font;
    private readonly Texture2D? normalFrame;
    private readonly Texture2D? hoverFrame;
    private readonly Texture2D? selectedFrame;
    private bool busy;

    internal event Action? SelectionChanged;

    internal int SelectedIndex { get; private set; }

    internal CharacterChoice Selected => choices.Count == 0
        ? new CharacterChoice(string.Empty, string.Empty, string.Empty)
        : choices[Math.Clamp(SelectedIndex, 0, choices.Count - 1)];

    internal AuthoredChoiceStrip(CharacterCreationArtwork artwork, Font? font)
    {
        this.artwork = artwork;
        this.font = font;
        normalFrame = artwork.Texture("CharGen_I278.dds");
        hoverFrame = artwork.Texture("CharGen_I27D.dds") ?? normalFrame;
        selectedFrame = artwork.Texture("CharGen_I280.dds") ?? hoverFrame;
        CustomMinimumSize = new Vector2(390, 92);
    }

    internal void SetChoices(IEnumerable<CharacterChoice> values, string selectedId = "")
    {
        foreach (var button in buttons) button.QueueFree();
        buttons.Clear();
        choices.Clear();
        choices.AddRange(values);
        SelectedIndex = Math.Max(0, choices.FindIndex(choice =>
            choice.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase)));

        for (var index = 0; index < choices.Count; index++)
        {
            var choiceIndex = index;
            var button = BuildButton(choices[index], index);
            button.Pressed += () => Select(choiceIndex, true);
            AddChild(button);
            buttons.Add(button);
        }
        RefreshFrames();
    }

    internal void SelectById(string id, bool emit = false)
    {
        var index = choices.FindIndex(choice => choice.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) Select(index, emit);
    }

    internal void SetBusy(bool value)
    {
        busy = value;
        foreach (var button in buttons) button.Disabled = value;
    }

    private Button BuildButton(CharacterChoice choice, int index)
    {
        var button = new Button
        {
            Name = "Choice_" + choice.Id.Replace('-', '_'),
            Position = new Vector2(index * 96, 0),
            Size = new Vector2(90, 90),
            TooltipText = choice.Label,
            FocusMode = FocusModeEnum.None,
            MouseDefaultCursorShape = CursorShape.PointingHand,
            ClipContents = true,
            Disabled = busy
        };
        foreach (var state in new[] { "normal", "hover", "pressed", "focus", "disabled", "hover_pressed" })
            button.AddThemeStyleboxOverride(state, new StyleBoxEmpty());

        var frame = new TextureRect
        {
            Name = "AuthoredIcon",
            Texture = artwork.Texture(choice.Icon),
            Position = new Vector2(16, 8),
            Size = new Vector2(58, 58),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        button.AddChild(frame);
        button.MouseEntered += () =>
        {
            if (index != SelectedIndex) frame.Texture = hoverFrame;
        };
        button.MouseExited += () =>
        {
            frame.Texture = index == SelectedIndex ? selectedFrame : normalFrame;
        };
        button.AddChild(new TextureRect
        {
            Name = "AuthoredFrame",
            Texture = normalFrame,
            Position = Vector2.Zero,
            Size = new Vector2(90, 90),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        });
        var label = new Label
        {
            Name = "ChoiceLabel",
            Text = choice.Label.ToUpperInvariant(),
            Position = new Vector2(-3, 68),
            Size = new Vector2(96, 22),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore
        };
        label.AddThemeFontSizeOverride("font_size", 12);
        label.AddThemeColorOverride("font_color", new Color(0.92f, 0.88f, 0.73f));
        label.AddThemeColorOverride("font_outline_color", Colors.Black);
        label.AddThemeConstantOverride("outline_size", 3);
        if (font is not null) label.AddThemeFontOverride("font", font);
        button.AddChild(label);
        return button;
    }

    private void Select(int index, bool emit)
    {
        if (index < 0 || index >= choices.Count || SelectedIndex == index) return;
        SelectedIndex = index;
        RefreshFrames();
        if (emit) SelectionChanged?.Invoke();
    }

    private void RefreshFrames()
    {
        for (var index = 0; index < buttons.Count; index++)
        {
            var frame = buttons[index].GetNode<TextureRect>("AuthoredFrame");
            frame.Texture = index == SelectedIndex ? selectedFrame : normalFrame;
        }
    }
}
