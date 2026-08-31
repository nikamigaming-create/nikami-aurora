using Godot;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;
using Nikami.Aurora.GodotRuntime.Presentation;

namespace Nikami.Aurora.GodotRuntime.MainMenu;

internal sealed partial class CharacterPreviewViewport : SubViewportContainer
{
    private readonly Dictionary<string, PackedScene> models = new(StringComparer.OrdinalIgnoreCase);
    private SubViewport viewport = null!;
    private Node3D modelRoot = null!;
    private Camera3D camera = null!;
    private Node3D? character;
    private readonly IGodotModelPostprocessor modelPostprocessor;

    internal CharacterPreviewViewport(IGodotModelPostprocessor modelPostprocessor) =>
        this.modelPostprocessor = modelPostprocessor;

    internal string CurrentModelPath { get; private set; } = string.Empty;
    internal string CurrentSelectionKey { get; private set; } = string.Empty;
    internal string LastFailure { get; private set; } = string.Empty;
    internal DragonAgeCharacterSelectionState IdentityState { get; private set; } =
        DragonAgeCharacterSelectionState.Empty;

    internal Image CaptureImage() => viewport.GetTexture().GetImage();

    public override void _Ready()
    {
        Stretch = true;
        MouseFilter = MouseFilterEnum.Ignore;
        viewport = new SubViewport
        {
            Name = "CharacterViewport",
            Size = new Vector2I(512, 704),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            Msaa3D = Viewport.Msaa.Msaa4X
        };
        AddChild(viewport);
        modelRoot = new Node3D { Name = "Model" };
        viewport.AddChild(modelRoot);
        camera = new Camera3D { Name = "Camera", Current = true, Fov = 34, Near = 0.05f };
        viewport.AddChild(camera);
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-38, -28, 0),
            LightColor = new Color(1.0f, 0.82f, 0.65f),
            LightEnergy = 1.7f,
            ShadowEnabled = true
        });
        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-20, 150, 0),
            LightColor = new Color(0.35f, 0.45f, 0.72f),
            LightEnergy = 0.85f
        });
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0, 0, 0, 0),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.42f, 0.38f, 0.34f),
            AmbientLightEnergy = 0.75f,
            TonemapMode = Godot.Environment.ToneMapper.Filmic
        };
        viewport.AddChild(new WorldEnvironment { Environment = environment });
    }

    public override void _Process(double delta)
    {
        if (character is not null) modelRoot.RotateY((float)delta * 0.16f);
    }

    public bool ShowCharacter(string race, string gender, string appearance)
    {
        var resolution = CachedDaoCharacterAppearanceCatalog.Resolve(race, gender, appearance);
        if (!resolution.IsReady || resolution.Appearance is not { } authored)
        {
            ClearCharacter();
            LastFailure = resolution.Failure;
            GD.PushWarning($"OPENDAO_CHARGEN_PREVIEW status=unsupported race={race} gender={gender} " +
                           $"appearance={appearance} availability={resolution.Availability} " +
                           $"reason={resolution.Failure} npc_substitution=0");
            return false;
        }
        var path = resolution.StandingPath;
        if (!models.TryGetValue(path, out var packed))
        {
            packed = Import(path);
            if (packed is null)
            {
                ClearCharacter();
                LastFailure = "source-bound-model-import-failed";
                return false;
            }
            models[path] = packed;
        }

        character?.QueueFree();
        character = packed.Instantiate<Node3D>();
        modelRoot.AddChild(character);
        modelRoot.Rotation = new Vector3(0, Mathf.Pi, 0);
        Frame(character);
        PlayDefaultAnimation(character);
        CurrentModelPath = path;
        CurrentSelectionKey = authored.SelectionKey;
        IdentityState = new DragonAgeCharacterSelectionState(authored.SelectionKey, true);
        LastFailure = string.Empty;
        GD.Print($"OPENDAO_CHARGEN_PREVIEW status=ready selection={authored.SelectionKey} " +
                 $"morph={authored.MorphResource} morph_sha256={authored.MorphSha256} " +
                 $"provenance={resolution.Provenance} path={path} " +
                 "npc_substitution=0 pbr=global-postprocessor parity_claim=none");
        return true;
    }

    private void Frame(Node3D node)
    {
        var bounds = SceneBounds.Calculate(node);
        if (bounds.Size.IsZeroApprox()) bounds = new Aabb(new Vector3(-0.5f, 0, -0.5f), new Vector3(1, 1.8f, 1));
        var center = bounds.GetCenter();
        node.Position -= center;
        var half = bounds.Size * 0.5f;
        var aspect = viewport.Size.X / (float)viewport.Size.Y;
        var verticalTangent = MathF.Tan(Mathf.DegToRad(camera.Fov) * 0.5f);
        var horizontalTangent = verticalTangent * aspect;
        var fitHeight = half.Y / Math.Max(verticalTangent, 0.01f);
        var fitWidth = half.X / Math.Max(horizontalTangent, 0.01f);
        var distance = (Math.Max(fitHeight, fitWidth) + half.Z) * 1.24f;
        camera.Position = new Vector3(0, 0, Math.Max(distance, 3.0f));
        camera.LookAt(Vector3.Zero, Vector3.Up);
    }

    private PackedScene? Import(string path)
    {
        var document = new GltfDocument();
        var state = new GltfState();
        var append = document.AppendFromFile(path, state);
        if (append != Error.Ok)
        {
            GD.PushError($"OPENDAO_CHARGEN_PREVIEW_IMPORT_FAIL stage=append error={append} path={path}");
            return null;
        }
        if (document.GenerateScene(state) is not Node3D imported)
        {
            GD.PushError($"OPENDAO_CHARGEN_PREVIEW_IMPORT_FAIL stage=generate path={path}");
            return null;
        }
        modelPostprocessor.Process(imported, state, path);
        var packed = new PackedScene();
        var pack = packed.Pack(imported);
        if (pack != Error.Ok)
        {
            GD.PushError($"OPENDAO_CHARGEN_PREVIEW_IMPORT_FAIL stage=pack error={pack} path={path}");
            imported.Free();
            return null;
        }
        imported.Free();
        return packed;
    }

    private void ClearCharacter()
    {
        character?.QueueFree();
        character = null;
        CurrentModelPath = string.Empty;
        CurrentSelectionKey = string.Empty;
        IdentityState = DragonAgeCharacterSelectionState.Empty;
    }

    private static void PlayDefaultAnimation(Node root)
    {
        foreach (var player in root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>())
            foreach (var name in player.GetAnimationList())
            {
                if (name == "RESET") continue;
                player.Play(name);
                return;
            }
    }
}
