using Godot;

namespace OpenDAO.Presentation.World;

/// <summary>
/// Recreates DAO's gold interactable silhouette for authored placeables near
/// the center of the gameplay camera. The loader marks exact installed
/// placeable visuals; this controller owns presentation only.
/// </summary>
public partial class PlaceableHighlightController : Node
{
    private const float MaximumDistance = 6.0f;
    private const float MinimumViewDot = 0.45f;
    private readonly List<Node3D> placeables = [];
    private readonly StandardMaterial3D highlightMaterial = CreateHighlightMaterial();
    private Camera3D? camera;
    private Node3D? selected;
    private Label? prompt;
    private ulong feedbackUntilMilliseconds;

    public event Action<Node3D>? UseRequested;

    public void Attach(Camera3D gameplayCamera, Node root)
    {
        camera = gameplayCamera;
        placeables.Clear();
        placeables.AddRange(root.FindChildren("*", "Node3D", true, false)
            .OfType<Node3D>()
            .Where(node => node.HasMeta("dao_placeable") &&
                           node.GetMeta("dao_placeable").AsBool() &&
                           (!node.HasMeta("dao_interactive") || node.GetMeta("dao_interactive").AsBool())));
        EnsurePrompt();
        ProcessMode = ProcessModeEnum.Inherit;
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (prompt is not null && Time.GetTicksMsec() < feedbackUntilMilliseconds) return;
        if (camera is null || !GodotObject.IsInstanceValid(camera) || !camera.Current)
        {
            Select(null);
            return;
        }
        var forward = -camera.GlobalBasis.Z.Normalized();
        Node3D? best = null;
        var bestScore = float.PositiveInfinity;
        foreach (var candidate in placeables)
        {
            if (!GodotObject.IsInstanceValid(candidate) || !candidate.Visible) continue;
            var bounds = SceneBounds.Calculate(candidate);
            var target = candidate.GlobalTransform * (bounds.Size.IsZeroApprox()
                ? Vector3.Up * 0.5f
                : bounds.GetCenter());
            var offset = target - camera.GlobalPosition;
            var distance = offset.Length();
            if (distance <= 0.01f || distance > MaximumDistance) continue;
            var viewDot = forward.Dot(offset / distance);
            if (viewDot < MinimumViewDot) continue;
            var score = distance + (1.0f - viewDot) * 3.0f;
            if (score >= bestScore) continue;
            best = candidate;
            bestScore = score;
        }
        Select(best);
        if (prompt is not null && selected is not null)
        {
            prompt.Text = $"[E] Use — {DisplayName(selected)}";
            prompt.Visible = true;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.E } ||
            selected is null || !GodotObject.IsInstanceValid(selected)) return;
        UseRequested?.Invoke(selected);
        GetViewport().SetInputAsHandled();
    }

    public override void _ExitTree()
    {
        Select(null);
        UseRequested = null;
    }

    public void ShowFeedback(string message, double seconds = 2.5)
    {
        EnsurePrompt();
        if (prompt is null) return;
        prompt.Text = message;
        prompt.Visible = true;
        feedbackUntilMilliseconds = Time.GetTicksMsec() + (ulong)(Math.Max(0.25, seconds) * 1000.0);
    }

    private void Select(Node3D? next)
    {
        if (selected == next) return;
        if (selected is not null && GodotObject.IsInstanceValid(selected)) SetOverlay(selected, null);
        selected = next;
        if (selected is null)
        {
            if (prompt is { } currentPrompt && Time.GetTicksMsec() >= feedbackUntilMilliseconds)
                currentPrompt.Visible = false;
            return;
        }
        SetOverlay(selected, highlightMaterial);
        GD.Print($"OPENDAO_PLACEABLE_HIGHLIGHT status=active " +
                 $"tag={selected.GetMeta("dao_tag").AsString()} " +
                 "source=retail-interactable-silhouette");
    }

    private void EnsurePrompt()
    {
        if (prompt is not null) return;
        var layer = new CanvasLayer { Name = "PlaceableInteractionLayer", Layer = 31 };
        AddChild(layer);
        prompt = new Label
        {
            Name = "PlaceableInteractionPrompt",
            Visible = false,
            Position = new Vector2(-260, -118),
            Size = new Vector2(520, 54),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        prompt.SetAnchorsPreset(Control.LayoutPreset.CenterBottom);
        prompt.AddThemeFontSizeOverride("font_size", 22);
        prompt.AddThemeColorOverride("font_color", new Color(0.96f, 0.86f, 0.68f));
        prompt.AddThemeColorOverride("font_shadow_color", new Color(0.02f, 0.01f, 0.01f));
        prompt.AddThemeConstantOverride("shadow_offset_x", 2);
        prompt.AddThemeConstantOverride("shadow_offset_y", 2);
        layer.AddChild(prompt);
    }

    private static string DisplayName(Node3D target)
    {
        var tag = target.GetMeta("dao_tag").AsString();
        return tag switch
        {
            "bec110ip_pc_possessions" => "Possessions",
            "bec110ip_to_alienage" => "Exit to the Alienage",
            _ => string.Join(' ', tag.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(word => char.ToUpperInvariant(word[0]) + word[1..]))
        };
    }

    private static void SetOverlay(Node root, Material? material)
    {
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
            mesh.MaterialOverlay = material;
    }

    private static StandardMaterial3D CreateHighlightMaterial() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Front,
        AlbedoColor = new Color(1.0f, 0.67f, 0.14f, 0.72f),
        EmissionEnabled = true,
        Emission = new Color(1.0f, 0.48f, 0.06f),
        EmissionEnergyMultiplier = 2.2f,
        Grow = true,
        GrowAmount = 0.018f
    };
}
