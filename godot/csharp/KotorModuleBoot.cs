using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Profiles.Kotor;
using NumericsVector3 = System.Numerics.Vector3;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot : Node3D
{
    private readonly IRuntimeEnvironment launchEnvironment = new GodotRuntimeEnvironment();
    private const float DefaultGameplayFieldOfView = 72.0f;
    // Dedicated to source-room visibility and third-person camera collision.
    // Player/world movement keeps its existing profile-owned walkmesh path and
    // never queries this physics layer.
    private const uint CameraVisibilityCollisionLayer = 1u << 7;
    private const uint ParticleCollisionSourceVisualLayer = 1u << 19;
    private const uint RuntimeCameraCullMask =
        ((1u << 20) - 1u) & ~ParticleCollisionSourceVisualLayer;
    private const float EnhancedParticleProximityFadeDistance = 0.45f;
    private const int EnhancedParticleFrameMinimumPixels = 128;
    private const int EnhancedParticleMaximumUpscale = 4;
    private const float EnhancedParticleMaximumQuadExtentMeters = 8.0f;
    private const int EmitterDepthTextureFlag = 0x0800;
    private const int EmitterPointToPointFlag = 0x0001;
    private const int EmitterPointToPointBezierFlag = 0x0002;
    private const int EmitterAffectedWindFlag = 0x0004;
    private const int EmitterRandomPlaybackFlag = 0x0020;
    private const int EmitterTintedFlag = 0x0008;
    private const int EmitterCollisionBounceFlag = 0x0010;
    private const int UnsupportedRoomEmitterFlags =
        0x0080 | // parent velocity inheritance
        0x0200 | // collision splat
        0x0400 | // particle inheritance
        EmitterDepthTextureFlag |
        0x1000;  // unknown source flag
    private static readonly Shader OdysseyOrientedParticleMixShader =
        CreateOrientedParticleShader(additive: false, twoSided: false);
    private static readonly Shader OdysseyOrientedParticleMixTwoSidedShader =
        CreateOrientedParticleShader(additive: false, twoSided: true);
    private static readonly Shader OdysseyOrientedParticleAddShader =
        CreateOrientedParticleShader(additive: true, twoSided: false);
    private static readonly Shader OdysseyOrientedParticleAddTwoSidedShader =
        CreateOrientedParticleShader(additive: true, twoSided: true);
    private static readonly Shader OdysseyLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_opaque, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform vec3 dynamic_ambient;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            uniform float dielectric_specular;
            uniform float material_roughness = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = base.rgb * dynamic_light_albedo_weight;
                vec3 baked = clamp(lightmap, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                EMISSION = base.rgb * max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                SPECULAR = dielectric_specular;
                METALLIC = 0.0;
                ROUGHNESS = material_roughness;
            }
            """
    };

    private static Shader CreateOrientedParticleShader(bool additive, bool twoSided)
    {
        var blend = additive ? "blend_add" : "blend_mix";
        var cull = twoSided ? "cull_disabled" : "cull_back";
        return new Shader
        {
            Code = $$"""
                shader_type spatial;
                render_mode unshaded, depth_draw_never, {{blend}}, {{cull}};
                uniform sampler2D particle_texture : source_color, repeat_disable, filter_linear_mipmap;
                uniform vec2 atlas_grid = vec2(1.0);
                uniform float exposure = 1.0;
                varying vec4 particle_tint;
                void vertex() {
                    particle_tint = COLOR;
                    float frame_count = max(1.0, atlas_grid.x * atlas_grid.y);
                    float normalized_frame = clamp(INSTANCE_CUSTOM.z, 0.0, 0.999999);
                    float frame = floor(normalized_frame * frame_count);
                    vec2 cell = vec2(mod(frame, atlas_grid.x), floor(frame / atlas_grid.x));
                    UV = (UV + cell) / atlas_grid;
                }
                void fragment() {
                    vec4 source = texture(particle_texture, UV) * particle_tint;
                    ALBEDO = source.rgb * exposure;
                    ALPHA = source.a;
                }
                """
        };
    }
    private static readonly Shader OdysseyTransparentLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_never, cull_disabled, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform vec3 dynamic_ambient;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            uniform float dielectric_specular;
            uniform float material_roughness = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = base.rgb * dynamic_light_albedo_weight;
                vec3 baked = clamp(lightmap, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                EMISSION = base.rgb * max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                SPECULAR = dielectric_specular;
                METALLIC = 0.0;
                ROUGHNESS = material_roughness;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseyEnvironmentLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_opaque, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            uniform float dielectric_specular;
            uniform float material_roughness = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                // Imported geometry maps Odyssey (x,y,z) to Godot (x,z,-y).
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                vec3 reflected_base = base.rgb + environment * authored_weight;
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = reflected_base * dynamic_light_albedo_weight;
                vec3 baked = clamp(lightmap, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                EMISSION = reflected_base * max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                SPECULAR = dielectric_specular;
                METALLIC = 0.0;
                ROUGHNESS = material_roughness;
            }
            """
    };
    private static readonly Shader OdysseyTransparentEnvironmentLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_never, cull_disabled, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            uniform float dielectric_specular;
            uniform float material_roughness = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                vec3 reflected_base = base.rgb + environment * authored_weight;
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = reflected_base * dynamic_light_albedo_weight;
                vec3 baked = clamp(lightmap, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                EMISSION = reflected_base * max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                SPECULAR = dielectric_specular;
                METALLIC = 0.0;
                ROUGHNESS = material_roughness;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseyEnvironmentShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_opaque, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                ALBEDO = base.rgb + environment * authored_weight;
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                METALLIC = 0.0;
                ROUGHNESS = 0.55;
            }
            """
    };
    private static readonly Shader OdysseyTransparentEnvironmentShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_never, cull_disabled, diffuse_lambert, specular_schlick_ggx;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform sampler2D normal_texture : hint_normal, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform bool has_normal_texture = false;
            uniform float normal_scale = 1.0;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                ALBEDO = base.rgb + environment * authored_weight;
                if (has_normal_texture) {
                    NORMAL_MAP = texture(normal_texture, UV).rgb;
                    NORMAL_MAP_DEPTH = normal_scale;
                }
                METALLIC = 0.0;
                ROUGHNESS = 0.55;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseyAdditiveLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 baked = clamp(texture(lightmap_texture, UV2).rgb, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                vec3 transfer = max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                ALBEDO = base.rgb * transfer;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseyAdditiveEnvironmentShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                ALBEDO = base.rgb + environment * authored_weight;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseyAdditiveEnvironmentLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, blend_add, depth_draw_never, cull_disabled;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform vec4 albedo_tint : source_color = vec4(1.0);
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform samplerCube environment_map : source_color, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            uniform float reflection_strength = 0.0;
            uniform float maximum_reflection_weight = 1.0;
            uniform float dynamic_light_albedo_weight;
            uniform float baked_emission_weight;
            uniform float dynamic_ambient_emission_weight;
            void fragment() {
                vec4 base = texture(albedo_texture, UV) * albedo_tint;
                vec3 reflected_view = reflect(-VIEW, NORMAL);
                vec3 reflected_world = normalize(mat3(INV_VIEW_MATRIX) * reflected_view);
                vec3 odyssey_direction = vec3(
                    reflected_world.x, -reflected_world.z, reflected_world.y);
                vec3 environment = texture(environment_map, odyssey_direction).rgb;
                float authored_weight = min(
                    base.a + base.a * (1.0 - base.a) * reflection_strength,
                    maximum_reflection_weight);
                vec3 surface = base.rgb + environment * authored_weight;
                vec3 baked = clamp(texture(lightmap_texture, UV2).rgb, vec3(0.0), vec3(1.0)) * baked_emission_weight;
                vec3 transfer = max(
                    baked, max(dynamic_ambient, vec3(0.0)) * dynamic_ambient_emission_weight);
                ALBEDO = surface * transfer;
                ALPHA = base.a;
            }
            """
    };
    private static readonly Shader OdysseySourceLightmapShader =
        CreateShaderWithoutNormalMapping(OdysseyLightmapShader);
    private static readonly Shader OdysseySourceTransparentLightmapShader =
        CreateShaderWithoutNormalMapping(OdysseyTransparentLightmapShader);
    private static readonly Shader OdysseySourceEnvironmentLightmapShader =
        CreateShaderWithoutNormalMapping(OdysseyEnvironmentLightmapShader);
    private static readonly Shader OdysseySourceTransparentEnvironmentLightmapShader =
        CreateShaderWithoutNormalMapping(OdysseyTransparentEnvironmentLightmapShader);
    private static readonly Shader OdysseySourceEnvironmentShader =
        CreateShaderWithoutNormalMapping(OdysseyEnvironmentShader);
    private static readonly Shader OdysseySourceTransparentEnvironmentShader =
        CreateShaderWithoutNormalMapping(OdysseyTransparentEnvironmentShader);

    private static Shader CreateShaderWithoutNormalMapping(Shader source)
    {
        var output = new List<string>();
        var skippingNormalBlock = false;
        foreach (var line in source.Code.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("uniform sampler2D normal_texture", StringComparison.Ordinal) ||
                trimmed.StartsWith("uniform bool has_normal_texture", StringComparison.Ordinal) ||
                trimmed.StartsWith("uniform float normal_scale", StringComparison.Ordinal))
                continue;
            if (trimmed.Equals("if (has_normal_texture) {", StringComparison.Ordinal))
            {
                skippingNormalBlock = true;
                continue;
            }
            if (skippingNormalBlock)
            {
                if (trimmed == "}")
                    skippingNormalBlock = false;
                continue;
            }
            output.Add(line);
        }
        return new Shader { Code = string.Join('\n', output) };
    }
    private CharacterBody3D playerBody = null!;
    private Node3D cameraPivot = null!;
    private SpringArm3D cameraArm = null!;
    private Camera3D camera = null!;
    private Camera3D cinematicCamera = null!;
    private XROrigin3D xrOrigin = null!;
    private XRCamera3D xrCamera = null!;
    private SubViewport? xrRenderViewport;
    private Camera3D? xrSpectatorCamera;
    private XRController3D xrLeftHand = null!;
    private XRController3D xrRightHand = null!;
    private Node3D xrRigTargetRoot = null!;
    private Node3D xrLeftGripTarget = null!;
    private Node3D xrRightGripTarget = null!;
    private XrTrackedArmBinding? xrLeftArm;
    private XrTrackedArmBinding? xrRightArm;
    private bool xrRigAcceptanceReported;
    private SubViewport? xrHudViewport;
    private Control? xrHudRoot;
    private MeshInstance3D? xrHudQuad;
    private int xrDialogueChoiceIndex;
    private bool xrHudWasVisible;
    private bool xrSnapTurnLatched;
    private bool xrMovementInputReported;
    private bool xrMovementAcceptedReported;
    private XRController3D? activeInteractionController;
    private bool xrActive;
    private bool xrSpectatorActive;
    private float xrSpectatorFieldOfView = DefaultGameplayFieldOfView;
    private Transform3D xrGameplayOriginOffset = Transform3D.Identity;
    private bool xrGameplayOriginCalibrated;
    private bool? xrLocalPlayerHeadVisible;
    private bool cleanExitRequested;
    private Node3D? playerModel;
    private AnimationPlayer? playerAnimationPlayer;
    private PlayerEquipmentVariantRecord? openingEquipmentVariant;
    private IReadOnlyList<PlayerEquipmentVariantRecord> playerEquipmentVariants = [];
    private PlayerRecord? basePlayerRecord;
    private Vector3? playerTalkOffset;
    private string playerManifestDirectory = "";
    private string currentPlayerAnimation = "";
    private string forcedPlayerAnimation = "";
    private float playerWalkSpeed = 1.7f;
    private float playerRunSpeed = 5.4f;
    private KotorMovementSimulation? movementSimulation;
    private NumericsVector3 simulationPlayerPosition;
    private float gameplayFieldOfView = DefaultGameplayFieldOfView;
    private AudioStreamPlayer dialogueVoice = null!;
    private AudioStreamPlayer areaMusic = null!;
    private Godot.Environment runtimeEnvironment = null!;
    private CanvasLayer overlayLayer = null!;
    private Label status = null!;
    private Label details = null!;
    private ColorRect loadingBackdrop = null!;
    private ColorRect cinematicFade = null!;
    private Tween? dialogueFadeTween;
    private PanelContainer dialoguePanel = null!;
    private Label dialogueSpeaker = null!;
    private Label dialogueText = null!;
    private VBoxContainer dialogueChoices = null!;
    private Label interactionPrompt = null!;
    private Label3D? worldNotice;
    private readonly List<Button> activeChoiceButtons = [];
    private readonly List<NavigationTriangle> navigationTriangles = [];
    private readonly List<InteractiveDoor> interactiveDoors = [];
    private readonly List<KotorDoorObstacle> currentDoorObstacles = [];
    private readonly List<MaterializedPlaceable> materializedPlaceables = [];
    private readonly List<MaterializedCreature> materializedCreatures = [];
    private readonly Dictionary<string, AnimationPlayer> actorAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> actorModels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> roomModels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RoomRecord> roomRecords =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CycleTextureBinding> cycleTextures = [];
    private readonly Dictionary<string, Tween> roomAlphaTweens =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CreatureRecord> actorRecords =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CreatureEffectRig> actorEffectRigs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Texture2D> actorEffectTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> actorTalkOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LipRig> actorLipRigs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CameraRecord> dialogueCameras = [];
    private readonly Dictionary<string, WaypointRecord> moduleWaypoints =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MaterializedSoundObject> moduleSoundObjects =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reportedUnsupportedScripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> playedDialogueMedia = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Cubemap> environmentMapTextures =
        new(StringComparer.OrdinalIgnoreCase);
    private float environmentReflectionStrength =
        KotorEnvironmentMaterialPolicy.SourceReflectionStrength;
    private float environmentMaximumReflectionWeight =
        KotorEnvironmentMaterialPolicy.SourceMaximumReflectionWeight;
    private bool enhancedPresentation;
    private int dynamicMaterialSurfaces;
    private int enhancedDynamicPbrSurfaces;
    private int enhancedDynamicNormalSurfaces;
    private int authoredDynamicNormalScaleSurfaces;
    private int transparentDynamicSurfaces;
    private int materializedCreatureEmitters;
    private int materializedCreatureLights;
    private int materializedCreatureEffectAnimations;
    private int additiveDynamicSurfaces;
    private int configuredAdditiveDynamicSurfaces;
    private KotorLightmapTransfer lightmapTransfer =
        KotorEnvironmentMaterialPolicy.LightmapTransfer(enhanced: false);
    private RenderingQualityDecision renderingQualityDecision = null!;
    private KotorRuntimeConfiguration runtimeConfiguration = null!;
    private string loadedModuleId = "";
    private KotorModuleContentMode moduleContentMode = KotorModuleContentMode.GenericWorld;
    private KotorGameplaySimulation? gameplaySimulation;
    private KotorCombatSimulation? firstEncounterCombat;
    private KotorCombatExperienceTable? combatExperienceTable;
    private CreatureCombatRecord? playerCombat;
    private string selectedCombatTarget = "end_sith2";
    private readonly Random combatRandom = new(0x4B4F544F);
    private int capturedFrames;
    private int captureMatchedFrames;
    private int captureTargetFrame;
    private bool captureCompleted;
    private int readyFrames;
    private bool moduleReady;
    private bool automatedChoiceApplied;
    private bool automatedMoveApplied;
    private bool automatedDoorApplied;
    private bool automatedLockerApplied;
    private bool automatedTutorialXpChain;
    private bool automatedEquipmentApplied;
    private bool automatedXrDialogueControlsApplied;
    private bool automatedCorridorTrigger;
    private bool automatedCorridorTriggerVerified;
    private bool automatedCorridorTransmissionVerified;
    private bool automatedFirstEncounterVerified;
    private int nextAutomatedCombatFrame;
    private bool showcaseRouteEnabled;
    private bool genericWorldShowcaseEnabled;
    private double genericWorldShowcaseSeconds;
    private bool genericWorldShowcaseFramed;
    private ShowcasePhase showcasePhase;
    private int showcasePhaseFrames;
    private int showcaseRouteFrames;
    private string showcaseChoiceNode = "";
    private int showcaseChoiceHoldFrames;
    private int showcaseOpeningChoiceCount;
    private int showcaseTransmissionChoiceCount;
    private int showcaseTransmissionAutomaticBaseline;
    private bool showcaseTransmissionVerified;
    private bool firstEncounterStarted;
    private bool firstEncounterCombatReady;
    private bool cinematicSequenceActive;
    private bool dialogueSequenceActive;
    private bool dialogueCameraActive;
    private bool dialogueCameraWasDynamic;
    private float dialogueFieldOfView = 55.0f;
    private string lastDynamicDialogueActor = "";
    private string dialogueOwnerActor = "";
    private string dialogueManifestDirectory = "";
    private string openingDialogueConversation = "";
    private DialogueGraph? openingDialogueGraph;
    private FirstEncounterRecord? firstEncounter;
    private DialogueGraph? firstEncounterGraph;
    private FirstEncounterAudioStreams? firstEncounterAudio;
    private FirstEncounterEffectTextures? firstEncounterEffectTextures;
    private string currentDialogueConversation = "";
    private DialogueGraph? pendingAutomaticDialogueGraph;
    private string pendingAutomaticDialogueTarget = "";
    private string currentDialogueNodeKey = "";
    private int automaticDialogueTransitionCount;
    private int encounterAttackSoundCount;
    private int encounterProjectileCount;
    private int encounterMuzzleFlashCount;
    private int encounterMuzzleLayerCount;
    private int encounterMuzzleLightCount;
    private int encounterImpactCount;
    private int encounterImpactSoundCount;
    private int encounterImpactLightCount;
    private int encounterProjectileTrailCount;
    private int encounterSourceHookJoinCount;
    private int roomSmokeEmitterCount;
    private int roomSparkEmitterCount;
    private bool damagedEndSmokeReady;
    private string currentMusicResref = "";
    private int areaMusicRequestGeneration;
    private string captureDialogueNode = "";
    private ulong inputLockedUntilMsec;
    private string currentVoiceActor = "";
    private LipTrack? currentLipTrack;
    private LipRig? currentLipRig;
    private int currentLipSegment = -1;
    private string lastDialogueSpeaker = "TRASK ULGO";
    private float yaw;
    private float pitch;

    public override void _Ready()
    {
        ConfigureFlatReferenceViewportIfRequested();
        CreateEnvironment();
        CreateCamera();
        TryInitializeOpenXR();
        CreateAudio();
        CreateOverlay();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (int.TryParse(launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_FRAME"),
                out var configuredCaptureFrame))
            captureTargetFrame = Math.Max(1, configuredCaptureFrame);
        captureDialogueNode =
            launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE") ?? "";
        showcaseRouteEnabled = launchEnvironment.Get(
            "NIKAMI_AURORA_SHOWCASE_ROUTE") == "1";
        genericWorldShowcaseEnabled = launchEnvironment.Get(
            "NIKAMI_AURORA_GENERIC_WORLD_SHOWCASE") == "1";
        if (showcaseRouteEnabled && genericWorldShowcaseEnabled)
            throw new InvalidDataException(
                "Story and generic-world showcase routes are mutually exclusive.");
        showcasePhase = showcaseRouteEnabled
            ? ShowcasePhase.OpeningDialogue
            : ShowcasePhase.Disabled;
        forcedPlayerAnimation =
            launchEnvironment.Get("NIKAMI_AURORA_TEST_PLAYER_ANIMATION") ?? "";

        var manifestPath = launchEnvironment.Get("NIKAMI_AURORA_MODULE_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifestPath))
            throw new InvalidDataException(
                "NIKAMI_AURORA_MODULE_MANIFEST is required; use Start-KotorGodot.ps1 " +
                "with -Module or -Manifest");
        Callable.From(() => LoadModuleAsync(manifestPath)).CallDeferred();
    }

    public override void _Process(double delta)
    {
        AdvanceCycleTextures(delta);
        var basis = new Basis(Vector3.Up, yaw);
        var rightIntent = 0.0f;
        var forwardIntent = 0.0f;
        if (Input.IsKeyPressed(Key.W)) forwardIntent += 1.0f;
        if (Input.IsKeyPressed(Key.S)) forwardIntent -= 1.0f;
        if (Input.IsKeyPressed(Key.A)) rightIntent -= 1.0f;
        if (Input.IsKeyPressed(Key.D)) rightIntent += 1.0f;
        if (xrActive)
        {
            var stick = xrLeftHand.GetVector2("primary");
            if (moduleReady && readyFrames is >= 10 and < 30 &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_XR_MOVEMENT") == "1")
                stick = new Vector2(0, 0.75f);
            var turnStick = xrRightHand.GetVector2("primary");
            if (moduleReady && readyFrames is >= 10 and < 13 &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_XR_SNAP_TURN") == "1")
                turnStick = new Vector2(0.8f, 0);
            UpdateXrSnapTurn(turnStick.X);
            rightIntent += stick.X;
            forwardIntent += stick.Y;
            if (!xrMovementInputReported && stick.Length() >= 0.2f)
            {
                xrMovementInputReported = true;
                GD.Print($"NIKAMI_AURORA_XR_MOVEMENT status=input axis={stick} " +
                         $"blockedByDialogue={dialoguePanel.Visible} " +
                         $"blockedByCinematic={cinematicSequenceActive}");
            }
            UpdateXrTrackedPlayerRig();
        }
        var sprinting = Input.IsKeyPressed(Key.Shift) ||
                        (xrActive && xrLeftHand.IsButtonPressed("primary_click"));
        var intent = KotorMovementIntent.FromAxes(rightIntent, forwardIntent, sprinting);
        var movementResult = !dialogueSequenceActive && !cinematicSequenceActive
            ? StepPlayer(intent, (float)delta)
            : new KotorMovementResult(
                simulationPlayerPosition, true, false, KotorLocomotionMode.Idle);
        if (xrActive && !xrMovementAcceptedReported &&
            movementResult.Mode != KotorLocomotionMode.Idle)
        {
            xrMovementAcceptedReported = true;
            GD.Print($"NIKAMI_AURORA_XR_MOVEMENT status=accepted " +
                     $"mode={movementResult.Mode} position={playerBody.GlobalPosition}");
        }
        var requestedAnimation = movementResult.Mode switch
        {
            KotorLocomotionMode.Walk => "walk",
            KotorLocomotionMode.Run => "run",
            _ => "pause1"
        };
        if (!dialogueSequenceActive)
            PlayPlayerAnimation(!string.IsNullOrWhiteSpace(forcedPlayerAnimation)
                ? forcedPlayerAnimation
                : requestedAnimation);
        UpdateInteractionPrompt();
        UpdateLipSync();

        if (moduleReady)
        {
            readyFrames++;
            if (!automatedDoorApplied &&
                readyFrames >= runtimeConfiguration.Automation.DoorFrame &&
                launchEnvironment.Get("NIKAMI_AURORA_TEST_OPEN_DOOR") == "1" &&
                interactiveDoors.Count > 0)
            {
                automatedDoorApplied = true;
                var openingDoor = RequireInteractiveDoor("end_door01");
                if (!IsDoorOpen(openingDoor))
                    ToggleDoor(openingDoor);
                else
                    GD.Print($"NIKAMI_AURORA_DOOR status=already-open tag={openingDoor.Source.Tag}");
            }
            if (!automatedLockerApplied &&
                readyFrames >= runtimeConfiguration.Automation.StateFrame &&
                launchEnvironment.Get("NIKAMI_AURORA_TEST_OPEN_LOCKER") == "1" &&
                materializedPlaceables.Count > 0)
            {
                automatedLockerApplied = true;
                UsePlaceable(materializedPlaceables[0]);
            }
            if (!automatedInventoryOpened &&
                readyFrames >= runtimeConfiguration.Automation.MenuOpenFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_INVENTORY_SCREEN") == "1")
            {
                automatedInventoryOpened = true;
                ShowInventory();
            }
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_INVENTORY_QUEST_FILTER") == "1" &&
                inventoryScreen?.Visible == true)
            {
                if (automatedInventoryQuestFilterStage == 0 &&
                    readyFrames >= runtimeConfiguration.Automation.PrimaryFrame)
                {
                    ToggleInventoryQuestItems();
                    if (visibleInventoryItems.Count != 0 ||
                        inventoryQuestItemsButton?.Text != flatUiRecord?.Inventory.AllItems.Text)
                        throw new InvalidDataException(
                            "Opening inventory quest-only filter did not produce its source-empty state");
                    automatedInventoryQuestFilterStage = 1;
                }
                else if (automatedInventoryQuestFilterStage == 1 &&
                         readyFrames >= runtimeConfiguration.Automation.SecondaryFrame)
                {
                    ToggleInventoryQuestItems();
                    var expectedAllItems = ExpectedInventoryRowCount(questItemsOnly: false);
                    if (visibleInventoryItems.Count != expectedAllItems || inventoryQuestItemsOnly)
                        throw new InvalidDataException(
                            "Opening inventory all-items filter did not restore source item types");
                    automatedInventoryQuestFilterStage = 2;
                    GD.Print($"NIKAMI_AURORA_INVENTORY_FILTER status=pass " +
                             $"all={expectedAllItems} quest=0 " +
                             $"all-restored={expectedAllItems}");
                }
            }
            if (!automatedInventoryScrollVerified &&
                readyFrames >= runtimeConfiguration.Automation.PrimaryFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_INVENTORY_SCROLL_REPEAT") == "1" &&
                inventoryScreen?.Visible == true)
            {
                var expectedRows = ExpectedInventoryRowCount(questItemsOnly: false);
                if (visibleInventoryItems.Count != expectedRows ||
                    inventorySourceScrollbar?.Visible != true ||
                    inventoryScrollThumb is null || inventoryScroll is null)
                    throw new InvalidDataException(
                        "Inventory overflow simulation did not materialize its source scrollbar");
                var thumbBefore = inventoryScrollThumb.Position.Y;
                ScrollInventoryBy(inventoryRowHeight);
                if (inventoryScroll.ScrollVertical != inventoryRowHeight ||
                    inventoryScrollThumb.Position.Y <= thumbBefore)
                    throw new InvalidDataException(
                        "Inventory source scrollbar did not advance one item row");
                var expectedBottom = visibleInventoryItems.Count * inventoryRowHeight -
                                     (int)inventoryScroll.Size.Y;
                ScrollInventoryBy(expectedBottom);
                if (inventoryScroll.ScrollVertical != expectedBottom)
                    throw new InvalidDataException(
                        "Inventory source scrollbar did not clamp to its final item row");
                if (inventoryScrollSlider is null)
                    throw new InvalidDataException(
                        "Inventory source scrollbar has no drag control");
                inventoryScrollSlider.Value = inventoryScrollSlider.MaxValue;
                if (inventoryScroll.ScrollVertical != 0)
                    throw new InvalidDataException(
                        "Inventory source scrollbar drag did not reach its first row");
                inventoryScrollSlider.Value = 0;
                if (inventoryScroll.ScrollVertical != expectedBottom)
                    throw new InvalidDataException(
                        "Inventory source scrollbar drag did not reach its final row");
                automatedInventoryScrollVerified = true;
                GD.Print($"NIKAMI_AURORA_INVENTORY_SCROLL_SIMULATION status=pass " +
                         $"rows={visibleInventoryItems.Count} rowHeight={inventoryRowHeight} " +
                         $"bottom={expectedBottom} input=arrows,drag");
            }
            if (!automatedInventoryPartySelectionVerified &&
                readyFrames >= runtimeConfiguration.Automation.PrimaryFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_INVENTORY_PARTY_SELECTION") == "1" &&
                inventoryScreen?.Visible == true)
            {
                var memberSource = flatUiRecord?.Inventory.PartyMembers.Single(member =>
                    member.SourceKind.Equals("utc", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException(
                        "Opening inventory has no UTC-backed party member");
                SelectInventoryPartyMember(memberSource.Id);
                var snapshot = gameplaySimulation?.CaptureSnapshot()
                    ?? throw new InvalidDataException(
                        "Opening inventory party selection has no profile state");
                var selected = snapshot.PartyMembers[snapshot.SelectedPartyMemberId];
                if (!selected.Id.Equals(memberSource.Id, StringComparison.OrdinalIgnoreCase) ||
                    selected.CurrentVitality != memberSource.CurrentVitality ||
                    selected.MaximumVitality != memberSource.MaximumVitality ||
                    selected.Defense != memberSource.Defense ||
                    inventoryVitality?.Text !=
                    $"{memberSource.CurrentVitality}/{memberSource.MaximumVitality}" ||
                    inventoryDefense?.Text != memberSource.Defense.ToString() ||
                    !ReferenceEquals(
                        inventoryPortrait?.Texture,
                        Texture(memberSource.Portrait.Resref)) ||
                    !inventoryPartyButtons.ContainsKey(memberSource.Id) ||
                    inventoryUseButton?.Disabled != false)
                    throw new InvalidDataException(
                        "Opening inventory party selection drifted from Trask's UTC state");
                automatedInventoryPartySelectionVerified = true;
                GD.Print($"NIKAMI_AURORA_INVENTORY_PARTY status=pass " +
                         $"selected={memberSource.Id} " +
                         $"vitality={memberSource.CurrentVitality}/" +
                         $"{memberSource.MaximumVitality} defense={memberSource.Defense} " +
                         $"medpacTarget=enabled");
            }
            if (!automatedEquipmentScreenOpened &&
                readyFrames >= runtimeConfiguration.Automation.MenuOpenFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN") == "1")
            {
                automatedEquipmentScreenOpened = true;
                ShowEquipment();
            }
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_EQUIPMENT_MENU_TRANSACTION") == "1" &&
                equipmentScreen?.Visible == true)
            {
                if (automatedEquipmentMenuStage == 0 && readyFrames >=
                    runtimeConfiguration.Automation.EquipmentTransactionFrames[0])
                {
                    if (equipmentOkButton?.Visible == true)
                        throw new InvalidDataException(
                            "Equipment OK was visible without a pending change");
                    SelectEquipmentSlot(KotorEquipmentSlot.Armor);
                    SelectEquipmentRow(1);
                    if (equipmentOkButton?.Visible != true)
                        throw new InvalidDataException(
                            "Equipment OK did not appear for a pending change");
                    CommitEquipmentSelection();
                    if (equipmentOkButton?.Visible == true)
                        throw new InvalidDataException(
                            "Equipment OK remained visible after commit");
                    automatedEquipmentMenuStage = 1;
                }
                else if (automatedEquipmentMenuStage == 1 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[1])
                {
                    SelectEquipmentRow(0);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 2;
                }
                else if (automatedEquipmentMenuStage == 2 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[2])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.LeftHand);
                    if (visibleEquipmentChoices.Count != 2)
                        throw new InvalidDataException(
                            "Source-valid left-hand Short Sword choice was not materialized");
                    SelectEquipmentRow(1);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 3;
                }
                else if (automatedEquipmentMenuStage == 3 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[3])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.Armor);
                    SelectEquipmentRow(1);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 4;
                }
                else if (automatedEquipmentMenuStage == 4 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[4])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.LeftHand);
                    SelectEquipmentRow(0);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 5;
                }
                else if (automatedEquipmentMenuStage == 5 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[5])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.Armor);
                    SelectEquipmentRow(0);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 6;
                }
                else if (automatedEquipmentMenuStage == 6 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[6])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.RightHand);
                    SelectEquipmentRow(1);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 7;
                }
                else if (automatedEquipmentMenuStage == 7 && readyFrames >=
                         runtimeConfiguration.Automation.EquipmentTransactionFrames[7])
                {
                    SelectEquipmentSlot(KotorEquipmentSlot.Armor);
                    SelectEquipmentRow(1);
                    CommitEquipmentSelection();
                    automatedEquipmentMenuStage = 8;
                    GD.Print("NIKAMI_AURORA_EQUIPMENT_UI_TRANSACTION status=pass " +
                             "variants=clothing,base,left-short-sword," +
                             "clothing-left-short-sword,short-sword,combined");
                }
            }
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_FLAT_MENU_NAVIGATION") == "1")
            {
                if (automatedFlatMenuNavigationStage == 0 &&
                    readyFrames >= runtimeConfiguration.Automation.PrimaryFrame)
                {
                    ShowInventory();
                    if (inventoryScreen?.Visible != true ||
                        equipmentScreen?.Visible == true ||
                        desktopHudRoot?.Visible == true)
                        throw new InvalidDataException(
                            "Equipment-to-inventory navigation visibility drifted");
                    automatedFlatMenuNavigationStage = 1;
                }
                else if (automatedFlatMenuNavigationStage == 1 &&
                         readyFrames >= runtimeConfiguration.Automation.SecondaryFrame)
                {
                    ShowEquipment();
                    if (equipmentScreen?.Visible != true ||
                        inventoryScreen?.Visible == true ||
                        desktopHudRoot?.Visible == true)
                        throw new InvalidDataException(
                            "Inventory-to-equipment navigation visibility drifted");
                    automatedFlatMenuNavigationStage = 2;
                    GD.Print("NIKAMI_AURORA_FLAT_MENU_NAVIGATION status=pass " +
                             "path=hud,equipment,inventory,equipment");
                }
            }
            if (!automatedTutorialXpChain &&
                readyFrames >= runtimeConfiguration.Automation.StateFrame &&
                launchEnvironment.Get("NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN") == "1" &&
                materializedPlaceables.Count > 0)
            {
                automatedTutorialXpChain = true;
                UsePlaceable(materializedPlaceables[0]);
                ExecuteScript("k_pend_door1xp");
                var finalExperience = RequireGameplaySimulation().CaptureSnapshot().PlayerExperience;
                if (finalExperience != 150)
                    throw new InvalidDataException(
                        $"Tutorial XP chain ended at {finalExperience}, expected 150");
                GD.Print("NIKAMI_AURORA_NCS_CHAIN status=pass xp=0->50->150");
            }
            if (!automatedEquipmentApplied &&
                readyFrames >= runtimeConfiguration.Automation.MenuOpenFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR") == "1")
            {
                automatedEquipmentApplied = true;
                EquipOpeningGear(null);
            }
            if (!automatedXrDialogueControlsApplied && xrActive &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_XR_DIALOGUE_CONTROLS") == "1" &&
                dialoguePanel.Visible && !dialogueVoice.Playing &&
                activeChoiceButtons.Count >= 2 &&
                activeChoiceButtons.All(button => !button.Disabled))
            {
                automatedXrDialogueControlsApplied = true;
                MoveXrDialogueChoice(1);
                SelectXrDialogueChoice();
            }
            if (!automatedCorridorTrigger &&
                readyFrames >= runtimeConfiguration.Automation.StateFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRIGGER") == "1" &&
                interactiveDoors.Count > 0)
            {
                automatedCorridorTrigger = true;
                var openingDoor = RequireInteractiveDoor("end_door01");
                if (!IsDoorOpen(openingDoor))
                    ToggleDoor(openingDoor);
                var start = playerBody.GlobalPosition;
                var accepted = MovePlayer(-basis.Z * 10.0f);
                GD.Print($"NIKAMI_AURORA_CORRIDOR_MOVE status={(accepted ? "accepted" : "rejected")} " +
                         $"from={start} to={playerBody.GlobalPosition} requested=10.000");
            }
            if (automatedCorridorTrigger && !automatedCorridorTriggerVerified &&
                readyFrames >= runtimeConfiguration.Automation.SceneReadyFrame)
            {
                automatedCorridorTriggerVerified = true;
                var snapshot = RequireGameplaySimulation().CaptureSnapshot();
                if (!snapshot.TriggerStates.Values.Any(value => value) ||
                    !snapshot.GlobalNumbers.TryGetValue("END_TRASK_DLG", out var global) ||
                    global != 10 || !dialoguePanel.Visible ||
                    !dialogueSpeaker.Text.Equals("CARTH", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "First corridor trigger did not reach Carth dialogue starter 8");
                GD.Print("NIKAMI_AURORA_CORRIDOR_TRIGGER status=pass " +
                         "global=END_TRASK_DLG:10 event=50 conversation=end_trask01 starter=8 " +
                         "speaker=CARTH");
            }
            if (!automatedCorridorTransmissionVerified &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRANSMISSION") == "1" &&
                currentDialogueNodeKey.Equals("entry:35", StringComparison.OrdinalIgnoreCase))
            {
                automatedCorridorTransmissionVerified = true;
                var snapshot = RequireGameplaySimulation().CaptureSnapshot();
                if (!snapshot.GlobalNumbers.TryGetValue("END_CARTH_DLG", out var carthGlobal) ||
                    carthGlobal != 1 ||
                    !snapshot.GlobalNumbers.TryGetValue("END_TRASK_DLG", out var traskGlobal) ||
                    traskGlobal != 11 || !snapshot.MapRevealed ||
                    !dialogueSpeaker.Text.Equals("TRASK ULGO", StringComparison.OrdinalIgnoreCase) ||
                    activeChoiceButtons.Count != 2 || automaticDialogueTransitionCount != 3)
                    throw new InvalidDataException(
                        "First corridor transmission did not reach the journal choice");
                GD.Print("NIKAMI_AURORA_CORRIDOR_TRANSMISSION status=pass " +
                         "nodes=32->33->34->35 automatic=3 " +
                         "globals=END_CARTH_DLG:1,END_TRASK_DLG:11 map=revealed " +
                         "speaker=TRASK_ULGO choices=2");
            }
            if (!firstEncounterStarted &&
                readyFrames >= runtimeConfiguration.Automation.SceneReadyFrame &&
                (launchEnvironment.Get(
                     "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1" ||
                 launchEnvironment.Get(
                     "NIKAMI_AURORA_TEST_FIRST_COMBAT") == "1"))
                StartFirstEncounter();
            if (firstEncounterCombatReady && !automatedFirstEncounterVerified &&
                (launchEnvironment.Get(
                     "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1" ||
                 launchEnvironment.Get(
                     "NIKAMI_AURORA_TEST_FIRST_COMBAT") == "1" ||
                 showcaseRouteEnabled))
            {
                automatedFirstEncounterVerified = true;
                var snapshot = RequireGameplaySimulation().CaptureSnapshot();
                var encounterDoor = RequireInteractiveDoor("end_door02");
                if (!snapshot.GlobalNumbers.TryGetValue("END_TRASK_DLG", out var traskGlobal) ||
                    traskGlobal != 1 || !IsDoorOpen(encounterDoor) ||
                    dialoguePanel.Visible || cinematicSequenceActive ||
                    encounterAttackSoundCount < 4 ||
                    encounterProjectileCount < 4 ||
                    encounterMuzzleFlashCount < 4 ||
                    encounterMuzzleLayerCount != encounterMuzzleFlashCount * 5 ||
                    encounterMuzzleLightCount != encounterMuzzleFlashCount ||
                    encounterProjectileTrailCount != encounterProjectileCount ||
                    encounterSourceHookJoinCount != encounterProjectileCount ||
                    encounterImpactCount < 3 ||
                    encounterImpactSoundCount != encounterImpactCount ||
                    encounterImpactLightCount != encounterImpactCount ||
                    roomSmokeEmitterCount != 9 || roomSparkEmitterCount != 3 ||
                    !damagedEndSmokeReady ||
                    firstEncounter is null ||
                    !IsFirstEncounterEnvironmentReady(firstEncounter) ||
                    !currentMusicResref.Equals(
                        "mus_bat_sithbs", StringComparison.OrdinalIgnoreCase) ||
                    !playedDialogueMedia.Contains("nm01aaroom03000_") ||
                    !playedDialogueMedia.Contains("nm01aaroom03001_") ||
                    !currentDialogueNodeKey.Equals(
                        "encounter:gameplay-ready", StringComparison.OrdinalIgnoreCase) ||
                    (!xrActive && !camera.Current))
                    throw new InvalidDataException(
                        "First encounter did not reach its combat-ready state");
                GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=pass " +
                         "door=end_door02 cameras=26,19,20 dialogue=end_room3 " +
                         "global=END_TRASK_DLG:1 stage=third-person-gameplay " +
                         $"voices=2 attacks={encounterAttackSoundCount} " +
                         $"projectiles={encounterProjectileCount} " +
                         $"muzzles={encounterMuzzleFlashCount} impacts={encounterImpactCount} " +
                         $"roomFx=smoke:{roomSmokeEmitterCount},sparks:{roomSparkEmitterCount} " +
                         $"environment={firstEncounter.EnvironmentPlaceables.Count} " +
                         $"music={currentMusicResref}");
                GD.Print("NIKAMI_AURORA_FX_SYNC status=pass " +
                         $"hooks={encounterSourceHookJoinCount} " +
                         $"projectiles={encounterProjectileCount} " +
                         $"trails={encounterProjectileTrailCount} " +
                         $"muzzle_flashes={encounterMuzzleFlashCount} " +
                         $"muzzle_layers={encounterMuzzleLayerCount} " +
                         $"muzzle_lights={encounterMuzzleLightCount} " +
                         $"impacts={encounterImpactCount} " +
                         $"impact_sounds={encounterImpactSoundCount} " +
                         $"impact_lights={encounterImpactLightCount} " +
                         "audio=positional sync=arrival parity_claim=none");
            }
            if (firstEncounterCombatReady &&
                launchEnvironment.Get("NIKAMI_AURORA_TEST_FIRST_COMBAT") == "1" &&
                readyFrames >= nextAutomatedCombatFrame)
            {
                nextAutomatedCombatFrame = readyFrames + 12;
                ResolveFirstEncounterPlayerTurn();
            }
            var configuredChoice = launchEnvironment.Get("NIKAMI_AURORA_DIALOGUE_CHOICE");
            if (!automatedChoiceApplied &&
                readyFrames >= runtimeConfiguration.Automation.ChoiceFrame &&
                int.TryParse(configuredChoice, out var choice) &&
                choice >= 0 && choice < activeChoiceButtons.Count)
            {
                automatedChoiceApplied = true;
                activeChoiceButtons[choice].EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print($"NIKAMI_AURORA_DIALOGUE_CHOICE status=selected index={choice}");
            }
            var configuredMove = launchEnvironment.Get("NIKAMI_AURORA_TEST_MOVE_METERS");
            if (!automatedMoveApplied &&
                readyFrames >= runtimeConfiguration.Automation.StateFrame &&
                double.TryParse(configuredMove, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var meters) && Math.Abs(meters) > 0.001)
            {
                automatedMoveApplied = true;
                var start = playerBody.GlobalPosition;
                var accepted = MovePlayer(-basis.Z * (float)meters);
                GD.Print($"NIKAMI_AURORA_NAV_TEST status={(accepted ? "accepted" : "rejected")} " +
                         $"from={start} to={playerBody.GlobalPosition} requested={meters:F3}");
            }
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_CLEAN") == "1")
                overlayLayer.Visible = false;
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP") == "1")
                FrameLipSyncCloseup(dialogueOwnerActor);
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_CREATURE") is { Length: > 0 } effectActor &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_CREATURE_EFFECT_ANIMATION")
                    is { Length: > 0 } effectAnimation)
                PlayActorAnimation(effectActor, effectAnimation, false);
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_CREATURE") is { Length: > 0 } creature)
                FrameCreatureInWorld(creature);
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_P2P_EMITTER_CLOSEUP") == "1")
                FramePointToPointEmitterCloseup();
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP") == "1")
                FramePlayerEquipmentCloseup();
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP") == "1")
                FrameChairCloseup();
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                launchEnvironment.Get(
                    "NIKAMI_AURORA_CAPTURE_XR_BODY_LOOKDOWN") == "1")
                FrameXrBodyLookDown();
            if (showcaseRouteEnabled)
                AdvanceShowcaseRoute();
            if (genericWorldShowcaseEnabled)
                AdvanceGenericWorldShowcase(delta);
        }

        UpdateXrSpectatorCamera();
        UpdateFlatUiVisibility();
        UpdateXrHudVisibility();
        var capturePath = launchEnvironment.Get("NIKAMI_AURORA_CAPTURE");
        var captureNodeMatches = string.IsNullOrWhiteSpace(captureDialogueNode) ||
                                 currentDialogueNodeKey.Equals(
                                     captureDialogueNode, StringComparison.OrdinalIgnoreCase);
        captureMatchedFrames = captureNodeMatches ? captureMatchedFrames + 1 : 0;
        if (moduleReady && !captureCompleted && !string.IsNullOrWhiteSpace(capturePath) &&
            ++capturedFrames >= captureTargetFrame &&
            captureNodeMatches && (!xrSpectatorActive || captureMatchedFrames >= 2))
        {
            captureCompleted = true;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            var captureImage = DisplayServer.GetName().Equals(
                "headless", StringComparison.OrdinalIgnoreCase)
                ? null
                : GetViewport().GetTexture().GetImage();
            var error = captureImage is null ||
                        xrSpectatorActive && !HasVisibleCapturePixels(captureImage)
                ? Error.Failed
                : captureImage.SavePng(capturePath);
            if (error != Error.Ok)
                GD.PushError("NIKAMI_AURORA_CAPTURE status=fail " +
                             $"source={(xrSpectatorActive ? "xr-spectator" : "root")} " +
                             $"reason={(captureImage is null ? "no-render-target" : "near-black")}");
            GD.Print($"NIKAMI_AURORA_CAPTURE status={error} " +
                     $"source={(xrSpectatorActive ? "xr-spectator" : "root")} " +
                     $"path={capturePath}");
            if (launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
                RequestCleanExit(error == Error.Ok ? 0 : 1);
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (HandleFlatUiInput(inputEvent))
            return;
        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            yaw -= motion.Relative.X * 0.0025f;
            pitch = Mathf.Clamp(pitch - motion.Relative.Y * 0.0025f, -0.75f, 0.45f);
            playerBody.Rotation = new Vector3(0, yaw, 0);
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
        }
        else if (inputEvent is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
        }
        else if (inputEvent is InputEventKey interact && interact.Pressed && interact.Keycode == Key.E)
        {
            HandleInteraction(null);
        }
        else if (inputEvent is InputEventKey target && target.Pressed &&
                 target.Keycode == Key.Tab && firstEncounterCombatReady)
        {
            CycleFirstEncounterTarget();
        }
        else if (inputEvent is InputEventKey attack && attack.Pressed &&
                 attack.Keycode == Key.Space && firstEncounterCombatReady)
        {
            ResolveFirstEncounterPlayerTurn();
        }
        else if (inputEvent is InputEventKey equip && equip.Pressed && equip.Keycode == Key.Q)
        {
            EquipOpeningGear(null);
        }
    }

    private void OnXrButtonPressed(XRController3D controller, string name)
    {
        if (dialoguePanel.Visible &&
            (name.EndsWith("ax_button", StringComparison.OrdinalIgnoreCase) ||
             name.EndsWith("by_button", StringComparison.OrdinalIgnoreCase)))
        {
            if (name.EndsWith("ax_button", StringComparison.OrdinalIgnoreCase) &&
                controller == xrRightHand)
                SelectXrDialogueChoice();
            else if (name.EndsWith("ax_button", StringComparison.OrdinalIgnoreCase))
                MoveXrDialogueChoice(-1);
            else
                MoveXrDialogueChoice(1);
            return;
        }
        if (name.EndsWith("ax_button", StringComparison.OrdinalIgnoreCase))
        {
            HandleInteraction(controller);
        }
        else if (name.EndsWith("by_button", StringComparison.OrdinalIgnoreCase))
        {
            EquipOpeningGear(controller);
        }
        else if (name.EndsWith("recenter", StringComparison.OrdinalIgnoreCase))
        {
            XRServer.CenterOnHmd(XRServer.RotationMode.ResetButKeepTilt, true);
            GD.Print("NIKAMI_AURORA_OPENXR status=recentered mode=keep-tilt");
        }
    }

    private void UpdateXrSnapTurn(float axis)
    {
        if (Math.Abs(axis) <= 0.25f)
        {
            xrSnapTurnLatched = false;
            return;
        }
        if (xrSnapTurnLatched || Math.Abs(axis) < 0.7f) return;
        xrSnapTurnLatched = true;
        var headBefore = xrCamera.GlobalPosition;
        var turnDegrees = axis > 0 ? -30.0f : 30.0f;
        yaw += Mathf.DegToRad(turnDegrees);
        playerBody.Rotation = new Vector3(0, yaw, 0);
        ApplyXrGameplayBase();
        var correction = headBefore - xrCamera.GlobalPosition;
        correction.Y = 0;
        var candidate = playerBody.GlobalPosition + correction;
        if (TryProjectToWalkmesh(candidate, out var ground))
        {
            candidate.Y = ground;
            playerBody.GlobalPosition = candidate;
            simulationPlayerPosition = ToNumerics(candidate);
            ApplyXrGameplayBase();
        }
        var headError = new Vector2(
            headBefore.X - xrCamera.GlobalPosition.X,
            headBefore.Z - xrCamera.GlobalPosition.Z).Length();
        GD.Print($"NIKAMI_AURORA_XR_TURN status=snap degrees={turnDegrees:F0} " +
                 $"yaw={Mathf.RadToDeg(yaw):F3} headError={headError:F6}");
    }

    private void MoveXrDialogueChoice(int delta)
    {
        if (activeChoiceButtons.Count == 0) return;
        FocusXrDialogueChoice(xrDialogueChoiceIndex + delta);
        GD.Print($"NIKAMI_AURORA_XR_DIALOGUE status=navigate " +
                 $"index={xrDialogueChoiceIndex} choices={activeChoiceButtons.Count}");
    }

    private void SelectXrDialogueChoice()
    {
        if (activeChoiceButtons.Count == 0) return;
        FocusXrDialogueChoice(xrDialogueChoiceIndex);
        var button = activeChoiceButtons[xrDialogueChoiceIndex];
        if (button.Disabled)
        {
            GD.Print($"NIKAMI_AURORA_XR_DIALOGUE status=blocked reason=voice " +
                     $"index={xrDialogueChoiceIndex}");
            return;
        }
        GD.Print($"NIKAMI_AURORA_XR_DIALOGUE status=selected " +
                 $"index={xrDialogueChoiceIndex} text={button.Text}");
        button.EmitSignal(BaseButton.SignalName.Pressed);
    }

    private void FocusXrDialogueChoice(int requested)
    {
        if (activeChoiceButtons.Count == 0)
        {
            xrDialogueChoiceIndex = 0;
            return;
        }
        xrDialogueChoiceIndex =
            (requested % activeChoiceButtons.Count + activeChoiceButtons.Count) %
            activeChoiceButtons.Count;
        for (var index = 0; index < activeChoiceButtons.Count; index++)
        {
            var button = activeChoiceButtons[index];
            button.Modulate = index == xrDialogueChoiceIndex
                ? new Color(0.55f, 0.88f, 1.0f)
                : Colors.White;
        }
        activeChoiceButtons[xrDialogueChoiceIndex].GrabFocus();
    }

    private void HandleInteraction(XRController3D? controller)
    {
        if (dialoguePanel.Visible || Time.GetTicksMsec() < inputLockedUntilMsec) return;
        activeInteractionController = controller;
        try
        {
            var placeable = NearestPlaceable(2.6f);
            var door = NearestDoor(2.6f);
            if (placeable is not null)
                UsePlaceable(placeable);
            else if (door is not null)
                ToggleDoor(door);
        }
        finally
        {
            activeInteractionController = null;
        }
    }

    private void EquipOpeningGear(XRController3D? controller)
    {
        if (dialoguePanel.Visible || Time.GetTicksMsec() < inputLockedUntilMsec) return;
        var variant = openingEquipmentVariant;
        if (variant is null || gameplaySimulation is null) return;
        var armorResref = variant.ArmorResref
            ?? throw new InvalidDataException("Opening equipment variant has no armor item");
        if (variant.LeftHandResref is not null)
            throw new InvalidDataException(
                "Opening equipment variant unexpectedly targets the left hand");
        var rightHandResref = variant.RightHandResref
            ?? throw new InvalidDataException("Opening equipment variant has no right-hand item");
        var snapshot = gameplaySimulation.CaptureSnapshot();
        if (snapshot.Equipment.TryGetValue(KotorEquipmentSlot.Armor, out var armor) &&
            armor.Equals(armorResref, StringComparison.OrdinalIgnoreCase) &&
            snapshot.Equipment.TryGetValue(KotorEquipmentSlot.RightHand, out var rightHand) &&
            rightHand.Equals(rightHandResref, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print("NIKAMI_AURORA_EQUIPMENT status=already-equipped " +
                     $"armor={armor} rightHand={rightHand}");
            return;
        }
        if (!snapshot.PlayerInventory.ContainsKey(armorResref) ||
            !snapshot.PlayerInventory.ContainsKey(rightHandResref))
        {
            GD.Print("NIKAMI_AURORA_EQUIPMENT status=unavailable " +
                     $"armor={armorResref} rightHand={rightHandResref}");
            return;
        }

        activeInteractionController = controller;
        try
        {
            ApplyGameplayTransition(gameplaySimulation.EquipItems([
                new KotorEquipRequest(armorResref, KotorEquipmentSlot.Armor),
                new KotorEquipRequest(rightHandResref, KotorEquipmentSlot.RightHand)
            ]));
        }
        finally
        {
            activeInteractionController = null;
        }
    }

    private bool EndarAutomationRequested()
    {
        string[] variables =
        [
            "NIKAMI_AURORA_TEST_OPEN_DOOR",
            "NIKAMI_AURORA_TEST_OPEN_LOCKER",
            "NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR",
            "NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN",
            "NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRIGGER",
            "NIKAMI_AURORA_TEST_FIRST_CORRIDOR_TRANSMISSION",
            "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER",
            "NIKAMI_AURORA_TEST_FIRST_COMBAT",
            "NIKAMI_AURORA_SHOWCASE_ROUTE",
            "NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP",
            "NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP",
            "NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP",
            "NIKAMI_AURORA_TEST_XR_DIALOGUE_CONTROLS"
        ];
        return variables.Any(variable =>
            launchEnvironment.Get(variable) == "1");
    }

    private async void LoadModuleAsync(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath))
                throw new FileNotFoundException("KOTOR module manifest is missing", manifestPath);
            var manifest = JsonSerializer.Deserialize<ModuleManifest>(
                await File.ReadAllTextAsync(manifestPath), JsonOptions())
                ?? throw new InvalidDataException("KOTOR module manifest is empty");
            if (manifest.Schema != "nikami-aurora-kotor-module-v1")
                throw new InvalidDataException($"Unsupported module manifest schema: {manifest.Schema}");
            loadedModuleId = KotorModulePresentationPolicy.RequireModuleId(manifest.Module);
            moduleContentMode = KotorModulePresentationPolicy.RequireContentMode(
                loadedModuleId, manifest.ContentMode, manifest.FirstEncounter is not null);
            if (!string.Equals(
                    manifest.MissingSourceAssetPolicy,
                    KotorModulePresentationPolicy.MissingSourceAssetPolicy,
                    StringComparison.Ordinal) ||
                manifest.UnresolvedTextureReferences.Count !=
                manifest.Counts.UnresolvedTextureReferences)
                throw new InvalidDataException(
                    "KOTOR missing-source-asset policy/report inventory is inconsistent");
            KotorModulePresentationPolicy.RequireEndarAutomation(
                moduleContentMode, EndarAutomationRequested());
            runtimeConfiguration = manifest.RuntimeConfiguration.Validate(requireSourceHash: true);
            playerCombat = manifest.Player.Combat;
            combatExperienceTable = new KotorCombatExperienceTable(
                manifest.CombatExperienceTable.SourceSha256,
                manifest.CombatExperienceTable.Rows);
            if (captureTargetFrame == 0)
                captureTargetFrame = runtimeConfiguration.Automation.SceneReadyFrame;

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            ConfigureFlatPresentation(manifest.Ui, manifestDirectory, manifest.ProfileId);
            UpdateLoadingProgress(runtimeConfiguration.Presentation.Loading.RoomLoadingStart);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (await CaptureLoadingPresentationIfRequested())
                return;

            dialogueCameras.Clear();
            foreach (var sourceCamera in manifest.Cameras)
                dialogueCameras[sourceCamera.Id] = sourceCamera;
            dialogueFieldOfView = manifest.CameraStyle.ViewAngle;
            gameplayFieldOfView = manifest.CameraStyle.ViewAngle;
            camera.Fov = gameplayFieldOfView;
            var initialPlayerExperience = runtimeConfiguration.Gameplay.PlayerExperience;
            if (int.TryParse(launchEnvironment.Get("NIKAMI_AURORA_TEST_PLAYER_XP"),
                    out var configuredPlayerXp))
                initialPlayerExperience = Math.Max(0, configuredPlayerXp);
            gameplaySimulation = CreateGameplaySimulation(manifest, initialPlayerExperience);
            moduleWaypoints.Clear();
            foreach (var waypoint in manifest.Waypoints)
            {
                if (!string.IsNullOrWhiteSpace(waypoint.Tag))
                    moduleWaypoints.TryAdd(waypoint.Tag, waypoint);
            }
            var initialGameplayState = gameplaySimulation.CaptureSnapshot();
            var supportedTriggers = initialGameplayState.TriggerStates.Count;
            GD.Print($"NIKAMI_AURORA_GAMEPLAY_STATE status=ready scripts={manifest.ScriptContracts.Count} " +
                      $"doors={manifest.Doors.Count} placeables={manifest.Placeables.Count} " +
                      $"triggers={supportedTriggers}/{manifest.Triggers.Count} " +
                      $"level={initialGameplayState.PlayerLevel} " +
                      $"xp={initialPlayerExperience} " +
                      $"next={initialGameplayState.NextLevelExperience}");
            ApplyAreaLighting(manifest.Lighting);
            environmentMapTextures.Clear();
            foreach (var pair in LoadOwnedEnvironmentMaps(
                         manifest.EnvironmentMaps, manifestDirectory))
                environmentMapTextures.Add(pair.Key, pair.Value);
            environmentReflectionStrength =
                KotorEnvironmentMaterialPolicy.ReflectionStrength(
                    enhancedPresentation);
            environmentMaximumReflectionWeight =
                KotorEnvironmentMaterialPolicy.MaximumReflectionWeight(
                    enhancedPresentation);
            var environmentMaterialBindings = new Dictionary<string, int>(
                StringComparer.OrdinalIgnoreCase);
            var loadedRooms = 0;
            var lightmappedOpaqueMaterials = 0;
            var baseOpaqueMaterials = 0;
            var lightmappedTransparentMaterials = 0;
            var baseTransparentMaterials = 0;
            var sourceAdditiveMaterials = 0;
            var additiveEnvironmentMaterials = 0;
            var additiveLightmappedMaterials = 0;
            var sourceDecalMaterials = 0;
            var enhancedPbrMaterials = 0;
            var normalMappedMaterials = 0;
            var authoredNormalScaleMaterials = 0;
            var cameraCollisionRooms = 0;
            var cameraCollisionShapes = 0;
            var sourcePlaceholderRooms = 0;
            roomSmokeEmitterCount = 0;
            roomSparkEmitterCount = 0;
            damagedEndSmokeReady = false;
            var materializedEmitterCount = 0;
            var alphaEmitterCount = 0;
            var additiveEmitterCount = 0;
            var singleEmitterCount = 0;
            var finiteSingleEmitterCount = 0;
            var orientedEmitterCount = 0;
            var orientedAlphaEmitterCount = 0;
            var normalizedGridEmitterCount = 0;
            var distributedEmitterCount = 0;
            var tintedEmitterCount = 0;
            var softFadeEmitterCount = 0;
            var depthAwareEmitterCount = 0;
            var atlasRangeEmitterCount = 0;
            var visualSafetyEmitterCount = 0;
            var maximumSmokeQuadExtent = 0.0f;
            var maximumSparkTrailExtent = 0.0f;
            var pointToPointEmitterCount = 0;
            var collisionBounceEmitterCount = 0;
            var particleCollisionRooms = 0;
            var particleCollisionWalkmeshTriangles = 0;
            var bounceCoefficients = new List<float>();
            var roomEmitterTextures = new Dictionary<string, Texture2D>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var room in manifest.Rooms)
            {
                Node3D imported;
                if (room.SourcePlaceholder)
                {
                    if (!room.Model.Equals("****", StringComparison.Ordinal) ||
                        !string.IsNullOrWhiteSpace(room.Glb) ||
                        room.WalkmeshTriangles is { Count: > 0 } ||
                        room.Lights is { Count: > 0 } ||
                        room.Emitters is { Count: > 0 })
                        throw new InvalidDataException(
                            "Odyssey source-room placeholder acquired fabricated content");
                    imported = new Node3D();
                    sourcePlaceholderRooms++;
                }
                else if (!string.IsNullOrWhiteSpace(room.Glb))
                {
                    var glbPath = Path.GetFullPath(Path.Combine(manifestDirectory,
                        room.Glb.Replace('/', Path.DirectorySeparatorChar)));
                    var document = new GltfDocument();
                    var state = new GltfState();
                    if (document.AppendFromFile(glbPath, state) != Error.Ok ||
                        document.GenerateScene(state) is not Node3D generatedRoom)
                        throw new InvalidDataException(
                            $"Godot could not import room {room.Model}: {glbPath}");
                    imported = generatedRoom;
                    var materialReport = ConfigureStaticRoomMaterials(
                        imported, ToColor(manifest.Lighting.DynamicAmbient),
                        environmentMapTextures, environmentReflectionStrength,
                        environmentMaximumReflectionWeight, lightmapTransfer,
                        environmentMaterialBindings);
                    lightmappedOpaqueMaterials += materialReport.LightmappedOpaque;
                    baseOpaqueMaterials += materialReport.BaseOpaque;
                    lightmappedTransparentMaterials += materialReport.LightmappedTransparent;
                    baseTransparentMaterials += materialReport.BaseTransparent;
                    sourceAdditiveMaterials += materialReport.SourceAdditive;
                    additiveEnvironmentMaterials +=
                        materialReport.AdditiveEnvironment;
                    additiveLightmappedMaterials +=
                        materialReport.AdditiveLightmapped;
                    sourceDecalMaterials += materialReport.SourceDecal;
                    enhancedPbrMaterials += materialReport.EnhancedPbr;
                    normalMappedMaterials += materialReport.NormalMapped;
                    authoredNormalScaleMaterials += materialReport.AuthoredNormalScale;
                    var roomCollisionShapes = BuildCameraVisibilityCollision(imported);
                    if (roomCollisionShapes > 0)
                        cameraCollisionRooms++;
                    cameraCollisionShapes += roomCollisionShapes;
                }
                else
                {
                    // A resolved source MDL may legitimately contain no render
                    // mesh. Retain its authored placement as an empty scene node;
                    // do not discard the room or invent replacement geometry.
                    imported = new Node3D();
                }
                imported.Name = room.Model;
                imported.Position = ToGodot(room.Position);
                AddChild(imported);
                if (!roomModels.TryAdd(room.Model, imported) ||
                    !roomRecords.TryAdd(room.Model, room))
                    throw new InvalidDataException(
                        $"Duplicate materialized room model: {room.Model}");
                var emitterReport = LoadRoomEmitters(
                    room, imported, manifestDirectory, roomEmitterTextures,
                    enhancedPresentation, ToColor(manifest.Lighting.DynamicAmbient));
                materializedEmitterCount += emitterReport.Total;
                alphaEmitterCount += emitterReport.Alpha;
                additiveEmitterCount += emitterReport.Additive;
                singleEmitterCount += emitterReport.Single;
                finiteSingleEmitterCount += emitterReport.FiniteSingle;
                orientedEmitterCount += emitterReport.Oriented;
                orientedAlphaEmitterCount += emitterReport.OrientedAlpha;
                normalizedGridEmitterCount += emitterReport.NormalizedGrid;
                distributedEmitterCount += emitterReport.Distributed;
                tintedEmitterCount += emitterReport.Tinted;
                softFadeEmitterCount += emitterReport.SoftFade;
                depthAwareEmitterCount += emitterReport.DepthAware;
                atlasRangeEmitterCount += emitterReport.AtlasRangeValidated;
                visualSafetyEmitterCount += emitterReport.VisualSafetyValidated;
                maximumSmokeQuadExtent = Math.Max(
                    maximumSmokeQuadExtent, emitterReport.MaximumSmokeQuadExtent);
                maximumSparkTrailExtent = Math.Max(
                    maximumSparkTrailExtent, emitterReport.MaximumSparkTrailExtent);
                pointToPointEmitterCount += emitterReport.PointToPoint;
                collisionBounceEmitterCount += emitterReport.CollisionBounce;
                particleCollisionRooms += emitterReport.ParticleCollisionRooms;
                particleCollisionWalkmeshTriangles +=
                    emitterReport.ParticleCollisionWalkmeshTriangles;
                bounceCoefficients.AddRange(emitterReport.BounceCoefficients);
                roomSmokeEmitterCount += emitterReport.Smoke;
                roomSparkEmitterCount += emitterReport.Spark;
                damagedEndSmokeReady |= emitterReport.DamagedEnd;
                loadedRooms++;
                UpdateLoadingProgress(
                    runtimeConfiguration.Presentation.Loading.RoomLoadingStart +
                    runtimeConfiguration.Presentation.Loading.RoomLoadingSpan *
                    loadedRooms / Math.Max(1, manifest.Rooms.Count));
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            var configuredMaterialSurfaces = lightmappedOpaqueMaterials + baseOpaqueMaterials +
                                             lightmappedTransparentMaterials + baseTransparentMaterials;
            var visualSourceRooms = manifest.Rooms.Count - sourcePlaceholderRooms;
            if (configuredMaterialSurfaces <= 0 && visualSourceRooms > 0)
                throw new InvalidDataException("Room material audit found no configured surfaces");
            if (cameraCollisionShapes <= 0 && visualSourceRooms > 0)
                throw new InvalidDataException(
                    "Source-room camera/visibility collision coverage drifted");
            if (sourcePlaceholderRooms != manifest.Counts.SourceRoomPlaceholders)
                throw new InvalidDataException(
                    "Odyssey source-room placeholder coverage drifted");
            GD.Print($"NIKAMI_AURORA_ROOM_PLACEHOLDERS status=preserved " +
                     $"source={sourcePlaceholderRooms} fabricated=0 skipped=0");
            if (additiveEnvironmentMaterials !=
                    manifest.Counts.AdditiveEnvironmentSurfaces ||
                additiveLightmappedMaterials !=
                    manifest.Counts.AdditiveLightmappedSurfaces)
                throw new InvalidDataException(
                    "KOTOR mixed source-material coverage drifted");
            GD.Print($"NIKAMI_AURORA_MIXED_MATERIALS status=ready " +
                     $"additive_environment={additiveEnvironmentMaterials} " +
                     $"additive_lightmap={additiveLightmappedMaterials} " +
                     "depth_write=disabled fabricated=0 parity_claim=none");
            GD.Print($"NIKAMI_AURORA_CAMERA_COLLISION status=ready " +
                     $"rooms={cameraCollisionRooms} shapes={cameraCollisionShapes} " +
                     "layer=8 movement=profile-walkmesh");
            GD.Print($"NIKAMI_AURORA_OPACITY status=pass policy=source-opaque " +
                      $"lightmapped={lightmappedOpaqueMaterials} base={baseOpaqueMaterials} " +
                      $"sourceTransparentLightmapped={lightmappedTransparentMaterials} " +
                      $"sourceTransparentBase={baseTransparentMaterials} " +
                      $"sourceAdditive={sourceAdditiveMaterials} " +
                      $"sourceDecal={sourceDecalMaterials} " +
                       "opaqueAlphaWrites=0 opaqueDepthWrite=opaque");
            if (sourceDecalMaterials != manifest.Counts.SourceDecalSurfaces)
                throw new InvalidDataException(
                    "KOTOR source decal material coverage drifted");
            GD.Print($"NIKAMI_AURORA_TXI_DECAL status=ready module={loadedModuleId} " +
                     $"surfaces={sourceDecalMaterials} depth_test=enabled " +
                     $"depth_write=disabled render_priority=" +
                     $"{KotorEnvironmentMaterialPolicy.SourceDecalRenderPriority}");
            GD.Print($"NIKAMI_AURORA_LIGHTMAP_TRANSFER status=ready " +
                     $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                     $"formula={lightmapTransfer.Formula} " +
                     $"diffuse_weight={lightmapTransfer.DynamicLightAlbedoWeight:F2} " +
                     $"baked_weight={lightmapTransfer.BakedEmissionWeight:F2} " +
                     $"dynamic_ambient_weight={lightmapTransfer.DynamicAmbientEmissionWeight:F2} " +
                     $"dynamic_lights={(lightmapTransfer.DynamicLightsEnabled ? 1 : 0)} " +
                     $"double_light={(lightmapTransfer.DynamicLightsEnabled ? "bounded" : "0")} " +
                     $"materials={lightmappedOpaqueMaterials + lightmappedTransparentMaterials} " +
                     "environment_variant=diffuse-plus-masked-cube-before-transfer " +
                     $"evidence={(enhancedPresentation ? "enhancement" : "source-contract")} " +
                      "parity_claim=none");
            var roomPbrCoverage = new KotorPbrCoverage(
                configuredMaterialSurfaces,
                sourceAdditiveMaterials,
                enhancedPbrMaterials,
                enhancedPresentation);
            KotorModulePresentationPolicy.RequirePbrCoverage(roomPbrCoverage);
            if (!enhancedPresentation && normalMappedMaterials != 0 ||
                enhancedPresentation && manifest.Counts.ResolvedBumpMaps > 0 &&
                normalMappedMaterials <= 0 ||
                authoredNormalScaleMaterials !=
                manifest.Counts.AuthoredBumpMapScaleSurfaces)
                throw new InvalidDataException(
                    "KOTOR source/enhanced room PBR coverage is incomplete");
            GD.Print($"NIKAMI_AURORA_ROOM_PBR status=ready module={loadedModuleId} " +
                      $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                      $"renderable_surfaces={roomPbrCoverage.RenderableSurfaces} " +
                      $"source_unshaded_surfaces={roomPbrCoverage.SourceUnshadedSurfaces} " +
                      $"pbr_eligible_surfaces={roomPbrCoverage.PbrEligibleSurfaces} " +
                      $"pbr_surfaces={enhancedPbrMaterials} " +
                     $"normal_mapped_surfaces={normalMappedMaterials} " +
                     $"authored_normal_scale_surfaces={authoredNormalScaleMaterials} " +
                     $"resolved_bump_maps={manifest.Counts.ResolvedBumpMaps} " +
                     $"dielectric_specular=" +
                     $"{KotorEnvironmentMaterialPolicy.DielectricSpecular(enhancedPresentation):F2} " +
                     $"fallback_roughness=" +
                     $"{KotorEnvironmentMaterialPolicy.FallbackRoughness(enhancedPresentation):F2} " +
                     "parity_claim=none");
            GD.Print($"NIKAMI_AURORA_ROOM_EMITTERS status=ready " +
                     $"module={loadedModuleId} authored={manifest.Counts.AuthoredEmitters} " +
                     $"materialized={materializedEmitterCount} alpha={alphaEmitterCount} " +
                     $"additive={additiveEmitterCount} single={singleEmitterCount} " +
                     $"finite_single={finiteSingleEmitterCount} " +
                     $"oriented={orientedEmitterCount} " +
                     $"oriented_alpha={orientedAlphaEmitterCount} " +
                     $"normalized_grid={normalizedGridEmitterCount} " +
                     $"distributed={distributedEmitterCount} " +
                      $"tinted={tintedEmitterCount} " +
                      $"point_to_point={pointToPointEmitterCount} " +
                      $"collision_bounce={collisionBounceEmitterCount} " +
                      $"collision_rooms={particleCollisionRooms} " +
                      $"collision_walkmesh_triangles={particleCollisionWalkmeshTriangles} " +
                      $"bounce_co={string.Join(',', bounceCoefficients.Order().Select(value =>
                          FormattableString.Invariant($"{value:0.000}")))} " +
                      $"smoke={roomSmokeEmitterCount} " +
                     $"sparks={roomSparkEmitterCount} soft_fade={softFadeEmitterCount} " +
                     $"soft_fade_distance=" +
                     $"{(enhancedPresentation ? EnhancedParticleProximityFadeDistance : 0):F2} " +
                     $"depth_aware={depthAwareEmitterCount} " +
                     $"atlas_range_validated={atlasRangeEmitterCount} " +
                     $"visual_safety_validated={visualSafetyEmitterCount} " +
                     $"smoke_quad_max={maximumSmokeQuadExtent:F3} " +
                     $"spark_trail_max={maximumSparkTrailExtent:F3} " +
                     $"quad_limit={EnhancedParticleMaximumQuadExtentMeters:F3}");
            var persistentSingleEmitterCount =
                singleEmitterCount - finiteSingleEmitterCount;
            if (enhancedPresentation
                    ? softFadeEmitterCount != materializedEmitterCount -
                      persistentSingleEmitterCount - orientedEmitterCount
                    : softFadeEmitterCount != 0)
                throw new InvalidDataException(
                    "KOTOR source/enhanced soft-particle coverage is incomplete");
            if (atlasRangeEmitterCount != materializedEmitterCount ||
                visualSafetyEmitterCount != materializedEmitterCount ||
                depthAwareEmitterCount != materializedEmitterCount)
                throw new InvalidDataException(
                    "KOTOR emitter atlas/depth/visual-safety coverage is incomplete");
            if (moduleContentMode == KotorModuleContentMode.EndarOpening &&
                (materializedEmitterCount != 12 || roomSmokeEmitterCount != 9 ||
                 roomSparkEmitterCount != 3 || collisionBounceEmitterCount != 3 ||
                 particleCollisionRooms != 2 ||
                 particleCollisionWalkmeshTriangles != 47 ||
                 bounceCoefficients.Count != 3 ||
                 !damagedEndSmokeReady))
                throw new InvalidDataException(
                    "Endar Spire room-emitter presentation contract drifted");

            var authoredLights = LoadAuthoredLights(manifest.Rooms, manifest.Lighting);
            if (authoredLights.Classified != manifest.Counts.AuthoredLights)
                throw new InvalidDataException(
                    "KOTOR authored-light classification coverage is incomplete");
            dynamicMaterialSurfaces = 0;
            enhancedDynamicPbrSurfaces = 0;
            enhancedDynamicNormalSurfaces = 0;
            authoredDynamicNormalScaleSurfaces = 0;
            transparentDynamicSurfaces = 0;
            additiveDynamicSurfaces = 0;
            configuredAdditiveDynamicSurfaces = 0;
            var materializedPlayer = LoadPlayerModel(
                manifest.Player, manifest.CameraStyle, manifestDirectory);
            var creatureModelRecords = manifest.Creatures
                .SelectMany(creature => creature.Models ?? [])
                .ToArray();
            var equippedWeaponRecords = creatureModelRecords.Where(model =>
                    string.Equals(model.Role, "rightWeapon", StringComparison.Ordinal) ||
                    string.Equals(model.Role, "leftWeapon", StringComparison.Ordinal))
                .ToArray();
            var equippedWeaponAdditiveSurfaces = equippedWeaponRecords.Sum(
                model => model.AdditiveSurfaces);
            var creatureAdditiveSurfaces = creatureModelRecords.Sum(
                model => model.AdditiveSurfaces);
            if (creatureModelRecords.Any(model =>
                    string.IsNullOrWhiteSpace(model.Model) ||
                    model.RenderSurfaces <= 0 || model.AdditiveSurfaces < 0 ||
                    model.AdditiveSurfaces > model.RenderSurfaces ||
                    model.MdlSha256.Length != 64 || model.MdxSha256.Length != 64 ||
                    !model.MdlSha256.All(Uri.IsHexDigit) ||
                    !model.MdxSha256.All(Uri.IsHexDigit)) ||
                manifest.Creatures.Where(creature =>
                        string.Equals(creature.RenderStatus, "ready",
                            StringComparison.OrdinalIgnoreCase))
                    .Any(creature => creature.Models is not { Count: > 0 } ||
                                     creature.Models.Count(model =>
                                         model.Role.Equals("body",
                                             StringComparison.Ordinal)) != 1) ||
                manifest.Counts.AuthoredCreatureModels != creatureModelRecords.Length ||
                manifest.Counts.EquippedWeaponModels != equippedWeaponRecords.Length ||
                manifest.Counts.EquippedWeaponAdditiveSurfaces !=
                    equippedWeaponAdditiveSurfaces)
                throw new InvalidDataException(
                    "KOTOR source-creature model/effect inventory is incomplete");
            var configuredAdditiveBeforeActors = configuredAdditiveDynamicSurfaces;
            var materializedActors = LoadActorModels(manifest.Creatures, manifestDirectory);
            var configuredCreatureAdditiveSurfaces =
                configuredAdditiveDynamicSurfaces - configuredAdditiveBeforeActors;
            if (configuredCreatureAdditiveSurfaces != creatureAdditiveSurfaces)
                throw new InvalidDataException(
                    "KOTOR source-creature additive-material coverage is incomplete");
            var unsupportedCreatureRecords = manifest.Creatures.Count(creature =>
                !string.Equals(creature.RenderStatus, "ready",
                    StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(creature.Glb));
            if (manifest.Counts.RenderReadyCreatures != materializedActors ||
                manifest.Counts.UnsupportedCreatures != unsupportedCreatureRecords)
                throw new InvalidDataException(
                    "KOTOR source-creature manifest inventory drifted");
            KotorModulePresentationPolicy.RequireCreaturePresentation(
                new KotorCreaturePresentationInventory(
                    manifest.Counts.Creatures,
                    materializedActors,
                    unsupportedCreatureRecords,
                    manifest.Counts.AuthoredCreatureModels,
                    creatureModelRecords.Length,
                    manifest.Counts.EquippedWeaponModels,
                    equippedWeaponRecords.Length,
                    manifest.Counts.EquippedWeaponAdditiveSurfaces,
                    equippedWeaponAdditiveSurfaces,
                    manifest.Counts.AuthoredCreatureEmitters,
                    materializedCreatureEmitters,
                    manifest.Counts.AuthoredCreatureLights,
                    materializedCreatureLights,
                    manifest.Counts.AuthoredCreatureEffectAnimations,
                    materializedCreatureEffectAnimations,
                    0));
            GD.Print($"NIKAMI_AURORA_CREATURES status=ready module={loadedModuleId} " +
                     $"expected={manifest.Counts.Creatures} " +
                     $"rendered={materializedActors} missing=0 unsupported=0 " +
                     $"models={creatureModelRecords.Length} " +
                     $"weapons={equippedWeaponRecords.Length} " +
                     $"weapon_additive_surfaces={equippedWeaponAdditiveSurfaces} " +
                     $"effect_emitters={materializedCreatureEmitters} " +
                     $"effect_lights={materializedCreatureLights} " +
                     $"effect_animations={materializedCreatureEffectAnimations} " +
                     "unsupported_effect_semantics=0 standins=0 environment=module-world " +
                     "pbr=global-enhanced");
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_DEBUG_CREATURE_MARKERS") == "1")
                AddCreatureMarkers(manifest.Creatures);
            var materializedDoors = LoadDoorModels(manifest.Doors, manifestDirectory);
            var materializedPlaceables = LoadPlaceableModels(manifest.Placeables, manifestDirectory);
            var dynamicPbrCoverage = new KotorPbrCoverage(
                dynamicMaterialSurfaces,
                additiveDynamicSurfaces,
                enhancedDynamicPbrSurfaces,
                enhancedPresentation);
            KotorModulePresentationPolicy.RequirePbrCoverage(dynamicPbrCoverage);
            if (configuredAdditiveDynamicSurfaces != additiveDynamicSurfaces)
                throw new InvalidDataException(
                    "KOTOR dynamic additive-material coverage is incomplete");
            if (!enhancedPresentation && enhancedDynamicNormalSurfaces != 0)
                throw new InvalidDataException(
                    "KOTOR dynamic-object material coverage is incomplete");
            GD.Print($"NIKAMI_AURORA_DYNAMIC_PBR status=ready module={loadedModuleId} " +
                      $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                      $"renderable_surfaces={dynamicPbrCoverage.RenderableSurfaces} " +
                      $"source_unshaded_surfaces={dynamicPbrCoverage.SourceUnshadedSurfaces} " +
                      $"pbr_eligible_surfaces={dynamicPbrCoverage.PbrEligibleSurfaces} " +
                      $"pbr_surfaces={enhancedDynamicPbrSurfaces} " +
                     $"normal_mapped_surfaces={enhancedDynamicNormalSurfaces} " +
                     $"authored_normal_scale_surfaces=" +
                     $"{authoredDynamicNormalScaleSurfaces} " +
                     $"transparent={transparentDynamicSurfaces} " +
                     $"additive={additiveDynamicSurfaces} " +
                     $"configured_additive={configuredAdditiveDynamicSurfaces} " +
                     $"dielectric_specular=" +
                     $"{KotorEnvironmentMaterialPolicy.DielectricSpecular(enhancedPresentation):F2} " +
                     $"fallback_roughness=" +
                     $"{KotorEnvironmentMaterialPolicy.FallbackRoughness(enhancedPresentation):F2} " +
                     "parity_claim=none");
            ConfigureSourceEnvironmentMaterials(
                this, environmentMapTextures, environmentReflectionStrength,
                environmentMaximumReflectionWeight,
                environmentMaterialBindings, enhancedPresentation);
            var unboundEnvironmentMaps = environmentMapTextures.Keys.Where(resref =>
                !environmentMaterialBindings.TryGetValue(resref, out var count) || count <= 0)
                .ToArray();
            if (unboundEnvironmentMaps.Length > 0)
                throw new InvalidDataException(
                    "Source environment-map material coverage is incomplete: " +
                    string.Join(',', unboundEnvironmentMaps));
            var boundEnvironmentMapCount = environmentMapTextures.Count -
                                           unboundEnvironmentMaps.Length;
            KotorModulePresentationPolicy.RequireVisualInventory(
                new KotorModuleVisualInventory(
                    manifest.Counts.Rooms,
                    loadedRooms,
                    manifest.Counts.MaterialSurfaces,
                    configuredMaterialSurfaces,
                    manifest.Counts.AuthoredEmitters,
                    materializedEmitterCount,
                    manifest.Counts.EnvironmentMaps,
                    boundEnvironmentMapCount,
                    manifest.Counts.UnresolvedTextureReferences,
                    manifest.UnresolvedTextureReferences.Count,
                    0));
            GD.Print($"NIKAMI_AURORA_SOURCE_ABSENCE status=reported " +
                     $"policy={manifest.MissingSourceAssetPolicy} " +
                     $"missing_assets={manifest.UnresolvedTextureReferences.Count} " +
                     "fabricated=0");
            GD.Print($"NIKAMI_AURORA_ENVIRONMENT_MAPS status=ready " +
                     $"maps={environmentMapTextures.Count} faces={environmentMapTextures.Count * 6} " +
                     $"tier={(enhancedPresentation ? "enhanced" : "source")} " +
                     $"boost={environmentReflectionStrength:F2} " +
                     $"maxWeight={environmentMaximumReflectionWeight:F2} " +
                     $"basis={KotorEnvironmentMaterialPolicy.SampleBasis} " +
                     $"bindings={string.Join(',', environmentMaterialBindings.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"))}");
            BuildNavigation(manifest.Rooms);
            var groundedCreatures = 0;
            var sourceHoverCreatures = 0;
            var correctedCreatureFloors = 0;
            foreach (var creature in materializedCreatures)
            {
                var boundsMinimum = creature.Source.Animation?.BoundsMinimum;
                if (boundsMinimum is not { Count: >= 3 } ||
                    !float.IsFinite(boundsMinimum[1]))
                    throw new InvalidDataException(
                        $"Creature source bounds are unavailable: " +
                        $"{creature.Source.Template}");
                if (!TryProjectToWalkmesh(creature.Model.GlobalPosition,
                        out var sourceFloor))
                    throw new InvalidDataException(
                        $"Creature source placement has no walkmesh floor: " +
                        $"{creature.Source.Template}:{creature.Model.GlobalPosition}");
                var sourceFoot = creature.Model.GlobalPosition.Y + boundsMinimum[1];
                var clearance = sourceFoot - sourceFloor;
                if (!float.IsFinite(clearance))
                    throw new InvalidDataException(
                        $"Creature source silhouette has non-finite floor clearance: " +
                        $"{creature.Source.Template}");
                var correctedFloor = clearance < -0.06f;
                if (correctedFloor)
                {
                    var correction = -clearance;
                    creature.Model.GlobalPosition += Vector3.Up * correction;
                    sourceFoot += correction;
                    clearance = sourceFoot - sourceFloor;
                    correctedCreatureFloors++;
                }
                var grounded = clearance <= 0.22f;
                groundedCreatures += grounded ? 1 : 0;
                sourceHoverCreatures += grounded ? 0 : 1;
                GD.Print($"NIKAMI_AURORA_CREATURE_GROUND status=ready " +
                         $"module={loadedModuleId} identity={creature.Source.Template} " +
                         $"source_floor={sourceFloor:F3} source_foot={sourceFoot:F3} " +
                          $"clearance={clearance:F3} " +
                          $"alignment={(correctedFloor ? "bounds-grounded" : "source-origin")} " +
                          $"classification={(grounded ? "grounded" : "source-hover")} " +
                         "silhouette=visual-review-required");
            }
            GD.Print($"NIKAMI_AURORA_CREATURE_GROUND_COVERAGE status=ready " +
                     $"module={loadedModuleId} expected={manifest.Counts.Creatures} " +
                      $"projected={materializedCreatures.Count} grounded={groundedCreatures} " +
                      $"source_hover={sourceHoverCreatures} " +
                      $"bounds_grounded={correctedCreatureFloors} below_floor=0");
            simulationPlayerPosition = ToNumerics(manifest.Entry.Position);
            var entry = ToGodot(manifest.Entry.Position);
            if (!TryProjectToWalkmesh(entry, out var entryGround))
                throw new InvalidDataException($"Authored entry point is not on the imported walkmesh: {entry}");
            entry.Y = entryGround;
            simulationPlayerPosition.Z = entryGround;
            var trask = manifest.Creatures.FirstOrDefault(creature =>
                creature.Template.Equals("end_trask", StringComparison.OrdinalIgnoreCase));
            playerBody.GlobalPosition = entry;
            if (trask is not null)
            {
                var target = ToGodot(trask.Position) + Vector3.Up * 1.2f;
                var direction = (target - playerBody.GlobalPosition).Normalized();
                yaw = Mathf.Atan2(-direction.X, -direction.Z);
                playerBody.Rotation = new Vector3(0, yaw, 0);
                cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            }

            UpdateLoadingProgress(
                runtimeConfiguration.Presentation.Loading.CompleteProgress);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            loadingBackdrop.Visible = false;
            StopRetailLoadingMusic();
            LoadModuleSoundObjects(manifest.SoundObjects ?? [], manifestDirectory);
            if (manifest.AreaMusic is { } sourceMusic)
            {
                if (sourceMusic.Schema != "nikami-aurora-kotor-area-music-v1" ||
                    sourceMusic.AmbientMusicSha256.Length != 64 ||
                    !sourceMusic.AmbientMusicSha256.All(Uri.IsHexDigit) ||
                    sourceMusic.StandardMusicId < 0 ||
                    sourceMusic.MusicDelayMilliseconds < 0)
                    throw new InvalidDataException("KOTOR area-music contract drifted");
                areaMusic.Stream = LoadOwnedAudio(
                    sourceMusic.BackgroundMusic, manifestDirectory);
                areaMusic.Play();
                currentMusicResref = sourceMusic.BackgroundMusic.Resref;
                GD.Print($"NIKAMI_AURORA_AREA_MUSIC status=playing " +
                         $"resref={currentMusicResref} row={sourceMusic.StandardMusicId} " +
                         $"battleDelayMs={sourceMusic.MusicDelayMilliseconds}");
            }
            status.Text = $"{loadedModuleId.ToUpperInvariant()}  •  {manifest.AreaResRef.ToUpperInvariant()}";
            details.Text = $"{manifest.Rooms.Count} authored / {loadedRooms} visual rooms  •  " +
                           $"{materializedActors} actor / {manifest.Counts.Creatures} creature placements  •  " +
                           $"{materializedPlayer} player avatar  •  " +
                           $"{manifest.Counts.WalkmeshTriangles} nav triangles  •  " +
                           $"{authoredLights.DynamicMaterialized}/" +
                           $"{manifest.Counts.AuthoredLights} dynamic source lights  •  " +
                           $"{materializedDoors} door / {manifest.Counts.Doors} placements  •  " +
                           $"{materializedPlaceables} placeable / {manifest.Counts.Placeables} placements  •  " +
                           $"source {manifest.Target.ExecutableSha256[..12]}";
            GD.Print($"NIKAMI_AURORA_KOTOR_BOOT status=pass module={loadedModuleId} " +
                     $"mode={manifest.ContentMode} rooms={loadedRooms} " +
                     $"authoredRooms={manifest.Rooms.Count} creatures={manifest.Counts.Creatures} " +
                     $"sha256={manifest.Target.ExecutableSha256}");
            GD.Print($"NIKAMI_AURORA_LIGHTING status=ready module={loadedModuleId} " +
                     $"authored={manifest.Counts.AuthoredLights} " +
                     $"dynamic_materialized={authoredLights.DynamicMaterialized} " +
                     $"baked_only={authoredLights.BakedOnly} " +
                     $"ambient_only={authoredLights.AmbientOnly} " +
                     $"disabled={authoredLights.Disabled} " +
                     $"static_transfer=source-lightmaps " +
                     $"ambient={ToColor(manifest.Lighting.DynamicAmbient)}");
            GD.Print($"NIKAMI_AURORA_NAV status=ready module={loadedModuleId} " +
                     $"triangles={navigationTriangles.Count} " +
                     $"entry={playerBody.GlobalPosition}");
            if (manifest.OpeningDialogue is not null)
                LoadOpeningDialogue(manifest.OpeningDialogue, manifestDirectory);
            else
            {
                var openingActor = manifest.Creatures.FirstOrDefault(creature => creature.Dialogue is not null);
                if (openingActor is not null)
                    LoadOpeningDialogue(
                        openingActor,
                        manifestDirectory,
                        launchEnvironment.Get(
                            "NIKAMI_AURORA_SKIP_OPENING_DIALOGUE") != "1");
            }
            if (manifest.OpeningDialogue is not null && gameplaySimulation is not null &&
                launchEnvironment.Get("NIKAMI_AURORA_SKIP_OPENING_DIALOGUE") != "1")
                ApplyGameplayTransition(gameplaySimulation.UpdateTriggers(
                    simulationPlayerPosition, simulationPlayerPosition));
            firstEncounter = manifest.FirstEncounter;
            if (firstEncounter is not null)
            {
                if (firstEncounter.Schema != "nikami-aurora-kotor-first-encounter-v1" ||
                    firstEncounter.Participants.Count != 3 ||
                    firstEncounter.EnvironmentPlaceables.Count != 6 ||
                    firstEncounter.PartyWaypoints.Count != 2 ||
                    firstEncounter.CameraIds.Any(id => !dialogueCameras.ContainsKey(id)) ||
                    firstEncounter.Participants.Any(participant =>
                        !actorModels.ContainsKey(participant.Tag) ||
                        !actorAnimations.ContainsKey(participant.Tag)) ||
                    !IsFirstEncounterEnvironmentReady(firstEncounter))
                    throw new InvalidDataException("First encounter manifest is incomplete");
                firstEncounterGraph = ReadDialogueGraph(
                    firstEncounter.SceneObject.Dialogue, manifestDirectory);
                firstEncounterAudio = LoadFirstEncounterAudio(
                    firstEncounter.Audio, manifestDirectory);
                firstEncounterEffectTextures = LoadFirstEncounterEffects(
                    firstEncounter.Effects, manifestDirectory);
                ValidateFirstEncounterEffectPresentation(
                    firstEncounterEffectTextures,
                    runtimeConfiguration.Presentation.FirstEncounter);
                areaMusic.Stream = firstEncounterAudio.BackgroundMusic;
                areaMusic.Play();
                currentMusicResref = firstEncounter.Audio.BackgroundMusic.Resref;
                GD.Print($"NIKAMI_AURORA_FIRST_ENCOUNTER status=ready " +
                         $"door={firstEncounter.DoorTag} " +
                         $"participants={string.Join(',', firstEncounter.Participants.Select(item => item.Tag))} " +
                         $"environment={firstEncounter.EnvironmentPlaceables.Count} " +
                         $"cameras={string.Join(',', firstEncounter.CameraIds)} " +
                         $"scripts={firstEncounter.Scripts.Count} " +
                         $"voice=2 sfx={firstEncounter.Audio.BlasterShot.Resref}," +
                         $"{firstEncounter.Audio.BlasterImpact.Resref} " +
                         $"music={firstEncounter.Audio.BackgroundMusic.Resref}," +
                         $"{firstEncounter.Audio.BattleMusic.Resref}");
            }
            capturedFrames = 0;
            readyFrames = 0;
            moduleReady = true;
        }
        catch (Exception exception)
        {
            status.Text = "KOTOR MODULE LOAD FAILED";
            details.Text = exception.Message;
            GD.PushError($"NIKAMI_AURORA_KOTOR_BOOT status=fail error={exception}");
            if (launchEnvironment.Get("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
                RequestCleanExit(1);
        }
    }

    private void CreateEnvironment()
    {
        var renderingMethod = RenderingServer.GetCurrentRenderingMethod();
        var requestedTier = launchEnvironment.Get(
            "NIKAMI_AURORA_PRESENTATION_TIER")?.Trim().ToLowerInvariant() ?? string.Empty;
        var backend = RenderingQualityPolicy.ParseBackend(renderingMethod);
        var tier = RenderingQualityPolicy.ParseTier(requestedTier, backend);
        renderingQualityDecision = RenderingQualityPolicy.Resolve(
            new RenderingQualityRequest(
                tier,
                backend,
                RenderingSelectionScope.Application,
                SelectionKey: null,
                AvailableCapabilities: RenderingQualityPolicy.AllCapabilities,
                SourceAuthorizedFeatures:
                    KotorEnvironmentMaterialPolicy.EnhancedAuthorizedRenderFeatures));
        enhancedPresentation = tier == RenderingPresentationTier.Enhanced;
        lightmapTransfer = KotorEnvironmentMaterialPolicy.LightmapTransfer(
            enhancedPresentation);

        runtimeEnvironment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.004f, 0.008f, 0.018f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.2f, 0.2f, 0.2f),
            AmbientLightEnergy = 1.0f,
            TonemapMode = enhancedPresentation
                ? Godot.Environment.ToneMapper.Agx
                : Godot.Environment.ToneMapper.Linear,
            TonemapExposure = 1.0f,
            TonemapAgxContrast = 1.08f,
            GlowEnabled = enhancedPresentation,
            GlowNormalized = true,
            GlowIntensity = 0.72f,
            GlowStrength = 0.55f,
            GlowBloom = 0.0f,
            GlowHdrThreshold = 1.55f,
            SsaoEnabled = renderingQualityDecision.Enables(
                EnhancedRenderingCapability.AmbientOcclusion),
            SsaoRadius = 1.35f,
            SsaoIntensity = 1.4f,
            SsaoPower = 1.25f,
            SsaoDetail = 0.65f,
            SsilEnabled = renderingQualityDecision.Enables(
                EnhancedRenderingCapability.ScreenSpaceIndirectLighting),
            SsilRadius = 2.5f,
            SsilIntensity = 0.45f,
            SsilSharpness = 0.9f,
            SsrEnabled = renderingQualityDecision.Reflections.Enabled,
            SdfgiEnabled = renderingQualityDecision.Sdfgi.Enabled,
            VolumetricFogEnabled = renderingQualityDecision.Volumetrics.Enabled,
            VolumetricFogDensity = 0.004f,
            VolumetricFogAlbedo = new Color(0.58f, 0.67f, 0.78f),
            VolumetricFogLength = 48.0f,
            VolumetricFogDetailSpread = 2.0f,
            VolumetricFogAmbientInject = 0.18f,
            VolumetricFogAnisotropy = 0.32f,
            VolumetricFogSkyAffect = 0.0f,
            VolumetricFogTemporalReprojectionEnabled = true,
            VolumetricFogTemporalReprojectionAmount = 0.9f
        };
        var worldEnvironment = new WorldEnvironment
        {
            Environment = runtimeEnvironment
        };
        AddChild(worldEnvironment);
        GD.Print(renderingQualityDecision.ToTelemetryMarker());
        GD.Print($"NIKAMI_AURORA_RENDER_PIPELINE status=ready " +
                 $"method={renderingMethod} tier={(enhancedPresentation ? "enhanced" : "source")} " +
                 $"tonemap={(enhancedPresentation ? "agx" : "linear")} " +
                 $"ssao={(runtimeEnvironment.SsaoEnabled ? 1 : 0)} " +
                 $"ssil={(runtimeEnvironment.SsilEnabled ? 1 : 0)} " +
                 $"ssr={(runtimeEnvironment.SsrEnabled ? 1 : 0)} " +
                 $"sdfgi={(runtimeEnvironment.SdfgiEnabled ? 1 : 0)} " +
                 $"volumetric_fog={(runtimeEnvironment.VolumetricFogEnabled ? 1 : 0)} " +
                 $"glow={(enhancedPresentation ? 1 : 0)}");
    }

    private void ApplyAreaLighting(AreaLightingRecord lighting)
    {
        runtimeEnvironment.AmbientLightColor = ToColor(lighting.DynamicAmbient);
        runtimeEnvironment.AmbientLightEnergy = 1.0f;
    }

    private static int BuildCameraVisibilityCollision(Node3D room)
    {
        var shapes = 0;
        foreach (var mesh in FindDescendants<MeshInstance3D>(room))
        {
            if (mesh.Mesh is null) continue;
            var opacity = Enumerable.Range(0, mesh.Mesh.GetSurfaceCount())
                .Select(surface => CameraSurfaceOpacity(mesh.GetActiveMaterial(surface)))
                .ToArray();
            var blockingSurfaces =
                KotorCameraCollisionPolicy.RequireBlockingSurfaceIndices(opacity);
            if (blockingSurfaces.Count == 0) continue;

            var blockingMesh = new ArrayMesh();
            foreach (var surface in blockingSurfaces)
            {
                var surfaceTool = new SurfaceTool();
                surfaceTool.CreateFrom(mesh.Mesh, surface);
                if (surfaceTool.GetPrimitiveType() != Mesh.PrimitiveType.Triangles)
                    throw new InvalidDataException(
                        $"Opaque room camera surface is not triangulated: {mesh.Name}:{surface}");
                _ = surfaceTool.Commit(blockingMesh);
            }
            if (blockingMesh.GetSurfaceCount() != blockingSurfaces.Count ||
                blockingMesh.CreateTrimeshShape() is not ConcavePolygonShape3D shape)
                throw new InvalidDataException(
                    $"Opaque room surfaces could not produce camera collision: {mesh.Name}");
            var body = new StaticBody3D
            {
                Name = $"CameraVisibility_{mesh.Name}_{shapes:D4}",
                CollisionLayer = CameraVisibilityCollisionLayer,
                CollisionMask = 0,
                InputRayPickable = false
            };
            body.SetMeta("source_opaque_surfaces", blockingSurfaces.Count);
            body.SetMeta("source_transparent_surfaces",
                opacity.Count(value => value == KotorCameraSurfaceOpacity.SourceTransparent));
            mesh.AddChild(body);
            body.AddChild(new CollisionShape3D { Shape = shape });
            shapes++;
        }
        return shapes;
    }

    private static KotorCameraSurfaceOpacity CameraSurfaceOpacity(Material? material) =>
        material switch
        {
            BaseMaterial3D source when
                source.Transparency == BaseMaterial3D.TransparencyEnum.Disabled =>
                KotorCameraSurfaceOpacity.SourceOpaque,
            BaseMaterial3D => KotorCameraSurfaceOpacity.SourceTransparent,
            ShaderMaterial source when source.Shader == OdysseyLightmapShader ||
                                       source.Shader == OdysseySourceLightmapShader ||
                                       source.Shader == OdysseyEnvironmentLightmapShader ||
                                       source.Shader == OdysseySourceEnvironmentLightmapShader ||
                                       source.Shader == OdysseyEnvironmentShader ||
                                       source.Shader == OdysseySourceEnvironmentShader =>
                KotorCameraSurfaceOpacity.SourceOpaque,
            ShaderMaterial source when source.Shader == OdysseyTransparentLightmapShader ||
                                       source.Shader == OdysseySourceTransparentLightmapShader ||
                                       source.Shader == OdysseyTransparentEnvironmentLightmapShader ||
                                       source.Shader == OdysseySourceTransparentEnvironmentLightmapShader ||
                                       source.Shader == OdysseyTransparentEnvironmentShader ||
                                       source.Shader == OdysseySourceTransparentEnvironmentShader ||
                                       source.Shader == OdysseyAdditiveLightmapShader ||
                                       source.Shader == OdysseyAdditiveEnvironmentShader ||
                                       source.Shader == OdysseyAdditiveEnvironmentLightmapShader =>
                KotorCameraSurfaceOpacity.SourceTransparent,
            _ => KotorCameraSurfaceOpacity.Unsupported
        };

    private void CreateCamera()
    {
        playerBody = new CharacterBody3D { Name = "Player" };
        AddChild(playerBody);
        var playerCollision = new CollisionShape3D
        {
            Position = Vector3.Up * 0.85f,
            Shape = new CapsuleShape3D { Radius = 0.32f, Height = 1.7f }
        };
        playerBody.AddChild(playerCollision);
        cameraPivot = new Node3D
        {
            Name = "CameraPivot",
            Position = Vector3.Up * 1.25f
        };
        playerBody.AddChild(cameraPivot);
        cameraArm = new SpringArm3D
        {
            Name = "CameraArm",
            SpringLength = 3.2f,
            Margin = 0.08f,
            CollisionMask = CameraVisibilityCollisionLayer
        };
        cameraPivot.AddChild(cameraArm);
        camera = new Camera3D
        {
            Current = true,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = DefaultGameplayFieldOfView,
            CullMask = RuntimeCameraCullMask
        };
        cameraArm.AddChild(camera);
        cinematicCamera = new Camera3D
        {
            Name = "CinematicCamera",
            Current = false,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = DefaultGameplayFieldOfView,
            CullMask = RuntimeCameraCullMask
        };
        AddChild(cinematicCamera);
        xrOrigin = new XROrigin3D { Name = "XROrigin" };
        playerBody.AddChild(xrOrigin);
        xrCamera = new XRCamera3D
        {
            Name = "XRCamera",
            Current = false,
            CullMask = RuntimeCameraCullMask
        };
        xrOrigin.AddChild(xrCamera);
        xrLeftHand = new XRController3D
        {
            Name = "XRLeftHand",
            Tracker = "left_hand",
            Pose = "grip"
        };
        xrLeftHand.ButtonPressed += action => OnXrButtonPressed(xrLeftHand, action.ToString());
        xrRightHand = new XRController3D
        {
            Name = "XRRightHand",
            Tracker = "right_hand",
            Pose = "grip"
        };
        xrRightHand.ButtonPressed += action => OnXrButtonPressed(xrRightHand, action.ToString());
        xrRigTargetRoot = new Node3D { Name = "XRTrackedRigTargets" };
        playerBody.AddChild(xrRigTargetRoot);
        xrLeftGripTarget = new Node3D { Name = "LeftAuthoredGripTarget" };
        xrRightGripTarget = new Node3D { Name = "RightAuthoredGripTarget" };
        xrRigTargetRoot.AddChild(xrLeftGripTarget);
        xrRigTargetRoot.AddChild(xrRightGripTarget);
    }

    private void AttachXrControllers()
    {
        if (xrLeftHand.GetParent() is not null) return;
        xrOrigin.AddChild(xrLeftHand);
        xrOrigin.AddChild(xrRightHand);
        GD.Print("NIKAMI_AURORA_XR_RIG status=controllers-attached " +
                 "visualProvider=owned-kotor-skinned-rig pose=grip " +
                 "proceduralFallback=removed");
    }

    private void CreateAudio()
    {
        dialogueVoice = new AudioStreamPlayer { Name = "DialogueVoice" };
        dialogueVoice.Finished += OnDialogueVoiceFinished;
        AddChild(dialogueVoice);
        areaMusic = new AudioStreamPlayer
        {
            Name = "AreaMusic",
            VolumeDb = -12.0f
        };
        AddChild(areaMusic);
    }

    private void CreateOverlay()
    {
        overlayLayer = new CanvasLayer();
        AddChild(overlayLayer);
        var xrHudParent = xrActive ? CreateXrHudSurface() : null;
        loadingBackdrop = new ColorRect
        {
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        loadingBackdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlayLayer.AddChild(loadingBackdrop);
        status = new Label
        {
            Text = "Loading",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        details = new Label
        {
            Text = "",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        loadingBackdrop.AddChild(status);
        loadingBackdrop.AddChild(details);

        var cinematicFadeLayer = new CanvasLayer { Name = "CinematicFadeLayer", Layer = 50 };
        AddChild(cinematicFadeLayer);
        cinematicFade = new ColorRect
        {
            Color = new Color(0, 0, 0, 0),
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Visible = false
        };
        cinematicFade.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        cinematicFadeLayer.AddChild(cinematicFade);

        interactionPrompt = new Label
        {
            Position = new Vector2(460, 610),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        interactionPrompt.AddThemeFontSizeOverride("font_size", 20);
        interactionPrompt.AddThemeColorOverride("font_color", new Color(0.45f, 0.88f, 1.0f));
        if (xrHudParent is not null)
        {
            interactionPrompt.Position = new Vector2(390, 525);
            xrHudParent.AddChild(interactionPrompt);
        }
        else
        {
            overlayLayer.AddChild(interactionPrompt);
        }

        dialoguePanel = new PanelContainer
        {
            AnchorLeft = xrActive ? 0.04f : 0.12f,
            AnchorTop = xrActive ? 0.04f : 0.66f,
            AnchorRight = xrActive ? 0.96f : 0.88f,
            AnchorBottom = xrActive ? 0.86f : 0.96f,
            OffsetLeft = 0,
            OffsetTop = 0,
            OffsetRight = 0,
            OffsetBottom = 0,
            Visible = false
        };
        var panelStyle = new StyleBoxFlat
        {
            BgColor = new Color(0.015f, 0.025f, 0.045f, 0.94f),
            BorderColor = new Color(0.2f, 0.62f, 0.9f, 0.9f),
            BorderWidthLeft = 2,
            BorderWidthTop = 2,
            BorderWidthRight = 2,
            BorderWidthBottom = 2,
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            ContentMarginLeft = 22,
            ContentMarginTop = 16,
            ContentMarginRight = 22,
            ContentMarginBottom = 16
        };
        dialoguePanel.AddThemeStyleboxOverride("panel", panelStyle);
        if (xrHudParent is not null)
        {
            xrHudParent.AddChild(dialoguePanel);
        }
        else
        {
            var dialogueLayer = new CanvasLayer { Name = "DialogueLayer" };
            AddChild(dialogueLayer);
            dialogueLayer.AddChild(dialoguePanel);
        }
        var dialogueLayout = new VBoxContainer();
        dialoguePanel.AddChild(dialogueLayout);
        dialogueSpeaker = new Label();
        dialogueSpeaker.AddThemeFontSizeOverride("font_size", xrActive ? 28 : 18);
        dialogueSpeaker.AddThemeColorOverride("font_color", new Color(0.4f, 0.82f, 1.0f));
        dialogueLayout.AddChild(dialogueSpeaker);
        dialogueText = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 64)
        };
        dialogueText.AddThemeFontSizeOverride("font_size", xrActive ? 34 : 22);
        dialogueLayout.AddChild(dialogueText);
        dialogueChoices = new VBoxContainer();
        dialogueLayout.AddChild(dialogueChoices);
        if (xrActive)
        {
            var xrHint = new Label
            {
                Text = "A select  •  B/Y next  •  X previous",
                MouseFilter = Control.MouseFilterEnum.Ignore
            };
            xrHint.AddThemeFontSizeOverride("font_size", 22);
            xrHint.AddThemeColorOverride(
                "font_color", new Color(0.62f, 0.78f, 0.9f));
            dialogueLayout.AddChild(xrHint);
        }
    }

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static KotorGameplaySimulation CreateGameplaySimulation(
        ModuleManifest manifest,
        int initialPlayerExperience)
    {
        ValidatePlayerEquipmentVariants(manifest);
        var contracts = manifest.ScriptContracts.Select(contract =>
        {
            if (contract.Schema != "nikami-aurora-kotor-script-contract-v1")
                throw new InvalidDataException($"Unsupported script contract: {contract.Resref}");
            var kind = contract.Kind switch
            {
                "plot-xp-if-player-xp" =>
                    KotorScriptContractKind.PlotExperienceIfPlayerExperience,
                "dialogue-open-door" => KotorScriptContractKind.DialogueOpenDoor,
                "trigger-dialogue" => KotorScriptContractKind.TriggerDialogue,
                "move-player-to-waypoint" => KotorScriptContractKind.MovePlayerToWaypoint,
                "module-start-presentation" =>
                    KotorScriptContractKind.ModuleStartPresentation,
                "play-sound-object-from-parameters" =>
                    KotorScriptContractKind.PlaySoundObjectFromParameters,
                "no-op" => KotorScriptContractKind.NoOp,
                "room-animation-from-parameters" =>
                    KotorScriptContractKind.RoomAnimationFromParameters,
                "global-number-add" => KotorScriptContractKind.GlobalNumberAdd,
                "global-number-set" => KotorScriptContractKind.GlobalNumberSet,
                "reveal-map" => KotorScriptContractKind.RevealMap,
                _ => throw new InvalidDataException(
                    $"Unsupported script contract kind {contract.Kind} for {contract.Resref}")
            };
            return new KotorScriptContract(
                contract.Resref,
                kind,
                contract.SourceSha256,
                contract.InstructionCount,
                contract.DoorTag,
                contract.RequiredPlayerXp,
                contract.PlotLabel,
                contract.PlotPercentage,
                contract.PlotBaseXp,
                contract.AwardedXp,
                contract.PauseConversation,
                contract.MoveTargetTag,
                contract.MoveRun,
                contract.MoveRange,
                contract.ResumeConversation,
                kind == KotorScriptContractKind.TriggerDialogue
                    ? new KotorTriggerDialogueBehavior(
                        contract.TriggerTemplate
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no template"),
                        contract.GlobalName,
                        contract.GlobalValue,
                        contract.ActorTag
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no actor"),
                        contract.UserEvent
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no user event"),
                        contract.InputLockSeconds
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no input lock"),
                        contract.DelaySeconds
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no delay"),
                        contract.Conversation
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no conversation"),
                        contract.DialogueStarter
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no starter"),
                        contract.ActorScriptSourceSha256,
                        contract.ActorScriptInstructionCount,
                        contract.ConditionScriptSourceSha256,
                        contract.ConditionScriptInstructionCount)
                    : null,
                contract.GlobalName,
                contract.GlobalValue,
                contract.FadeInWaitSeconds,
                contract.FadeInLengthSeconds,
                contract.MusicRestartDelaySeconds);
        }).ToArray();
        var contractResrefs = contracts.Select(contract => contract.Resref)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var doors = manifest.Doors.Select((door, index) =>
            new KotorDoorDefinition(DoorInstanceId(index), door.Tag, door.OnOpen));
        var placeables = manifest.Placeables.Select((placeable, index) =>
            new KotorPlaceableDefinition(
                PlaceableInstanceId(index),
                placeable.Tag,
                placeable.OnInventory,
                (placeable.Inventory ?? []).Select(item => new KotorItemStack(
                    new KotorItemDefinition(
                        item.Resref,
                        item.DisplayName,
                        item.Tag,
                        item.UtiSha256,
                        placeable.BaseItemsSha256 ?? throw new InvalidDataException(
                            $"Placeable {placeable.Template} inventory has no baseitems hash"),
                        item.BaseItem,
                        item.Charges,
                        item.StackSize,
                        item.ModelVariation,
                        item.BodyVariation,
                        item.TextureVariation,
                        item.EquipableSlots,
                        item.ItemClass,
                        item.ModelType,
                        item.DefaultModel,
                        item.DefaultIcon),
                    item.Quantity,
                    item.Droppable,
                    item.Infinite)).ToArray()));
        var triggers = manifest.Triggers.Select((trigger, index) =>
                new KotorTriggerDefinition(
                    TriggerInstanceId(index),
                    trigger.Template,
                    trigger.Geometry.Select(point =>
                        ToNumericsWithOffset(point, trigger.Position)).ToArray(),
                    trigger.OnEnter))
            .Where(trigger => trigger.OnEnterScript is not null &&
                              contractResrefs.Contains(trigger.OnEnterScript))
            .ToArray();
        var partySources = manifest.Ui.Inventory.PartyMembers;
        var playerPartySource = partySources.Single(member => member.IsPlayer);
        var configuredPlayer = manifest.RuntimeConfiguration.Gameplay.PlayerPartyMember;
        if (!playerPartySource.Id.Equals(configuredPlayer.Id, StringComparison.OrdinalIgnoreCase) ||
            !playerPartySource.DisplayName.Equals(
                configuredPlayer.DisplayName, StringComparison.Ordinal) ||
            !playerPartySource.SourceKind.Equals("profile", StringComparison.OrdinalIgnoreCase) ||
            playerPartySource.CurrentVitality != configuredPlayer.CurrentVitality ||
            playerPartySource.MaximumVitality != configuredPlayer.MaximumVitality ||
            playerPartySource.Defense != configuredPlayer.Defense)
            throw new InvalidDataException(
                "Opening inventory player party baseline is incomplete");
        var companionSources = partySources.Where(member => !member.IsPlayer)
            .ToArray();
        if (companionSources.Any(member =>
                member.SourceKind != "utc" ||
                member.UtcSha256?.Length != 64 ||
                string.IsNullOrWhiteSpace(member.ArmorResref) ||
                member.ArmorUtiSha256?.Length != 64 ||
                member.BaseItemsSha256?.Length != 64))
            throw new InvalidDataException(
                "Opening inventory companion evidence is incomplete");
        var partyMembers = partySources.Select(member =>
            new KotorPartyMemberDefinition(
                member.Id,
                member.DisplayName,
                member.CurrentVitality,
                member.MaximumVitality,
                member.Defense,
                member.IsPlayer)).ToArray();
        return new KotorGameplaySimulation(
            contracts,
            doors,
            placeables,
            new KotorGameplayInitialState(
                initialPlayerExperience,
                manifest.RuntimeConfiguration.Gameplay.PlayerCredits,
                partyMembers),
            new KotorExperienceTable(
                manifest.ExperienceTable.SourceSha256,
                manifest.ExperienceTable.Thresholds),
            triggers);
    }

    private static void ValidatePlayerEquipmentVariants(ModuleManifest manifest)
    {
        var itemSources = manifest.Placeables.SelectMany(placeable =>
            (placeable.Inventory ?? []).Select(item =>
                (Item: item, BaseItemsSha256: placeable.BaseItemsSha256)));
        foreach (var variant in manifest.Player.EquipmentVariants ?? [])
        {
            var hasArmor = !string.IsNullOrWhiteSpace(variant.ArmorResref);
            var hasLeftHand = !string.IsNullOrWhiteSpace(variant.LeftHandResref);
            var hasRightHand = !string.IsNullOrWhiteSpace(variant.RightHandResref);
            var expectedWeaponHook = hasLeftHand
                ? "lhand"
                : hasRightHand
                    ? "rhand"
                    : null;
            if (variant.Schema != "nikami-aurora-kotor-player-equipment-v1" ||
                string.IsNullOrWhiteSpace(variant.Glb) ||
                (!hasArmor && !hasLeftHand && !hasRightHand) ||
                (hasLeftHand && hasRightHand) ||
                (hasLeftHand || hasRightHand) !=
                !string.IsNullOrWhiteSpace(variant.WeaponModel) ||
                !string.Equals(
                    variant.WeaponHook,
                    expectedWeaponHook,
                    StringComparison.OrdinalIgnoreCase) ||
                variant.Animation.SkinCount <= 0 ||
                variant.Animation.HeadSkinCount <= 0 ||
                !variant.Animation.Animations.Contains("pause1", StringComparer.OrdinalIgnoreCase) ||
                !variant.Animation.Animations.Contains("walk", StringComparer.OrdinalIgnoreCase) ||
                !variant.Animation.Animations.Contains("run", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Player equipment variant is incomplete: {variant.Id}");
            var armor = hasArmor ? itemSources.SingleOrDefault(source =>
                source.Item.Resref.Equals(
                    variant.ArmorResref, StringComparison.OrdinalIgnoreCase)) : default;
            var leftHand = hasLeftHand ? itemSources.SingleOrDefault(source =>
                source.Item.Resref.Equals(
                    variant.LeftHandResref, StringComparison.OrdinalIgnoreCase)) : default;
            var rightHand = hasRightHand ? itemSources.SingleOrDefault(source =>
                source.Item.Resref.Equals(
                    variant.RightHandResref, StringComparison.OrdinalIgnoreCase)) : default;
            var armorValid = !hasArmor ||
                armor.Item is not null &&
                armor.Item.UtiSha256.Equals(
                    variant.ArmorUtiSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    armor.BaseItemsSha256, variant.BaseItemsSha256,
                    StringComparison.OrdinalIgnoreCase);
            var leftHandValid = !hasLeftHand ||
                leftHand.Item is not null &&
                leftHand.Item.UtiSha256.Equals(
                    variant.LeftHandUtiSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    leftHand.BaseItemsSha256, variant.BaseItemsSha256,
                    StringComparison.OrdinalIgnoreCase);
            var rightHandValid = !hasRightHand ||
                rightHand.Item is not null &&
                rightHand.Item.UtiSha256.Equals(
                    variant.RightHandUtiSha256, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    rightHand.BaseItemsSha256, variant.BaseItemsSha256,
                    StringComparison.OrdinalIgnoreCase);
            if (!armorValid || !leftHandValid || !rightHandValid)
                throw new InvalidDataException(
                    $"Player equipment variant sources drifted: {variant.Id}");
        }
    }

    private static void ValidateBasePlayerPresentation(PlayerRecord player)
    {
        var animation = player.Animation;
        var rigidPlayer = player.RigKind.Equals(
            "rigid", StringComparison.OrdinalIgnoreCase);
        var skinnedHumanoidPlayer = player.RigKind.Equals(
            "humanoid", StringComparison.OrdinalIgnoreCase);
        var rigidHumanoidPlayer = player.RigKind.Equals(
            "rigid-humanoid", StringComparison.OrdinalIgnoreCase);
        var humanoidPlayer = skinnedHumanoidPlayer || rigidHumanoidPlayer;
        if ((!humanoidPlayer && !rigidPlayer) ||
            player.AppearanceId < 0 || player.PortraitId < 0 ||
            (!rigidPlayer && player.HeadIndex < 0) ||
            string.IsNullOrWhiteSpace(player.AppearanceLabel) ||
            string.IsNullOrWhiteSpace(player.BodyModel) ||
            (!rigidPlayer && string.IsNullOrWhiteSpace(player.BodyTexture)) ||
            (!rigidPlayer && string.IsNullOrWhiteSpace(player.HeadModel)) ||
            !float.IsFinite(player.Height) || player.Height <= 0 ||
            !float.IsFinite(player.WalkDistance) || player.WalkDistance <= 0 ||
            !float.IsFinite(player.RunDistance) || player.RunDistance <= 0 ||
            (!rigidPlayer && !ValidSourcePoint(player.TalkOffset)) ||
            (!rigidPlayer && !ValidSourcePoint(player.CameraOffset)) ||
            animation.MeshCount <= 0 || animation.VertexCount <= 0 ||
            animation.TriangleCount <= 0 ||
            (skinnedHumanoidPlayer && animation.SkinCount <= 0) ||
            (skinnedHumanoidPlayer && animation.HeadSkinCount <= 0) ||
            !animation.Animations.Contains("pause1", StringComparer.OrdinalIgnoreCase) ||
            !animation.Animations.Contains("walk", StringComparer.OrdinalIgnoreCase) ||
            !animation.Animations.Contains("run", StringComparer.OrdinalIgnoreCase) ||
            (!rigidPlayer &&
             !animation.Animations.Contains("talk", StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                "Base player appearance/body/head/animation contract is incomplete");
    }

    private static bool ValidSourcePoint(IReadOnlyList<float>? point) =>
        point is { Count: >= 3 } && point.Take(3).All(float.IsFinite);

    private static string DoorInstanceId(int index) => $"door:{index:D4}";

    private static string PlaceableInstanceId(int index) => $"placeable:{index:D4}";

    private static string TriggerInstanceId(int index) => $"trigger:{index:D4}";

    private sealed class XrTrackedArmBinding(
        bool left,
        XRController3D controller,
        Node3D target,
        MeshInstance3D mesh,
        Skeleton3D skeleton,
        Fabrik3D ik,
        int shoulderBone,
        int elbowBone,
        int handBone,
        int socketBone,
        float upperLength,
        float lowerLength)
    {
        public bool Left { get; } = left;
        public XRController3D Controller { get; } = controller;
        public Node3D Target { get; } = target;
        public MeshInstance3D Mesh { get; } = mesh;
        public Skeleton3D Skeleton { get; } = skeleton;
        public Fabrik3D Ik { get; } = ik;
        public int ShoulderBone { get; } = shoulderBone;
        public int ElbowBone { get; } = elbowBone;
        public int HandBone { get; } = handBone;
        public int SocketBone { get; } = socketBone;
        public float UpperLength { get; } = upperLength;
        public float LowerLength { get; } = lowerLength;
        public int TrackingFrames { get; set; }
        public int StableFrames { get; set; }
        public bool Calibrated { get; set; }
        public bool TargetClamped { get; set; }
        public float SocketError { get; set; } = float.PositiveInfinity;
        public Basis ControllerToSocketBasis { get; set; } = Basis.Identity;
    }

    private enum ShowcasePhase
    {
        Disabled,
        OpeningDialogue,
        Gear,
        Corridor,
        Transmission,
        EncounterLeadIn,
        Encounter,
        FinalHold,
        Complete
    }

}
