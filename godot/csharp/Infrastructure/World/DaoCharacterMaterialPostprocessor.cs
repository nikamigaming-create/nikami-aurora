using Godot;
using System.Text.Json;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;

namespace OpenDAO.Infrastructure.World;

/// <summary>
/// Restores BioWare character material semantics lost by generic glTF import.
/// DAO HairAlpha stores strand coverage/luminance in the packed texture alpha;
/// treating its RGB normal data as albedo produces the blue-black hair visible
/// in earlier comparison captures.
/// </summary>
public sealed class DaoCharacterMaterialPostprocessor : IGodotModelPostprocessor, ICharacterLightingBinder
{
    private const string MaterialContractMetaPrefix = "opendao_character_material_contract_";
    private const string HairShaderPath = "res://shaders/dao_character_hair.gdshader";
    private const string FaceShaderPath = "res://shaders/dao_facefx_material.gdshader";
    private const string EyelashShaderPath = "res://shaders/dao_character_eyelash.gdshader";
    private const string ArmourSkinShaderPath = "res://shaders/dao_character_armour_skin.gdshader";
    private Shader? hairShader;
    private Shader? faceShader;
    private Shader? eyelashShader;
    private Shader? armourSkinShader;
    private string? cacheFingerprint;
    private readonly List<WeakReference<ShaderMaterial>> hairMaterials = [];
    private readonly List<WeakReference<ShaderMaterial>> faceMaterials = [];
    private readonly List<WeakReference<ShaderMaterial>> eyelashMaterials = [];
    private readonly List<WeakReference<ShaderMaterial>> armourSkinMaterials = [];
    private CharacterLighting? currentLighting;

    public string CacheFingerprint => cacheFingerprint ??= BuildCacheFingerprint();

    public void Prepare(PackedScene scene)
    {
        if (scene.Instantiate() is not Node root) return;
        try
        {
            Prepare(root);
        }
        finally
        {
            root.Free();
        }
    }

