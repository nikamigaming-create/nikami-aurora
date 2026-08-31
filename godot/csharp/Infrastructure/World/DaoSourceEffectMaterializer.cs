using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.GodotRuntime.Infrastructure.Archives;
using Nikami.Aurora.GodotRuntime.Rendering;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

/// <summary>
/// Materializes the supported subset of installed DAO MMH particle graphs.
/// Generated import-cache fallback meshes are never loaded by this path.
/// </summary>
internal sealed class DaoSourceEffectMaterializer
{
    private const string ModelArchiveRelativePath = "packages/core/data/modelhierarchies.erf";
    private const string MaterialArchiveRelativePath = "packages/core/data/materialobjects.erf";
    private const string TextureArchiveRelativePath = "packages/core/textures/high/texturepack.erf";
    private static readonly Shader IndependentBillboardMixShader =
        CreateIndependentScaleShader(additive: false, billboard: true);
    private static readonly Shader IndependentBillboardAddShader =
        CreateIndependentScaleShader(additive: true, billboard: true);
    private static readonly Shader IndependentHorizontalMixShader =
        CreateIndependentScaleShader(additive: false, billboard: false);
    private static readonly Shader IndependentHorizontalAddShader =
        CreateIndependentScaleShader(additive: true, billboard: false);

    private readonly ErfArchive models;
    private readonly ErfArchive materials;
    private readonly ErfArchive textures;
    private readonly Dictionary<string, Texture2D> textureCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> enhancedAtlasTextureCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> validatedMaterials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DragonAgeEffectDefinition> decodedContracts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> decodeFailures =
        new(StringComparer.OrdinalIgnoreCase);

