// Matthew W, 2026-08-12

using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Godot;
using OpenDAO.Infrastructure.Archives;

namespace OpenDAO.MainMenu;

internal sealed record GfxAtlasRegion(
    string SourceName,
    string AtlasName,
    int X,
    int Y,
    int Width,
    int Height,
    int AtlasWidth,
    int AtlasHeight);

internal sealed class GfxAtlas
{
    private readonly ErfArchive archive;
    private readonly Dictionary<string, GfxAtlasRegion> regions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> pages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> textures =
        new(StringComparer.OrdinalIgnoreCase);

    public GfxAtlas(ErfArchive archive, params string[] mapNames)
    {
        this.archive = archive;
        foreach (var mapName in mapNames)
        {
            if (!archive.Contains(mapName))
            {
                continue;
            }

            ParseMap(Encoding.UTF8.GetString(archive.Read(mapName)));
        }
    }

    public bool Contains(string sourceName) => regions.ContainsKey(sourceName);

    public Texture2D? Load(string sourceName)
    {
        if (textures.TryGetValue(sourceName, out var cached))
        {
            return cached;
        }

        if (!regions.TryGetValue(sourceName, out var region))
        {
            return null;
        }

        if (!pages.TryGetValue(region.AtlasName, out var page))
        {
            var image = new Image();
            if (image.LoadDdsFromBuffer(archive.Read(region.AtlasName)) != Error.Ok ||
                image.IsEmpty())
            {
                return null;
            }

            page = ImageTexture.CreateFromImage(image);
            pages[region.AtlasName] = page;
        }

        var texture = new AtlasTexture
        {
            Atlas = page,
            Region = new Rect2(region.X, region.Y, region.Width, region.Height),
            FilterClip = true
        };
        textures[sourceName] = texture;
        return texture;
    }

    private void ParseMap(string xml)
    {
        var document = XDocument.Parse(xml);
        foreach (var atlasElement in document.Descendants("AtlasTexture"))
        {
            var atlasName = atlasElement.Attribute("Name")?.Value;
            if (atlasName is null)
            {
                continue;
            }

            foreach (var sourceElement in atlasElement.Elements("SourceTexture"))
            {
                var sourceName = sourceElement.Attribute("Name")?.Value;
                var uv = Numbers(sourceElement.Attribute("OffsetAndScale")?.Value, 4);
                var atlasSize = Numbers(sourceElement.Attribute("AtlasSize")?.Value, 2);
                var originalSize = Numbers(sourceElement.Attribute("OriginalImageSize")?.Value, 2);
                if (sourceName is null || uv is null || atlasSize is null || originalSize is null)
                {
                    continue;
                }

                var atlasWidth = (int)atlasSize[0];
                var atlasHeight = (int)atlasSize[1];
                var width = (int)originalSize[0];
                var height = (int)originalSize[1];
                var x = (int)Math.Round(uv[0] * atlasWidth, MidpointRounding.AwayFromZero);
                var y = (int)Math.Round(uv[1] * atlasHeight, MidpointRounding.AwayFromZero);
                if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
                    x + width > atlasWidth || y + height > atlasHeight)
                {
                    continue;
                }

                regions.TryAdd(
                    sourceName,
                    new GfxAtlasRegion(
                        sourceName,
                        atlasName,
                        x,
                        y,
                        width,
                        height,
                        atlasWidth,
                        atlasHeight));
            }
        }
    }

    private static double[]? Numbers(string? value, int expected)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != expected)
        {
            return null;
        }

        var numbers = new double[expected];
        for (var index = 0; index < expected; index++)
        {
            if (!double.TryParse(
                    parts[index],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out numbers[index]))
            {
                return null;
            }
        }

        return numbers;
    }
}