    public void Prepare(Node root)
    {
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
        {
            if (mesh.Mesh is null) continue;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not ShaderMaterial material) continue;
                RestoreMaterialContract(mesh, surface, material);
                if (material.ResourceName.EndsWith("_OpenDAOHair", StringComparison.Ordinal))
                {
                    TrackMaterial(hairMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
                else if (material.ResourceName.EndsWith("_OpenDAOFace0", StringComparison.Ordinal))
                {
                    TrackMaterial(faceMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
                else if (material.ResourceName.EndsWith("_OpenDAOEyelash0", StringComparison.Ordinal))
                {
                    TrackMaterial(eyelashMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, false);
                }
                else if (material.ResourceName.EndsWith("_OpenDAOArmourSkin", StringComparison.Ordinal))
                {
                    TrackMaterial(armourSkinMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
            }
        }
        ValidateStoredSkinContinuity(root);
    }

    public void Process(Node3D model, GltfState sourceState)
    {
        hairShader ??= GD.Load<Shader>(HairShaderPath);
        faceShader ??= GD.Load<Shader>(FaceShaderPath);
        eyelashShader ??= GD.Load<Shader>(EyelashShaderPath);
        armourSkinShader ??= GD.Load<Shader>(ArmourSkinShaderPath);
        if (hairShader is null || faceShader is null || eyelashShader is null || armourSkinShader is null)
        {
            GD.PushError("OPENDAO_CHARACTER_MATERIAL_FAIL reason=shader-missing " +
                         $"hair={(hairShader is null ? 0 : 1)} face={(faceShader is null ? 0 : 1)} " +
                         $"eyelash={(eyelashShader is null ? 0 : 1)} " +
                         $"armour_skin={(armourSkinShader is null ? 0 : 1)}");
            return;
        }

        using var document = JsonDocument.Parse(Godot.Json.Stringify(sourceState.Json));
        var hairPalettes = ReadHairPalettes(document.RootElement);
        var facePalettes = ReadFacePalettes(document.RootElement);
        var eyelashContracts = ReadEyelashContracts(document.RootElement);
        var armourSkinContracts = ReadArmourSkinContracts(document.RootElement);
        ValidateSkinContinuity(facePalettes.Values, armourSkinContracts.Values);
        var installedHair = 0;
        var installedFaces = 0;
        var installedEyelashes = 0;
        var installedArmourSkin = 0;
        foreach (var mesh in model.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
        {
            if (mesh.Mesh is null) continue;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not BaseMaterial3D source ||
                    source.AlbedoTexture is null) continue;
                if (armourSkinContracts.TryGetValue(source.ResourceName, out var armourSkinContract) &&
                    source.AOTexture is not null)
                {
                    var material = new ShaderMaterial
                    {
                        Shader = armourSkinShader,
                        ResourceName = source.ResourceName + "_OpenDAOArmourSkin"
                    };
                    material.SetShaderParameter("albedo", source.AlbedoColor);
                    material.SetShaderParameter("texture_albedo", source.AlbedoTexture);
                    material.SetShaderParameter("texture_tint_mask", source.AOTexture);
                    material.SetShaderParameter("skin_diffuse", armourSkinContract.SkinDiffuse);
                    material.SetShaderParameter("skin_opacity", armourSkinContract.SkinOpacity);
                    material.SetShaderParameter("roughness", source.Roughness);
                    material.SetShaderParameter("specular", source.MetallicSpecular);
                    material.SetShaderParameter("metallic", source.Metallic);
                    material.SetShaderParameter("normal_strength",
                        source.NormalEnabled ? source.NormalScale : 0.0f);
                    material.SetShaderParameter("use_normal_texture",
                        source.NormalEnabled && source.NormalTexture is not null);
                    if (source.NormalTexture is not null)
                        material.SetShaderParameter("texture_normal", source.NormalTexture);
                    material.SetShaderParameter("uv1_scale", source.Uv1Scale);
                    material.SetShaderParameter("uv1_offset", source.Uv1Offset);
                    mesh.SetSurfaceOverrideMaterial(surface, material);
                    StoreMaterialContract(mesh, surface, new
                    {
                        kind = "armour-skin",
                        skinDiffuse = Channels(armourSkinContract.SkinDiffuse),
                        skinOpacity = armourSkinContract.SkinOpacity,
                        roughness = source.Roughness,
                        specular = source.MetallicSpecular,
                        metallic = source.Metallic,
                        normalStrength = source.NormalEnabled ? source.NormalScale : 0.0f,
                        useNormalTexture = source.NormalEnabled && source.NormalTexture is not null,
                        uv1Scale = Channels(source.Uv1Scale),
                        uv1Offset = Channels(source.Uv1Offset)
                    });
                    TrackMaterial(armourSkinMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                    GD.Print($"OPENDAO_CHARACTER_ARMOUR_SKIN_PALETTE material={source.ResourceName} " +
                             $"skin={armourSkinContract.SkinDiffuse} " +
                             $"opacity={armourSkinContract.SkinOpacity:0.###}");
                    installedArmourSkin++;
                    continue;
                }
                if (eyelashContracts.TryGetValue(source.ResourceName, out var eyelashContract))
                {
                    var material = new ShaderMaterial
                    {
                        Shader = eyelashShader,
                        ResourceName = source.ResourceName + "_OpenDAOEyelash0"
                    };
                    material.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    material.SetShaderParameter("alpha_threshold", eyelashContract.AlphaThreshold);
                    material.SetShaderParameter("mip_bias", eyelashContract.MipBias);
                    mesh.SetSurfaceOverrideMaterial(surface, material);
                    StoreMaterialContract(mesh, surface, new
                    {
                        kind = "eyelash",
                        alphaThreshold = eyelashContract.AlphaThreshold,
                        mipBias = eyelashContract.MipBias
                    });
                    TrackMaterial(eyelashMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, false);
                    installedEyelashes++;
                    continue;
                }
                if (IsHairMaterial(mesh, source) &&
                    hairPalettes.TryGetValue(source.ResourceName, out var hairPalette))
                {
                    var material = new ShaderMaterial
                    {
                        Shader = hairShader,
                        ResourceName = source.ResourceName + "_OpenDAOHair"
                    };
                    material.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    material.SetShaderParameter("tint_noise_texture", source.NormalTexture);
                    material.SetShaderParameter("diffuse_tint_0", hairPalette.Diffuse0);
                    material.SetShaderParameter("diffuse_tint_1", hairPalette.Diffuse1);
                    material.SetShaderParameter("diffuse_tint_2", hairPalette.Diffuse2);
                    material.SetShaderParameter("specular_tint_0", hairPalette.Specular0);
                    material.SetShaderParameter("specular_tint_1", hairPalette.Specular1);
                    material.SetShaderParameter("specular_tint_2", hairPalette.Specular2);
                    material.SetShaderParameter("primary_specular_mask", hairPalette.PrimarySpecularMask);
                    material.SetShaderParameter("primary_specular_power", hairPalette.PrimarySpecularPower);
                    material.SetShaderParameter("secondary_specular_power", hairPalette.SecondarySpecularPower);
                    material.SetShaderParameter("noise_tiling", hairPalette.NoiseTiling);
                    material.SetShaderParameter("packed_bioware", true);
                    material.SetShaderParameter("roughness", source.Roughness);
                    material.SetShaderParameter("metallic_value", source.Metallic);
                    material.SetShaderParameter("has_roughness_map", source.RoughnessTexture is not null);
                    if (source.RoughnessTexture is not null)
                        material.SetShaderParameter("roughness_texture", source.RoughnessTexture);
                    material.SetShaderParameter("has_metallic_map", source.MetallicTexture is not null);
                    if (source.MetallicTexture is not null)
                        material.SetShaderParameter("metallic_texture", source.MetallicTexture);
                    mesh.SetSurfaceOverrideMaterial(surface, material);
                    StoreMaterialContract(mesh, surface, new
                    {
                        kind = "hair",
                        diffuse0 = Channels(hairPalette.Diffuse0),
                        diffuse1 = Channels(hairPalette.Diffuse1),
                        diffuse2 = Channels(hairPalette.Diffuse2),
                        specular0 = Channels(hairPalette.Specular0),
                        specular1 = Channels(hairPalette.Specular1),
                        specular2 = Channels(hairPalette.Specular2),
                        primarySpecularMask = hairPalette.PrimarySpecularMask,
                        primarySpecularPower = hairPalette.PrimarySpecularPower,
                        secondarySpecularPower = hairPalette.SecondarySpecularPower,
                        noiseTiling = hairPalette.NoiseTiling,
                        roughness = source.Roughness,
                        metallic = source.Metallic,
                        hasRoughnessMap = source.RoughnessTexture is not null,
                        hasMetallicMap = source.MetallicTexture is not null
                    });
                    TrackMaterial(hairMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                    GD.Print($"OPENDAO_CHARACTER_HAIR_PALETTE material={source.ResourceName} " +
                             $"d0={hairPalette.Diffuse0} d1={hairPalette.Diffuse1} " +
                             $"d2={hairPalette.Diffuse2} noise_tiling={hairPalette.NoiseTiling:0.###}");
                    installedHair++;
                    continue;
                }

                if (!IsFaceMaterial(mesh, source) ||
                    !facePalettes.TryGetValue(source.ResourceName, out var facePalette) ||
                    source.AOTexture is null) continue;
                var faceMaterial = new ShaderMaterial
                {
                    Shader = faceShader,
                    ResourceName = source.ResourceName + "_OpenDAOFace0"
                };
                faceMaterial.SetShaderParameter("albedo", source.AlbedoColor);
                faceMaterial.SetShaderParameter("texture_albedo", source.AlbedoTexture);
                faceMaterial.SetShaderParameter("texture_tint_mask", source.AOTexture);
                faceMaterial.SetShaderParameter("tint_diffuse_0", facePalette.Diffuse0);
                faceMaterial.SetShaderParameter("tint_diffuse_1", facePalette.Diffuse1);
                faceMaterial.SetShaderParameter("tint_diffuse_2", facePalette.Diffuse2);
                faceMaterial.SetShaderParameter("tint_diffuse_3", facePalette.Diffuse3);
                faceMaterial.SetShaderParameter("tint_specular_0", facePalette.Specular0);
                faceMaterial.SetShaderParameter("tint_specular_1", facePalette.Specular1);
                faceMaterial.SetShaderParameter("tint_specular_2", facePalette.Specular2);
                faceMaterial.SetShaderParameter("tint_specular_3", facePalette.Specular3);
                faceMaterial.SetShaderParameter("tint_diffuse_opacity", facePalette.DiffuseOpacity);
                faceMaterial.SetShaderParameter("tint_specular_opacity", facePalette.SpecularOpacity);
                faceMaterial.SetShaderParameter("use_face_tint", true);
                faceMaterial.SetShaderParameter("use_facefx_emotions", false);
                faceMaterial.SetShaderParameter("roughness", source.Roughness);
                faceMaterial.SetShaderParameter("specular", source.MetallicSpecular);
                faceMaterial.SetShaderParameter("metallic", source.Metallic);
                faceMaterial.SetShaderParameter("normal_strength", source.NormalEnabled ? source.NormalScale : 0.0f);
                faceMaterial.SetShaderParameter("use_normal_texture",
                    source.NormalEnabled && source.NormalTexture is not null);
                if (source.NormalTexture is not null)
                    faceMaterial.SetShaderParameter("texture_normal", source.NormalTexture);
                faceMaterial.SetShaderParameter("use_roughness_texture", source.RoughnessTexture is not null);
                if (source.RoughnessTexture is not null)
                    faceMaterial.SetShaderParameter("texture_roughness", source.RoughnessTexture);
                faceMaterial.SetShaderParameter("use_metallic_texture", source.MetallicTexture is not null);
                if (source.MetallicTexture is not null)
                    faceMaterial.SetShaderParameter("texture_metallic", source.MetallicTexture);
                faceMaterial.SetShaderParameter("uv1_scale", source.Uv1Scale);
                faceMaterial.SetShaderParameter("uv1_offset", source.Uv1Offset);
                mesh.SetSurfaceOverrideMaterial(surface, faceMaterial);
                StoreMaterialContract(mesh, surface, new
                {
                    kind = "face",
                    diffuse0 = Channels(facePalette.Diffuse0),
                    diffuse1 = Channels(facePalette.Diffuse1),
                    diffuse2 = Channels(facePalette.Diffuse2),
                    diffuse3 = Channels(facePalette.Diffuse3),
                    specular0 = Channels(facePalette.Specular0),
                    specular1 = Channels(facePalette.Specular1),
                    specular2 = Channels(facePalette.Specular2),
                    specular3 = Channels(facePalette.Specular3),
                    diffuseOpacity = Channels(facePalette.DiffuseOpacity),
                    specularOpacity = Channels(facePalette.SpecularOpacity),
                    roughness = source.Roughness,
                    specular = source.MetallicSpecular,
                    metallic = source.Metallic,
                    normalStrength = source.NormalEnabled ? source.NormalScale : 0.0f,
                    useNormalTexture = source.NormalEnabled && source.NormalTexture is not null,
                    useRoughnessTexture = source.RoughnessTexture is not null,
                    useMetallicTexture = source.MetallicTexture is not null,
                    uv1Scale = Channels(source.Uv1Scale),
                    uv1Offset = Channels(source.Uv1Offset)
                });
                TrackMaterial(faceMaterials, faceMaterial);
                if (currentLighting is not null) BindAuthoredLighting(faceMaterial, currentLighting, true);
                GD.Print($"OPENDAO_CHARACTER_FACE_PALETTE material={source.ResourceName} " +
                         $"d0={facePalette.Diffuse0} d1={facePalette.Diffuse1} " +
                         $"d2={facePalette.Diffuse2} skin={facePalette.Diffuse3} " +
                         $"opacity={facePalette.DiffuseOpacity}");
                installedFaces++;
            }
        }
        if (installedHair > 0)
            GD.Print($"OPENDAO_CHARACTER_HAIR_MATERIAL status=ready surfaces={installedHair} " +
                     $"source=retail-hair0-psh palette=exact");
        if (installedFaces > 0)
            GD.Print($"OPENDAO_CHARACTER_FACE_MATERIAL status=ready surfaces={installedFaces} " +
                     "source=retail-face0-psh palette=exact");
        if (installedEyelashes > 0)
            GD.Print($"OPENDAO_CHARACTER_EYELASH_MATERIAL status=ready surfaces={installedEyelashes} " +
                     "source=retail-eyelash0-psh state=eyelashpnch alpha_ref=20 mip_bias=-2.5");
        if (installedArmourSkin > 0)
            GD.Print($"OPENDAO_CHARACTER_ARMOUR_SKIN_MATERIAL status=ready surfaces={installedArmourSkin} " +
                     "source=retail-character-mat semantic=ArmourSkinTint shader=Ch1ArmTnt " +
                     "skin_mask=alpha lighting=retail-affect-domain-1");
    }

    private static bool IsHairMaterial(MeshInstance3D mesh, BaseMaterial3D material)
    {
        var identity = $"{mesh.Name}|{material.ResourceName}".ToLowerInvariant();
        if (identity.Contains("bld", StringComparison.Ordinal)) return false;
        return identity.Contains("_har_", StringComparison.Ordinal) ||
               identity.Contains("har_all", StringComparison.Ordinal) ||
               identity.Contains("hair", StringComparison.Ordinal);
    }

    public void Apply(AuthoredLightingProfile lighting, float focusX, float focusY, float focusZ)
    {
        var sourceLights = lighting.CharacterPointLights.Length > 0
            ? lighting.CharacterPointLights
            : lighting.PointLights;
        var sourceContract = lighting.CharacterPointLights.Length > 0
            ? "retail-affect-domain-1"
            : "legacy-world-light-fallback";
        var nearest = sourceLights
            .OrderBy(light => DistanceSquared(light, focusX, focusY, focusZ))
            .Take(3).ToArray();
        currentLighting = new CharacterLighting(lighting, nearest);
        BindTrackedMaterials(hairMaterials, currentLighting, true);
        BindTrackedMaterials(faceMaterials, currentLighting, true);
        BindTrackedMaterials(eyelashMaterials, currentLighting, false);
        BindTrackedMaterials(armourSkinMaterials, currentLighting, true);
        GD.Print($"OPENDAO_CHARACTER_LIGHTING status=ready probe={lighting.ProbeResource} " +
                 $"point_lights={string.Join(',', nearest.Select(light => light.Name))} " +
                 $"materials=hair:{hairMaterials.Count},face:{faceMaterials.Count},eyelash:{eyelashMaterials.Count}," +
                 $"armour_skin:{armourSkinMaterials.Count} " +
                 $"source={sourceContract} " +
                 "upload=raw-linear-radiance attenuation=cosine-squared wrapped_diffuse=0.75NdotL+0.25");
    }

    private static float DistanceSquared(AuthoredPointLightProfile light, float x, float y, float z)
    {
        var dx = light.X - x;
        var dy = light.Y - y;
        var dz = light.Z - z;
        return dx * dx + dy * dy + dz * dz;
    }

    private static void BindAuthoredLighting(ShaderMaterial material, CharacterLighting lighting,
        bool bindPointLights)
    {
        var profile = lighting.Profile;
        material.SetShaderParameter("dao_probe_enabled", profile.ProbeLoaded);
        BindMatrix(material, "dao_probe_r", profile.ProbeMatrixR);
        BindMatrix(material, "dao_probe_g", profile.ProbeMatrixG);
        BindMatrix(material, "dao_probe_b", profile.ProbeMatrixB);
        material.SetShaderParameter("dao_character_light", ReadRgb(profile.CharacterSunColor));
        if (!bindPointLights) return;
        material.SetShaderParameter("dao_point_count", lighting.PointLights.Length);
        for (var index = 0; index < 3; index++)
        {
            var light = index < lighting.PointLights.Length ? lighting.PointLights[index] : null;
            material.SetShaderParameter($"dao_point_position_{index}", light is null
                ? Vector4.Zero
                : new Vector4(light.X, light.Z, -light.Y, light.Radius));
            material.SetShaderParameter($"dao_point_color_{index}", light is null
                ? Vector4.Zero
                : new Vector4(light.Red, light.Green, light.Blue, 1));
        }
    }

    private static Vector3 ReadRgb(IReadOnlyList<float> values) => values.Count >= 3
        ? new Vector3(values[0], values[1], values[2])
        : Vector3.Zero;

    private static void TrackMaterial(List<WeakReference<ShaderMaterial>> materials,
        ShaderMaterial material)
    {
        if (materials.Any(reference => reference.TryGetTarget(out var existing) && existing == material)) return;
        materials.Add(new WeakReference<ShaderMaterial>(material));
    }

    private static void BindTrackedMaterials(List<WeakReference<ShaderMaterial>> materials,
        CharacterLighting lighting, bool bindPointLights)
    {
        var retained = new List<WeakReference<ShaderMaterial>>(materials.Count);
        foreach (var reference in materials)
        {
            if (!reference.TryGetTarget(out var material) || !GodotObject.IsInstanceValid(material)) continue;
            BindAuthoredLighting(material, lighting, bindPointLights);
            retained.Add(reference);
        }
        materials.Clear();
        materials.AddRange(retained);
    }

    private static void BindMatrix(ShaderMaterial material, string prefix, IReadOnlyList<float> matrix)
    {
        for (var row = 0; row < 4; row++)
        {
            var offset = row * 4;
            var value = matrix.Count >= offset + 4
                ? new Vector4(matrix[offset], matrix[offset + 1], matrix[offset + 2], matrix[offset + 3])
                : Vector4.Zero;
            material.SetShaderParameter($"{prefix}{row}", value);
        }
    }

    private static bool IsFaceMaterial(MeshInstance3D mesh, BaseMaterial3D material)
    {
        var identity = $"{mesh.Name}|{material.ResourceName}".ToLowerInvariant();
        return identity.Contains("facem1", StringComparison.Ordinal) ||
               identity.Contains("_hed_", StringComparison.Ordinal);
    }

    private static IReadOnlyDictionary<string, HairPalette> ReadHairPalettes(JsonElement root)
    {
        var result = new Dictionary<string, HairPalette>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("materials", out var materials)) return result;
        foreach (var material in materials.EnumerateArray())
        {
            if (!material.TryGetProperty("name", out var nameElement) ||
                !material.TryGetProperty("extras", out var extras) ||
                !extras.TryGetProperty("daoHair", out var hair) ||
                hair.GetProperty("schema").GetString() != "opendao-hairalpha-v1") continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (name.Length == 0) continue;
            result[name] = new HairPalette(
                ReadColor(hair.GetProperty("diffuse0")),
                ReadColor(hair.GetProperty("diffuse1")),
                ReadColor(hair.GetProperty("diffuse2")),
                ReadColor(hair.GetProperty("specular0")),
                ReadColor(hair.GetProperty("specular1")),
                ReadColor(hair.GetProperty("specular2")),
                hair.TryGetProperty("primarySpecularMask", out var primaryMask)
                    ? primaryMask.GetSingle() : 0.01f,
                hair.TryGetProperty("primarySpecularPower", out var primaryPower)
                    ? primaryPower.GetSingle() : 60.0f,
                hair.TryGetProperty("secondarySpecularPower", out var secondaryPower)
                    ? secondaryPower.GetSingle() : 62.0f,
                hair.GetProperty("noiseTiling").GetSingle());
        }
        return result;
    }

    private static IReadOnlyDictionary<string, FacePalette> ReadFacePalettes(JsonElement root)
    {
        var result = new Dictionary<string, FacePalette>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("materials", out var materials)) return result;
        foreach (var material in materials.EnumerateArray())
        {
            if (!material.TryGetProperty("name", out var nameElement) ||
                !material.TryGetProperty("extras", out var extras) ||
                !extras.TryGetProperty("daoFace", out var face) ||
                face.GetProperty("schema").GetString() != "opendao-face0-v1") continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (name.Length == 0) continue;
            result[name] = new FacePalette(
                ReadColor(face.GetProperty("diffuse0")),
                ReadColor(face.GetProperty("diffuse1")),
                ReadColor(face.GetProperty("diffuse2")),
                ReadColor(face.GetProperty("diffuse3")),
                ReadColor(face.GetProperty("specular0")),
                ReadColor(face.GetProperty("specular1")),
                ReadColor(face.GetProperty("specular2")),
                ReadColor(face.GetProperty("specular3")),
                ReadVector4(face.GetProperty("diffuseOpacity")),
                ReadVector4(face.GetProperty("specularOpacity")));
        }
        return result;
    }

    private static IReadOnlyDictionary<string, ArmourSkinContract> ReadArmourSkinContracts(JsonElement root)
    {
        var result = new Dictionary<string, ArmourSkinContract>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("materials", out var materials)) return result;
        foreach (var material in materials.EnumerateArray())
        {
            if (!material.TryGetProperty("name", out var nameElement) ||
                !material.TryGetProperty("extras", out var extras) ||
                !extras.TryGetProperty("daoArmourSkin", out var armourSkin) ||
                armourSkin.GetProperty("schema").GetString() != "opendao-character-armour-skin-v1") continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (name.Length == 0 || armourSkin.GetProperty("semantic").GetString() != "ArmourSkinTint" ||
                armourSkin.GetProperty("maskChannel").GetString() != "alpha") continue;
            result[name] = new ArmourSkinContract(
                ReadColor(armourSkin.GetProperty("skinDiffuse")),
                armourSkin.GetProperty("skinOpacity").GetSingle());
        }
        return result;
    }

    private static void ValidateSkinContinuity(IEnumerable<FacePalette> faces,
        IEnumerable<ArmourSkinContract> bodies)
    {
        var face = faces.FirstOrDefault();
        var body = bodies.FirstOrDefault();
        if (face is null || body is null) return;
        ReportSkinContinuity(face.Diffuse3, body.SkinDiffuse, "exported-tint-contract");
    }

    private static void ValidateStoredSkinContinuity(Node root)
    {
        Color? face = null;
        Color? body = null;
        foreach (var mesh in root.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
        {
            if (mesh.Mesh is null) continue;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                var key = MaterialContractMetaPrefix + surface;
                if (!mesh.HasMeta(key)) continue;
                var stored = mesh.GetMeta(key).AsString();
                if (string.IsNullOrWhiteSpace(stored)) continue;
                using var document = JsonDocument.Parse(stored);
                var contract = document.RootElement;
                var kind = contract.GetProperty("kind").GetString();
                if (kind == "face") face = ReadColor(contract.GetProperty("diffuse3"));
                else if (kind == "armour-skin") body = ReadColor(contract.GetProperty("skinDiffuse"));
            }
        }
        if (face is not null && body is not null)
            ReportSkinContinuity(face.Value, body.Value, "cached-material-contract");
    }

    private static void ReportSkinContinuity(Color face, Color body, string source)
    {
        var matched = Math.Abs(face.R - body.R) <= 0.00001f &&
                      Math.Abs(face.G - body.G) <= 0.00001f &&
                      Math.Abs(face.B - body.B) <= 0.00001f;
        if (!matched)
        {
            GD.PushError($"OPENDAO_CHARACTER_SKIN_CONTINUITY_FAIL face={face} " +
                         $"body={body} source={source}");
            return;
        }
        GD.Print($"OPENDAO_CHARACTER_SKIN_CONTINUITY status=ready face={face} " +
                 $"body={body} source=shared-tint-alpha-zone input={source}");
    }

    private static IReadOnlyDictionary<string, EyelashContract> ReadEyelashContracts(JsonElement root)
    {
        var result = new Dictionary<string, EyelashContract>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("materials", out var materials)) return result;
        foreach (var material in materials.EnumerateArray())
        {
            if (!material.TryGetProperty("name", out var nameElement) ||
                !material.TryGetProperty("extras", out var extras) ||
                !extras.TryGetProperty("daoEyelash", out var eyelash) ||
                eyelash.GetProperty("schema").GetString() != "opendao-eyelash0-v1") continue;
            var name = nameElement.GetString() ?? string.Empty;
            if (name.Length == 0) continue;
            result[name] = new EyelashContract(
                eyelash.GetProperty("alphaThreshold").GetSingle(),
                eyelash.GetProperty("mipBias").GetSingle());
        }
        return result;
    }

    private static Color ReadColor(JsonElement value)
    {
        var channels = value.EnumerateArray().Select(channel => channel.GetSingle()).ToArray();
        return new Color(channels[0], channels[1], channels[2], 1);
    }

    private static Vector4 ReadVector4(JsonElement value)
    {
        var channels = value.EnumerateArray().Select(channel => channel.GetSingle()).ToArray();
        return new Vector4(channels[0], channels[1], channels[2], channels[3]);
    }

    private static void StoreMaterialContract(MeshInstance3D mesh, int surface, object contract) =>
        mesh.SetMeta(MaterialContractMetaPrefix + surface,
            JsonSerializer.Serialize(contract));

    private static void RestoreMaterialContract(MeshInstance3D mesh, int surface,
        ShaderMaterial material)
    {
        var key = MaterialContractMetaPrefix + surface;
        if (!mesh.HasMeta(key)) return;
        var stored = mesh.GetMeta(key).AsString();
        if (string.IsNullOrWhiteSpace(stored)) return;
        using var document = JsonDocument.Parse(stored);
        var contract = document.RootElement;
        if (!contract.TryGetProperty("kind", out var kindElement)) return;
        switch (kindElement.GetString())
        {
            case "hair":
                SetColor(material, "diffuse_tint_0", contract, "diffuse0");
                SetColor(material, "diffuse_tint_1", contract, "diffuse1");
                SetColor(material, "diffuse_tint_2", contract, "diffuse2");
                SetColor(material, "specular_tint_0", contract, "specular0");
                SetColor(material, "specular_tint_1", contract, "specular1");
                SetColor(material, "specular_tint_2", contract, "specular2");
                material.SetShaderParameter("primary_specular_mask",
                    contract.GetProperty("primarySpecularMask").GetSingle());
                material.SetShaderParameter("primary_specular_power",
                    contract.GetProperty("primarySpecularPower").GetSingle());
                material.SetShaderParameter("secondary_specular_power",
                    contract.GetProperty("secondarySpecularPower").GetSingle());
                material.SetShaderParameter("noise_tiling", contract.GetProperty("noiseTiling").GetSingle());
                material.SetShaderParameter("roughness", contract.GetProperty("roughness").GetSingle());
                material.SetShaderParameter("metallic_value", contract.GetProperty("metallic").GetSingle());
                material.SetShaderParameter("has_roughness_map",
                    contract.GetProperty("hasRoughnessMap").GetBoolean());
                material.SetShaderParameter("has_metallic_map",
                    contract.GetProperty("hasMetallicMap").GetBoolean());
                material.SetShaderParameter("packed_bioware", true);
                break;
            case "face":
                SetColor(material, "tint_diffuse_0", contract, "diffuse0");
                SetColor(material, "tint_diffuse_1", contract, "diffuse1");
                SetColor(material, "tint_diffuse_2", contract, "diffuse2");
                SetColor(material, "tint_diffuse_3", contract, "diffuse3");
                SetColor(material, "tint_specular_0", contract, "specular0");
                SetColor(material, "tint_specular_1", contract, "specular1");
                SetColor(material, "tint_specular_2", contract, "specular2");
                SetColor(material, "tint_specular_3", contract, "specular3");
                material.SetShaderParameter("tint_diffuse_opacity",
                    ReadVector4(contract.GetProperty("diffuseOpacity")));
                material.SetShaderParameter("tint_specular_opacity",
                    ReadVector4(contract.GetProperty("specularOpacity")));
                material.SetShaderParameter("use_face_tint", true);
                material.SetShaderParameter("use_facefx_emotions", false);
                material.SetShaderParameter("roughness", contract.GetProperty("roughness").GetSingle());
                material.SetShaderParameter("specular", contract.GetProperty("specular").GetSingle());
                material.SetShaderParameter("metallic", contract.GetProperty("metallic").GetSingle());
                material.SetShaderParameter("normal_strength",
                    contract.GetProperty("normalStrength").GetSingle());
                material.SetShaderParameter("use_normal_texture",
                    contract.GetProperty("useNormalTexture").GetBoolean());
                material.SetShaderParameter("use_roughness_texture",
                    contract.GetProperty("useRoughnessTexture").GetBoolean());
                material.SetShaderParameter("use_metallic_texture",
                    contract.GetProperty("useMetallicTexture").GetBoolean());
                material.SetShaderParameter("uv1_scale", ReadVector3(contract.GetProperty("uv1Scale")));
                material.SetShaderParameter("uv1_offset", ReadVector3(contract.GetProperty("uv1Offset")));
                break;
            case "armour-skin":
                SetColor(material, "skin_diffuse", contract, "skinDiffuse");
                material.SetShaderParameter("skin_opacity", contract.GetProperty("skinOpacity").GetSingle());
                material.SetShaderParameter("roughness", contract.GetProperty("roughness").GetSingle());
                material.SetShaderParameter("specular", contract.GetProperty("specular").GetSingle());
                material.SetShaderParameter("metallic", contract.GetProperty("metallic").GetSingle());
                material.SetShaderParameter("normal_strength",
                    contract.GetProperty("normalStrength").GetSingle());
                material.SetShaderParameter("use_normal_texture",
                    contract.GetProperty("useNormalTexture").GetBoolean());
                material.SetShaderParameter("uv1_scale", ReadVector3(contract.GetProperty("uv1Scale")));
                material.SetShaderParameter("uv1_offset", ReadVector3(contract.GetProperty("uv1Offset")));
                break;
            case "eyelash":
                material.SetShaderParameter("alpha_threshold",
                    contract.GetProperty("alphaThreshold").GetSingle());
                material.SetShaderParameter("mip_bias", contract.GetProperty("mipBias").GetSingle());
                break;
        }
        GD.Print($"OPENDAO_CHARACTER_MATERIAL_CONTRACT status=restored " +
                 $"kind={kindElement.GetString()} surface={surface}");
    }

    private static void SetColor(ShaderMaterial material, string parameter,
        JsonElement contract, string property) =>
        material.SetShaderParameter(parameter, ReadColor(contract.GetProperty(property)));

    private static Vector3 ReadVector3(JsonElement value)
    {
        var channels = value.EnumerateArray().Select(channel => channel.GetSingle()).ToArray();
        return new Vector3(channels[0], channels[1], channels[2]);
    }

    private static float[] Channels(Color value) => [value.R, value.G, value.B];
    private static float[] Channels(Vector4 value) => [value.X, value.Y, value.Z, value.W];
    private static float[] Channels(Vector3 value) => [value.X, value.Y, value.Z];

    private static string BuildCacheFingerprint()
    {
        var paths = new[] { HairShaderPath, FaceShaderPath, EyelashShaderPath, ArmourSkinShaderPath }
            .Select(ProjectSettings.GlobalizePath).ToArray();
        if (paths.Any(path => !File.Exists(path)))
            return "dao-character-materials-v7|shader-missing";
        using var stream = new MemoryStream();
        foreach (var path in paths) stream.Write(File.ReadAllBytes(path));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            stream.ToArray())).ToLowerInvariant();
        // v7 adds Character.mat/ArmourSkinTint while preserving the v6
        // mesh-owned copy of every character-material uniform
        // contract and restores it after PackedScene cache loads. Godot 4.7's
        // glTF/PackedScene path does not reliably retain custom ShaderMaterial
        // parameter overrides by itself.
        return $"dao-character-materials-v7|{hash}";
    }

    private sealed record HairPalette(Color Diffuse0, Color Diffuse1, Color Diffuse2,
        Color Specular0, Color Specular1, Color Specular2,
        float PrimarySpecularMask, float PrimarySpecularPower,
        float SecondarySpecularPower, float NoiseTiling);

    private sealed record FacePalette(Color Diffuse0, Color Diffuse1, Color Diffuse2,
        Color Diffuse3, Color Specular0, Color Specular1, Color Specular2,
        Color Specular3, Vector4 DiffuseOpacity, Vector4 SpecularOpacity);

    private sealed record ArmourSkinContract(Color SkinDiffuse, float SkinOpacity);

    private sealed record EyelashContract(float AlphaThreshold, float MipBias);

    private sealed record CharacterLighting(AuthoredLightingProfile Profile,
        AuthoredPointLightProfile[] PointLights);
}
