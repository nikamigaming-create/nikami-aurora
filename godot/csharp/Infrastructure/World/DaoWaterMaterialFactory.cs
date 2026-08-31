using System.Text.Json.Nodes;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

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
        var enhancedPresentation = UseEnhancedPresentation();
        if (!descriptor.ParameterContractReady ||
            (!descriptor.RenderAuthorized && !enhancedPresentation))
        {
            GD.Print($"OPENDAO_WATER_MATERIAL status=unsupported " +
                     $"layout={profile.LayoutName.ToLowerInvariant()} definition={definitionName} " +
                     $"render_authorized={(descriptor.RenderAuthorized ? 1 : 0)} " +
                     $"parameter_contract={(descriptor.ParameterContractReady ? "ready" : "unsupported")} " +
                     $"reason={(descriptor.RenderBlocker.Length > 0 ? descriptor.RenderBlocker : "source-water-semantics-unavailable")} " +
                     "fallback=imported-gltf-material source_tier=fail-closed");
            return null;
        }
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
        var presentation = descriptor.RenderAuthorized
            ? "source-authorized"
            : "enhanced-bounded-pbr";
        material.SetMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta,
            $"kind=installed-water-contract;name={descriptor.Name};presentation={presentation};" +
            $"source_normal_identity_sha256={IdentitySha256(SourceIdentity(sourceMesh))};" +
            $"mao_sha256={descriptor.MaoSha256};" +
            $"material_sha256={descriptor.MaterialSha256};state_sha256={descriptor.StateSha256};" +
            $"vertex_shader_sha256={descriptor.VertexShaderSha256};" +
            $"pixel_shader_sha256={descriptor.PixelShaderSha256};" +
            $"pbr_status={(descriptor.RenderAuthorized ? "source-shader" : "enhanced-shader")};" +
            $"mao_status={(descriptor.RenderAuthorized ? "source-water-contract" : "unsupported")};" +
            $"semantic_status={(descriptor.RenderAuthorized ? "authorized" : "enhanced-non-parity")};" +
            $"render_blocker={descriptor.RenderBlocker}");
        materials[cacheKey] = material;
        GD.Print($"OPENDAO_WATER_MATERIAL status=ready layout={profile.LayoutName.ToLowerInvariant()} " +
                 $"definition={definitionName} " +
                 $"normal={descriptor.NormalResource} presentation={presentation} " +
                 $"source_semantics={(descriptor.RenderAuthorized ? "authorized" : "unsupported")} " +
                 "opaque_white_fallback=blocked parity_claim=none " +
                 "source=installed-mao+are+water-shader");
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
                var waterFog = record["water_fog_params"] as JsonArray;
                var parameterContractReady = IsFiniteVector(vsh, 12) &&
                                             IsFiniteVector(psh, 8) &&
                                             IsFiniteVector(waterFog, 4);
                result[name] = new Descriptor(name, Text(record, "normal_resource"),
                    Vector(vsh, 0), Vector(vsh, 4), Vector(vsh, 8),
                    Vector(psh, 0), Vector(psh, 4),
                    Vector(waterFog, 0),
                    record["render_authorized"]?.GetValue<bool>() ?? false,
                    parameterContractReady,
                    Text(record, "render_blocker"), Text(record, "mao_sha256"),
                    Text(record, "material_sha256"), Text(record, "state_sha256"),
                    Text(record, "vertex_shader_sha256"), Text(record, "pixel_shader_sha256"));
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

    private static string SourceIdentity(Mesh mesh)
    {
        for (var surface = 0; surface < mesh.GetSurfaceCount(); surface++)
        {
            var material = mesh.SurfaceGetMaterial(surface);
            if (material?.HasMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta) == true)
                return material.GetMeta(DaoCharacterMaterialPostprocessor.WorldMaterialIdentityMeta)
                    .AsString();
        }
        throw new InvalidDataException("Water normal material has no source identity.");
    }

    private static string IdentitySha256(string identity) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();

    private static string Text(JsonObject value, string key) =>
        value[key]?.GetValue<string>() ?? string.Empty;

    private static Vector4 Vector(JsonArray? values, int start)
    {
        float At(int index) => values is not null && index < values.Count
            ? values[index]?.GetValue<float>() ?? 0
            : 0;
        return new Vector4(At(start), At(start + 1), At(start + 2), At(start + 3));
    }

    private static bool IsFiniteVector(JsonArray? values, int expected) =>
        values is { Count: var count } && count == expected &&
        values.All(value => value is not null &&
                            float.IsFinite(value.GetValue<float>()));

    private static bool UseEnhancedPresentation()
    {
        var backend = RenderingQualityPolicy.ParseBackend(
            RenderingServer.GetCurrentRenderingMethod().ToString());
        var requested = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_PRESENTATION_TIER");
        return RenderingQualityPolicy.ParseTier(requested, backend) ==
               RenderingPresentationTier.Enhanced;
    }

    private sealed record Descriptor(
        string Name,
        string NormalResource,
        Vector4 Vsh0,
        Vector4 Vsh1,
        Vector4 Vsh2,
        Vector4 Psh0,
        Vector4 Psh1,
        Vector4 WaterFog,
        bool RenderAuthorized,
        bool ParameterContractReady,
        string RenderBlocker,
        string MaoSha256,
        string MaterialSha256,
        string StateSha256,
        string VertexShaderSha256,
        string PixelShaderSha256);
}
