using System.Text.Json;
using Godot;
using OpenDAO.Infrastructure.Archives;
using OpenDAO.MainMenu;

namespace OpenDAO.Presentation.World;

internal sealed record DaoLoadingArtworkRule(
    string AreaPrefix,
    string Resource);

internal sealed record DaoLoadingPresentationProfile(
    int SchemaVersion,
    string ArchiveRelativePath,
    string MovieResource,
    string[] AtlasMaps,
    string ScaleMode,
    int AdvanceFrames,
    string ArtworkArchiveRelativePath,
    string ArtworkScaleMode,
    DaoLoadingArtworkRule[] ArtworkRules,
    int Layer,
    float[] BackgroundColor);

/// <summary>
/// Owns the opaque, retail-authored presentation shown while an area and its
/// opening cinematic are being assembled. No retail bytes enter the project:
/// the configured GFx movie and atlases are read from the selected installation.
/// </summary>
internal sealed class DaoLoadingPresentation
{
    internal const string ProfileResource = "res://config/dao/ui/loading.json";
    internal const string CaptureEnvironmentVariable = "OPENDAO_LOADING_CAPTURE";
    internal const string CaptureExitEnvironmentVariable = "OPENDAO_LOADING_CAPTURE_EXIT";
    internal const string AdvanceFramesEnvironmentVariable = "OPENDAO_LOADING_ADVANCE_FRAMES";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Node host;
    private readonly CanvasLayer layer;
    private bool hidden;

    private DaoLoadingPresentation(Node host, CanvasLayer layer)
    {
        this.host = host;
        this.layer = layer;
    }

    internal static DaoLoadingPresentation Show(Node host, string gameRoot, string areaId)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        var profilePath = ProjectSettings.GlobalizePath(ProfileResource);
        var profile = JsonSerializer.Deserialize<DaoLoadingPresentationProfile>(
            File.ReadAllText(profilePath), JsonOptions)
            ?? throw new InvalidDataException("DAO loading profile is empty.");
        Validate(profile);

        var normalizedRoot = Path.GetFullPath(gameRoot);
        var archivePath = ResolveUnderRoot(normalizedRoot, profile.ArchiveRelativePath);
        var archive = ErfArchive.Open(archivePath);
        if (!archive.Contains(profile.MovieResource))
            throw new InvalidDataException(
                $"DAO loading movie is absent: {profile.MovieResource}");
        foreach (var atlasMap in profile.AtlasMaps)
            if (!archive.Contains(atlasMap))
                throw new InvalidDataException($"DAO loading atlas map is absent: {atlasMap}");

        var atlas = new GfxAtlas(archive, profile.AtlasMaps);
        var scaleMode = profile.ScaleMode.Equals("fit-stage",
            StringComparison.OrdinalIgnoreCase)
            ? RetailGfxScaleMode.FitStage
            : throw new InvalidDataException(
                $"Unsupported DAO loading scale mode: {profile.ScaleMode}");
        var advanceFrames = ResolveAdvanceFrames(profile.AdvanceFrames);
        var canvas = new RetailGfxCanvas(
            "RetailLoadingMovie",
            archive,
            atlas,
            profile.MovieResource,
            RetailGfxAnchor.TopLeft,
            scaleMode: scaleMode,
            advanceFrames: advanceFrames);
        if (canvas.QuadCount == 0)
            throw new InvalidDataException(
                $"DAO loading movie produced no drawable quads: {profile.MovieResource}");

        var layer = new CanvasLayer
        {
            Name = "DaoLoadingPresentation",
            Layer = profile.Layer
        };
        var background = new ColorRect
        {
            Name = "OpaqueBackground",
            Color = new Color(
                profile.BackgroundColor[0],
                profile.BackgroundColor[1],
                profile.BackgroundColor[2],
                profile.BackgroundColor[3]),
            MouseFilter = Control.MouseFilterEnum.Stop
        };
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(background);
        layer.AddChild(canvas);
        var artworkResource = ResolveArtwork(profile, areaId);
        if (artworkResource.Length > 0)
        {
            var artworkArchivePath = ResolveUnderRoot(
                normalizedRoot, profile.ArtworkArchiveRelativePath);
            var artworkArchive = ErfArchive.Open(artworkArchivePath);
            if (!artworkArchive.Contains(artworkResource))
                throw new InvalidDataException(
                    $"DAO loading artwork is absent: {artworkResource}");
            var image = new Image();
            var imageError = image.LoadDdsFromBuffer(artworkArchive.Read(artworkResource));
            if (imageError != Error.Ok || image.IsEmpty())
                throw new InvalidDataException(
                    $"DAO loading artwork could not be decoded: {artworkResource} ({imageError})");
            var artwork = new TextureRect
            {
                Name = "AreaLoadingArtwork",
                Texture = ImageTexture.CreateFromImage(image),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = profile.ArtworkScaleMode.Equals("cover",
                    StringComparison.OrdinalIgnoreCase)
                    ? TextureRect.StretchModeEnum.KeepAspectCovered
                    : throw new InvalidDataException(
                        $"Unsupported DAO loading artwork scale mode: " +
                        profile.ArtworkScaleMode),
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            artwork.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            layer.AddChild(artwork);
        }
        host.AddChild(layer);
        GD.Print($"OPENDAO_RETAIL_LOADING_SCREEN status=ready " +
                 $"source={profile.MovieResource} archive=installed-guiexport " +
                 $"quads={canvas.QuadCount} stage={canvas.StageSize.X:0.#}x{canvas.StageSize.Y:0.#} " +
                 $"scale={profile.ScaleMode} frame={advanceFrames}/{canvas.RootFrameCount} " +
                 $"labels={string.Join(',', canvas.RootLabels)} artwork=" +
                 $"{(artworkResource.Length == 0 ? "none" : artworkResource)} area={areaId}");
        return new DaoLoadingPresentation(host, layer);
    }

