using Godot;
using System.Buffers.Binary;
using System.Text.Json;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Core;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

/// <summary>
/// Restores BioWare character material semantics lost by generic glTF import.
/// DAO HairAlpha stores strand coverage/luminance in the packed texture alpha;
/// treating its RGB normal data as albedo produces the blue-black hair visible
/// in earlier comparison captures.
/// </summary>
public sealed class DaoCharacterMaterialPostprocessor : IGodotModelPostprocessor, ICharacterLightingBinder
{
    private const string MaterialContractMetaPrefix = "opendao_character_material_contract_";
    internal const string WorldMaterialIdentityMetaPrefix = "opendao_world_material_identity_";
    internal const string WorldMaterialIdentityMeta = "opendao_world_material_identity";
    private const string HairShaderPath = "res://shaders/dao_character_hair.gdshader";
    private const string FaceShaderPath = "res://shaders/dao_facefx_material.gdshader";
    private const string EyelashShaderPath = "res://shaders/dao_character_eyelash.gdshader";
    private const string ArmourSkinShaderPath = "res://shaders/dao_character_armour_skin.gdshader";
    private const string EnhancedHairShaderPath = "res://shaders/dao_character_hair_enhanced.gdshader";
    private const string EnhancedFaceShaderPath = "res://shaders/dao_facefx_material_enhanced.gdshader";
    private const string EnhancedEyelashShaderPath = "res://shaders/dao_character_eyelash_enhanced.gdshader";
    private const string EnhancedArmourSkinShaderPath =
        "res://shaders/dao_character_armour_skin_enhanced.gdshader";
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
                if (mesh.GetActiveMaterial(surface) is not Material activeMaterial) continue;
                RestoreWorldMaterialIdentity(mesh, surface, activeMaterial);
                if (activeMaterial is not ShaderMaterial material) continue;
                RestoreMaterialContract(mesh, surface, material);
                if (material.ResourceName.EndsWith("_Nikami.Aurora.GodotRuntimeHair", StringComparison.Ordinal))
                {
                    TrackMaterial(hairMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
                else if (material.ResourceName.EndsWith("_Nikami.Aurora.GodotRuntimeFace0", StringComparison.Ordinal))
                {
                    TrackMaterial(faceMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
                else if (material.ResourceName.EndsWith("_Nikami.Aurora.GodotRuntimeEyelash0", StringComparison.Ordinal))
                {
                    TrackMaterial(eyelashMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, false);
                }
                else if (material.ResourceName.EndsWith("_Nikami.Aurora.GodotRuntimeArmourSkin", StringComparison.Ordinal))
                {
                    TrackMaterial(armourSkinMaterials, material);
                    if (currentLighting is not null) BindAuthoredLighting(material, currentLighting, true);
                }
            }
        }
        ValidateStoredSkinContinuity(root);
    }

    public void Process(Node3D model, GltfState sourceState, string sourcePath)
    {
        var enhancedPresentation = UseEnhancedPresentation();
        hairShader ??= GD.Load<Shader>(enhancedPresentation
            ? EnhancedHairShaderPath : HairShaderPath);
        faceShader ??= GD.Load<Shader>(enhancedPresentation
            ? EnhancedFaceShaderPath : FaceShaderPath);
        eyelashShader ??= GD.Load<Shader>(enhancedPresentation
            ? EnhancedEyelashShaderPath : EyelashShaderPath);
        armourSkinShader ??= GD.Load<Shader>(enhancedPresentation
            ? EnhancedArmourSkinShaderPath : ArmourSkinShaderPath);
        if (hairShader is null || faceShader is null || eyelashShader is null || armourSkinShader is null)
        {
            GD.PushError("OPENDAO_CHARACTER_MATERIAL_FAIL reason=shader-missing " +
                         $"hair={(hairShader is null ? 0 : 1)} face={(faceShader is null ? 0 : 1)} " +
                         $"eyelash={(eyelashShader is null ? 0 : 1)} " +
                         $"armour_skin={(armourSkinShader is null ? 0 : 1)}");
            return;
        }

        using var document = JsonDocument.Parse(Godot.Json.Stringify(sourceState.Json));
        var worldMaterialIdentities = ReadWorldMaterialIdentities(
            document.RootElement, sourcePath);
        var stateMaterialIndices = ReadStateMaterialIndices(
            sourceState, worldMaterialIdentities.Materials.Count, sourcePath);
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
            // Generated UCX/COLL nodes are source collision carriers, never
            // render surfaces. The world batching policy suppresses them from
            // draw submission, so do not invent a material identity for their
            // Godot-synthesized placeholder material.
            if (mesh.Mesh is null || WorldCollisionPolicy.IsCollisionProxy(mesh.Name)) continue;
            for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
            {
                if (mesh.GetActiveMaterial(surface) is not BaseMaterial3D importedSource) continue;
                var binding = ResolveWorldMaterialBinding(document.RootElement, sourceState,
                    mesh, surface, importedSource, stateMaterialIndices,
                    worldMaterialIdentities, sourcePath);
                if (importedSource.Duplicate() is not BaseMaterial3D source)
                    throw new InvalidDataException(
                        $"Imported material cannot be isolated for identity binding: " +
                        $"path={sourcePath} mesh={mesh.Name} surface={surface}");
                source.ResourceName = binding.MaterialName;
                mesh.SetSurfaceOverrideMaterial(surface, source);
                ApplyImportedPbrContract(source, binding.Pbr,
                    enhancedPresentation);
                BindWorldMaterialIdentity(source, binding.Identity);
                StoreWorldMaterialIdentity(mesh, surface, binding.Identity);
                if (source.AlbedoTexture is null) continue;
                if (armourSkinContracts.TryGetValue(source.ResourceName, out var armourSkinContract) &&
                    source.AOTexture is not null)
                {
                    var material = new ShaderMaterial
                    {
                        Shader = armourSkinShader,
                        ResourceName = source.ResourceName + "_Nikami.Aurora.GodotRuntimeArmourSkin"
                    };
                    CopyWorldMaterialIdentity(source, material, "armour-skin-shader");
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
                        ResourceName = source.ResourceName + "_Nikami.Aurora.GodotRuntimeEyelash0"
                    };
                    CopyWorldMaterialIdentity(source, material, "eyelash-shader");
                    material.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    material.SetShaderParameter("alpha_threshold", eyelashContract.AlphaThreshold);
                    material.SetShaderParameter("mip_bias", eyelashContract.MipBias);
                    material.SetShaderParameter("roughness", source.Roughness);
                    material.SetShaderParameter("specular", source.MetallicSpecular);
                    material.SetShaderParameter("metallic", source.Metallic);
                    mesh.SetSurfaceOverrideMaterial(surface, material);
                    StoreMaterialContract(mesh, surface, new
                    {
                        kind = "eyelash",
                        alphaThreshold = eyelashContract.AlphaThreshold,
                        mipBias = eyelashContract.MipBias,
                        roughness = source.Roughness,
                        specular = source.MetallicSpecular,
                        metallic = source.Metallic
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
                        ResourceName = source.ResourceName + "_Nikami.Aurora.GodotRuntimeHair"
                    };
                    CopyWorldMaterialIdentity(source, material, "hair-shader");
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
                    ResourceName = source.ResourceName + "_Nikami.Aurora.GodotRuntimeFace0"
                };
                CopyWorldMaterialIdentity(source, faceMaterial, "face-shader");
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
        var customSurfaces = installedHair + installedFaces + installedEyelashes +
                             installedArmourSkin;
        if (customSurfaces > 0)
            GD.Print("OPENDAO_CHARACTER_PBR_PIPELINE status=ready " +
                     $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                     $"surfaces={customSurfaces} shaded={(enhancedPresentation ? customSurfaces : 0)} " +
                     $"authored_unshaded={(enhancedPresentation ? 0 : customSurfaces)} " +
                     $"variant={(enhancedPresentation ? "godot-pbr" : "dao-authored-lighting")} " +
                     "layout_override=none parity_claim=none");
        GD.Print($"OPENDAO_IMPORTED_PBR status=ready " +
                 $"model={worldMaterialIdentities.ModelName} " +
                 $"materials={worldMaterialIdentities.Materials.Count} " +
                 $"alpha_mask={worldMaterialIdentities.Materials.Count(value => value.Pbr.AlphaMode == "MASK")} " +
                 $"alpha_blend={worldMaterialIdentities.Materials.Count(value => value.Pbr.AlphaMode == "BLEND")} " +
                 $"double_sided={worldMaterialIdentities.Materials.Count(value => value.Pbr.DoubleSided)} " +
                 $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                 "source=gltf-material-contract mao=unsupported layout_override=none");
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
                 $"probe_sha256={lighting.ProbeResourceSha256} " +
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

    private static void StoreWorldMaterialIdentity(
        MeshInstance3D mesh, int surface, string identity) =>
        mesh.SetMeta(WorldMaterialIdentityMetaPrefix + surface, identity);

    internal static bool HasStoredWorldMaterialIdentity(MeshInstance3D mesh, int surface) =>
        mesh.HasMeta(WorldMaterialIdentityMetaPrefix + surface) &&
        !string.IsNullOrWhiteSpace(
            mesh.GetMeta(WorldMaterialIdentityMetaPrefix + surface).AsString());

    private static void RestoreWorldMaterialIdentity(
        MeshInstance3D mesh, int surface, Material material)
    {
        var key = WorldMaterialIdentityMetaPrefix + surface;
        if (!mesh.HasMeta(key)) return;
        var identity = mesh.GetMeta(key).AsString();
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException(
                $"Stored world material identity is blank: mesh={mesh.Name} surface={surface}");
        material.SetMeta(WorldMaterialIdentityMeta, identity);
    }

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
                material.SetShaderParameter("roughness", contract.GetProperty("roughness").GetSingle());
                material.SetShaderParameter("specular", contract.GetProperty("specular").GetSingle());
                material.SetShaderParameter("metallic", contract.GetProperty("metallic").GetSingle());
                break;
        }
        GD.Print($"OPENDAO_CHARACTER_MATERIAL_CONTRACT status=restored " +
                 $"kind={kindElement.GetString()} surface={surface}");
    }

    private static void SetColor(ShaderMaterial material, string parameter,
        JsonElement contract, string property) =>
        material.SetShaderParameter(parameter, ReadColor(contract.GetProperty(property)));

    private static WorldMaterialIdentityCatalog ReadWorldMaterialIdentities(
        JsonElement root, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new InvalidDataException("Imported source model path is absent.");
        var sourceModelPath = Path.GetFullPath(sourcePath);
        ValidateOwnedImportPath(sourceModelPath);
        if (!File.Exists(sourceModelPath))
            throw new InvalidDataException($"Imported source model is absent: {sourceModelPath}");
        var sourceBasePath = Path.GetDirectoryName(sourceModelPath) ??
                             throw new InvalidDataException(
                                 $"Imported source model has no parent directory: {sourceModelPath}");
        var sourceModelBytes = File.ReadAllBytes(sourceModelPath);
        var sourceModelHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(sourceModelBytes))
            .ToLowerInvariant();
        var materials = root.TryGetProperty("materials", out var materialArray) &&
                        materialArray.ValueKind == JsonValueKind.Array
            ? materialArray
            : default;
        var textures = root.TryGetProperty("textures", out var textureArray) &&
                       textureArray.ValueKind == JsonValueKind.Array
            ? textureArray.EnumerateArray().ToArray()
            : [];
        using var sourceGlb = Path.GetExtension(sourceModelPath)
            .Equals(".glb", StringComparison.OrdinalIgnoreCase)
            ? ReadSourceGlb(sourceModelBytes, sourceModelPath)
            : null;
        var images = root.TryGetProperty("images", out var imageArray) &&
                     imageArray.ValueKind == JsonValueKind.Array
            ? imageArray.EnumerateArray().ToArray()
            : [];

        static float FiniteScalar(JsonElement owner, string property, float fallback)
        {
            if (!owner.TryGetProperty(property, out var element)) return fallback;
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetSingle(out var value) ||
                !float.IsFinite(value))
                throw new InvalidDataException(
                    $"glTF material {property} must be a finite number.");
            return value;
        }

