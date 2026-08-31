// Matthew W, 2026-08-14

using Godot;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

/// <summary>
/// Clickable presentation of the original DAO MAP pin coordinates.  It is a
/// map surface, not an inferred route graph: only pins with a validated MAP
/// arrival waypoint (or an explicit choice among authored entrances) can be
/// travelled to.
/// </summary>
[Tool]
public partial class WorldMapCanvas : Control
{
    private readonly List<WorldMapMarker> markers = [];
    private Rect2 mapRect;
    private Rect2 mapImageRect;
    private WorldMapMarker? selected;
    private Texture2D? mapTexture;
    private string mapLoadError = string.Empty;
    private float zoom = 1.0f;
    private Vector2 pan = Vector2.Zero;
    private Vector2 dragStart;
    private Vector2 previousPointer;
    private bool pointerDown;
    private bool panning;

    internal event Action<WorldMapMarker>? MarkerSelected;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        ClipContents = true;
        // Package startup smoke validates the executable/pack pairing before
        // user-owned imported data is optionally staged beside it. Do not
        // initialize presentation assets that smoke intentionally omits.
        if (OS.GetEnvironment("OPENDAO_SMOKE_EXIT") == "1")
        {
            return;
        }
        // Keep the launcher visibly useful while its route catalog is being
        // rebuilt or reports an error.  A route load must never turn the map
        // surface into a silent black rectangle.
        mapTexture = LoadMapTexture("wide_open_world", out mapLoadError);
        TooltipText = "Click a gold pin to choose its authored arrival";
        QueueRedraw();
    }

    internal void SetMarkers(string map, IEnumerable<WorldMapMarker> values, WorldMapMarker? selectedMarker)
    {
        markers.Clear();
        markers.AddRange(values);
        selected = selectedMarker;
        mapTexture = LoadMapTexture(map, out mapLoadError);
        zoom = 1.0f;
        pan = Vector2.Zero;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var panel = new Rect2(Vector2.Zero, Size);
        DrawStyleBox(PanelStyle(), panel);
        mapRect = panel.GrowIndividual(-28, -24, -28, -32);
        if (mapRect.Size.X <= 0 || mapRect.Size.Y <= 0)
        {
            return;
        }

        var baseSize = FitMapToViewport(mapRect.Size);
        mapImageRect = new Rect2(mapRect.GetCenter() + pan - baseSize * zoom * 0.5f,
            baseSize * zoom);
        if (mapTexture is not null)
        {
            DrawTextureRect(mapTexture, mapImageRect, false);
        }
        else
        {
            DrawRect(mapRect, new Color(0.07f, 0.08f, 0.07f, 0.96f), true);
            DrawString(ThemeDB.FallbackFont, mapRect.GetCenter() - new Vector2(150, 0),
                mapLoadError, HorizontalAlignment.Center, 300, 14,
                new Color(0.75f, 0.55f, 0.42f));
        }
        DrawRect(mapRect, new Color(0.76f, 0.64f, 0.27f, 0.85f), false, 2.0f);

        foreach (var marker in markers)
        {
            var point = ToCanvas(marker.Position);
            var color = marker.CanTravel
                ? new Color(0.97f, 0.77f, 0.28f)
                : marker.Status == 6 ? new Color(0.48f, 0.24f, 0.22f) : new Color(0.38f, 0.4f, 0.37f);
            var isSelected = marker == selected;
            if (isSelected)
            {
                DrawCircle(point, 12.0f, new Color(color.R, color.G, color.B, 0.22f));
                DrawArc(point, 9.0f, 0.0f, Mathf.Tau, 20, color, 2.0f);
            }
            DrawCircle(point, marker.CanTravel ? 5.5f : 4.0f, color);
            DrawCircle(point, marker.CanTravel ? 2.0f : 1.5f, new Color(0.08f, 0.08f, 0.06f));
        }

        var font = ThemeDB.FallbackFont;
        DrawString(font, mapRect.Position + new Vector2(12, 22),
            "DRAG TO PAN  •  MOUSE WHEEL TO ZOOM", HorizontalAlignment.Left, -1, 13,
            new Color(0.76f, 0.71f, 0.55f, 0.85f));
        if (selected is not null)
        {
            var label = selected.AreaId.ToUpperInvariant();
            DrawString(font, new Vector2(mapRect.Position.X + 12, mapRect.End.Y - 10), label,
                HorizontalAlignment.Left, mapRect.Size.X - 24, 14,
                selected.CanTravel ? new Color(1.0f, 0.84f, 0.44f) : new Color(0.62f, 0.55f, 0.48f));
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton wheel && wheel.Pressed &&
            wheel.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            ZoomAt(wheel.Position, wheel.ButtonIndex == MouseButton.WheelUp ? 1.18f : 1.0f / 1.18f);
            return;
        }

        if (@event is InputEventMouseButton click && click.ButtonIndex == MouseButton.Left)
        {
            if (click.Pressed)
            {
                pointerDown = true;
                panning = false;
                dragStart = previousPointer = click.Position;
                return;
            }

            if (pointerDown && !panning)
            {
                SelectAt(click.Position);
            }
            pointerDown = false;
            panning = false;
            MouseDefaultCursorShape = CursorShape.PointingHand;
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            if (pointerDown)
            {
                if (!panning && motion.Position.DistanceTo(dragStart) >= 4.0f)
                {
                    panning = true;
                    MouseDefaultCursorShape = CursorShape.Drag;
                }
                if (panning)
                {
                    pan += motion.Position - previousPointer;
                    ClampPan();
                    QueueRedraw();
                }
                previousPointer = motion.Position;
                return;
            }

            var nearest = NearestMarker(motion.Position);
            TooltipText = nearest is null ? "Drag to pan • mouse wheel to zoom" :
                nearest.CanTravel ? $"{nearest.AreaId} — click to choose" :
                $"{nearest.AreaId} — no validated imported arrival";
        }
    }

    private Vector2 ToCanvas(Vector2 position)
    {
        const float coordinateWidth = 800.0f;
        // MAPO pins use an 800 x 640 authoring canvas; a few Fade pins sit
        // below y=600, so do not squash them against the lower edge.
        const float coordinateHeight = 640.0f;
        var normalized = new Vector2(
            Mathf.Clamp(position.X / coordinateWidth, 0.0f, 1.0f),
            Mathf.Clamp(position.Y / coordinateHeight, 0.0f, 1.0f));
        return mapRect.GetCenter() + pan + (normalized - Vector2.One * 0.5f) * mapImageRect.Size;
    }

    private WorldMapMarker? NearestMarker(Vector2 point) => markers
        .Select(marker => (Marker: marker, Distance: ToCanvas(marker.Position).DistanceTo(point)))
        .Where(value => value.Distance <= 16.0f)
        .OrderBy(value => value.Distance)
        .Select(value => value.Marker)
        .FirstOrDefault();

    private void SelectAt(Vector2 point)
    {
        var nearest = NearestMarker(point);
        if (nearest is null)
        {
            return;
        }

        selected = nearest;
        MarkerSelected?.Invoke(nearest);
        QueueRedraw();
    }

    private void ZoomAt(Vector2 pointer, float factor)
    {
        var before = zoom;
        zoom = Mathf.Clamp(zoom * factor, 1.0f, 4.0f);
        if (Mathf.IsEqualApprox(before, zoom))
        {
            return;
        }

        pan += (pointer - mapRect.GetCenter() - pan) * (zoom / before - 1.0f);
        ClampPan();
        QueueRedraw();
    }

    private void ClampPan()
    {
        var maximum = FitMapToViewport(mapRect.Size) * (zoom - 1.0f) * 0.5f;
        pan = new Vector2(Mathf.Clamp(pan.X, -maximum.X, maximum.X),
            Mathf.Clamp(pan.Y, -maximum.Y, maximum.Y));
    }

    private Vector2 FitMapToViewport(Vector2 available)
    {
        if (mapTexture is null || mapTexture.GetHeight() <= 0)
        {
            return available;
        }

        var textureAspect = (float)mapTexture.GetWidth() / mapTexture.GetHeight();
        var viewportAspect = available.X / available.Y;
        return viewportAspect > textureAspect
            ? new Vector2(available.Y * textureAspect, available.Y)
            : new Vector2(available.X, available.X / textureAspect);
    }

    private static Texture2D? LoadMapTexture(string map, out string error)
    {
        // Imported map art is a sidecar of the selected campaign catalog. It
        // must not depend on a developer checkout or on res:// surviving a
        // PCK export with user-owned source data excluded.
        var sidecarPath = AreaCatalog.ResolveWorldMapArtworkPath(map);
        if (File.Exists(sidecarPath))
        {
            var image = Image.LoadFromFile(sidecarPath);
            if (!image.IsEmpty())
            {
                var sidecarTexture = ImageTexture.CreateFromImage(image);
                error = string.Empty;
                GD.Print("OPENDAO_WORLD_MAP_ART map=" + map + " path=" + sidecarPath +
                         " size=" + sidecarTexture.GetWidth() + "x" + sidecarTexture.GetHeight());
                return sidecarTexture;
            }
        }

        // Keep existing developer caches usable while Import-DAO-All is
        // upgraded. This fallback is never used as a package-data contract.
        var resourcePath = $"res://assets/generated/worldmaps/{map}.png";
        // ResourceLoader.Load logs an engine ERROR for an excluded PCK asset.
        // Probe first so a deliberately data-less renderer package reports a
        // clear import requirement instead of failing its own startup smoke.
        var texture = ResourceLoader.Exists(resourcePath)
            ? ResourceLoader.Load<Texture2D>(resourcePath)
            : null;
        if (texture is not null)
        {
            error = string.Empty;
            GD.Print("OPENDAO_WORLD_MAP_ART map=" + map + " path=" + resourcePath +
                     " size=" + texture.GetWidth() + "x" + texture.GetHeight());
            return texture;
        }

        error = "Map artwork missing: " + map + " (import the catalog sidecar)";
        GD.PushWarning("Nikami.Aurora.GodotRuntime: " + error + " path=" + sidecarPath);
        return null;
    }

    private static StyleBoxFlat PanelStyle() => new()
    {
        BgColor = new Color(0.02f, 0.025f, 0.02f, 0.96f),
        BorderColor = new Color(0.44f, 0.38f, 0.25f, 0.9f),
        BorderWidthLeft = 1,
        BorderWidthTop = 1,
        BorderWidthRight = 1,
        BorderWidthBottom = 1,
        CornerRadiusTopLeft = 3,
        CornerRadiusTopRight = 3,
        CornerRadiusBottomLeft = 3,
        CornerRadiusBottomRight = 3,
    };
}
