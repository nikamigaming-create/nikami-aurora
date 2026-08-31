using System.Text.Json.Nodes;
using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

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
        if (shader is null || palette is null || paletteNormal is null)
            throw new InvalidDataException(
                $"DAO terrain descriptor cannot bind its source resources: " +
                $"layout={profile.LayoutName} definition={definitionName} " +
                $"shader={(shader is null ? 0 : 1)} palette={(palette is null ? 0 : 1)} " +
                $"normal={(paletteNormal is null ? 0 : 1)}");

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
        material.SetMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta,
            $"kind=installed-terrain-contract;name={descriptor.Name};" +
            $"source_palette_identity_sha256={IdentitySha256(SourceIdentity(sourceMesh))};" +
            $"palette_resource={descriptor.Palette};" +
            $"mask_v={FileIdentity(profile.TerrainMaterials, descriptor.MaskV)};" +
            $"mask_a={FileIdentity(profile.TerrainMaterials, descriptor.MaskA)};" +
            $"mask_a2={FileIdentity(profile.TerrainMaterials, descriptor.MaskA2)};" +
            "pbr_status=source-shader;mao_status=source-terrain-contract;" +
            "semantic_status=haven-maskv-maska-maska2");
        materials[cacheKey] = material;
        GD.Print($"OPENDAO_TERRAIN_MATERIAL status=ready layout={profile.LayoutName.ToLowerInvariant()} " +
                 $"definition={definitionName} source=haven-maskv-maska-maska2");
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
                    RequiredText(record, "name"), RequiredText(record, "palette"),
                    RequiredText(record, "maskV"), RequiredText(record, "maskA"),
                    RequiredText(record, "maskA2"),
                    RequiredVector(record["palDim"] as JsonArray, 4, "palDim"),
                    RequiredVector(record["palParam"] as JsonArray, 4, "palParam"),
                    RequiredVector(record["uvScales"] as JsonArray, 8, "uvScales", 0),
                    RequiredVector(record["uvScales"] as JsonArray, 8, "uvScales", 4));
                if (!result.TryAdd(descriptor.Name, descriptor))
                    throw new InvalidDataException(
                        $"DAO terrain material descriptor is duplicated: {descriptor.Name}");
            }
        }
        catalogs[manifestPath] = result;
        return result;
    }

    private Texture2D LoadTexture(string manifestPath, string relativePath)
    {
        var path = ResolvePayload(manifestPath, relativePath);
        if (textures.TryGetValue(path, out var cached)) return cached;
        if (!File.Exists(path))
            throw new InvalidDataException($"DAO terrain texture is absent: {path}");
        var image = Image.LoadFromFile(path);
        if (image is null || image.IsEmpty())
            throw new InvalidDataException($"DAO terrain texture cannot be decoded: {path}");
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

    private static string SourceIdentity(Mesh mesh)
    {
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var material = mesh.SurfaceGetMaterial(surface);
            if (material?.HasMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta) == true)
                return material.GetMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta)
                    .AsString();
        }
        throw new InvalidDataException("Terrain palette material has no source identity.");
    }

    private static string FileIdentity(string manifestPath, string relativePath)
    {
        var path = ResolvePayload(manifestPath, relativePath);
        if (!File.Exists(path))
            throw new InvalidDataException($"Terrain material payload is absent: {path}");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            File.ReadAllBytes(path))).ToLowerInvariant();
    }

    private static string IdentitySha256(string identity) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    private static string Globalize(string path) =>
        path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;

    private static string ResolvePayload(string manifestPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("DAO terrain payload path is absent.");
        var root = Path.GetFullPath(Path.GetDirectoryName(Globalize(manifestPath)) ??
                                    throw new InvalidDataException(
                                        "DAO terrain manifest has no parent directory."));
        var path = Path.GetFullPath(Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"DAO terrain payload escapes its manifest root: {relativePath}");
        return path;
    }

    private static string RequiredText(JsonObject value, string key)
    {
        var result = value[key]?.GetValue<string>() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result))
            throw new InvalidDataException(
                $"DAO terrain material descriptor field is absent: {key}");
        return result;
    }

    private static Vector4 RequiredVector(JsonArray? values, int expected,
        string field, int start = 0)
    {
        if (values is null || values.Count != expected)
            throw new InvalidDataException(
                $"DAO terrain material {field} must contain {expected} values.");
        float At(int index)
        {
            var value = values[index]?.GetValue<float>() ?? float.NaN;
            if (!float.IsFinite(value))
                throw new InvalidDataException(
                    $"DAO terrain material {field}[{index}] is not finite.");
            return value;
        }
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
