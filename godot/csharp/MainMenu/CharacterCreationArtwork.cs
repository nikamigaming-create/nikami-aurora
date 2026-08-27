using Godot;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

/// <summary>
/// Provides authored character-generation textures directly from guiexport.erf.
/// Controls own layout and state; this class owns archive and atlas concerns only.
/// </summary>
internal sealed class CharacterCreationArtwork
{
    private static readonly string[] AtlasMaps =
    [
        "atl_chargen_dxt1_dat.xml",
        "atl_chargen_dxt5_dat.xml",
        "atl_shared_dxt1_dat.xml",
        "atl_shared_dxt5_dat.xml"
    ];

    private readonly ErfArchive archive;
    private readonly GfxAtlas atlas;
    private readonly GfxMovie chargen;

    private CharacterCreationArtwork(ErfArchive archive, GfxAtlas atlas, GfxMovie chargen)
    {
        this.archive = archive;
        this.atlas = atlas;
        this.chargen = chargen;
    }

    public static CharacterCreationArtwork Open(string archivePath)
    {
        var archive = ErfArchive.Open(archivePath);
        return new CharacterCreationArtwork(archive,
            new GfxAtlas(archive, AtlasMaps),
            GfxMovie.Read(archive.Read("chargen.gfx")));
    }

    public Texture2D? Texture(string resourceName) => atlas.Load(resourceName);

    public int CountAvailable(IEnumerable<string> resourceNames) =>
        resourceNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(atlas.Contains);

    public RetailGfxCanvas CreateScreen(string label) => new(
        $"RetailChargen{label}", archive, atlas, "chargen.gfx", RetailGfxAnchor.TopLeft,
        select: quad => SelectBoundScreenQuad(label, quad),
        rootLabel: label, authoredCoordinates: true);

    public Vector2 InstancePosition(string label, string instanceName)
    {
        var player = new GfxPlayer(chargen);
        if (!player.SeekRoot(label) || player.FindInstance(instanceName) is not { } transform)
            throw new InvalidDataException($"chargen.gfx instance is absent: {label}:{instanceName}");
        return new Vector2((float)transform.TranslateX, (float)transform.TranslateY);
    }

    private static GfxQuad? SelectBoundScreenQuad(string label, GfxQuad quad)
    {
        if (!label.Equals("SoundsetAppearance", StringComparison.Ordinal)) return quad;
        // PortraitPicture_mc is populated dynamically by Scaleform from the
        // selected character. Until that live binding is supplied, drawing its
        // static level-up mask produces a false grey disc over the summary.
        return quad.Image.Name is "levelUp_I143.dds" or "CharGen_I258.dds" ? null : quad;
    }

}
