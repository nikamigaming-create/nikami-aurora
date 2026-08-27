using Godot;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

internal static class RetailGuiFontLoader
{
    private const string FontLibrary = "gfxfontlib.gfx";
    private const string GeneratedFontDirectory = "user://fonts";
    public const string DragonText = "Dragon Text";

    public static FontFile? LoadDragonText(ErfArchive archive) => Load(archive, DragonText);

    public static FontFile? Load(ErfArchive archive, string face)
    {
        if (!archive.Contains(FontLibrary))
        {
            return null;
        }

        try
        {
            var directory = ProjectSettings.GlobalizePath(GeneratedFontDirectory);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory,
                face.Replace(" ", string.Empty).ToLowerInvariant() + ".ttf");
            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                var source = ScaleformFont.Extract(archive, FontLibrary, face);
                if (source is null || source.Glyphs.Count == 0)
                {
                    return null;
                }

                File.WriteAllBytes(target, source.ToTrueType());
            }

            var font = new FontFile();
            if (font.LoadDynamicFont(target) != Error.Ok)
            {
                File.Delete(target);
                return null;
            }

            font.Antialiasing = TextServer.FontAntialiasing.Gray;
            font.MultichannelSignedDistanceField = false;
            return font;
        }
        catch (Exception exception)
        {
            GD.PushWarning("The game font could not be converted: " + exception.Message);
            return null;
        }
    }
}