    public DaoSourceEffectMaterializer(string gameRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameRoot);
        var root = Path.GetFullPath(gameRoot);
        models = ErfArchive.Open(ResolveUnderRoot(root, ModelArchiveRelativePath));
        materials = ErfArchive.Open(ResolveUnderRoot(root, MaterialArchiveRelativePath));
        textures = ErfArchive.Open(ResolveUnderRoot(root, TextureArchiveRelativePath));
    }

    private static Shader CreateIndependentScaleShader(bool additive, bool billboard)
    {
        var blend = additive ? "blend_add" : "blend_mix";
        var scaleVertex = billboard
            ? "VERTEX.xy *= axis_ratio;"
            : "VERTEX.xz *= axis_ratio;";
        var billboardVertex = billboard
            ? """
                mat4 world = mat4(
                    vec4(normalize(INV_VIEW_MATRIX[0].xyz) * length(MODEL_MATRIX[0].xyz), 0.0),
                    vec4(normalize(INV_VIEW_MATRIX[1].xyz) * length(MODEL_MATRIX[1].xyz), 0.0),
                    vec4(normalize(INV_VIEW_MATRIX[2].xyz) * length(MODEL_MATRIX[2].xyz), 0.0),
                    MODEL_MATRIX[3]);
                float angle_cos = cos(INSTANCE_CUSTOM.x);
                float angle_sin = sin(INSTANCE_CUSTOM.x);
                world = world * mat4(
                    vec4(angle_cos, -angle_sin, 0.0, 0.0),
                    vec4(angle_sin, angle_cos, 0.0, 0.0),
                    vec4(0.0, 0.0, 1.0, 0.0),
                    vec4(0.0, 0.0, 0.0, 1.0));
                MODELVIEW_MATRIX = VIEW_MATRIX * world;
                """
            : string.Empty;
        return new Shader
        {
            Code = $$"""
                shader_type spatial;
                render_mode unshaded, depth_draw_never, cull_disabled, {{blend}};
                uniform sampler2D particle_texture : source_color, repeat_disable, filter_linear_mipmap;
                uniform sampler2D scale_x_ratio : repeat_disable, filter_linear;
                uniform sampler2D scale_y_ratio : repeat_disable, filter_linear;
                uniform vec2 atlas_grid = vec2(1.0);
                uniform vec4 albedo_color : source_color = vec4(1.0);
                varying vec4 particle_tint;
                void vertex() {
                    float phase = clamp(INSTANCE_CUSTOM.y, 0.0, 1.0);
                    vec2 axis_ratio = vec2(
                        texture(scale_x_ratio, vec2(phase, 0.0)).r,
                        texture(scale_y_ratio, vec2(phase, 0.0)).r);
                    {{scaleVertex}}
                    {{billboardVertex}}
                    particle_tint = COLOR;
                    float frame_count = max(1.0, atlas_grid.x * atlas_grid.y);
                    float normalized_frame = clamp(INSTANCE_CUSTOM.z, 0.0, 0.999999);
                    float frame = floor(normalized_frame * frame_count);
                    vec2 cell = vec2(mod(frame, atlas_grid.x), floor(frame / atlas_grid.x));
                    UV = (UV + cell) / atlas_grid;
                }
                void fragment() {
                    vec4 source = texture(particle_texture, UV) * particle_tint * albedo_color;
                    ALBEDO = source.rgb;
                    ALPHA = source.a;
                }
                """
        };
    }

    public DaoEffectMaterializationReport Materialize(
        JsonObject? props, string layoutName, Node3D destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (props is null) return default;
        var definitions = 0;
        var instances = 0;
        var rendered = 0;
        var emitters = 0;
        var unsupportedDistortion = 0;
        var unsupportedSemanticEmitters = 0;
        var independentScaleEmitterPlacements = 0;
        var readabilityValidatedEmitterPlacements = 0;
        var expectedRenderedEmitterPlacements = 0;
        var maximumCardDimension = 0f;
        var maximumVisibilityExtent = 0f;
        var maximumAtlasFrames = 0;
        var maximumAnimationCycles = 0f;
        var minimumProximityFade = float.PositiveInfinity;
        var maximumProximityFade = 0f;
        var supportedDefinitions = 0;
        var supportedInstances = 0;
        var unsupportedDefinitions = 0;
        var unsupportedInstances = 0;
        var enhancedPresentation = UseEnhancedPresentation();
        var unsupportedResrefs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (definitionName, value) in props)
        {
            if (value is not JsonObject record) continue;
            var relative = record["file"]?.GetValue<string>() ?? string.Empty;
            if (!IsEffect(relative)) continue;
            definitions++;
            if (record["instances"] is not JsonArray sourceInstances)
                throw new InvalidDataException(
                    $"DAO effect definition has no authored instances: {definitionName}");
            instances += sourceInstances.Count;
            if (!TryResolveContract(relative, out var contract, out var contractKind,
                    out var contractFailure))
            {
                // Unknown emitter semantics never fall back to the generated
                // GLB card. Preserve and validate the placement inventory so
                // an arbitrary area still loads with an explicit unsupported
                // delta instead of silently dropping or fabricating effects.
                foreach (var sourceNode in sourceInstances)
                {
                    if (sourceNode is not JsonObject sourceInstance)
                        throw new InvalidDataException(
                            $"DAO effect instance is not an object: {definitionName}");
                    _ = ReadRequiredTransform(sourceInstance, definitionName);
                }
                unsupportedDefinitions++;
                unsupportedInstances += sourceInstances.Count;
                unsupportedResrefs.Add(Path.GetFileNameWithoutExtension(relative));
                GD.Print("OPENDAO_EFFECT_DEFINITION status=unsupported " +
                         "materialized=absent parity=unsupported " +
                         $"definition={Path.GetFileNameWithoutExtension(relative)} " +
                         $"instances={sourceInstances.Count} emitters=0 " +
                         $"reason={contractFailure} " +
                         "fallback_glb=blocked source=installed-placement-only");
                continue;
            }
            if (contract.Emitters.Count == 0)
                throw new InvalidDataException(
                    $"DAO effect has no source-supported emitters: {contract.ResRef}");
            supportedDefinitions++;
            supportedInstances += sourceInstances.Count;
            ValidatePayload(models, contract.ResRef + ".mmh", contract.ModelHierarchySha256);
            ValidateResources(contract);
            var definitionRendered = 0;
            foreach (var sourceNode in sourceInstances)
            {
                if (sourceNode is not JsonObject sourceInstance)
                    throw new InvalidDataException(
                        $"DAO effect instance is not an object: {contract.ResRef}");
                var root = new Node3D
                {
                    Name = SanitizeNodeName($"Effect_{contract.ResRef}_{definitionRendered:D3}"),
                    Transform = ReadRequiredTransform(sourceInstance, contract.ResRef)
                };
                root.SetMeta("dao_effect", true);
                root.SetMeta("dao_effect_resref", contract.ResRef);
                root.SetMeta("dao_effect_source", "installed-mmh-mao-dds");
                root.SetMeta("dao_effect_contract_kind", contractKind);
                root.SetMeta("dao_effect_mmh_sha256", contract.ModelHierarchySha256);
                foreach (var emitter in contract.Emitters)
                {
                    var node = CreateEmitter(emitter, contract.PresimulateSeconds,
                        enhancedPresentation, contract.ResRef, out var readability);
                    root.AddChild(node);
                    readabilityValidatedEmitterPlacements++;
                    maximumCardDimension = Math.Max(maximumCardDimension,
                        Math.Max(readability.MaximumCardWidthMeters,
                            readability.MaximumCardHeightMeters));
                    maximumVisibilityExtent = Math.Max(maximumVisibilityExtent,
                        readability.VisibilityBoundsExtentMeters);
                    maximumAtlasFrames = Math.Max(maximumAtlasFrames,
                        readability.AtlasFrames);
                    maximumAnimationCycles = Math.Max(maximumAnimationCycles,
                        readability.AnimationCyclesPerLifetime);
                    if (readability.ProximityFadeDistanceMeters is { } fade)
                    {
                        minimumProximityFade = Math.Min(minimumProximityFade, fade);
                        maximumProximityFade = Math.Max(maximumProximityFade, fade);
                    }
                }
                destination.AddChild(root);
                definitionRendered++;
                rendered++;
                emitters += contract.Emitters.Count;
            }
            unsupportedDistortion += contract.UnsupportedDistortionEmitters * definitionRendered;
            unsupportedSemanticEmitters +=
                (contract.UnsupportedEmitterSemantics?.Count ?? 0) * definitionRendered;
            expectedRenderedEmitterPlacements += contract.Emitters.Count * definitionRendered;
            var independentScaleEmitters = contract.Emitters.Count(emitter =>
                emitter.IndependentScaleAxes);
            independentScaleEmitterPlacements += independentScaleEmitters * definitionRendered;
            GD.Print($"OPENDAO_EFFECT_DEFINITION status=ready materialized=ready " +
                     $"parity={(contract.UnsupportedDistortionEmitters == 0 &&
                                  (contract.UnsupportedEmitterSemantics?.Count ?? 0) == 0 ? "source-supported" : "partial")} " +
                     $"definition={contract.ResRef} " +
                     $"instances={definitionRendered} emitters={contract.Emitters.Count} " +
                     $"source_mmh_sha256={contract.ModelHierarchySha256} " +
                     $"contract_kind={contractKind} " +
                     $"distortion_skipped={contract.UnsupportedDistortionEmitters} " +
                     $"semantic_emitters_skipped={contract.UnsupportedEmitterSemantics?.Count ?? 0} " +
                     $"unsupported_semantics={(contract.UnsupportedEmitterSemantics is { Count: > 0 } ? string.Join(',', contract.UnsupportedEmitterSemantics.Distinct(StringComparer.Ordinal).Order()) : "none")} " +
                     "local_basis=dao-to-godot-conjugated source_direction=positive-z " +
                     $"independent_scale_emitters={independentScaleEmitters} " +
                     $"scale_axis_contract={(independentScaleEmitters > 0 ? "source-independent-x-y" : "constant-aspect")} " +
                     $"atlas_edge_feather={(enhancedPresentation ? "enabled-per-source-cell" : "source-disabled")} " +
                     $"soft_intersection={(enhancedPresentation ? (independentScaleEmitters > 0 ? "enhanced-standard-material-only" : "enhanced-proximity-fade") : "source-disabled")} " +
                     $"fire_card_shaping={(enhancedPresentation && IsFireDefinition(contract.ResRef) ? "atlas-feather+emitter-scale+warm-core" : "source-unchanged")} " +
                     "fallback_glb=blocked source=installed-mmh-mao-dds");
        }

        if (rendered != supportedInstances || emitters != expectedRenderedEmitterPlacements ||
            readabilityValidatedEmitterPlacements != expectedRenderedEmitterPlacements ||
            supportedInstances + unsupportedInstances != instances ||
            supportedDefinitions + unsupportedDefinitions != definitions)
            throw new InvalidDataException(
                $"DAO effect coverage mismatch: definitions={definitions} " +
                $"supported_definitions={supportedDefinitions} " +
                $"unsupported_definitions={unsupportedDefinitions} instances={instances} " +
                $"supported_instances={supportedInstances} unsupported_instances={unsupportedInstances} " +
                $"rendered={rendered} emitter_placements={emitters} " +
                $"expected_emitter_placements={expectedRenderedEmitterPlacements} " +
                $"readability_validated={readabilityValidatedEmitterPlacements}");
        var status = unsupportedDefinitions == 0 ? "ready" : "partial";
        var materialized = supportedDefinitions == 0 && unsupportedDefinitions > 0
            ? "unsupported"
            : definitions == 0 ? "not-required" : "ready";
        var parity = unsupportedDefinitions > 0
            ? supportedDefinitions == 0 ? "unsupported" : "partial"
            : unsupportedDistortion == 0 && unsupportedSemanticEmitters == 0
                ? definitions == 0 ? "not-applicable" : "source-supported"
                : "partial";
        GD.Print($"OPENDAO_WORLD_EFFECT_CENSUS status={status} materialized={materialized} parity={parity} " +
                 $"layout={layoutName.ToLowerInvariant()} definitions={definitions} " +
                 $"instances={instances} rendered={rendered} emitters={emitters} " +
                 $"supported_definitions={supportedDefinitions} " +
                 $"unsupported_definitions={unsupportedDefinitions} " +
                 $"supported_instances={supportedInstances} " +
                 $"unsupported_instances={unsupportedInstances} " +
                 $"unsupported_resrefs={(unsupportedResrefs.Count == 0 ? "none" : string.Join(',', unsupportedResrefs))} " +
                 $"distortion_skipped={unsupportedDistortion} " +
                 $"semantic_emitters_skipped={unsupportedSemanticEmitters} " +
                 $"independent_scale_emitter_placements={independentScaleEmitterPlacements} " +
                 $"rendered_emitter_placements={emitters} " +
                 $"readability_validated_emitter_placements={readabilityValidatedEmitterPlacements} " +
                 $"known_supported_graph_emitter_placements=" +
                 $"{emitters + unsupportedDistortion + unsupportedSemanticEmitters} " +
                 $"maximum_card_dimension={maximumCardDimension:R} " +
                 $"maximum_visibility_extent={maximumVisibilityExtent:R} " +
                 $"maximum_atlas_frames={maximumAtlasFrames} " +
                 $"maximum_animation_cycles={maximumAnimationCycles:R} " +
                 $"proximity_fade_min={(float.IsPositiveInfinity(minimumProximityFade) ? 0 : minimumProximityFade):R} " +
                 $"proximity_fade_max={maximumProximityFade:R} " +
                 "scale_axis_contract=source-independent-x-y+constant-aspect " +
                 $"atlas_edge_feather={(enhancedPresentation ? "enabled-per-source-cell" : "source-disabled")} " +
                 $"soft_intersection={(enhancedPresentation ? (independentScaleEmitterPlacements > 0 ? "enhanced-standard-material-only" : "enhanced-proximity-fade") : "source-disabled")} " +
                 $"fire_card_shaping={(enhancedPresentation ? "enabled-for-fire-definitions" : "source-disabled")} " +
                 "policy=source-mmh-mao-dds-fail-closed");
        return new DaoEffectMaterializationReport(
            definitions, instances, rendered, emitters, unsupportedDistortion,
            unsupportedSemanticEmitters,
            supportedDefinitions, unsupportedDefinitions, supportedInstances,
            unsupportedInstances, unsupportedResrefs.ToArray());
    }

    private GpuParticles3D CreateEmitter(DragonAgeEffectEmitter source, float presimulate,
        bool enhancedPresentation, string effectResRef,
        out DragonAgeEffectReadabilityContract readability)
    {
        var sourceTexture = LoadTexture(source.Texture, source.TextureSha256);
        var enhancedFire = enhancedPresentation && IsFireDefinition(effectResRef);
        var texture = enhancedPresentation
            ? EnhancedAtlasTexture(sourceTexture, source.Texture, source.Columns, source.Rows)
            : sourceTexture;
        var presentationScale = enhancedFire
            ? EnhancedFireScale(source.Name)
            : 1f;
        // Independent-axis decoded emitters deliberately carry ScaleAspect=null:
        // their X/max and Y/max values are sampled from every age key. A
        // non-null ScaleAspect belongs only to the constant-aspect path.
        readability = DragonAgeOriginsEffectPresentationPolicy.Evaluate(
            source, sourceTexture.GetWidth(), sourceTexture.GetHeight(),
            presentationScale, enhancedPresentation);
        var scaleAspect = readability.MeshAspect;
        var ageMap = DragonAgeOriginsEffectPresentationPolicy.ResolveAgeMap(source);
        var gradient = new Gradient
        {
            Offsets = ageMap.Select(key => Mathf.Clamp(key.Time, 0, 1)).ToArray(),
            Colors = ageMap.Select(key => ToColor(key.Color)).ToArray()
        };
        var scaleCurve = new Curve
        {
            MinValue = 0,
            MaxValue = Math.Max(1, ageMap.Max(key =>
                Math.Max(key.Scale.X, key.Scale.Y)) * presentationScale)
        };
        foreach (var key in ageMap)
            scaleCurve.AddPoint(new Vector2(Mathf.Clamp(key.Time, 0, 1),
                Math.Max(key.Scale.X, key.Scale.Y) * presentationScale));
        CurveTexture? scaleXRatio = null;
        CurveTexture? scaleYRatio = null;
        if (source.IndependentScaleAxes)
        {
            var xRatio = new Curve { MinValue = 0, MaxValue = 1 };
            var yRatio = new Curve { MinValue = 0, MaxValue = 1 };
            foreach (var key in ageMap)
            {
                var maximum = Math.Max(key.Scale.X, key.Scale.Y);
                if (!float.IsFinite(key.Scale.X) || !float.IsFinite(key.Scale.Y) ||
                    key.Scale.X <= .000001f || key.Scale.Y <= .000001f ||
                    maximum <= .000001f)
                    throw new InvalidDataException(
                        $"DAO independent scale curve is invalid: {effectResRef}/{source.Name}");
                var time = Mathf.Clamp(key.Time, 0, 1);
                xRatio.AddPoint(new Vector2(time, key.Scale.X / maximum));
                yRatio.AddPoint(new Vector2(time, key.Scale.Y / maximum));
            }
            scaleXRatio = new CurveTexture { Curve = xRatio };
            scaleYRatio = new CurveTexture { Curve = yRatio };
        }
        var frameCount = readability.AtlasFrames;
        var animationCycles = readability.AnimationCyclesPerLifetime;
        var convertedRotation = ToQuaternion(DragonAgeOriginsCoordinateSystem.Convert(
            source.LocalRotation)).Normalized();
        var accelerationVector = ConvertVector(source.WorldAcceleration) +
                                 Vector3.Down * source.Gravity;
        if (!source.AccelerationInObjectSpace)
            accelerationVector = new Basis(convertedRotation).Inverse() * accelerationVector;
        var process = new ParticleProcessMaterial
        {
            Direction = ConvertVector(source.SourceDirection).Normalized(),
            Spread = source.SpreadDegrees,
            InitialVelocityMin = Math.Max(0, source.Velocity - source.VelocityRange),
            InitialVelocityMax = Math.Max(0, source.Velocity + source.VelocityRange),
            LinearAccelMin = source.Acceleration,
            LinearAccelMax = source.Acceleration,
            Gravity = accelerationVector,
            ScaleMin = Math.Max(0, 1 - source.ScaleRange),
            ScaleMax = 1 + source.ScaleRange,
            ScaleCurve = new CurveTexture { Curve = scaleCurve },
            ColorRamp = new GradientTexture1D { Gradient = gradient },
            LifetimeRandomness = Mathf.Clamp(
                source.LifetimeRange / Math.Max(.0001f, source.Lifetime + source.LifetimeRange),
                0, 1),
            AnimSpeedMin = animationCycles,
            AnimSpeedMax = animationCycles,
            AnimOffsetMin = 0,
            AnimOffsetMax = source.FramesPerSecond > 0 ? 0 : 1,
            AngleMin = source.InitialRotationDegrees - source.InitialRotationRangeDegrees,
            AngleMax = source.InitialRotationDegrees + source.InitialRotationRangeDegrees,
            AngularVelocityMin = source.AngularVelocityDegrees -
                                 source.AngularVelocityRangeDegrees,
            AngularVelocityMax = source.AngularVelocityDegrees +
                                 source.AngularVelocityRangeDegrees
        };
        switch (source.Volume)
        {
            case DragonAgeEffectVolume.Sphere:
                process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere;
                process.EmissionSphereRadius = Math.Max(.001f, source.VolumeExtents.X);
                break;
            case DragonAgeEffectVolume.Box:
                process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box;
                process.EmissionBoxExtents = ConvertExtents(source.VolumeExtents);
                break;
            default:
                process.EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Point;
                break;
        }

        var sizeExtent = readability.MaximumAnimatedScale;
        // Source color/alpha keys already carry the authored radiance envelope.
        // A blanket enhanced-tier multiplier made beams, smoke, and sparks
        // disappear; only fire keeps its separately reviewed treatment.
        var albedoColor = enhancedPresentation &&
                          source.Blend == DragonAgeEffectBlend.Additive && enhancedFire
            ? EnhancedFireTint(source.Name)
            : Colors.White;
        Material material;
        if (source.IndependentScaleAxes)
        {
            var shader = (source.Blend, source.Orientation) switch
            {
                (DragonAgeEffectBlend.Additive,
                    DragonAgeEffectOrientation.CameraBillboard) =>
                    IndependentBillboardAddShader,
                (DragonAgeEffectBlend.Alpha,
                    DragonAgeEffectOrientation.CameraBillboard) =>
                    IndependentBillboardMixShader,
                (DragonAgeEffectBlend.Additive,
                    DragonAgeEffectOrientation.HorizontalPlane) =>
                    IndependentHorizontalAddShader,
                _ => IndependentHorizontalMixShader
            };
            var independentMaterial = new ShaderMaterial
            {
                ResourceName = source.MaterialObject + "_InstalledDAO_IndependentXY",
                Shader = shader
            };
            independentMaterial.SetShaderParameter("particle_texture", texture);
            independentMaterial.SetShaderParameter("scale_x_ratio", scaleXRatio!);
            independentMaterial.SetShaderParameter("scale_y_ratio", scaleYRatio!);
            independentMaterial.SetShaderParameter("atlas_grid",
                new Vector2(source.Columns, source.Rows));
            independentMaterial.SetShaderParameter("albedo_color", albedoColor);
            material = independentMaterial;
        }
        else
        {
            material = new StandardMaterial3D
            {
                ResourceName = source.MaterialObject + "_InstalledDAO",
                AlbedoTexture = texture,
                AlbedoColor = albedoColor,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = source.Blend == DragonAgeEffectBlend.Additive
                    ? BaseMaterial3D.BlendModeEnum.Add
                    : BaseMaterial3D.BlendModeEnum.Mix,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
                BillboardMode = source.Orientation == DragonAgeEffectOrientation.CameraBillboard
                    ? BaseMaterial3D.BillboardModeEnum.Particles
                    : BaseMaterial3D.BillboardModeEnum.Disabled,
                BillboardKeepScale = true,
                ParticlesAnimHFrames = source.Columns,
                ParticlesAnimVFrames = source.Rows,
                ParticlesAnimLoop = true,
                // Proximity fade is only available on the standard material
                // path. Independent-axis emitters remain source-shaped and do
                // not receive a fabricated soft-intersection approximation.
                ProximityFadeEnabled = readability.ProximityFadeDistanceMeters.HasValue,
                ProximityFadeDistance = readability.ProximityFadeDistanceMeters ??
                                        DragonAgeOriginsEffectPresentationPolicy
                                            .MinimumProximityFadeDistanceMeters,
                DistanceFadeMode = BaseMaterial3D.DistanceFadeModeEnum.Disabled
            };
        }
        var meshAspect = scaleAspect;
        Mesh drawMesh = source.Orientation == DragonAgeEffectOrientation.HorizontalPlane
            ? new PlaneMesh
            {
                Size = new Vector2(meshAspect.X, meshAspect.Y),
                Orientation = PlaneMesh.OrientationEnum.Y,
                Material = material
            }
            : new QuadMesh
            {
                Size = new Vector2(meshAspect.X, meshAspect.Y),
                Material = material
            };
        var boundsExtent = readability.VisibilityBoundsExtentMeters;
        var result = new GpuParticles3D
        {
            Name = SanitizeNodeName("Emitter_" + source.Name),
            Transform = new Transform3D(
                new Basis(convertedRotation),
                ConvertVector(source.Translation)),
            Amount = Mathf.Clamp((int)Math.Ceiling(
                (source.BirthRate + Math.Abs(source.BirthRateRange)) *
                (source.Lifetime + Math.Abs(source.LifetimeRange))), 1, 2048),
            Lifetime = Math.Max(.05f, source.Lifetime + source.LifetimeRange),
            Preprocess = Math.Max(0, presimulate),
            Randomness = Mathf.Clamp(
                source.BirthRateRange / Math.Max(1, source.BirthRate + source.BirthRateRange),
                0, 1),
            FixedFps = 30,
            Interpolate = true,
            LocalCoords = true,
            DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
            ProcessMaterial = process,
            DrawPass1 = drawMesh,
            VisibilityAabb = new Aabb(
                Vector3.One * -boundsExtent,
                Vector3.One * boundsExtent * 2),
            Layers = WorldRenderLayers.Gameplay,
            Emitting = true
        };
        result.SetMeta("dao_effect_scale_axis_contract",
            source.IndependentScaleAxes ? "source-independent-x-y" : "constant-aspect");
        result.SetMeta("dao_effect_scale_age_keys", ageMap.Count);
        result.SetMeta("dao_effect_maximum_card_dimension",
            Math.Max(readability.MaximumCardWidthMeters,
                readability.MaximumCardHeightMeters));
        result.SetMeta("dao_effect_atlas_grid",
            $"{readability.AtlasColumns}x{readability.AtlasRows}");
        result.SetMeta("dao_effect_atlas_frames", readability.AtlasFrames);
        result.SetMeta("dao_effect_animation_cycles",
            readability.AnimationCyclesPerLifetime);
        result.SetMeta("dao_effect_visibility_extent",
            readability.VisibilityBoundsExtentMeters);
        result.SetMeta("dao_effect_proximity_fade",
            readability.ProximityFadeDistanceMeters ?? 0);
        return result;
    }

    private void ValidateResources(DragonAgeEffectDefinition definition)
    {
        foreach (var emitter in definition.Emitters)
        {
            if (validatedMaterials.Add(emitter.MaterialObject))
                ValidatePayload(materials, emitter.MaterialObject, emitter.MaterialSha256);
            _ = LoadTexture(emitter.Texture, emitter.TextureSha256);
        }
    }

    private bool TryResolveContract(string pathOrResRef,
        out DragonAgeEffectDefinition definition, out string kind, out string failure)
    {
        if (DragonAgeOriginsEffectCatalog.TryResolve(pathOrResRef, out definition!))
        {
            kind = "curated-source-contract";
            failure = string.Empty;
            return true;
        }
        var resRef = Path.GetFileNameWithoutExtension(
            pathOrResRef.Replace('\\', '/')).ToLowerInvariant();
        if (decodedContracts.TryGetValue(resRef, out definition!))
        {
            kind = "decoded-source-contract";
            failure = string.Empty;
            return true;
        }
        if (decodeFailures.TryGetValue(resRef, out failure!))
        {
            kind = "unsupported";
            return false;
        }
        var member = resRef + ".mmh";
        if (!models.Contains(member))
        {
            definition = null!;
            kind = "unsupported";
            failure = "source-mmh-absent";
            decodeFailures[resRef] = failure;
            return false;
        }
        var payload = models.Read(member);
        if (!DragonAgeOriginsEffectGraphDecoder.TryDecode(
                resRef, payload,
                material => materials.Contains(material) ? materials.Read(material) : null,
                texture => textures.Contains(texture) ? textures.Read(texture) : null,
                out definition!, out failure!))
        {
            kind = "unsupported";
            decodeFailures[resRef] = failure;
            return false;
        }
        decodedContracts[resRef] = definition;
        kind = "decoded-source-contract";
        failure = string.Empty;
        return true;
    }

    private Texture2D LoadTexture(string member, string expectedHash)
    {
        if (textureCache.TryGetValue(member, out var cached)) return cached;
        var bytes = ValidatePayload(textures, member, expectedHash);
        var image = new Image();
        var error = image.LoadDdsFromBuffer(bytes);
        if (error != Error.Ok || image.IsEmpty())
            throw new InvalidDataException(
                $"Installed DAO effect texture could not be decoded: {member} ({error})");
        if (!image.HasMipmaps()) image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = member + "_InstalledDAO";
        textureCache[member] = texture;
        return texture;
    }

    private Texture2D EnhancedAtlasTexture(Texture2D source, string member,
        int columns, int rows)
    {
        if (columns <= 0 || rows <= 0)
            throw new InvalidDataException(
                $"DAO effect atlas dimensions are not positive: {member} {columns}x{rows}");
        var cacheKey = $"{member}|{columns}x{rows}|edge-feather-v1";
        if (enhancedAtlasTextureCache.TryGetValue(cacheKey, out var cached)) return cached;
        var image = source.GetImage();
        if (image is null || image.IsEmpty())
            throw new InvalidDataException(
                $"DAO effect atlas cannot be read for enhanced edge shaping: {member}");
        if (image.IsCompressed())
        {
            var error = image.Decompress();
            if (error != Error.Ok)
                throw new InvalidDataException(
                    $"DAO effect atlas cannot be decompressed: {member} ({error})");
        }
        image.Convert(Image.Format.Rgba8);
        var width = image.GetWidth();
        var height = image.GetHeight();
        if (width % columns != 0 || height % rows != 0)
            throw new InvalidDataException(
                $"DAO effect atlas does not divide into its source frame grid: " +
                $"{member} image={width}x{height} grid={columns}x{rows}");
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        var feather = Math.Max(1f, Math.Min(cellWidth, cellHeight) * .18f);
        for (var y = 0; y < height; y++)
        {
            var cellY = y % cellHeight;
            var distanceY = Math.Min(cellY + .5f, cellHeight - cellY - .5f);
            for (var x = 0; x < width; x++)
            {
                var cellX = x % cellWidth;
                var distanceX = Math.Min(cellX + .5f, cellWidth - cellX - .5f);
                var linear = Math.Clamp(Math.Min(distanceX, distanceY) / feather, 0, 1);
                var edge = linear * linear * (3 - 2 * linear);
                var color = image.GetPixel(x, y);
                image.SetPixel(x, y, new Color(
                    color.R * edge, color.G * edge, color.B * edge, color.A * edge));
            }
        }
        image.GenerateMipmaps();
        var texture = ImageTexture.CreateFromImage(image);
        texture.ResourceName = member + "_InstalledDAO_EnhancedAtlasFeather";
        enhancedAtlasTextureCache[cacheKey] = texture;
        return texture;
    }

    private static byte[] ValidatePayload(ErfArchive archive, string member, string expectedHash)
    {
        if (!archive.Contains(member))
            throw new InvalidDataException(
                $"Installed DAO effect resource is absent: {member}");
        var bytes = archive.Read(member);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!hash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Installed DAO effect identity drifted: {member} sha256={hash.ToLowerInvariant()}");
        return bytes;
    }

    private static Transform3D ReadRequiredTransform(JsonObject record, string resRef)
    {
        if (record["position"] is not JsonArray { Count: 3 } positionValues ||
            record["rotation"] is not JsonArray { Count: 4 } rotationValues)
            throw new InvalidDataException(
                $"DAO effect instance transform is incomplete: {resRef}");
        var position = new Vector3(
            RequiredNumber(positionValues[0], resRef, "position.x"),
            RequiredNumber(positionValues[1], resRef, "position.y"),
            RequiredNumber(positionValues[2], resRef, "position.z"));
        var values = record["rotation"] as JsonArray;
        var rotation = new Quaternion(
            RequiredNumber(values![0], resRef, "rotation.x"),
            RequiredNumber(values[1], resRef, "rotation.y"),
            RequiredNumber(values[2], resRef, "rotation.z"),
            RequiredNumber(values[3], resRef, "rotation.w"));
        if (rotation.LengthSquared() < .000001f)
            throw new InvalidDataException(
                $"DAO effect instance rotation is degenerate: {resRef}");
        rotation = rotation.Normalized();
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var basis = conversion * new Basis(rotation) * conversion.Inverse();
        var scale = RequiredNumber(record["scale"], resRef, "scale");
        if (scale <= 0)
            throw new InvalidDataException(
                $"DAO effect instance scale is not positive: {resRef} scale={scale}");
        return new Transform3D(basis.Scaled(Vector3.One * scale), ConvertVector(position));
    }

    private static Vector3 ReadVector(JsonArray? value) => value is { Count: >= 3 }
        ? new Vector3(Number(value[0]), Number(value[1]), Number(value[2]))
        : Vector3.Zero;

    private static Vector3 ConvertVector(System.Numerics.Vector3 source) =>
        new(source.X, source.Z, -source.Y);

    private static Vector3 ConvertVector(Vector3 source) =>
        new(source.X, source.Z, -source.Y);

    private static Vector3 ConvertExtents(System.Numerics.Vector3 source) =>
        new(Math.Abs(source.X), Math.Abs(source.Z), Math.Abs(source.Y));

    private static Color ToColor(System.Numerics.Vector4 source) =>
        new(source.X, source.Y, source.Z, source.W);

    private static Quaternion ToQuaternion(System.Numerics.Quaternion source) =>
        new(source.X, source.Y, source.Z, source.W);

    private static float Number(JsonNode? value) => value?.GetValue<float>() ?? 0;

    private static float RequiredNumber(JsonNode? value, string resRef, string field)
    {
        if (value is null)
            throw new InvalidDataException($"DAO effect instance is missing {field}: {resRef}");
        float result;
        try
        {
            result = value.GetValue<float>();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new InvalidDataException(
                $"DAO effect instance {field} is not numeric: {resRef}", error);
        }
        if (!float.IsFinite(result))
            throw new InvalidDataException(
                $"DAO effect instance {field} is not finite: {resRef}");
        return result;
    }

    private static bool UseEnhancedPresentation()
    {
        var backend = RenderingQualityPolicy.ParseBackend(
            RenderingServer.GetCurrentRenderingMethod().ToString());
        var requested = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_PRESENTATION_TIER");
        return RenderingQualityPolicy.ParseTier(requested, backend) ==
               RenderingPresentationTier.Enhanced;
    }

    private static bool IsEffect(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("fxe_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("fxp_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("fxm_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("fxa_", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("fxc_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsFireDefinition(string resRef) =>
        resRef.StartsWith("fxe_fire_", StringComparison.OrdinalIgnoreCase);

    private static float EnhancedFireScale(string emitterName) =>
        emitterName.Contains("Glow", StringComparison.OrdinalIgnoreCase)
            ? .45f
            : emitterName.Contains("Flame", StringComparison.OrdinalIgnoreCase) ||
              emitterName.Contains("FireTall", StringComparison.OrdinalIgnoreCase)
                ? .42f
                : emitterName.Contains("Ember", StringComparison.OrdinalIgnoreCase)
                    ? .75f
                    : .58f;

    private static Color EnhancedFireTint(string emitterName)
    {
        if (emitterName.Contains("Glow", StringComparison.OrdinalIgnoreCase))
            return new Color(.18f, .18f, .18f, .62f);
        if (emitterName.Contains("Ember", StringComparison.OrdinalIgnoreCase))
            return new Color(.55f, .25f, .06f, .9f);
        // The installed FireMeat/Flame sheets are grayscale radiance masks.
        // Their recovered particle color is white, so a neutral exposure-only
        // correction produces the pale smear caught by the arbitrary-area
        // capture. Enhanced presentation supplies a bounded incandescent core;
        // source tier keeps the exact installed sheet/color contract.
        return new Color(.52f, .24f, .07f, .92f);
    }

    private static string ResolveUnderRoot(string root, string relative)
    {
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(root,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("DAO effect archive path escapes the installed root.");
        return result;
    }

    private static string SanitizeNodeName(string value) =>
        value.Replace(':', '_').Replace('/', '_').Replace('\\', '_').Replace('.', '_');
}

internal readonly record struct DaoEffectMaterializationReport(
    int Definitions,
    int Instances,
    int Rendered,
    int Emitters,
    int UnsupportedDistortionEmitters,
    int UnsupportedSemanticEmitters,
    int SupportedDefinitions,
    int UnsupportedDefinitions,
    int SupportedInstances,
    int UnsupportedInstances,
    IReadOnlyList<string>? UnsupportedResrefs);
