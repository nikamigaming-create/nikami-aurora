using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot : Node3D
{
    private const float DefaultGameplayFieldOfView = 72.0f;
    private static readonly Shader OdysseyLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded;
            uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic;
            uniform sampler2D lightmap_texture : source_color, filter_linear_mipmap_anisotropic;
            void fragment() {
                vec4 base = texture(albedo_texture, UV);
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = base.rgb * min(vec3(1.0), lightmap);
                ALPHA = base.a;
                if (ALPHA < 0.05) discard;
            }
            """
    };
    private CharacterBody3D playerBody = null!;
    private Node3D cameraPivot = null!;
    private SpringArm3D cameraArm = null!;
    private Camera3D camera = null!;
    private XROrigin3D xrOrigin = null!;
    private XRCamera3D xrCamera = null!;
    private bool xrActive;
    private Node3D? playerModel;
    private AnimationPlayer? playerAnimationPlayer;
    private string currentPlayerAnimation = "";
    private string forcedPlayerAnimation = "";
    private float playerWalkSpeed = 1.7f;
    private float playerRunSpeed = 5.4f;
    private float gameplayFieldOfView = DefaultGameplayFieldOfView;
    private AudioStreamPlayer dialogueVoice = null!;
    private Godot.Environment runtimeEnvironment = null!;
    private CanvasLayer overlayLayer = null!;
    private Label status = null!;
    private Label details = null!;
    private ColorRect loadingBackdrop = null!;
    private PanelContainer dialoguePanel = null!;
    private Label dialogueSpeaker = null!;
    private Label dialogueText = null!;
    private VBoxContainer dialogueChoices = null!;
    private Label interactionPrompt = null!;
    private readonly List<Button> activeChoiceButtons = [];
    private readonly List<NavigationTriangle> navigationTriangles = [];
    private readonly List<InteractiveDoor> interactiveDoors = [];
    private readonly List<InteractivePlaceable> interactivePlaceables = [];
    private readonly Dictionary<string, AnimationPlayer> actorAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> actorModels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> actorTalkOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LipRig> actorLipRigs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CameraRecord> dialogueCameras = [];
    private readonly Dictionary<string, ScriptContractRecord> scriptContracts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> reportedUnsupportedScripts = new(StringComparer.OrdinalIgnoreCase);
    private int capturedFrames;
    private int captureTargetFrame = 60;
    private int readyFrames;
    private bool moduleReady;
    private bool automatedChoiceApplied;
    private bool automatedMoveApplied;
    private bool automatedDoorApplied;
    private bool automatedLockerApplied;
    private bool automatedTutorialXpChain;
    private bool dialogueCameraActive;
    private float dialogueFieldOfView = 55.0f;
    private string dialogueOwnerActor = "";
    private string dialogueManifestDirectory = "";
    private string currentVoiceActor = "";
    private LipTrack? currentLipTrack;
    private LipRig? currentLipRig;
    private int currentLipSegment = -1;
    private string lastDialogueSpeaker = "TRASK ULGO";
    private float yaw;
    private float pitch;
    private int playerExperience;

    public override void _Ready()
    {
        CreateEnvironment();
        CreateCamera();
        TryInitializeOpenXR();
        CreateAudio();
        CreateOverlay();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_FRAME"),
                out var configuredCaptureFrame))
            captureTargetFrame = Math.Max(1, configuredCaptureFrame);
        forcedPlayerAnimation =
            System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_PLAYER_ANIMATION") ?? "";

        var manifestPath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_MODULE_MANIFEST");
        if (string.IsNullOrWhiteSpace(manifestPath))
        {
            manifestPath = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"),
                "..", "local", "kotor", "end_m01aa", "module-manifest.json"));
        }
        Callable.From(() => LoadModuleAsync(manifestPath)).CallDeferred();
    }

    public override void _Process(double delta)
    {
        var basis = new Basis(Vector3.Up, yaw);
        var movement = Vector3.Zero;
        if (Input.IsKeyPressed(Key.W)) movement -= basis.Z;
        if (Input.IsKeyPressed(Key.S)) movement += basis.Z;
        if (Input.IsKeyPressed(Key.A)) movement -= basis.X;
        if (Input.IsKeyPressed(Key.D)) movement += basis.X;
        var playerMoved = false;
        var sprinting = Input.IsKeyPressed(Key.Shift);
        if (!dialoguePanel.Visible && movement.LengthSquared() > 0.001f)
        {
            var speed = sprinting ? playerRunSpeed : playerWalkSpeed;
            playerMoved = MovePlayer(movement.Normalized() * speed * (float)delta);
        }
        PlayPlayerAnimation(!string.IsNullOrWhiteSpace(forcedPlayerAnimation)
            ? forcedPlayerAnimation
            : playerMoved ? sprinting ? "run" : "walk" : "pause1");
        UpdateInteractionPrompt();
        UpdateLipSync();

        if (moduleReady)
        {
            readyFrames++;
            if (!automatedDoorApplied && readyFrames >= 10 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_DOOR") == "1" &&
                interactiveDoors.Count > 0)
            {
                automatedDoorApplied = true;
                if (!interactiveDoors[0].Open)
                    ToggleDoor(interactiveDoors[0]);
                else
                    GD.Print($"NIKAMI_AURORA_DOOR status=already-open tag={interactiveDoors[0].Source.Tag}");
            }
            if (!automatedLockerApplied && readyFrames >= 30 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_LOCKER") == "1" &&
                interactivePlaceables.Count > 0)
            {
                automatedLockerApplied = true;
                UsePlaceable(interactivePlaceables[0]);
            }
            if (!automatedTutorialXpChain && readyFrames >= 30 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN") == "1" &&
                interactivePlaceables.Count > 0)
            {
                automatedTutorialXpChain = true;
                UsePlaceable(interactivePlaceables[0]);
                ExecuteScript("k_pend_door1xp");
                if (playerExperience != 150)
                    throw new InvalidDataException(
                        $"Tutorial XP chain ended at {playerExperience}, expected 150");
                GD.Print("NIKAMI_AURORA_NCS_CHAIN status=pass xp=0->50->150");
            }
            var configuredChoice = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_DIALOGUE_CHOICE");
            if (!automatedChoiceApplied && readyFrames >= 20 &&
                int.TryParse(configuredChoice, out var choice) &&
                choice >= 0 && choice < activeChoiceButtons.Count)
            {
                automatedChoiceApplied = true;
                activeChoiceButtons[choice].EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print($"NIKAMI_AURORA_DIALOGUE_CHOICE status=selected index={choice}");
            }
            var configuredMove = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_MOVE_METERS");
            if (!automatedMoveApplied && readyFrames >= 30 &&
                double.TryParse(configuredMove, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var meters) && Math.Abs(meters) > 0.001)
            {
                automatedMoveApplied = true;
                var start = playerBody.GlobalPosition;
                var accepted = MovePlayer(-basis.Z * (float)meters);
                GD.Print($"NIKAMI_AURORA_NAV_TEST status={(accepted ? "accepted" : "rejected")} " +
                         $"from={start} to={playerBody.GlobalPosition} requested={meters:F3}");
            }
            if (readyFrames == 40 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_CLEAN") == "1")
                overlayLayer.Visible = false;
            if (readyFrames == 40 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP") == "1")
                FrameLipSyncCloseup(dialogueOwnerActor);
        }

        var capturePath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE");
        if (moduleReady && !string.IsNullOrWhiteSpace(capturePath) &&
            ++capturedFrames == captureTargetFrame)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(capturePath))!);
            var error = GetViewport().GetTexture().GetImage().SavePng(capturePath);
            GD.Print($"NIKAMI_AURORA_CAPTURE status={error} path={capturePath}");
            if (System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
                GetTree().Quit(error == Error.Ok ? 0 : 1);
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
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
            var placeable = NearestPlaceable(2.6f);
            var door = NearestDoor(2.6f);
            if (placeable is not null && !dialoguePanel.Visible)
                UsePlaceable(placeable);
            else if (door is not null && !dialoguePanel.Visible)
                ToggleDoor(door);
        }
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

            status.Text = $"LOADING {manifest.Module.ToUpperInvariant()}";
            details.Text = "Resolving owned Odyssey geometry...";
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            dialogueCameras.Clear();
            foreach (var sourceCamera in manifest.Cameras)
                dialogueCameras[sourceCamera.Id] = sourceCamera;
            dialogueFieldOfView = manifest.CameraStyle.ViewAngle;
            gameplayFieldOfView = manifest.CameraStyle.ViewAngle;
            camera.Fov = gameplayFieldOfView;
            scriptContracts.Clear();
            foreach (var contract in manifest.ScriptContracts)
            {
                if (contract.Schema != "nikami-aurora-kotor-script-contract-v1")
                    throw new InvalidDataException($"Unsupported script contract: {contract.Resref}");
                scriptContracts[contract.Resref] = contract;
            }
            if (int.TryParse(System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_PLAYER_XP"),
                    out var configuredPlayerXp))
                playerExperience = Math.Max(0, configuredPlayerXp);
            ApplyAreaLighting(manifest.Lighting);
            var loadedRooms = 0;
            foreach (var room in manifest.Rooms)
            {
                if (string.IsNullOrWhiteSpace(room.Glb)) continue;
                var glbPath = Path.GetFullPath(Path.Combine(manifestDirectory,
                    room.Glb.Replace('/', Path.DirectorySeparatorChar)));
                var document = new GltfDocument();
                var state = new GltfState();
                if (document.AppendFromFile(glbPath, state) != Error.Ok ||
                    document.GenerateScene(state) is not Node3D imported)
                    throw new InvalidDataException($"Godot could not import room {room.Model}: {glbPath}");
                imported.Name = room.Model;
                imported.Position = ToGodot(room.Position);
                ConfigureStaticRoomMaterials(imported);
                AddChild(imported);
                loadedRooms++;
                details.Text = $"Rooms {loadedRooms}/{manifest.Rooms.Count}  •  {room.Model}";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }

            var authoredLights = LoadAuthoredLights(manifest.Rooms, manifest.Lighting);
            var materializedPlayer = LoadPlayerModel(
                manifest.Player, manifest.CameraStyle, manifestDirectory);
            var materializedActors = LoadActorModels(manifest.Creatures, manifestDirectory);
            AddCreatureMarkers(manifest.Creatures);
            var materializedDoors = LoadDoorModels(manifest.Doors, manifestDirectory);
            var materializedPlaceables = LoadPlaceableModels(manifest.Placeables, manifestDirectory);
            BuildNavigation(manifest.Rooms);
            var entry = ToGodot(manifest.Entry.Position);
            if (!TryProjectToWalkmesh(entry, out var entryGround))
                throw new InvalidDataException($"Authored entry point is not on the imported walkmesh: {entry}");
            entry.Y = entryGround;
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

            loadingBackdrop.Visible = false;
            status.Text = $"{manifest.Module.ToUpperInvariant()}  •  ENDAR SPIRE";
            details.Text = $"{manifest.Rooms.Count} authored / {loadedRooms} visual rooms  •  " +
                           $"{materializedActors} actor / {manifest.Counts.Creatures} creature placements  •  " +
                           $"{materializedPlayer} player avatar  •  " +
                           $"{manifest.Counts.WalkmeshTriangles} nav triangles  •  " +
                           $"{authoredLights}/{manifest.Counts.AuthoredLights} source lights  •  " +
                           $"{materializedDoors} door / {manifest.Counts.Doors} placements  •  " +
                           $"{materializedPlaceables} placeable / {manifest.Counts.Placeables} placements  •  " +
                           $"source {manifest.Target.ExecutableSha256[..12]}";
            GD.Print($"NIKAMI_AURORA_KOTOR_BOOT status=pass module={manifest.Module} " +
                     $"rooms={loadedRooms} authoredRooms={manifest.Rooms.Count} creatures={manifest.Counts.Creatures} " +
                     $"sha256={manifest.Target.ExecutableSha256}");
            GD.Print($"NIKAMI_AURORA_LIGHTING status=ready authored={manifest.Counts.AuthoredLights} " +
                     $"materialized={authoredLights} ambient={ToColor(manifest.Lighting.DynamicAmbient)}");
            GD.Print($"NIKAMI_AURORA_NAV status=ready triangles={navigationTriangles.Count} " +
                     $"entry={playerBody.GlobalPosition}");
            var openingActor = manifest.Creatures.FirstOrDefault(creature => creature.Dialogue is not null);
            if (openingActor is not null &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_SKIP_OPENING_DIALOGUE") != "1")
                LoadOpeningDialogue(openingActor, manifestDirectory);
            capturedFrames = 0;
            readyFrames = 0;
            moduleReady = true;
        }
        catch (Exception exception)
        {
            status.Text = "KOTOR MODULE LOAD FAILED";
            details.Text = exception.Message;
            GD.PushError($"NIKAMI_AURORA_KOTOR_BOOT status=fail error={exception}");
        }
    }

    private void CreateEnvironment()
    {
        runtimeEnvironment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.004f, 0.008f, 0.018f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.2f, 0.2f, 0.2f),
            AmbientLightEnergy = 1.0f
        };
        var worldEnvironment = new WorldEnvironment
        {
            Environment = runtimeEnvironment
        };
        AddChild(worldEnvironment);
    }

    private void ApplyAreaLighting(AreaLightingRecord lighting)
    {
        runtimeEnvironment.AmbientLightColor = ToColor(lighting.DynamicAmbient);
        runtimeEnvironment.AmbientLightEnergy = 1.0f;
    }

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
            Margin = 0.08f
        };
        cameraPivot.AddChild(cameraArm);
        camera = new Camera3D
        {
            Current = true,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = DefaultGameplayFieldOfView
        };
        cameraArm.AddChild(camera);
        xrOrigin = new XROrigin3D { Name = "XROrigin" };
        playerBody.AddChild(xrOrigin);
        xrCamera = new XRCamera3D
        {
            Name = "XRCamera",
            Current = false
        };
        xrOrigin.AddChild(xrCamera);
    }

    private void CreateAudio()
    {
        dialogueVoice = new AudioStreamPlayer { Name = "DialogueVoice" };
        dialogueVoice.Finished += OnDialogueVoiceFinished;
        AddChild(dialogueVoice);
    }

    private void TryInitializeOpenXR()
    {
        if (System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_OPENXR") != "1")
        {
            GD.Print("NIKAMI_AURORA_OPENXR status=disabled");
            return;
        }
        var openXr = XRServer.FindInterface("OpenXR");
        if (openXr is null || (!openXr.IsInitialized() && !openXr.Initialize()))
        {
            GD.PushWarning("NIKAMI_AURORA_OPENXR status=unavailable fallback=desktop");
            return;
        }
        xrActive = true;
        xrOrigin.Current = true;
        xrOrigin.WorldScale = 1.0f;
        camera.Current = false;
        xrCamera.Current = true;
        GetViewport().UseXR = true;
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        GD.Print("NIKAMI_AURORA_OPENXR status=ready worldScale=1.000 " +
                 "authority=hmd-relative-to-game-camera");
    }

    private void CreateOverlay()
    {
        overlayLayer = new CanvasLayer();
        AddChild(overlayLayer);
        loadingBackdrop = new ColorRect
        {
            Color = new Color(0.005f, 0.012f, 0.025f, 0.94f),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        loadingBackdrop.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        overlayLayer.AddChild(loadingBackdrop);

        var panel = new VBoxContainer
        {
            Position = new Vector2(36, 32),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        overlayLayer.AddChild(panel);
        var brand = new Label { Text = "NIKAMI / AURORA", MouseFilter = Control.MouseFilterEnum.Ignore };
        brand.AddThemeFontSizeOverride("font_size", 18);
        brand.AddThemeColorOverride("font_color", new Color(0.42f, 0.78f, 1.0f));
        panel.AddChild(brand);
        status = new Label { Text = "INITIALIZING ODYSSEY PROFILE" };
        status.AddThemeFontSizeOverride("font_size", 30);
        panel.AddChild(status);
        details = new Label { Text = "Waiting for module manifest..." };
        details.AddThemeFontSizeOverride("font_size", 16);
        details.AddThemeColorOverride("font_color", new Color(0.72f, 0.8f, 0.9f));
        panel.AddChild(details);

        var controls = new Label
        {
            Text = "WASD move  •  mouse look  •  Shift sprint  •  E interact  •  Esc release mouse",
            Position = new Vector2(36, 680),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        controls.AddThemeFontSizeOverride("font_size", 14);
        controls.AddThemeColorOverride("font_color", new Color(0.62f, 0.7f, 0.8f));
        overlayLayer.AddChild(controls);

        interactionPrompt = new Label
        {
            Position = new Vector2(460, 610),
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        interactionPrompt.AddThemeFontSizeOverride("font_size", 20);
        interactionPrompt.AddThemeColorOverride("font_color", new Color(0.45f, 0.88f, 1.0f));
        overlayLayer.AddChild(interactionPrompt);

        dialoguePanel = new PanelContainer
        {
            AnchorLeft = 0.12f,
            AnchorTop = 0.66f,
            AnchorRight = 0.88f,
            AnchorBottom = 0.96f,
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
        overlayLayer.AddChild(dialoguePanel);
        var dialogueLayout = new VBoxContainer();
        dialoguePanel.AddChild(dialogueLayout);
        dialogueSpeaker = new Label();
        dialogueSpeaker.AddThemeFontSizeOverride("font_size", 18);
        dialogueSpeaker.AddThemeColorOverride("font_color", new Color(0.4f, 0.82f, 1.0f));
        dialogueLayout.AddChild(dialogueSpeaker);
        dialogueText = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 64)
        };
        dialogueText.AddThemeFontSizeOverride("font_size", 22);
        dialogueLayout.AddChild(dialogueText);
        dialogueChoices = new VBoxContainer();
        dialogueLayout.AddChild(dialogueChoices);
    }

    private void LoadOpeningDialogue(CreatureRecord actor, string manifestDirectory)
    {
        if (actor.Dialogue is null) return;
        var path = Path.GetFullPath(Path.Combine(manifestDirectory,
            actor.Dialogue.Path.Replace('/', Path.DirectorySeparatorChar)));
        var graph = JsonSerializer.Deserialize<DialogueGraph>(File.ReadAllText(path), JsonOptions())
                    ?? throw new InvalidDataException($"Dialogue graph is empty: {path}");
        if (graph.Schema != "nikami-aurora-kotor-dialogue-v1" || graph.Starters.Count == 0)
            throw new InvalidDataException($"Unsupported dialogue graph: {path}");
        var starterIndex = Math.Clamp(graph.OpeningStarter, 0, graph.Starters.Count - 1);
        dialogueOwnerActor = actor.Template;
        dialogueManifestDirectory = manifestDirectory;
        PresentDialogueNode(graph, graph.Starters[starterIndex].Target, new HashSet<string>(), 0);
    }

    private void PlayActorAnimation(string actor, string requested)
    {
        if (!actorAnimations.TryGetValue(actor, out var player)) return;
        var match = player.GetAnimationList().FirstOrDefault(name =>
            name.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            name.ToString().EndsWith('/' + requested, StringComparison.OrdinalIgnoreCase));
        if (match == default) return;
        player.Play(match);
        GD.Print($"NIKAMI_AURORA_ACTOR_ANIMATION status=playing actor={actor} animation={match}");
    }

    private void SetPresentationCameraBase(Vector3 position, Vector3 target, Vector3 up, float fov)
    {
        if (xrActive)
        {
            xrOrigin.TopLevel = true;
            xrOrigin.GlobalPosition = position;
            xrOrigin.LookAt(target, up);
        }
        else
        {
            camera.TopLevel = true;
            camera.GlobalPosition = position;
            camera.LookAt(target, up);
            camera.Fov = fov;
        }
        dialogueCameraActive = true;
    }

    private void ApplyDialogueCamera(DialogueNode node)
    {
        if (node.CameraId is int cameraId && cameraId > 0 &&
            dialogueCameras.TryGetValue(cameraId, out var source))
        {
            var position = ToGodot(source.Position) + Vector3.Up * source.Height;
            var forward = ToGodot(source.Forward).Normalized();
            var up = ToGodot(source.Up).Normalized();
            if (forward.LengthSquared() < 0.99f || up.LengthSquared() < 0.99f)
                throw new InvalidDataException($"Authored camera {cameraId} has an invalid basis");
            var fov = node.CameraFov is > 0 ? node.CameraFov.Value : source.Fov;
            SetPresentationCameraBase(position, position + forward, up, fov);
            GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active mode=static id={cameraId} " +
                     $"fov={fov:F3} position={position} xr={xrActive}");
            return;
        }
        var speakerActor = ResolveDialogueActor(node);
        if (string.IsNullOrWhiteSpace(node.Text) || speakerActor is null ||
            !actorModels.TryGetValue(speakerActor, out var speaker))
            return;

        var listenerPosition = playerBody.GlobalPosition + Vector3.Up * 1.55f;
        var talkDummy = FindDescendantBySuffix<Node3D>(speaker, "talkdummy");
        var speakerPosition = talkDummy?.GlobalPosition ??
            (actorTalkOffsets.TryGetValue(speakerActor, out var talkOffset)
                ? speaker.GlobalTransform * talkOffset
                : speaker.GlobalPosition + Vector3.Up * 1.55f);
        var listenerToSpeaker = speakerPosition - listenerPosition;
        var distance = listenerToSpeaker.Length();
        if (distance < 0.01f) return;
        var direction = listenerToSpeaker / distance;
        var offset = Math.Min(0.25f * distance, 1.0f);
        var side = direction.Cross(Vector3.Down).Normalized();
        var center = 0.5f * (listenerPosition + speakerPosition);
        var eye = center - offset * direction + offset * side + 0.1f * Vector3.Up;
        var target = speakerPosition - 0.1f * distance * side + 0.1f * Vector3.Up;
        SetPresentationCameraBase(eye, target, Vector3.Up, dialogueFieldOfView);
        GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active mode=speaker actor={speakerActor} " +
                 $"fov={dialogueFieldOfView:F3} position={eye} xr={xrActive}");
    }

    private string? ResolveDialogueActor(DialogueNode node)
    {
        if (node.Kind != "entry") return null;
        return string.IsNullOrWhiteSpace(node.Speaker) ? dialogueOwnerActor : node.Speaker;
    }

    private void PlayDialoguePerformance(DialogueNode node, string actor)
    {
        ClearLipPose();
        PlayActorAnimation(actor, "tlknorm");
        currentVoiceActor = actor;
        actorLipRigs.TryGetValue(actor, out currentLipRig);
        currentLipTrack = null;
        currentLipSegment = -1;
        dialogueVoice.Stop();
        if (node.Media?.AudioPath is not { Length: > 0 } audioRelative)
            return;

        var audioPath = ResolveBundlePath(audioRelative);
        var audioBytes = File.ReadAllBytes(audioPath);
        AudioStream stream = node.Media.AudioFormat.ToLowerInvariant() switch
        {
            "mp3" => AudioStreamMP3.LoadFromBuffer(audioBytes),
            "wav" => AudioStreamWav.LoadFromBuffer(audioBytes, new Godot.Collections.Dictionary()),
            _ => throw new InvalidDataException(
                $"Unsupported dialogue audio format: {node.Media.AudioFormat}")
        };
        dialogueVoice.Stream = stream;
        if (node.Media.LipPath is { Length: > 0 } lipRelative)
        {
            var lipPath = ResolveBundlePath(lipRelative);
            currentLipTrack = JsonSerializer.Deserialize<LipTrack>(
                File.ReadAllText(lipPath), JsonOptions())
                ?? throw new InvalidDataException($"LIP track is empty: {lipPath}");
            if (currentLipTrack.Schema != "nikami-aurora-kotor-lip-v1")
                throw new InvalidDataException($"Unsupported LIP track: {lipPath}");
        }
        dialogueVoice.Play();
        GD.Print($"NIKAMI_AURORA_DIALOGUE_AUDIO status=playing actor={actor} sound={node.Sound} " +
                 $"format={node.Media.AudioFormat} bytes={audioBytes.Length} " +
                 $"duration={stream.GetLength():F3} lipFrames={currentLipTrack?.Frames.Count ?? 0}");
    }

    private string ResolveBundlePath(string relativePath)
    {
        var root = Path.GetFullPath(dialogueManifestDirectory) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(
            dialogueManifestDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Dialogue media path escapes the bundle: {relativePath}");
        return fullPath;
    }

    private void UpdateLipSync()
    {
        if (currentLipTrack is null || !dialogueVoice.Playing || currentLipTrack.Frames.Count == 0)
            return;
        var time = dialogueVoice.GetPlaybackPosition();
        var rightIndex = currentLipTrack.Frames.Count - 1;
        for (var index = 0; index < currentLipTrack.Frames.Count; index++)
        {
            if (time <= currentLipTrack.Frames[index].Time)
            {
                rightIndex = index;
                break;
            }
        }
        var leftIndex = Math.Max(0, rightIndex - 1);
        var segmentChanged = rightIndex != currentLipSegment;
        currentLipSegment = rightIndex;
        var left = currentLipTrack.Frames[leftIndex];
        var right = currentLipTrack.Frames[rightIndex];
        var span = right.Time - left.Time;
        var factor = span > 0.000001f ? Math.Clamp((time - left.Time) / span, 0.0f, 1.0f) : 0.0f;
        if (currentLipRig is not null)
            currentLipRig.Modifier.SetSample(left.Shape, right.Shape, factor);
        if (segmentChanged)
            GD.Print($"NIKAMI_AURORA_LIP status=sample actor={currentVoiceActor} time={time:F3} " +
                     $"left={left.Shape} right={right.Shape} factor={factor:F3}");
    }

    private void OnDialogueVoiceFinished()
    {
        ClearLipPose();
        currentLipTrack = null;
        currentLipSegment = -1;
        if (!string.IsNullOrWhiteSpace(currentVoiceActor))
            PlayActorAnimation(currentVoiceActor, "pause1");
        GD.Print($"NIKAMI_AURORA_DIALOGUE_AUDIO status=finished actor={currentVoiceActor}");
        currentVoiceActor = "";
        foreach (var button in activeChoiceButtons)
            button.Disabled = false;
    }

    private void StopDialoguePerformance()
    {
        var actor = currentVoiceActor;
        dialogueVoice.Stop();
        ClearLipPose();
        currentLipTrack = null;
        currentLipSegment = -1;
        currentVoiceActor = "";
        if (!string.IsNullOrWhiteSpace(actor))
            PlayActorAnimation(actor, "pause1");
    }

    private void RestoreGameplayCamera()
    {
        if (!dialogueCameraActive) return;
        dialogueCameraActive = false;
        if (xrActive)
        {
            xrOrigin.TopLevel = false;
            xrOrigin.Position = cameraPivot.Position;
            xrOrigin.Rotation = Vector3.Zero;
        }
        else
        {
            camera.TopLevel = false;
            camera.Transform = Transform3D.Identity;
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            camera.Fov = gameplayFieldOfView;
        }
        GD.Print("NIKAMI_AURORA_DIALOGUE_CAMERA status=released");
    }

    private void FrameLipSyncCloseup(string actor)
    {
        if (!actorModels.TryGetValue(actor, out var model)) return;
        var target = actorTalkOffsets.TryGetValue(actor, out var talkOffset)
            ? model.GlobalTransform * talkOffset
            : model.GlobalPosition + Vector3.Up * 1.6f;
        var forward = -model.GlobalTransform.Basis.Z.Normalized();
        var eye = target + forward * 1.35f + Vector3.Up * 0.03f;
        SetPresentationCameraBase(eye, target, Vector3.Up, 40.0f);
        GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active mode=lip-closeup actor={actor} " +
                 $"fov=40.000 position={eye} xr={xrActive}");
    }

    private static T? FindDescendant<T>(Node node) where T : Node
    {
        if (node is T match) return match;
        foreach (var child in node.GetChildren())
        {
            var found = FindDescendant<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindDescendantBySuffix<T>(Node node, string suffix) where T : Node
    {
        if (node is T match && node.Name.ToString().EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return match;
        foreach (var child in node.GetChildren())
        {
            var found = FindDescendantBySuffix<T>(child, suffix);
            if (found is not null) return found;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(Node node) where T : Node
    {
        if (node is T match) yield return match;
        foreach (var child in node.GetChildren())
        {
            foreach (var found in FindDescendants<T>(child))
                yield return found;
        }
    }

    private static LipRig? BuildLipRig(Node actor, AnimationPlayer animationPlayer)
    {
        var talk = animationPlayer.GetAnimation("talk");
        if (talk is null) return null;
        var skeletons = FindDescendants<Skeleton3D>(actor).ToArray();
        var tracks = new Dictionary<int, KotorLipModifier.TrackBinding>();
        Skeleton3D? targetSkeleton = null;
        for (var trackIndex = 0; trackIndex < talk.GetTrackCount(); trackIndex++)
        {
            var type = talk.TrackGetType(trackIndex);
            if (type is not Animation.TrackType.Position3D and not Animation.TrackType.Rotation3D)
                continue;
            var path = talk.TrackGetPath(trackIndex).ToString();
            var separator = path.LastIndexOf(':');
            if (separator < 0 || separator == path.Length - 1) continue;
            var boneName = path[(separator + 1)..];
            var skeleton = skeletons.FirstOrDefault(candidate => candidate.FindBone(boneName) >= 0);
            if (skeleton is null) continue;
            if (targetSkeleton is not null && targetSkeleton != skeleton)
                throw new InvalidDataException("Talk animation spans multiple skeletons");
            targetSkeleton = skeleton;
            var boneIndex = skeleton.FindBone(boneName);
            if (!tracks.TryGetValue(boneIndex, out var boneTrack))
            {
                boneTrack = new KotorLipModifier.TrackBinding(boneIndex);
                tracks[boneIndex] = boneTrack;
            }
            if (type == Animation.TrackType.Position3D)
                boneTrack.PositionTrack = trackIndex;
            else
                boneTrack.RotationTrack = trackIndex;
        }
        if (targetSkeleton is null || tracks.Count == 0) return null;
        var ordered = tracks.Values.OrderBy(track => BoneDepth(targetSkeleton, track.BoneIndex)).ToArray();
        var modifier = new KotorLipModifier { Name = "KotorLipModifier" };
        targetSkeleton.AddChild(modifier);
        modifier.Configure(talk, ordered);
        return new LipRig(modifier, talk, ordered);
    }

    private static int BoneDepth(Skeleton3D skeleton, int boneIndex)
    {
        var depth = 0;
        while ((boneIndex = skeleton.GetBoneParent(boneIndex)) >= 0)
            depth++;
        return depth;
    }

    private void ClearLipPose()
    {
        currentLipRig?.Modifier.StopSample();
        currentLipRig = null;
    }

    private void PresentDialogueNode(DialogueGraph graph, string key, HashSet<string> visited, int depth)
    {
        if (depth > 32 || !visited.Add(key) || !graph.Nodes.TryGetValue(key, out var node))
        {
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            RestoreGameplayCamera();
            return;
        }
        ExecuteScript(node.Script1);
        ExecuteScript(node.Script2);
        ApplyDialogueCamera(node);
        if (string.IsNullOrWhiteSpace(node.Text))
        {
            if (node.Links.Count > 0)
                PresentDialogueNode(graph, node.Links[0].Target, visited, depth + 1);
            return;
        }
        var speakerActor = ResolveDialogueActor(node);
        if (speakerActor is not null)
            PlayDialoguePerformance(node, speakerActor);

        dialoguePanel.Visible = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (node.Kind == "reply")
            lastDialogueSpeaker = "PLAYER";
        else if (node.Speaker.Equals("end_trask", StringComparison.OrdinalIgnoreCase))
            lastDialogueSpeaker = "TRASK ULGO";
        else if (!string.IsNullOrWhiteSpace(node.Speaker))
            lastDialogueSpeaker = node.Speaker.ToUpperInvariant();
        dialogueSpeaker.Text = lastDialogueSpeaker;
        dialogueText.Text = node.Text;
        foreach (var child in dialogueChoices.GetChildren())
            child.QueueFree();
        activeChoiceButtons.Clear();

        var choices = new List<(DialogueNode Node, string Target)>();
        foreach (var link in node.Links)
        {
            var target = ResolveVisibleNode(graph, link.Target, new HashSet<string>(), 0);
            if (target is not null)
                choices.Add((target, link.Target));
        }
        if (choices.Count == 0)
        {
            var close = CreateChoiceButton("Continue");
            close.Pressed += () =>
            {
                dialoguePanel.Visible = false;
                StopDialoguePerformance();
                RestoreGameplayCamera();
            };
            close.Disabled = dialogueVoice.Playing;
            dialogueChoices.AddChild(close);
            activeChoiceButtons.Add(close);
            return;
        }
        foreach (var choice in choices)
        {
            var label = choice.Node.Kind == "reply" ? choice.Node.Text : "Continue";
            var button = CreateChoiceButton(label);
            button.Disabled = dialogueVoice.Playing;
            var targetKey = choice.Target;
            button.Pressed += () => FollowDialogueChoice(graph, targetKey);
            dialogueChoices.AddChild(button);
            activeChoiceButtons.Add(button);
        }
    }

    private void FollowDialogueChoice(DialogueGraph graph, string key)
    {
        var visible = ResolveVisibleNode(graph, key, new HashSet<string>(), 0);
        if (visible is null)
        {
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            RestoreGameplayCamera();
            return;
        }
        if (visible.Kind == "reply" && visible.Links.Count > 0)
            PresentDialogueNode(graph, visible.Links[0].Target, new HashSet<string>(), 0);
        else
            PresentDialogueNode(graph, key, new HashSet<string>(), 0);
    }

    private static DialogueNode? ResolveVisibleNode(DialogueGraph graph, string key,
        HashSet<string> visited, int depth)
    {
        if (depth > 32 || !visited.Add(key) || !graph.Nodes.TryGetValue(key, out var node)) return null;
        if (!string.IsNullOrWhiteSpace(node.Text)) return node;
        return node.Links.Count > 0
            ? ResolveVisibleNode(graph, node.Links[0].Target, visited, depth + 1)
            : null;
    }

    private static Button CreateChoiceButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, 34)
        };
        button.AddThemeFontSizeOverride("font_size", 16);
        return button;
    }

    private void AddCreatureMarkers(IEnumerable<CreatureRecord> creatures)
    {
        foreach (var creature in creatures)
        {
            if (!string.IsNullOrWhiteSpace(creature.Glb)) continue;
            var isTrask = creature.Template.Equals("end_trask", StringComparison.OrdinalIgnoreCase);
            var isCarth = creature.Template.StartsWith("p_carth", StringComparison.OrdinalIgnoreCase);
            var material = new StandardMaterial3D
            {
                AlbedoColor = isTrask
                    ? new Color(0.2f, 0.95f, 0.45f, 0.9f)
                    : isCarth
                        ? new Color(0.2f, 0.7f, 1.0f, 0.9f)
                        : new Color(0.95f, 0.22f, 0.18f, 0.55f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha
            };
            var marker = new MeshInstance3D
            {
                Name = $"Authored_{creature.Template}",
                Mesh = new CapsuleMesh { Radius = 0.32f, Height = 1.7f },
                MaterialOverride = material,
                Position = ToGodot(creature.Position) + Vector3.Up * 0.85f
            };
            AddChild(marker);
        }
    }

    private int LoadPlayerModel(PlayerRecord source, CameraStyleRecord cameraStyle,
        string manifestDirectory)
    {
        if (source.Schema != "nikami-aurora-kotor-player-v1" ||
            string.IsNullOrWhiteSpace(source.Glb))
            throw new InvalidDataException("Player manifest is missing or unsupported");
        var path = Path.GetFullPath(Path.Combine(manifestDirectory,
            source.Glb.Replace('/', Path.DirectorySeparatorChar)));
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D model)
            throw new InvalidDataException($"Godot could not import player model: {path}");
        model.Name = "PlayerModel";
        playerBody.AddChild(model);
        playerModel = model;
        playerAnimationPlayer = FindDescendant<AnimationPlayer>(model)
            ?? throw new InvalidDataException("Player model has no animation player");
        foreach (var animationName in playerAnimationPlayer.GetAnimationList())
        {
            var animation = playerAnimationPlayer.GetAnimation(animationName);
            if (animation is not null)
                animation.LoopMode = Animation.LoopModeEnum.Linear;
        }
        var walkName = FindAnimationName(playerAnimationPlayer, "walk");
        var runName = FindAnimationName(playerAnimationPlayer, "run");
        var walkAnimation = playerAnimationPlayer.GetAnimation(walkName);
        var runAnimation = playerAnimationPlayer.GetAnimation(runName);
        if (walkAnimation is null || runAnimation is null)
            throw new InvalidDataException("Player movement animations are missing");
        playerWalkSpeed = source.WalkDistance / (float)walkAnimation.GetLength();
        playerRunSpeed = source.RunDistance / (float)runAnimation.GetLength();
        cameraPivot.Position = source.CameraOffset is { Count: >= 3 }
            ? ToGodot(source.CameraOffset)
            : Vector3.Up * source.Height;
        xrOrigin.Position = cameraPivot.Position;
        var cameraDistance = Math.Max(0.1f, cameraStyle.Distance);
        var cameraHeight = cameraStyle.Height;
        cameraArm.SpringLength = Mathf.Sqrt(
            cameraDistance * cameraDistance + cameraHeight * cameraHeight);
        pitch = -Mathf.Atan2(cameraHeight, cameraDistance);
        cameraPivot.Rotation = new Vector3(pitch, 0, 0);
        PlayPlayerAnimation("pause1");
        GD.Print($"NIKAMI_AURORA_PLAYER status=ready appearance={source.AppearanceId} " +
                 $"label={source.AppearanceLabel} body={source.BodyModel} head={source.HeadModel} " +
                 $"skins={source.Animation.SkinCount} animations={string.Join(',', source.Animation.Animations)} " +
                 $"walkSpeed={playerWalkSpeed:F3} runSpeed={playerRunSpeed:F3} " +
                 $"cameraDistance={cameraDistance:F3} cameraHeight={cameraHeight:F3} " +
                 $"sourcePitch={cameraStyle.PitchDegrees:F3}");
        return 1;
    }

    private static StringName FindAnimationName(AnimationPlayer player, string requested)
    {
        var match = player.GetAnimationList().FirstOrDefault(name =>
            name.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            name.ToString().EndsWith('/' + requested, StringComparison.OrdinalIgnoreCase));
        if (match == default)
            throw new InvalidDataException($"Animation is missing: {requested}");
        return match;
    }

    private void PlayPlayerAnimation(string requested)
    {
        if (playerAnimationPlayer is null ||
            currentPlayerAnimation.Equals(requested, StringComparison.OrdinalIgnoreCase))
            return;
        var match = FindAnimationName(playerAnimationPlayer, requested);
        playerAnimationPlayer.Play(match, customBlend: 0.12);
        currentPlayerAnimation = requested;
        GD.Print($"NIKAMI_AURORA_PLAYER_ANIMATION status=playing animation={match}");
    }

    private int LoadActorModels(IEnumerable<CreatureRecord> creatures, string manifestDirectory)
    {
        actorModels.Clear();
        actorAnimations.Clear();
        actorTalkOffsets.Clear();
        actorLipRigs.Clear();
        var loaded = 0;
        foreach (var creature in creatures)
        {
            if (string.IsNullOrWhiteSpace(creature.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                creature.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D actor)
                throw new InvalidDataException($"Godot could not import actor {creature.Template}: {path}");
            actor.Name = $"Actor_{creature.Template}";
            actor.Position = ToGodot(creature.Position);
            // PyKotor's bearing zero faces native +Y. KOTOR_TO_GODOT maps that
            // axis to Godot -Z, so the yaw sign is preserved.
            actor.Rotation = new Vector3(0, creature.Bearing, 0);
            AddChild(actor);
            actorModels[creature.Template] = actor;
            if (creature.TalkOffset is { Count: >= 3 })
                actorTalkOffsets[creature.Template] = ToGodot(creature.TalkOffset);
            var animationPlayer = FindDescendant<AnimationPlayer>(actor);
            if (animationPlayer is not null)
            {
                actorAnimations[creature.Template] = animationPlayer;
                foreach (var animationName in animationPlayer.GetAnimationList())
                {
                    var animation = animationPlayer.GetAnimation(animationName);
                    if (animation is not null)
                        animation.LoopMode = Animation.LoopModeEnum.Linear;
                }
                PlayActorAnimation(creature.Template, "pause1");
                GD.Print($"NIKAMI_AURORA_ACTOR_ANIMATION status=ready actor={creature.Template} " +
                         $"tracks={string.Join(',', animationPlayer.GetAnimationList())}");
                var lipRig = BuildLipRig(actor, animationPlayer);
                if (lipRig is not null)
                {
                    actorLipRigs[creature.Template] = lipRig;
                    GD.Print($"NIKAMI_AURORA_LIP_RIG status=ready actor={creature.Template} " +
                             $"bones={lipRig.Tracks.Count} tracks={lipRig.TrackCount} " +
                             $"shapes=16 duration={lipRig.Animation.GetLength():F3}");
                }
            }
            loaded++;
        }
        return loaded;
    }

    private int LoadDoorModels(IEnumerable<DoorRecord> doors, string manifestDirectory)
    {
        interactiveDoors.Clear();
        foreach (var door in doors)
        {
            if (string.IsNullOrWhiteSpace(door.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                door.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException($"Godot could not import door {door.Tag}: {path}");
            model.Name = $"Door_{door.Tag}";
            model.Position = ToGodot(door.Position);
            model.Rotation = new Vector3(0, door.Bearing, 0);
            AddChild(model);
            interactiveDoors.Add(new InteractiveDoor(door, model, model.Position));
            GD.Print($"NIKAMI_AURORA_DOOR status=ready tag={door.Tag} model={door.Model} " +
                     $"conversation={door.Conversation} nativeOnOpen={door.OnOpen}");
        }
        return interactiveDoors.Count;
    }

    private int LoadPlaceableModels(IEnumerable<PlaceableRecord> placeables, string manifestDirectory)
    {
        interactivePlaceables.Clear();
        foreach (var placeable in placeables)
        {
            if (string.IsNullOrWhiteSpace(placeable.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                placeable.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException(
                    $"Godot could not import placeable {placeable.Tag}: {path}");
            model.Name = $"Placeable_{placeable.Tag}";
            model.Position = ToGodot(placeable.Position);
            model.Rotation = new Vector3(0, placeable.Bearing, 0);
            AddChild(model);
            interactivePlaceables.Add(new InteractivePlaceable(placeable, model));
            GD.Print($"NIKAMI_AURORA_PLACEABLE status=ready tag={placeable.Tag} " +
                     $"model={placeable.Model} nativeOnInventory={placeable.OnInventory}");
        }
        return interactivePlaceables.Count;
    }

    private void UpdateInteractionPrompt()
    {
        if (dialoguePanel.Visible)
        {
            interactionPrompt.Visible = false;
            return;
        }
        var placeable = NearestPlaceable(2.6f);
        if (placeable is not null)
        {
            interactionPrompt.Visible = true;
            interactionPrompt.Text = placeable.Opened
                ? "LOCKER OPENED"
                : "E  OPEN FOOTLOCKER";
            return;
        }
        var door = NearestDoor(2.6f);
        interactionPrompt.Visible = door is not null;
        if (door is not null)
            interactionPrompt.Text = door.Open ? "E  CLOSE LOCKDOWN DOOR" : "E  OPEN LOCKDOWN DOOR";
    }

    private InteractivePlaceable? NearestPlaceable(float maximumDistance)
    {
        InteractivePlaceable? nearest = null;
        var best = maximumDistance;
        foreach (var placeable in interactivePlaceables)
        {
            var delta = placeable.Model.Position - playerBody.GlobalPosition;
            delta.Y = 0;
            var distance = delta.Length();
            if (distance >= best) continue;
            best = distance;
            nearest = placeable;
        }
        return nearest;
    }

    private void UsePlaceable(InteractivePlaceable placeable)
    {
        if (placeable.Opened)
        {
            GD.Print($"NIKAMI_AURORA_PLACEABLE status=already-open tag={placeable.Source.Tag}");
            return;
        }
        placeable.Opened = true;
        GD.Print($"NIKAMI_AURORA_PLACEABLE status=opened tag={placeable.Source.Tag} " +
                 $"model={placeable.Source.Model} nativeOnInventory={placeable.Source.OnInventory}");
        ExecuteScript(placeable.Source.OnInventory);
    }

    private InteractiveDoor? NearestDoor(float maximumDistance)
    {
        InteractiveDoor? nearest = null;
        var best = maximumDistance;
        foreach (var door in interactiveDoors)
        {
            var delta = door.ClosedPosition - playerBody.GlobalPosition;
            delta.Y = 0;
            var distance = delta.Length();
            if (distance >= best) continue;
            best = distance;
            nearest = door;
        }
        return nearest;
    }

    private void ToggleDoor(InteractiveDoor door)
    {
        door.Open = !door.Open;
        var destination = door.Open ? door.ClosedPosition + Vector3.Up * 2.8f : door.ClosedPosition;
        CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut)
            .TweenProperty(door.Model, "position", destination, 0.7);
        GD.Print($"NIKAMI_AURORA_DOOR status={(door.Open ? "opened" : "closed")} " +
                 $"tag={door.Source.Tag} model={door.Source.Model} conversation={door.Source.Conversation} " +
                 $"nativeOnOpen={door.Source.OnOpen}");
        if (door.Open)
            ExecuteScript(door.Source.OnOpen);
    }

    private void ExecuteScript(string? resref)
    {
        if (string.IsNullOrWhiteSpace(resref)) return;
        if (!scriptContracts.TryGetValue(resref, out var contract))
        {
            if (reportedUnsupportedScripts.Add(resref))
                GD.Print($"NIKAMI_AURORA_NCS status=unsupported script={resref}");
            return;
        }
        switch (contract.Kind)
        {
            case "dialogue-open-door":
                {
                    var door = interactiveDoors.FirstOrDefault(candidate =>
                        candidate.Source.Tag.Equals(contract.DoorTag, StringComparison.OrdinalIgnoreCase));
                    if (door is null)
                        throw new InvalidDataException(
                            $"Script {resref} could not resolve door tag {contract.DoorTag}");
                    if (!door.Open)
                        ToggleDoor(door);
                    GD.Print($"NIKAMI_AURORA_NCS status=executed script={resref} kind={contract.Kind} " +
                             $"door={door.Source.Tag} pause={contract.PauseConversation} " +
                             $"moveTarget={contract.MoveTargetTag} run={contract.MoveRun} " +
                             $"range={contract.MoveRange:F3} resume={contract.ResumeConversation}");
                    break;
                }
            case "plot-xp-if-player-xp":
                {
                    var required = contract.RequiredPlayerXp
                        ?? throw new InvalidDataException($"Script {resref} has no XP precondition");
                    var awarded = contract.AwardedXp
                        ?? throw new InvalidDataException($"Script {resref} has no XP award");
                    if (playerExperience == required)
                    {
                        var before = playerExperience;
                        playerExperience += awarded;
                        GD.Print($"NIKAMI_AURORA_NCS status=executed script={resref} kind={contract.Kind} " +
                                 $"plot={contract.PlotLabel} percentage={contract.PlotPercentage} " +
                                 $"base={contract.PlotBaseXp} awarded={awarded} xp={before}->{playerExperience}");
                    }
                    else
                    {
                        GD.Print($"NIKAMI_AURORA_NCS status=skipped script={resref} kind={contract.Kind} " +
                                 $"requiredXp={required} actualXp={playerExperience}");
                    }
                    break;
                }
            default:
                throw new InvalidDataException(
                    $"Unsupported script contract kind {contract.Kind} for {resref}");
        }
    }

    private int LoadAuthoredLights(IEnumerable<RoomRecord> rooms, AreaLightingRecord lighting)
    {
        var loaded = 0;
        foreach (var room in rooms)
        {
            if (room.Lights is null) continue;
            foreach (var source in room.Lights)
            {
                // Odyssey treats radius >= 100 as directional. No such light is
                // present in the Endar Spire opening; keep that separate mapping
                // out of this point-light path.
                if (source.Radius <= 0 || source.Radius >= 100 || source.Multiplier <= 0 ||
                    source.AmbientOnly)
                    continue;
                var light = new OmniLight3D
                {
                    Name = $"SourceLight_{room.Model}_{source.Name}",
                    Position = ToGodotWithOffset(source.Position, room.Position),
                    LightColor = ToColor(source.Color),
                    LightEnergy = source.Multiplier,
                    LightSpecular = 0.0f,
                    OmniRange = source.Radius,
                    OmniAttenuation = 1.0f,
                    ShadowEnabled = lighting.Shadows && source.Shadow
                };
                AddChild(light);
                loaded++;
            }
        }
        return loaded;
    }

    private static void ConfigureStaticRoomMaterials(Node node)
    {
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source) continue;
                if (source.AlbedoTexture is not null && source.EmissionTexture is not null)
                {
                    var lightmapped = new ShaderMaterial { Shader = OdysseyLightmapShader };
                    lightmapped.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    lightmapped.SetShaderParameter("lightmap_texture", source.EmissionTexture);
                    instance.SetSurfaceOverrideMaterial(surface, lightmapped);
                    continue;
                }
                var material = (BaseMaterial3D)source.Duplicate();
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Metallic = 0;
                material.Roughness = 1;
                instance.SetSurfaceOverrideMaterial(surface, material);
            }
        }
        foreach (var child in node.GetChildren())
            ConfigureStaticRoomMaterials(child);
    }

    private void BuildNavigation(IEnumerable<RoomRecord> rooms)
    {
        navigationTriangles.Clear();
        foreach (var room in rooms)
        {
            if (room.WalkmeshTriangles is null) continue;
            foreach (var triangle in room.WalkmeshTriangles)
            {
                if (triangle.Count != 3) continue;
                navigationTriangles.Add(new NavigationTriangle(
                    ToGodotWithOffset(triangle[0], room.Position),
                    ToGodotWithOffset(triangle[1], room.Position),
                    ToGodotWithOffset(triangle[2], room.Position)));
            }
        }
    }

    private bool MovePlayer(Vector3 displacement)
    {
        if (navigationTriangles.Count == 0) return false;
        var candidate = playerBody.GlobalPosition + new Vector3(displacement.X, 0, displacement.Z);
        foreach (var door in interactiveDoors)
        {
            if (door.Open) continue;
            var obstruction = candidate - door.ClosedPosition;
            obstruction.Y = 0;
            if (obstruction.LengthSquared() < 0.65f * 0.65f) return false;
        }
        if (!TryProjectToWalkmesh(candidate, out var ground)) return false;
        candidate.Y = ground;
        playerBody.GlobalPosition = candidate;
        return true;
    }

    private bool TryProjectToWalkmesh(Vector3 position, out float ground)
    {
        ground = 0;
        var bestDistance = float.PositiveInfinity;
        var found = false;
        foreach (var triangle in navigationTriangles)
        {
            var a = new Vector2(triangle.A.X, triangle.A.Z);
            var b = new Vector2(triangle.B.X, triangle.B.Z);
            var c = new Vector2(triangle.C.X, triangle.C.Z);
            var p = new Vector2(position.X, position.Z);
            var denominator = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Mathf.Abs(denominator) < 0.000001f) continue;
            var wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / denominator;
            var wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / denominator;
            var wc = 1.0f - wa - wb;
            if (wa < -0.002f || wb < -0.002f || wc < -0.002f) continue;
            var candidateGround = wa * triangle.A.Y + wb * triangle.B.Y + wc * triangle.C.Y;
            var distance = Mathf.Abs(candidateGround - position.Y);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            ground = candidateGround;
            found = true;
        }
        return found;
    }

    private static Vector3 ToGodot(IReadOnlyList<float> source) =>
        new(source[0], source[2], -source[1]);

    private static Color ToColor(IReadOnlyList<float> source) =>
        new(source[0], source[1], source[2]);

    private static Vector3 ToGodotWithOffset(IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        ToGodot(new[] { source[0] + offset[0], source[1] + offset[1], source[2] + offset[2] });

    private static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record ModuleManifest(
        string Schema,
        string Module,
        EntryRecord Entry,
        TargetRecord Target,
        AreaLightingRecord Lighting,
        CameraStyleRecord CameraStyle,
        PlayerRecord Player,
        IReadOnlyList<RoomRecord> Rooms,
        IReadOnlyList<CreatureRecord> Creatures,
        IReadOnlyList<DoorRecord> Doors,
        IReadOnlyList<PlaceableRecord> Placeables,
        IReadOnlyList<CameraRecord> Cameras,
        IReadOnlyList<ScriptContractRecord> ScriptContracts,
        CountRecord Counts);

    private sealed record EntryRecord(IReadOnlyList<float> Position, float DirectionRadians);
    private sealed record TargetRecord(string ExecutableSha256);
    private sealed record AreaLightingRecord(IReadOnlyList<float> DynamicAmbient, bool Shadows,
        int ShadowOpacity, string SourceSha256);
    private sealed record CameraStyleRecord(int Id, float ViewAngle, float Distance,
        float PitchDegrees, float Height, string SourceSha256);
    private sealed record PlayerRecord(string Schema, string Glb, int PortraitId,
        int AppearanceId, string AppearanceLabel, string BodyModel, string BodyTexture,
        int HeadIndex, string HeadModel, float Height, float WalkDistance, float RunDistance,
        IReadOnlyList<float>? TalkOffset, IReadOnlyList<float>? CameraOffset,
        PlayerAnimationRecord Animation);
    private sealed record PlayerAnimationRecord(int MeshCount, int VertexCount, int TriangleCount,
        int SkinCount, int HeadSkinCount, IReadOnlyList<string> Animations);
    private sealed record RoomRecord(string Model, string? Glb, IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<float>>>? WalkmeshTriangles,
        IReadOnlyList<LightRecord>? Lights);
    private sealed record LightRecord(string Name, IReadOnlyList<float> Position,
        IReadOnlyList<float> Color, float Radius, float Multiplier, bool AmbientOnly,
        int DynamicType, bool AffectDynamic, bool Shadow, int Priority);
    private sealed record CameraRecord(int Id, IReadOnlyList<float> Position, float Height, float Fov,
        float PitchDegrees, IReadOnlyList<float> OrientationWxyz, IReadOnlyList<float> Forward,
        IReadOnlyList<float> Up);
    private sealed record CreatureRecord(string Template, IReadOnlyList<float> Position, float Bearing,
        string? Glb, string? Conversation, DialogueReference? Dialogue,
        IReadOnlyList<float>? TalkOffset);
    private sealed record DoorRecord(string Template, string Tag, IReadOnlyList<float> Position, float Bearing,
        string LinkedToModule, string? Glb, string? Model, string? Conversation, string? OnOpen,
        bool Locked, bool KeyRequired);
    private sealed record PlaceableRecord(string Template, string Tag,
        IReadOnlyList<float> Position, float Bearing, string? Glb, string? Model,
        string? OnInventory, bool Locked, bool Useable, bool HasInventory, int AnimationState);
    private sealed record DialogueReference(string Path, string SourceSha256, int StarterCount,
        int NodeCount, int OpeningStarter);
    private sealed record DialogueGraph(string Schema, int OpeningStarter,
        IReadOnlyList<DialogueLink> Starters, IReadOnlyDictionary<string, DialogueNode> Nodes);
    private sealed record DialogueNode(string Kind, string Text, string Speaker, string Sound,
        string Script1, string Script2,
        int CameraAngle, int? CameraId, float? CameraFov, float? CameraHeight,
        IReadOnlyList<DialogueAnimation> Animations, DialogueMedia? Media,
        IReadOnlyList<DialogueLink> Links);
    private sealed record DialogueAnimation(int AnimationId, string Participant);
    private sealed record DialogueMedia(string AudioPath, string AudioFormat, string AudioSha256,
        int AudioByteCount, string? LipPath, string? LipSourceSha256, float? LipLength,
        int? LipFrameCount);
    private sealed record LipTrack(string Schema, string Resref, string SourceSha256, float Length,
        IReadOnlyList<LipFrame> Frames);
    private sealed record LipFrame(float Time, int Shape);
    private sealed record ScriptContractRecord(string Schema, string Resref, string Kind,
        string SourceSha256, int InstructionCount, string? DoorTag, int? RequiredPlayerXp,
        string? PlotLabel, int? PlotPercentage, int? PlotBaseXp, int? AwardedXp,
        bool? PauseConversation, string? MoveTargetTag, bool? MoveRun, float? MoveRange,
        bool? ResumeConversation);
    private sealed class LipRig(KotorLipModifier modifier, Animation animation,
        IReadOnlyList<KotorLipModifier.TrackBinding> tracks)
    {
        public KotorLipModifier Modifier { get; } = modifier;
        public Animation Animation { get; } = animation;
        public IReadOnlyList<KotorLipModifier.TrackBinding> Tracks { get; } = tracks;
        public int TrackCount => Tracks.Sum(track =>
            (track.PositionTrack >= 0 ? 1 : 0) + (track.RotationTrack >= 0 ? 1 : 0));
    }
    private sealed record DialogueLink(string Target, string Condition1, bool Condition1Not,
        string Condition2, bool Condition2Not, int Logic);
    private sealed record CountRecord(int Rooms, int Creatures, int Doors, int Waypoints, int Cameras,
        int Placeables, int Triggers, int WalkmeshTriangles, int AuthoredLights);
    private readonly record struct NavigationTriangle(Vector3 A, Vector3 B, Vector3 C);
    private sealed class InteractiveDoor(DoorRecord source, Node3D model, Vector3 closedPosition)
    {
        public DoorRecord Source { get; } = source;
        public Node3D Model { get; } = model;
        public Vector3 ClosedPosition { get; } = closedPosition;
        public bool Open { get; set; }
    }
    private sealed class InteractivePlaceable(PlaceableRecord source, Node3D model)
    {
        public PlaceableRecord Source { get; } = source;
        public Node3D Model { get; } = model;
        public bool Opened { get; set; }
    }
}
