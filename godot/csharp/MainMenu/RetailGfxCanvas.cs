using Godot;
using Nikami.Aurora.GodotRuntime.Infrastructure.Archives;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

internal enum RetailGfxAnchor
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

internal enum RetailGfxScaleMode
{
    Hud,
    FitStage
}

internal readonly record struct RetailGfxPlacement(float Scale, Vector2 Offset)
{
    internal Vector2 Point(Vector2 reference) => (reference + Offset) * Scale;

    internal Vector2 Size(Vector2 reference) => reference * Scale;
}

internal static class RetailGfxLayout
{
    // DAO keeps the individual Scaleform movies in their authored 1024x768
    // coordinate space, but scales the composed HUD relative to a 1080p
    // presentation surface. At 1280x720 this is exactly 2/3, matching the
    // retail oracle; the remaining virtual width is used by the movie anchor.
    internal const float ReferenceHeight = 1080.0f;

    internal static RetailGfxPlacement Resolve(
        Vector2 viewport,
        GfxRect stage,
        RetailGfxAnchor anchor)
    {
        var scale = Math.Max(0.0001f,
            Math.Min(1.0f, viewport.Y / ReferenceHeight));
        var virtualViewport = viewport / Math.Max(0.0001f, scale);
        var horizontal = anchor switch
        {
            RetailGfxAnchor.TopCenter or RetailGfxAnchor.BottomCenter =>
                (virtualViewport.X - (float)stage.Width) * 0.5f,
            RetailGfxAnchor.TopRight or RetailGfxAnchor.BottomRight =>
                virtualViewport.X - (float)stage.Width,
            _ => 0.0f
        };
        var vertical = anchor switch
        {
            RetailGfxAnchor.BottomLeft or RetailGfxAnchor.BottomCenter or
                RetailGfxAnchor.BottomRight => virtualViewport.Y - (float)stage.Height,
            _ => 0.0f
        };
        return new RetailGfxPlacement(scale, new Vector2(horizontal, vertical));
    }

    internal static RetailGfxPlacement FitStage(Vector2 viewport, GfxRect stage)
    {
        var stageWidth = Math.Max(0.0001f, (float)stage.Width);
        var stageHeight = Math.Max(0.0001f, (float)stage.Height);
        var scale = Math.Max(0.0001f,
            Math.Min(viewport.X / stageWidth, viewport.Y / stageHeight));
        var virtualViewport = viewport / scale;
        var offset = new Vector2(
            (virtualViewport.X - stageWidth) * 0.5f - (float)stage.XMin,
            (virtualViewport.Y - stageHeight) * 0.5f - (float)stage.YMin);
        return new RetailGfxPlacement(scale, offset);
    }

    internal static void Place(
        Control control,
        Rect2 reference,
        Vector2 viewport,
        GfxRect stage,
        RetailGfxAnchor anchor)
    {
        var placement = Resolve(viewport, stage, anchor);
        control.Position = placement.Point(reference.Position);
        control.Size = placement.Size(reference.Size);
    }
}

internal sealed partial class RetailGfxCanvas : Control
{
    private readonly GfxRect stage;
    private readonly RetailGfxAnchor anchor;
    private readonly bool authoredCoordinates;
    private readonly RetailGfxScaleMode scaleMode;
    private readonly List<DrawCommand> commands;

    internal RetailGfxCanvas(
        string name,
        ErfArchive archive,
        GfxAtlas atlas,
        string movieResource,
        RetailGfxAnchor anchor,
        Func<GfxQuad, GfxQuad?>? select = null,
        Func<GfxQuad, Rect2?>? sourceRegion = null,
        string rootLabel = "",
        bool authoredCoordinates = false,
        RetailGfxScaleMode scaleMode = RetailGfxScaleMode.Hud,
        int advanceFrames = 1)
    {
        Name = name;
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        TextureFilter = TextureFilterEnum.Linear;
        this.anchor = anchor;
        this.authoredCoordinates = authoredCoordinates;
        this.scaleMode = scaleMode;

        var movie = GfxMovie.Read(archive.Read(movieResource));
        stage = movie.Stage;
        var player = new GfxPlayer(movie);
        if (rootLabel.Length > 0)
        {
            if (!player.SeekRoot(rootLabel))
                throw new InvalidDataException($"GFx root label is absent: {movieResource}:{rootLabel}");
        }
        else
        {
            for (var frame = 0; frame < Math.Max(1, advanceFrames); frame++)
                player.Advance();
        }
        RootFrameCount = movie.Root.Frames.Count;
        RootLabels = movie.Root.Labels.Keys.Order(StringComparer.Ordinal).ToArray();
        commands = [];
        foreach (var source in player.Collect())
        {
            var selected = select is null ? source : select(source);
            if (selected is null || atlas.Load(selected.Image.Name) is not { } texture)
            {
                continue;
            }

            var region = sourceRegion?.Invoke(selected) ??
                         new Rect2(Vector2.Zero, selected.Image.Width, selected.Image.Height);
            if (region.Size.X <= 0 || region.Size.Y <= 0)
            {
                continue;
            }

            commands.Add(new DrawCommand(
                texture,
                region,
                BitmapTransform(selected.Fill).Concat(selected.Transform),
                selected.Alpha));
        }

        Resized += QueueRedraw;
    }

    internal int QuadCount => commands.Count;

    internal Vector2 StageSize => new((float)stage.Width, (float)stage.Height);

    internal int RootFrameCount { get; }

    internal IReadOnlyList<string> RootLabels { get; }

    public override void _Draw()
    {
        var placement = authoredCoordinates
            ? new RetailGfxPlacement(1.0f, Vector2.Zero)
            : scaleMode == RetailGfxScaleMode.FitStage
                ? RetailGfxLayout.FitStage(Size, stage)
                : RetailGfxLayout.Resolve(Size, stage, anchor);
        var placementMatrix = new GfxMatrix(
            placement.Scale, 0, 0, placement.Scale,
            placement.Offset.X * placement.Scale,
            placement.Offset.Y * placement.Scale);
        foreach (var command in commands)
        {
            DrawSetTransformMatrix(ToGodot(command.Transform.Concat(placementMatrix)));
            DrawTextureRectRegion(
                command.Texture,
                new Rect2(Vector2.Zero, command.Source.Size),
                command.Source,
                new Color(1, 1, 1, command.Alpha));
        }

        DrawSetTransformMatrix(Transform2D.Identity);
    }

    private static GfxMatrix BitmapTransform(GfxMatrix fill) => new(
        fill.ScaleX / 20.0,
        fill.RotateSkew0 / 20.0,
        fill.RotateSkew1 / 20.0,
        fill.ScaleY / 20.0,
        fill.TranslateX,
        fill.TranslateY);

    private static Transform2D ToGodot(GfxMatrix value) => new(
        new Vector2((float)value.ScaleX, (float)value.RotateSkew0),
        new Vector2((float)value.RotateSkew1, (float)value.ScaleY),
        new Vector2((float)value.TranslateX, (float)value.TranslateY));

    private sealed record DrawCommand(
        Texture2D Texture,
        Rect2 Source,
        GfxMatrix Transform,
        float Alpha);
}
