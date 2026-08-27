// Matthew W, 2026-08-12

using Godot;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

internal sealed class MainMenuAssetLoader(Control stage)
{
    public const string PlatePicture = "startmenu2_I84.dds";
    public const string PressedPlatePicture = "startmenu2_I8D.dds";

    private static readonly string[] AtlasMaps =
    [
        "atl_startmenu2_dxt1_dat.xml",
        "atl_startmenu2_dxt5_dat.xml",
        "atl_shared_dxt5_dat.xml"
    ];

    private static readonly (string Node, string Picture)[] Layers =
    [
        ("Scenery/Sky", "dxt1_cloud1a.jpg.dds"),
        ("Scenery/CloudBand", "dxt1_cloud2.png.dds"),
        ("Scenery/Ridge", "startmenu2_ID1.dds"),
        ("Scenery/Valley", "startmenu2_ID4.dds"),
        ("Scenery/MistFar", "startmenu2_ID7.dds"),
        ("Scenery/Peaks", "startmenu2_IDA.dds"),
        ("Scenery/MistMid", "startmenu2_ID7.dds"),
        ("Scenery/CliffRight", "startmenu2_IDF.dds"),
        ("Scenery/CliffLeft", "startmenu2_IDE.dds"),
        ("Scenery/Ground", "startmenu2_IDD.dds"),
        ("Scenery/Ridgeline", "startmenu2_IE5.dds"),
        ("Scenery/Sword", "startmenu2_IF4.dds"),
        ("Scenery/Foreground", "startmenu2_IF3.dds"),
        ("Scenery/Haze", "startmenu2_IF6.dds"),
        ("Scenery/Mountains", "startmenu2_IFA.dds"),
        ("Scenery/MistNear", "startmenu2_ID7.dds"),
        ("Frame/Wordmark", "startmenu2_IB2.dds"),
        ("Frame/Subtitle", "startmenu2_IA7.dds"),
        ("Frame/BottomBar", "LoginNewAccount_IAB.dds"),
        ("Options/Panel", "OptionsMenu_I11F.dds"),
        ("Options/Tab", "OptionsMenu_I107.dds"),
        ("Options/RuleA", "OptionsMenu_IC3.dds"),
        ("Options/RuleB", "OptionsMenu_IC3.dds"),
        ("Options/Swirl", "OptionsMenu_IB3.dds"),
        ("Options/SwirlInner", "OptionsMenu_IB7.dds"),
        ("Options/SwirlCore", "OptionsMenu_IBB.dds"),
        ("Options/Header", "OptionsMenu_I125.dds"),
        ("Options/ResolutionTop", "OptionsMenu_ICB.dds"),
        ("Options/ResolutionBottom", "OptionsMenu_ID8.dds"),
        ("Options/ResolutionArrow", "inventory_I2F1.dds"),
        ("Options/DisplayTop", "OptionsMenu_ICB.dds"),
        ("Options/DisplayBottom", "OptionsMenu_ID8.dds"),
        ("Options/DisplayArrow", "inventory_I2F1.dds"),
        ("Options/OkPlate", "Login_I62.dds"),
        ("Options/CancelPlate", "Login_I62.dds")
    ];

    public Texture2D? Plate { get; private set; }

    public Texture2D? PressedPlate { get; private set; }

    public FontFile? Font { get; private set; }

    public string Load(string archivePath)
    {
        var archive = ErfArchive.Open(archivePath);
        var atlas = new GfxAtlas(archive, AtlasMaps);
        var missing = new List<string>();

        Font = RetailGuiFontLoader.LoadDragonText(archive);
        if (Font is null)
        {
            missing.Add(RetailGuiFontLoader.DragonText);
        }

        foreach (var (node, picture) in Layers)
        {
            var target = stage.GetNodeOrNull<Control>(node);
            if (target is null)
            {
                missing.Add($"{node} is not in the scene");
                continue;
            }

            var texture = atlas.Load(picture);
            if (texture is null)
            {
                missing.Add(picture);
            }

            target.Set("texture", texture is null ? default : Variant.From(texture));
        }

        Plate = atlas.Load(PlatePicture);
        PressedPlate = atlas.Load(PressedPlatePicture) ?? Plate;
        if (Plate is null)
        {
            missing.Add(PlatePicture);
        }

        if (missing.Count == 0)
        {
            return string.Empty;
        }

        GD.PushWarning("Title screen artwork is incomplete: " + string.Join(" | ", missing));
        return "Some title screen artwork is unavailable.";
    }

}