    internal async Task<bool> CaptureIfRequestedAsync(CancellationToken cancellationToken)
    {
        var path = OS.GetEnvironment(CaptureEnvironmentVariable).Trim();
        if (path.Length == 0) return false;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError("OPENDAO_LOADING_CAPTURE status=fail reason=headless-display-server");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        await host.ToSignal(
            RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        cancellationToken.ThrowIfCancellationRequested();
        var error = host.GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"OPENDAO_LOADING_CAPTURE status={(error == Error.Ok ? "pass" : "fail")} " +
                 $"capture={path}");
        if (error != Error.Ok)
            throw new IOException($"Unable to capture DAO loading presentation: {error}");
        if (OS.GetEnvironment(CaptureExitEnvironmentVariable) != "1") return false;
        host.GetTree().Quit(0);
        return true;
    }

    internal void Hide()
    {
        if (hidden) return;
        hidden = true;
        if (GodotObject.IsInstanceValid(layer))
        {
            layer.Visible = false;
            layer.QueueFree();
        }
        GD.Print("OPENDAO_RETAIL_LOADING_SCREEN status=hidden boundary=first-owned-frame");
    }

    private static void Validate(DaoLoadingPresentationProfile profile)
    {
        if (profile.SchemaVersion != 1)
            throw new InvalidDataException(
                $"Unsupported DAO loading profile schema: {profile.SchemaVersion}");
        if (string.IsNullOrWhiteSpace(profile.ArchiveRelativePath) ||
            Path.IsPathRooted(profile.ArchiveRelativePath))
            throw new InvalidDataException("DAO loading archive path must be relative.");
        if (string.IsNullOrWhiteSpace(profile.MovieResource))
            throw new InvalidDataException("DAO loading movie is not configured.");
        if (profile.AtlasMaps is not { Length: > 0 } ||
            profile.AtlasMaps.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("DAO loading atlas maps are not configured.");
        if (profile.Layer is < 1 or > 127)
            throw new InvalidDataException("DAO loading layer must be between 1 and 127.");
        if (profile.AdvanceFrames is < 1 or > 10000)
            throw new InvalidDataException(
                "DAO loading advanceFrames must be between 1 and 10000.");
        if (string.IsNullOrWhiteSpace(profile.ArtworkArchiveRelativePath) ||
            Path.IsPathRooted(profile.ArtworkArchiveRelativePath))
            throw new InvalidDataException(
                "DAO loading artwork archive path must be relative.");
        if (!profile.ArtworkScaleMode.Equals("cover", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Unsupported DAO loading artwork scale mode: {profile.ArtworkScaleMode}");
        if (profile.ArtworkRules is null || profile.ArtworkRules.Any(rule =>
                string.IsNullOrWhiteSpace(rule.AreaPrefix) ||
                string.IsNullOrWhiteSpace(rule.Resource)))
            throw new InvalidDataException("DAO loading artwork rules are invalid.");
        if (profile.BackgroundColor is not { Length: 4 } ||
            profile.BackgroundColor.Any(value => value is < 0 or > 1))
            throw new InvalidDataException(
                "DAO loading background must contain four normalized channels.");
    }

    private static string ResolveUnderRoot(string root, string relativePath)
    {
        var resolved = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"DAO loading archive escapes the selected installation: {resolved}");
        if (!File.Exists(resolved))
            throw new FileNotFoundException("DAO loading archive was not found.", resolved);
        return resolved;
    }

    private static int ResolveAdvanceFrames(int configured)
    {
        var overrideValue = OS.GetEnvironment(AdvanceFramesEnvironmentVariable).Trim();
        if (overrideValue.Length == 0) return configured;
        if (!int.TryParse(overrideValue, out var parsed) || parsed is < 1 or > 10000)
            throw new InvalidDataException(
                $"Invalid {AdvanceFramesEnvironmentVariable}: {overrideValue}");
        return parsed;
    }

    private static string ResolveArtwork(DaoLoadingPresentationProfile profile, string areaId) =>
        profile.ArtworkRules
            .Where(rule => areaId.StartsWith(rule.AreaPrefix,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(rule => rule.AreaPrefix.Length)
            .Select(rule => rule.Resource)
            .FirstOrDefault() ?? string.Empty;
}