        static float[] FiniteVector(JsonElement owner, string property,
            IReadOnlyList<float> fallback)
        {
            if (!owner.TryGetProperty(property, out var element)) return fallback.ToArray();
            if (element.ValueKind != JsonValueKind.Array ||
                element.GetArrayLength() != fallback.Count)
                throw new InvalidDataException(
                    $"glTF material {property} must contain {fallback.Count} values.");
            var result = new float[fallback.Count];
            var index = 0;
            foreach (var channel in element.EnumerateArray())
            {
                if (channel.ValueKind != JsonValueKind.Number ||
                    !channel.TryGetSingle(out result[index]) || !float.IsFinite(result[index]))
                    throw new InvalidDataException(
                        $"glTF material {property}[{index}] must be finite.");
                index++;
            }
            return result;
        }

        static string Channels(IReadOnlyList<float> values) => string.Join(',', values.Select(
            value => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

        string TextureIdentity(JsonElement owner, string property)
        {
            if (!owner.TryGetProperty(property, out var textureInfo)) return "none";
            if (textureInfo.ValueKind != JsonValueKind.Object ||
                !textureInfo.TryGetProperty("index", out var textureIndexElement))
                throw new InvalidDataException(
                    $"glTF material {property} must contain a texture index.");
            var textureIndex = ReadStrictJsonIndex(
                textureIndexElement, $"glTF material {property} texture");
            if (textureIndex < 0 || textureIndex >= textures.Length ||
                !textures[textureIndex].TryGetProperty("source", out var sourceElement))
                throw new InvalidDataException($"glTF material {property} has an invalid texture index.");
            var imageIndex = ReadStrictJsonIndex(
                sourceElement, $"glTF material {property} image");
            if (imageIndex < 0 || imageIndex >= images.Length)
                throw new InvalidDataException($"glTF material {property} has an invalid image source.");
            var image = images[imageIndex];
            var hasUri = image.TryGetProperty("uri", out var uriElement);
            var hasBufferView = image.TryGetProperty("bufferView", out var bufferViewElement);
            if (hasUri == hasBufferView)
                throw new InvalidDataException(
                    $"glTF material {property} image must use exactly one URI or bufferView source.");
            if (hasBufferView)
            {
                if (sourceGlb is null ||
                    !image.TryGetProperty("mimeType", out var mimeTypeElement) ||
                    mimeTypeElement.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException(
                        $"glTF material {property} embedded image has no validated GLB MIME contract.");
                var bufferView = ReadStrictJsonIndex(bufferViewElement,
                    $"glTF material {property} embedded image bufferView");
                return HashEmbeddedImage(sourceGlb, imageIndex, bufferView,
                    mimeTypeElement.GetString() ?? string.Empty, property);
            }
            var uri = Uri.UnescapeDataString(uriElement.GetString() ?? string.Empty);
            if (uri.Length == 0 || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"glTF material {property} has no external source identity.");
            var path = Path.GetFullPath(Path.Combine(sourceBasePath,
                uri.Replace('/', Path.DirectorySeparatorChar)));
            ValidateOwnedImportPath(path);
            if (!File.Exists(path))
                throw new InvalidDataException($"glTF material texture is absent: {path}");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                File.ReadAllBytes(path))).ToLowerInvariant();
            var claimed = Path.GetFileNameWithoutExtension(path);
            if (claimed.Length != 64 || !claimed.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                !hash.Equals(claimed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"glTF material texture identity is not content-addressed: {path}");
            return hash;
        }

        var result = new List<WorldMaterialIdentity>();
        if (materials.ValueKind != JsonValueKind.Array)
            return new WorldMaterialIdentityCatalog(
                Path.GetFileName(sourceModelPath), sourceModelHash, result);
        foreach (var record in materials.EnumerateArray())
        {
            var name = record.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (name.Length == 0)
                throw new InvalidDataException("glTF material has no source name.");
            var pbr = record.TryGetProperty("pbrMetallicRoughness", out var pbrElement)
                ? pbrElement
                : default;
            if (pbr.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
                throw new InvalidDataException(
                    $"glTF material pbrMetallicRoughness must be an object: {name}");
            var baseColor = pbr.ValueKind == JsonValueKind.Object
                ? TextureIdentity(pbr, "baseColorTexture")
                : "none";
            var metallicRoughness = pbr.ValueKind == JsonValueKind.Object
                ? TextureIdentity(pbr, "metallicRoughnessTexture")
                : "none";
            var normal = TextureIdentity(record, "normalTexture");
            var occlusion = TextureIdentity(record, "occlusionTexture");
            var emissive = TextureIdentity(record, "emissiveTexture");
            var baseColorFactor = pbr.ValueKind == JsonValueKind.Object
                ? FiniteVector(pbr, "baseColorFactor", [1, 1, 1, 1])
                : new float[] { 1, 1, 1, 1 };
            var metallicFactor = pbr.ValueKind == JsonValueKind.Object
                ? FiniteScalar(pbr, "metallicFactor", 1)
                : 1;
            var roughnessFactor = pbr.ValueKind == JsonValueKind.Object
                ? FiniteScalar(pbr, "roughnessFactor", 1)
                : 1;
            if (metallicFactor is < 0 or > 1 || roughnessFactor is < 0 or > 1 ||
                baseColorFactor.Any(value => value is < 0 or > 1))
                throw new InvalidDataException(
                    $"glTF material PBR factors are outside [0,1]: {name}");
            var alphaMode = record.TryGetProperty("alphaMode", out var alphaModeElement)
                ? alphaModeElement.GetString() ?? string.Empty
                : "OPAQUE";
            if (alphaMode is not ("OPAQUE" or "MASK" or "BLEND"))
                throw new InvalidDataException(
                    $"glTF material alpha mode is unsupported: {name} ({alphaMode})");
            var alphaCutoff = FiniteScalar(record, "alphaCutoff", .5f);
            if (alphaCutoff is < 0 or > 1)
                throw new InvalidDataException(
                    $"glTF material alpha cutoff is outside [0,1]: {name}");
            var doubleSided = record.TryGetProperty("doubleSided", out var doubleSidedElement) &&
                              doubleSidedElement.ValueKind == JsonValueKind.True;
            if (record.TryGetProperty("doubleSided", out doubleSidedElement) &&
                doubleSidedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException(
                    $"glTF material doubleSided must be boolean: {name}");
            var normalScale = record.TryGetProperty("normalTexture", out var normalTexture) &&
                              normalTexture.ValueKind == JsonValueKind.Object
                ? FiniteScalar(normalTexture, "scale", 1)
                : 1;
            if (normalScale < 0)
                throw new InvalidDataException(
                    $"glTF material normal scale is negative: {name}");
            var emissiveFactor = FiniteVector(record, "emissiveFactor", [0, 0, 0]);
            var pbrContract = new WorldPbrContract(
                new Color(baseColorFactor[0], baseColorFactor[1], baseColorFactor[2],
                    baseColorFactor[3]),
                metallicFactor, roughnessFactor, normalScale, alphaMode,
                alphaCutoff, doubleSided);
            result.Add(new WorldMaterialIdentity(name,
                $"kind=installed-gltf-pbr;model={Path.GetFileName(sourceModelPath)};" +
                $"model_sha256={sourceModelHash};name={name};base_color={baseColor};" +
                $"normal={normal};metallic_roughness={metallicRoughness};" +
                $"occlusion={occlusion};emissive={emissive};" +
                $"base_color_factor={Channels(baseColorFactor)};" +
                $"metallic_factor={metallicFactor:R};roughness_factor={roughnessFactor:R};" +
                $"normal_scale={normalScale:R};emissive_factor={Channels(emissiveFactor)};" +
                $"alpha_mode={alphaMode};alpha_cutoff={alphaCutoff:R};" +
                $"double_sided={(doubleSided ? 1 : 0)};pbr_status=ready;" +
                "mao_status=unsupported;semantic_status=imported-gltf-slots-mao-unresolved",
                pbrContract));
        }
        return new WorldMaterialIdentityCatalog(
            Path.GetFileName(sourceModelPath), sourceModelHash, result);
    }

    private static IReadOnlyDictionary<ulong, int> ReadStateMaterialIndices(
        GltfState sourceState, int expectedCount, string sourcePath)
    {
        if (sourceState.Materials.Count != expectedCount)
            throw new InvalidDataException(
                $"Godot/source glTF material count mismatch: path={sourcePath} " +
                $"state={sourceState.Materials.Count} source={expectedCount}");
        var result = new Dictionary<ulong, int>();
        for (var index = 0; index < sourceState.Materials.Count; index++)
        {
            var material = sourceState.Materials[index] ??
                           throw new InvalidDataException(
                               $"Godot glTF material is null: path={sourcePath} material={index}");
            if (!result.TryAdd(material.GetInstanceId(), index))
                throw new InvalidDataException(
                    $"Godot glTF material instance is ambiguous: path={sourcePath} material={index}");
        }
        return result;
    }

    private static WorldSurfaceMaterialBinding ResolveWorldMaterialBinding(
        JsonElement root, GltfState sourceState, MeshInstance3D mesh, int surface,
        BaseMaterial3D importedMaterial, IReadOnlyDictionary<ulong, int> stateMaterialIndices,
        WorldMaterialIdentityCatalog catalog, string sourcePath)
    {
        var stateMapped = stateMaterialIndices.TryGetValue(
            importedMaterial.GetInstanceId(), out var stateMaterialIndex);
        var nodeIndex = sourceState.GetNodeIndex(mesh);
        if (nodeIndex < 0)
        {
            if (!stateMapped || stateMaterialIndex < 0 ||
                stateMaterialIndex >= catalog.Materials.Count)
                throw new InvalidDataException(
                    $"Imported mesh has neither a state-material nor glTF node mapping: " +
                    $"path={sourcePath} mesh={mesh.Name} surface={surface} node={nodeIndex}");
            return CreateIndexedWorldMaterialBinding(importedMaterial,
                catalog, stateMaterialIndex, surface,
                "generated-unmapped", "generated-unmapped", "state-resource");
        }
        if (!root.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array || nodeIndex >= nodes.GetArrayLength())
            throw new InvalidDataException(
                $"Imported mesh has no exact glTF node mapping: path={sourcePath} " +
                $"mesh={mesh.Name} surface={surface} node={nodeIndex}");
        var node = nodes[nodeIndex];
        if (!node.TryGetProperty("mesh", out var meshIndexElement))
            throw new InvalidDataException(
                $"Imported mesh node has no glTF mesh index: path={sourcePath} " +
                $"mesh={mesh.Name} surface={surface} node={nodeIndex}");
        var meshIndex = ReadStrictJsonIndex(meshIndexElement,
            $"glTF node {nodeIndex} mesh");
        if (!root.TryGetProperty("meshes", out var meshes) ||
            meshes.ValueKind != JsonValueKind.Array || meshIndex < 0 ||
            meshIndex >= meshes.GetArrayLength() ||
            !meshes[meshIndex].TryGetProperty("primitives", out var primitives) ||
            primitives.ValueKind != JsonValueKind.Array ||
            primitives.GetArrayLength() != mesh.Mesh!.GetSurfaceCount() ||
            surface < 0 || surface >= primitives.GetArrayLength())
            throw new InvalidDataException(
                $"Imported mesh surfaces do not match glTF primitives: path={sourcePath} " +
                $"mesh={mesh.Name} surface={surface} node={nodeIndex} source_mesh={meshIndex}");

        var primitive = primitives[surface];
        var materialIndex = primitive.TryGetProperty("material", out var materialIndexElement)
            ? ReadStrictJsonIndex(materialIndexElement,
                $"glTF mesh {meshIndex} primitive {surface} material")
            : -1;
        if (stateMapped && stateMaterialIndex != materialIndex)
            throw new InvalidDataException(
                $"Godot/source glTF material mapping disagrees: path={sourcePath} " +
                $"mesh={mesh.Name} surface={surface} node={nodeIndex} source_mesh={meshIndex} " +
                $"state_material={stateMaterialIndex} primitive_material={materialIndex}");
        if (materialIndex < -1 || materialIndex >= catalog.Materials.Count)
            throw new InvalidDataException(
                $"glTF primitive material index is out of range: path={sourcePath} " +
                $"mesh={mesh.Name} surface={surface} node={nodeIndex} source_mesh={meshIndex} " +
                $"material={materialIndex}");

        var mapping = stateMapped ? "state-resource+primitive" : "primitive-validated";
        if (materialIndex == -1)
        {
            if (stateMapped)
                throw new InvalidDataException(
                    $"Godot material exists for a glTF default-material primitive: path={sourcePath} " +
                    $"mesh={mesh.Name} surface={surface} node={nodeIndex} source_mesh={meshIndex}");
            return new WorldSurfaceMaterialBinding("gltf_default",
                $"kind=gltf-default;model={catalog.ModelName};" +
                $"model_sha256={catalog.ModelSha256};node={nodeIndex};mesh={meshIndex};" +
                $"surface={surface};material=default;mapping={mapping};" +
                "pbr_status=ready;mao_status=not-applicable;" +
                "semantic_status=gltf-default-no-material",
                WorldPbrContract.Default);
        }

        return CreateIndexedWorldMaterialBinding(importedMaterial, catalog,
            materialIndex, surface, nodeIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            meshIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), mapping);
    }

    private static WorldSurfaceMaterialBinding CreateIndexedWorldMaterialBinding(
        BaseMaterial3D importedMaterial, WorldMaterialIdentityCatalog catalog,
        int materialIndex, int surface, string nodeIdentity, string meshIdentity,
        string mapping)
    {
        var sourceMaterial = catalog.Materials[materialIndex];
        var runtimeNameStatus = importedMaterial.ResourceName.Length == 0
            ? "blank"
            : importedMaterial.ResourceName.Equals(sourceMaterial.Name, StringComparison.Ordinal)
                ? "match"
                : "mismatch-ignored-index-authoritative";
        return new WorldSurfaceMaterialBinding(sourceMaterial.Name,
            sourceMaterial.Identity + $";node={nodeIdentity};mesh={meshIdentity};surface={surface};" +
            $"material={materialIndex};mapping={mapping};runtime_name_status={runtimeNameStatus}",
            sourceMaterial.Pbr);
    }

    private sealed record WorldMaterialIdentity(
        string Name,
        string Identity,
        WorldPbrContract Pbr);

    private sealed record WorldMaterialIdentityCatalog(
        string ModelName, string ModelSha256, IReadOnlyList<WorldMaterialIdentity> Materials);

    private sealed record WorldSurfaceMaterialBinding(
        string MaterialName,
        string Identity,
        WorldPbrContract Pbr);

    private sealed record WorldPbrContract(
        Color BaseColor,
        float Metallic,
        float Roughness,
        float NormalScale,
        string AlphaMode,
        float AlphaCutoff,
        bool DoubleSided)
    {
        public static WorldPbrContract Default { get; } = new(
            Colors.White, 1, 1, 1, "OPAQUE", .5f, false);
    }

    private static SourceGlb ReadSourceGlb(byte[] bytes, string path)
    {
        const uint GlbMagic = 0x46546c67;
        const uint JsonChunk = 0x4e4f534a;
        const uint BinaryChunk = 0x004e4942;
        if (bytes.Length < 20 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != GlbMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) != 2 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) != bytes.Length)
            throw new InvalidDataException($"Imported GLB header is malformed: {path}");

        var offset = 12;
        JsonDocument? json = null;
        var binaryOffset = -1;
        var binaryLength = 0;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 8)
                throw new InvalidDataException($"Imported GLB chunk header is truncated: {path}");
            var chunkLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)));
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
            offset += 8;
            if (chunkLength < 0 || chunkLength > bytes.Length - offset)
                throw new InvalidDataException($"Imported GLB chunk is out of range: {path}");
            if (chunkType == JsonChunk)
            {
                if (json is not null || binaryOffset >= 0)
                    throw new InvalidDataException($"Imported GLB JSON chunk order is invalid: {path}");
                json = JsonDocument.Parse(bytes.AsMemory(offset, chunkLength));
            }
            else if (chunkType == BinaryChunk)
            {
                if (json is null || binaryOffset >= 0)
                    throw new InvalidDataException($"Imported GLB binary chunk order is invalid: {path}");
                binaryOffset = offset;
                binaryLength = chunkLength;
            }
            else
            {
                throw new InvalidDataException($"Imported GLB has an unsupported chunk type: {path}");
            }
            offset += chunkLength;
        }
        if (offset != bytes.Length || json is null)
        {
            json?.Dispose();
            throw new InvalidDataException($"Imported GLB container is incomplete: {path}");
        }
        return new SourceGlb(bytes, json, binaryOffset, binaryLength);
    }

    private static string HashEmbeddedImage(SourceGlb glb, int imageIndex,
        int expectedBufferView, string expectedMimeType, string property)
    {
        if (expectedMimeType is not ("image/png" or "image/jpeg" or "image/webp") ||
            !glb.Json.RootElement.TryGetProperty("images", out var rawImages) ||
            rawImages.ValueKind != JsonValueKind.Array || imageIndex >= rawImages.GetArrayLength())
            throw new InvalidDataException(
                $"glTF material {property} embedded image MIME/index is unsupported.");
        var rawImage = rawImages[imageIndex];
        if (rawImage.TryGetProperty("uri", out _) ||
            !rawImage.TryGetProperty("bufferView", out var rawBufferViewElement) ||
            ReadStrictJsonIndex(rawBufferViewElement,
                $"glTF material {property} source bufferView") != expectedBufferView ||
            !rawImage.TryGetProperty("mimeType", out var rawMimeTypeElement) ||
            rawMimeTypeElement.ValueKind != JsonValueKind.String ||
            !string.Equals(rawMimeTypeElement.GetString(), expectedMimeType,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"glTF material {property} embedded image disagrees with the source GLB.");
        if (!glb.Json.RootElement.TryGetProperty("bufferViews", out var bufferViews) ||
            bufferViews.ValueKind != JsonValueKind.Array || expectedBufferView < 0 ||
            expectedBufferView >= bufferViews.GetArrayLength())
            throw new InvalidDataException(
                $"glTF material {property} embedded image bufferView is absent.");
        var view = bufferViews[expectedBufferView];
        if (!view.TryGetProperty("buffer", out var bufferElement) ||
            ReadStrictJsonIndex(bufferElement,
                $"glTF material {property} embedded image buffer") != 0 ||
            !view.TryGetProperty("byteLength", out var lengthElement))
            throw new InvalidDataException(
                $"glTF material {property} embedded image buffer contract is malformed.");
        var byteOffset = view.TryGetProperty("byteOffset", out var offsetElement)
            ? ReadStrictJsonIndex(offsetElement,
                $"glTF material {property} embedded image byteOffset")
            : 0;
        var byteLength = ReadStrictJsonIndex(lengthElement,
            $"glTF material {property} embedded image byteLength");
        if (glb.BinaryOffset < 0 || byteOffset < 0 || byteLength <= 0 ||
            byteOffset > glb.BinaryLength - byteLength)
            throw new InvalidDataException(
                $"glTF material {property} embedded image byte range is invalid.");
        var payload = glb.Bytes.AsSpan(glb.BinaryOffset + byteOffset, byteLength);
        var validSignature = expectedMimeType switch
        {
            "image/png" => payload.Length >= 8 &&
                           payload[..8].SequenceEqual(
                               new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            "image/jpeg" => payload.Length >= 2 && payload[0] == 0xff && payload[1] == 0xd8,
            "image/webp" => payload.Length >= 12 &&
                            payload[..4].SequenceEqual("RIFF"u8) &&
                            payload.Slice(8, 4).SequenceEqual("WEBP"u8),
            _ => false
        };
        if (!validSignature)
            throw new InvalidDataException(
                $"glTF material {property} embedded image signature disagrees with its MIME type.");
        return Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed class SourceGlb(
        byte[] bytes, JsonDocument json, int binaryOffset, int binaryLength) : IDisposable
    {
        public byte[] Bytes { get; } = bytes;
        public JsonDocument Json { get; } = json;
        public int BinaryOffset { get; } = binaryOffset;
        public int BinaryLength { get; } = binaryLength;
        public void Dispose() => Json.Dispose();
    }

    private static int ReadStrictJsonIndex(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) ||
            !double.IsFinite(number) || number != Math.Truncate(number) ||
            number < int.MinValue || number > int.MaxValue)
            throw new InvalidDataException($"{label} index is not a finite Int32 value.");
        return (int)number;
    }

    private static void ValidateOwnedImportPath(string path)
    {
        if (IsWithin(path, DaoRuntimePaths.Cache()) ||
            IsWithin(path, DaoRuntimePaths.Generated())) return;
        throw new InvalidDataException(
            $"Imported material payload escapes configured owned-data roots: {path}");
    }

    private static bool IsWithin(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root);
        var prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar,
                         Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void BindWorldMaterialIdentity(Material material, string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            throw new InvalidDataException("Imported material source identity is absent.");
        material.SetMeta(WorldMaterialIdentityMeta, identity);
    }

    private static void CopyWorldMaterialIdentity(
        Material source, Material target, string shaderSemantic)
    {
        if (!source.HasMeta(WorldMaterialIdentityMeta))
            throw new InvalidDataException(
                $"Shader material source identity is absent: {source.ResourceName}");
        target.SetMeta(WorldMaterialIdentityMeta,
            source.GetMeta(WorldMaterialIdentityMeta).AsString() +
            $";runtime_semantic={shaderSemantic}");
    }

    private static void ApplyImportedPbrContract(BaseMaterial3D material,
        WorldPbrContract source, bool enhanced)
    {
        material.AlbedoColor = source.BaseColor;
        material.Metallic = source.Metallic;
        material.Roughness = source.Roughness;
        material.NormalScale = source.NormalScale;
        material.NormalEnabled = material.NormalTexture is not null && source.NormalScale > 0;
        material.CullMode = source.DoubleSided
            ? BaseMaterial3D.CullModeEnum.Disabled
            : BaseMaterial3D.CullModeEnum.Back;
        switch (source.AlphaMode)
        {
            case "OPAQUE":
                material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                break;
            case "MASK":
                material.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                material.AlphaScissorThreshold = source.AlphaCutoff;
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
                break;
            case "BLEND":
                material.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported imported glTF alpha mode: {source.AlphaMode}");
        }
        // Filtering is a quality tier, not a source material semantic. Source
        // keeps the importer contract; enhanced Forward+ uses the installed DDS
        // mip chain anisotropically for oblique terrain, roofs, and cards.
        if (enhanced)
            material.TextureFilter =
                BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic;
    }

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
        var paths = new[]
            {
                HairShaderPath, FaceShaderPath, EyelashShaderPath, ArmourSkinShaderPath,
                EnhancedHairShaderPath, EnhancedFaceShaderPath, EnhancedEyelashShaderPath,
                EnhancedArmourSkinShaderPath
            }
            .Select(ProjectSettings.GlobalizePath).ToArray();
        if (paths.Any(path => !File.Exists(path)))
            return "dao-character-materials-v7|shader-missing";
        using var stream = new MemoryStream();
        foreach (var path in paths) stream.Write(File.ReadAllBytes(path));
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            stream.ToArray())).ToLowerInvariant();
        // v14 selects separate enhanced shaded character shaders while keeping
        // the source tier on the installed SH/point-light unshaded contract.
        // v13 binds exact glTF PBR factors, alpha mode/cutoff, and double-sided
        // state for every imported area and removes name-based foliage repair.
        // v12 adds hard surface-identity publication diagnostics. v11
        // preserves exact source identity on MeshInstance metadata so it
        // can be republished after Godot PackedScene material serialization.
        // v10 binds exact source-model/material/texture identities and must not
        // revive v9 PackedScenes created before that metadata existed. v8 also
        // restores anisotropic source sampling and coverage-tested foliage
        // instead of sorted alpha cards. v7 adds Character.mat/ArmourSkinTint while preserving the v6
        // mesh-owned copy of every character-material uniform
        // contract and restores it after PackedScene cache loads. Godot 4.7's
        // glTF/PackedScene path does not reliably retain custom ShaderMaterial
        // parameter overrides by itself.
        return $"dao-character-materials-v14|tier=" +
               $"{(UseEnhancedPresentation() ? "enhanced" : "source")}|{hash}";
    }

    private static bool UseEnhancedPresentation()
    {
        var requested = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_PRESENTATION_TIER")?.Trim().ToLowerInvariant() ?? string.Empty;
        var backend = RenderingQualityPolicy.ParseBackend(
            RenderingServer.GetCurrentRenderingMethod().ToString());
        return RenderingQualityPolicy.ParseTier(requested, backend) ==
               RenderingPresentationTier.Enhanced;
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
