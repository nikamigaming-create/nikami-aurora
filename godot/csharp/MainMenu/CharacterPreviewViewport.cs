using Godot;
using OpenDAO.Application.Characters;
using OpenDAO.Infrastructure.World;
using OpenDAO.Infrastructure.Configuration;
using OpenDAO.Presentation;

namespace OpenDAO.MainMenu;

internal sealed partial class CharacterPreviewViewport : SubViewportContainer
{
    private static readonly Lazy<IReadOnlyDictionary<string, string>> ModelIndex = new(BuildModelIndex);
    private readonly Dictionary<string, PackedScene> models = new(StringComparer.OrdinalIgnoreCase);
    private SubViewport viewport = null!;
    private Node3D modelRoot = null!;
    private Camera3D camera = null!;
    private Node3D? character;
    private readonly IGodotModelPostprocessor modelPostprocessor;

    internal CharacterPreviewViewport(IGodotModelPostprocessor modelPostprocessor) =>
        this.modelPostprocessor = modelPostprocessor;

    internal string CurrentModelPath { get; private set; } = string.Empty;

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
        var path = ResolveModel(race, gender, appearance);
        if (path.Length == 0)
        {
            CurrentModelPath = string.Empty;
            return false;
        }
        if (!models.TryGetValue(path, out var packed))
        {
            packed = Import(path);
            if (packed is null) return false;
            models[path] = packed;
        }

        character?.QueueFree();
        character = packed.Instantiate<Node3D>();
        modelRoot.AddChild(character);
        modelRoot.Rotation = new Vector3(0, Mathf.Pi, 0);
        Frame(character);
        PlayDefaultAnimation(character);
        CurrentModelPath = path;
        GD.Print($"OPENDAO_CHARGEN_PREVIEW race={race} gender={gender} appearance={appearance} path={path}");
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
        modelPostprocessor.Process(imported, state);
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

    private static string ResolveModel(string race, string gender, string appearance)
    {
        if (RetailCharacterAppearanceCatalog.Resolve(race, gender, appearance) is { } retail)
        {
            var authored = DaoRuntimePaths.Cache(retail.StandingRelativePath);
            if (File.Exists(authored)) return authored;
        }
        var index = int.TryParse(appearance.AsSpan(appearance.LastIndexOf('-') + 1), out var parsed)
            ? Math.Clamp(parsed, 1, 4)
            : 1;
        var candidates = (race, gender) switch
        {
            ("dwarf", "female") => new[] { $"bdc100cr_amb_f_{index}.glb", "bdn120cr_rica.glb" },
            ("dwarf", _) => new[] { $"bdc100cr_amb_m_{index}.glb", "bdn100cr_gorim.glb" },
            ("elf", "female") => new[]
            {
                new[] { "bec210cr_elf_servantf.glb", "bec100cr_elf_commoner_f.glb",
                    "den300cr_crowd_elf_fem_3.glb", "ntb100cr_elf_female_03.glb" }[index - 1]
            },
            ("elf", _) => new[]
            {
                new[] { "bec210cr_elf_servantm.glb", "bec100cr_homeless_elf_man.glb",
                    "den300cr_crowd_elf_male_3.glb", "ntb100cr_elf_male_03.glb" }[index - 1]
            },
            ("human", "female") => new[]
            {
                new[] { "arl110cr_villager_f_1.glb", "arl110cr_villager_f_2.glb",
                    "arl150cr_bella.glb", "den211cr_nigella.glb" }[index - 1]
            },
            _ => new[]
            {
                new[] { "arl100cr_tomas.glb", "arl100cr_militia_1.glb",
                    "arl100cr_murdock.glb", "arl100cr_watchman.glb" }[index - 1]
            }
        };
        foreach (var candidate in candidates)
            if (ModelIndex.Value.TryGetValue(candidate, out var match)) return match;
        return string.Empty;
    }

    private static IReadOnlyDictionary<string, string> BuildModelIndex()
    {
        var root = DaoRuntimePaths.Cache("areas");
        if (!Directory.Exists(root)) return new Dictionary<string, string>();
        return Directory.EnumerateFiles(root, "*.glb", SearchOption.AllDirectories)
            .GroupBy(path => Path.GetFileName(path)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
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
