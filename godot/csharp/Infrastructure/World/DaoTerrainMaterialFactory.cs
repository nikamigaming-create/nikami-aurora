using System.Text.Json.Nodes;
using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;

namespace OpenDAO.Infrastructure.World;

public interface IDaoTerrainMaterialFactory
{
    Material? Create(WorldProfile profile, string definitionName, Mesh sourceMesh);
}

public sealed class DaoTerrainMaterialFactory(IJsonStore store) : IDaoTerrainMaterialFactory
{
    private const string ShaderPath = "res://shaders/dao_terrain.gdshader";
    private readonly Dictionary<string, IReadOnlyDictionary<string, Descriptor>> catalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> textures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> materials =
        new(StringComparer.OrdinalIgnoreCase);

    public Material? Create(WorldProfile profile, string definitionName, Mesh sourceMesh)
    {
        var catalog = Catalog(profile.TerrainMaterials);
        var wanted = definitionName.EndsWith(".mao", StringComparison.OrdinalIgnoreCase)
            ? definitionName
            : definitionName + ".mao";
        if (!catalog.TryGetValue(wanted, out var descriptor))
        {
            GD.PushWarning($"OPENDAO_TERRAIN_MATERIAL status=unavailable definition={definitionName} " +
                           "reason=descriptor-absent");
            return null;
        }

        var cacheKey = profile.TerrainMaterials + "\n" + descriptor.Name;
        if (materials.TryGetValue(cacheKey, out var cached)) return cached;

        var shader = GD.Load<Shader>(ShaderPath);
        var palette = SourceAlbedo(sourceMesh);
        var maskV = LoadTexture(profile.TerrainMaterials, descriptor.MaskV);
        var maskA = LoadTexture(profile.TerrainMaterials, descriptor.MaskA);
        var maskA2 = LoadTexture(profile.TerrainMaterials, descriptor.MaskA2);
        var paletteNormal = SourceNormal(sourceMesh);
        if (shader is null || palette is null || paletteNormal is null || maskV is null ||
            maskA is null || maskA2 is null)
        {
            GD.PushWarning($"OPENDAO_TERRAIN_MATERIAL status=unavailable definition={definitionName} " +
                           $"reason=resource-absent shader={(shader is null ? 0 : 1)} " +
                           $"palette={(palette is null ? 0 : 1)} normal={(paletteNormal is null ? 0 : 1)} " +
                           $"mask_v={(maskV is null ? 0 : 1)} mask_a={(maskA is null ? 0 : 1)} " +
                           $"mask_a2={(maskA2 is null ? 0 : 1)}");
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("palette", palette);
        material.SetShaderParameter("palette_normal", paletteNormal);
        material.SetShaderParameter("mask_v", maskV);
        material.SetShaderParameter("mask_a", maskA);
        material.SetShaderParameter("mask_a2", maskA2);
        material.SetShaderParameter("pal_dim", descriptor.PalDim);
        material.SetShaderParameter("pal_param", descriptor.PalParam);
        material.SetShaderParameter("uv_scales0", descriptor.UvScales0);
        material.SetShaderParameter("uv_scales1", descriptor.UvScales1);
        materials[cacheKey] = material;
        GD.Print($"OPENDAO_TERRAIN_MATERIAL status=ready definition={definitionName} " +
                 "source=haven-maskv-maska-maska2");
        return material;
    }

    private IReadOnlyDictionary<string, Descriptor> Catalog(string manifestPath)
    {
        if (catalogs.TryGetValue(manifestPath, out var cached)) return cached;
        var result = new Dictionary<string, Descriptor>(StringComparer.OrdinalIgnoreCase);
        if (store.Read(manifestPath)?["terrain"]?["materials"] is JsonArray records)
        {
            foreach (var record in records.OfType<JsonObject>())
            {
                var descriptor = new Descriptor(
                    Text(record, "name"), Text(record, "palette"), Text(record, "maskV"),
                    Text(record, "maskA"), Text(record, "maskA2"),
                    Vector(record["palDim"] as JsonArray), Vector(record["palParam"] as JsonArray),
                    Vector(record["uvScales"] as JsonArray, 0),
                    Vector(record["uvScales"] as JsonArray, 4));
                if (descriptor.Name.Length > 0) result[descriptor.Name] = descriptor;
            }
        }
        catalogs[manifestPath] = result;
        return result;
    }

    private Texture2D? LoadTexture(string manifestPath, string relativePath)
    {
        if (relativePath.Length == 0) return null;
        var root = Path.GetDirectoryName(Globalize(manifestPath)) ?? string.Empty;
        var path = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (textures.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path)) return null;
        var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty()) return null;
        var texture = ImageTexture.CreateFromImage(image);
        textures[path] = texture;
        return texture;
    }

    private static Texture2D? SourceNormal(Mesh mesh)
    {
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetMaterial(surface) is BaseMaterial3D { NormalTexture: not null } material)
                return material.NormalTexture;
        }
        return null;
    }

    private static Texture2D? SourceAlbedo(Mesh mesh)
    {
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            if (mesh.SurfaceGetMaterial(surface) is BaseMaterial3D { AlbedoTexture: not null } material)
                return material.AlbedoTexture;
        }
        return null;
    }

    private static string Globalize(string path) =>
        path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    private static string Text(JsonObject value, string key) =>
        value[key]?.GetValue<string>() ?? string.Empty;

    private static Vector4 Vector(JsonArray? values, int start = 0)
    {
        float At(int index) => values is not null && index < values.Count
            ? values[index]?.GetValue<float>() ?? 0
            : 0;
        return new Vector4(At(start), At(start + 1), At(start + 2), At(start + 3));
    }

    private sealed record Descriptor(
        string Name,
        string Palette,
        string MaskV,
        string MaskA,
        string MaskA2,
        Vector4 PalDim,
        Vector4 PalParam,
        Vector4 UvScales0,
        Vector4 UvScales1);
}
