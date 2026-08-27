using System.Text.Json.Nodes;
using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;

namespace OpenDAO.Infrastructure.World;

public interface IDaoWaterMaterialFactory
{
    Material? Create(WorldProfile profile, string definitionName, Mesh sourceMesh);
}

public sealed class DaoWaterMaterialFactory(IJsonStore store) : IDaoWaterMaterialFactory
{
    private const string ShaderPath = "res://shaders/dao_water.gdshader";
    private readonly Dictionary<string, IReadOnlyDictionary<string, Descriptor>> catalogs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Material> materials =
        new(StringComparer.OrdinalIgnoreCase);

    public Material? Create(WorldProfile profile, string definitionName, Mesh sourceMesh)
    {
        var wanted = definitionName.EndsWith(".mao", StringComparison.OrdinalIgnoreCase)
            ? definitionName
            : definitionName + ".mao";
        if (!Catalog(profile.TerrainMaterials).TryGetValue(wanted, out var descriptor)) return null;
        var cacheKey = profile.TerrainMaterials + "\n" + descriptor.Name;
        if (materials.TryGetValue(cacheKey, out var cached)) return cached;

        var shader = GD.Load<Shader>(ShaderPath);
        var normal = SourceNormal(sourceMesh);
        if (shader is null || normal is null)
        {
            GD.PushWarning($"OPENDAO_WATER_MATERIAL status=unavailable definition={definitionName} " +
                           $"reason=resource-absent shader={(shader is null ? 0 : 1)} " +
                           $"normal={(normal is null ? 0 : 1)}");
            return null;
        }

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("normal_map", normal);
        material.SetShaderParameter("vsh_params0", descriptor.Vsh0);
        material.SetShaderParameter("vsh_params1", descriptor.Vsh1);
        material.SetShaderParameter("vsh_params2", descriptor.Vsh2);
        material.SetShaderParameter("psh_params0", descriptor.Psh0);
        material.SetShaderParameter("psh_params1", descriptor.Psh1);
        material.SetShaderParameter("water_fog", descriptor.WaterFog);
        materials[cacheKey] = material;
        GD.Print($"OPENDAO_WATER_MATERIAL status=ready definition={definitionName} " +
                 $"normal={descriptor.NormalResource} source=installed-mao+are+water-shader");
        return material;
    }

    private IReadOnlyDictionary<string, Descriptor> Catalog(string manifestPath)
    {
        if (catalogs.TryGetValue(manifestPath, out var cached)) return cached;
        var result = new Dictionary<string, Descriptor>(StringComparer.OrdinalIgnoreCase);
        if (store.Read(manifestPath)?["water_materials"] is JsonArray records)
        {
            foreach (var record in records.OfType<JsonObject>())
            {
                var name = Text(record, "name");
                if (name.Length == 0) continue;
                var vsh = record["vsh_params"] as JsonArray;
                var psh = record["psh_params"] as JsonArray;
                result[name] = new Descriptor(name, Text(record, "normal_resource"),
                    Vector(vsh, 0), Vector(vsh, 4), Vector(vsh, 8),
                    Vector(psh, 0), Vector(psh, 4),
                    Vector(record["water_fog_params"] as JsonArray, 0));
            }
        }
        catalogs[manifestPath] = result;
        return result;
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

    private static string Text(JsonObject value, string key) =>
        value[key]?.GetValue<string>() ?? string.Empty;

    private static Vector4 Vector(JsonArray? values, int start)
    {
        float At(int index) => values is not null && index < values.Count
            ? values[index]?.GetValue<float>() ?? 0
            : 0;
        return new Vector4(At(start), At(start + 1), At(start + 2), At(start + 3));
    }

    private sealed record Descriptor(
        string Name,
        string NormalResource,
        Vector4 Vsh0,
        Vector4 Vsh1,
        Vector4 Vsh2,
        Vector4 Psh0,
        Vector4 Psh1,
        Vector4 WaterFog);
}
