using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Profiles.Kotor;
using NumericsVector3 = System.Numerics.Vector3;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot : Node3D
{
    private const float DefaultGameplayFieldOfView = 72.0f;
    private static readonly Shader OdysseyLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, depth_draw_opaque;
            uniform sampler2D albedo_texture : source_color, filter_linear_mipmap_anisotropic;
            uniform sampler2D lightmap_texture : source_color, filter_linear_mipmap_anisotropic;
            void fragment() {
                vec4 base = texture(albedo_texture, UV);
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                // Current room GLBs declare these surfaces opaque. Writing
                // ALPHA at all would move them into Godot's transparent pass
                // and disable the depth behavior solid furniture requires.
                ALBEDO = base.rgb * min(vec3(1.0), lightmap);
            }
            """
    };
    private CharacterBody3D playerBody = null!;
    private Node3D cameraPivot = null!;
    private SpringArm3D cameraArm = null!;
    private Camera3D camera = null!;
    private XROrigin3D xrOrigin = null!;
    private XRCamera3D xrCamera = null!;
    private XRController3D xrLeftHand = null!;
    private XRController3D xrRightHand = null!;
    private Node3D xrLeftModelContainer = null!;
    private Node3D xrRightModelContainer = null!;
    private OpenXRRenderModelManager? xrLeftModelManager;
    private OpenXRRenderModelManager? xrRightModelManager;
    private MeshInstance3D xrLeftFallback = null!;
    private MeshInstance3D xrRightFallback = null!;
    private Node3D? xrLeftVendorModel;
    private Node3D? xrRightVendorModel;
    private XRController3D? activeInteractionController;
    private bool xrActive;
    private Node3D? playerModel;
    private AnimationPlayer? playerAnimationPlayer;
    private PlayerEquipmentVariantRecord? openingEquipmentVariant;
    private string playerManifestDirectory = "";
    private string currentPlayerAnimation = "";
    private string forcedPlayerAnimation = "";
    private float playerWalkSpeed = 1.7f;
    private float playerRunSpeed = 5.4f;
    private KotorMovementSimulation? movementSimulation;
    private NumericsVector3 simulationPlayerPosition;
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
    private Label3D? worldNotice;
    private readonly List<Button> activeChoiceButtons = [];
    private readonly List<NavigationTriangle> navigationTriangles = [];
    private readonly List<InteractiveDoor> interactiveDoors = [];
    private readonly List<MaterializedPlaceable> materializedPlaceables = [];
    private readonly Dictionary<string, AnimationPlayer> actorAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> actorModels =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Vector3> actorTalkOffsets =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LipRig> actorLipRigs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CameraRecord> dialogueCameras = [];
    private readonly HashSet<string> reportedUnsupportedScripts = new(StringComparer.OrdinalIgnoreCase);
    private KotorGameplaySimulation? gameplaySimulation;
    private int capturedFrames;
    private int captureTargetFrame = 60;
    private int readyFrames;
    private bool moduleReady;
    private bool automatedChoiceApplied;
    private bool automatedMoveApplied;
    private bool automatedDoorApplied;
    private bool automatedLockerApplied;
    private bool automatedTutorialXpChain;
    private bool automatedEquipmentApplied;
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
        var rightIntent = 0.0f;
        var forwardIntent = 0.0f;
        if (Input.IsKeyPressed(Key.W)) forwardIntent += 1.0f;
        if (Input.IsKeyPressed(Key.S)) forwardIntent -= 1.0f;
        if (Input.IsKeyPressed(Key.A)) rightIntent -= 1.0f;
        if (Input.IsKeyPressed(Key.D)) rightIntent += 1.0f;
        if (xrActive)
        {
            var stick = xrLeftHand.GetVector2("primary");
            rightIntent += stick.X;
            forwardIntent += stick.Y;
            UpdateControllerModelFallbacks();
        }
        var sprinting = Input.IsKeyPressed(Key.Shift) ||
                        (xrActive && xrLeftHand.IsButtonPressed("primary_click"));
        var intent = KotorMovementIntent.FromAxes(rightIntent, forwardIntent, sprinting);
        var movementResult = !dialoguePanel.Visible
            ? StepPlayer(intent, (float)delta)
            : new KotorMovementResult(
                simulationPlayerPosition, true, false, KotorLocomotionMode.Idle);
        var requestedAnimation = movementResult.Mode switch
        {
            KotorLocomotionMode.Walk => "walk",
            KotorLocomotionMode.Run => "run",
            _ => "pause1"
        };
        PlayPlayerAnimation(!string.IsNullOrWhiteSpace(forcedPlayerAnimation)
            ? forcedPlayerAnimation
            : requestedAnimation);
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
                if (!IsDoorOpen(interactiveDoors[0]))
                    ToggleDoor(interactiveDoors[0]);
                else
                    GD.Print($"NIKAMI_AURORA_DOOR status=already-open tag={interactiveDoors[0].Source.Tag}");
            }
            if (!automatedLockerApplied && readyFrames >= 30 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_LOCKER") == "1" &&
                materializedPlaceables.Count > 0)
            {
                automatedLockerApplied = true;
                UsePlaceable(materializedPlaceables[0]);
            }
            if (!automatedTutorialXpChain && readyFrames >= 30 &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_TUTORIAL_XP_CHAIN") == "1" &&
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
            if (!automatedEquipmentApplied && readyFrames >= 45 &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR") == "1")
            {
                automatedEquipmentApplied = true;
                EquipOpeningGear(null);
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
            if (readyFrames == 60 &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP") == "1")
                FramePlayerEquipmentCloseup();
            if (readyFrames == 60 &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP") == "1")
                FrameChairCloseup();
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
            HandleInteraction(null);
        }
        else if (inputEvent is InputEventKey equip && equip.Pressed && equip.Keycode == Key.Q)
        {
            EquipOpeningGear(null);
        }
    }

    private void OnXrButtonPressed(XRController3D controller, string name)
    {
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

    private void HandleInteraction(XRController3D? controller)
    {
        if (dialoguePanel.Visible) return;
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
        if (dialoguePanel.Visible) return;
        var variant = openingEquipmentVariant;
        if (variant is null || gameplaySimulation is null) return;
        var snapshot = gameplaySimulation.CaptureSnapshot();
        if (snapshot.Equipment.TryGetValue(KotorEquipmentSlot.Armor, out var armor) &&
            armor.Equals(variant.ArmorResref, StringComparison.OrdinalIgnoreCase) &&
            snapshot.Equipment.TryGetValue(KotorEquipmentSlot.RightHand, out var rightHand) &&
            rightHand.Equals(variant.RightHandResref, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print("NIKAMI_AURORA_EQUIPMENT status=already-equipped " +
                     $"armor={armor} rightHand={rightHand}");
            return;
        }
        if (!snapshot.PlayerInventory.ContainsKey(variant.ArmorResref) ||
            !snapshot.PlayerInventory.ContainsKey(variant.RightHandResref))
        {
            GD.Print("NIKAMI_AURORA_EQUIPMENT status=unavailable " +
                     $"armor={variant.ArmorResref} rightHand={variant.RightHandResref}");
            return;
        }

        activeInteractionController = controller;
        try
        {
            ApplyGameplayTransition(gameplaySimulation.EquipItems([
                new KotorEquipRequest(variant.ArmorResref, KotorEquipmentSlot.Armor),
                new KotorEquipRequest(variant.RightHandResref, KotorEquipmentSlot.RightHand)
            ]));
        }
        finally
        {
            activeInteractionController = null;
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
            var initialPlayerExperience = 0;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_PLAYER_XP"),
                    out var configuredPlayerXp))
                initialPlayerExperience = Math.Max(0, configuredPlayerXp);
            gameplaySimulation = CreateGameplaySimulation(manifest, initialPlayerExperience);
            GD.Print($"NIKAMI_AURORA_GAMEPLAY_STATE status=ready scripts={manifest.ScriptContracts.Count} " +
                     $"doors={manifest.Doors.Count} placeables={manifest.Placeables.Count} " +
                     $"xp={initialPlayerExperience}");
            ApplyAreaLighting(manifest.Lighting);
            var loadedRooms = 0;
            var lightmappedOpaqueMaterials = 0;
            var baseOpaqueMaterials = 0;
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
                var materialReport = ConfigureStaticRoomMaterials(imported);
                lightmappedOpaqueMaterials += materialReport.LightmappedOpaque;
                baseOpaqueMaterials += materialReport.BaseOpaque;
                AddChild(imported);
                loadedRooms++;
                details.Text = $"Rooms {loadedRooms}/{manifest.Rooms.Count}  •  {room.Model}";
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            }
            if (lightmappedOpaqueMaterials == 0 || baseOpaqueMaterials == 0)
                throw new InvalidDataException("Room opacity audit found no configured materials");
            GD.Print($"NIKAMI_AURORA_OPACITY status=pass policy=source-opaque " +
                     $"lightmapped={lightmappedOpaqueMaterials} base={baseOpaqueMaterials} " +
                     "alphaWrites=0 depthWrite=opaque");

            var authoredLights = LoadAuthoredLights(manifest.Rooms, manifest.Lighting);
            var materializedPlayer = LoadPlayerModel(
                manifest.Player, manifest.CameraStyle, manifestDirectory);
            var materializedActors = LoadActorModels(manifest.Creatures, manifestDirectory);
            AddCreatureMarkers(manifest.Creatures);
            var materializedDoors = LoadDoorModels(manifest.Doors, manifestDirectory);
            var materializedPlaceables = LoadPlaceableModels(manifest.Placeables, manifestDirectory);
            BuildNavigation(manifest.Rooms);
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
        xrLeftHand = new XRController3D
        {
            Name = "XRLeftHand",
            Tracker = "left_hand",
            Pose = "grip"
        };
        xrLeftHand.ButtonPressed += action => OnXrButtonPressed(xrLeftHand, action.ToString());
        xrOrigin.AddChild(xrLeftHand);
        xrRightHand = new XRController3D
        {
            Name = "XRRightHand",
            Tracker = "right_hand",
            Pose = "grip"
        };
        xrRightHand.ButtonPressed += action => OnXrButtonPressed(xrRightHand, action.ToString());
        xrOrigin.AddChild(xrRightHand);
        (xrLeftModelContainer, xrLeftFallback) =
            CreateControllerPresentation(xrLeftHand, true);
        (xrRightModelContainer, xrRightFallback) =
            CreateControllerPresentation(xrRightHand, false);
        GD.Print("NIKAMI_AURORA_OPENXR_MODELS status=configured " +
                 "portable=deferred vendor=dynamic fallback=left,right pose=grip");
    }

    private static (Node3D Container, MeshInstance3D Fallback)
        CreateControllerPresentation(XRController3D controller, bool left)
    {
        var container = new Node3D { Name = "ControllerModelContainer" };
        controller.AddChild(container);
        var material = new StandardMaterial3D
        {
            AlbedoColor = left
                ? new Color(0.18f, 0.55f, 0.95f)
                : new Color(0.95f, 0.38f, 0.18f),
            Metallic = 0.15f,
            Roughness = 0.55f
        };
        var fallback = new MeshInstance3D
        {
            Name = "ProceduralFallback",
            Mesh = new BoxMesh { Size = new Vector3(0.07f, 0.11f, 0.16f) },
            MaterialOverride = material
        };
        container.AddChild(fallback);
        return (container, fallback);
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
        xrLeftModelManager = CreatePortableRenderModel(xrLeftModelContainer, true);
        xrRightModelManager = CreatePortableRenderModel(xrRightModelContainer, false);
        xrLeftVendorModel = TryCreateMetaRenderModel(xrLeftModelContainer, true);
        xrRightVendorModel = TryCreateMetaRenderModel(xrRightModelContainer, false);
        GD.Print("NIKAMI_AURORA_OPENXR status=ready worldScale=1.000 " +
                 "authority=hmd-relative-to-game-camera");
        GD.Print($"NIKAMI_AURORA_OPENXR_MODELS status=ready portable=true " +
                 $"metaFb={xrLeftVendorModel is not null && xrRightVendorModel is not null} " +
                 "fallback=procedural");
    }

    private static OpenXRRenderModelManager CreatePortableRenderModel(
        Node3D container,
        bool left)
    {
        var manager = new OpenXRRenderModelManager
        {
            Name = "OpenXRRenderModelManager",
            Tracker = left
                ? OpenXRRenderModelManager.RenderModelTracker.LeftHand
                : OpenXRRenderModelManager.RenderModelTracker.RightHand,
            MakeLocalToPose = "grip"
        };
        container.AddChild(manager);
        return manager;
    }

    private static Node3D? TryCreateMetaRenderModel(Node3D container, bool left)
    {
        if (!ClassDB.ClassExists("OpenXRFbRenderModel") ||
            ClassDB.Instantiate("OpenXRFbRenderModel").AsGodotObject() is not Node3D model)
            return null;
        model.Name = "OpenXRFbRenderModel";
        model.Set("render_model_type", left ? 0 : 1);
        container.AddChild(model);
        return model;
    }

    private void UpdateControllerModelFallbacks()
    {
        xrLeftFallback.Visible = !HasLoadedControllerModel(
            xrLeftModelManager, xrLeftVendorModel);
        xrRightFallback.Visible = !HasLoadedControllerModel(
            xrRightModelManager, xrRightVendorModel);
    }

    private static bool HasLoadedControllerModel(
        OpenXRRenderModelManager? portable, Node3D? vendor) =>
        portable?.GetChildCount() > 0 || vendor?.GetChildCount() > 0;

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
            Text = "WASD move  •  mouse look  •  Shift sprint  •  E interact  •  " +
                   "Q equip opening gear  •  Esc release mouse",
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

    private void FramePlayerEquipmentCloseup()
    {
        if (playerModel is null) return;
        if (worldNotice is not null && GodotObject.IsInstanceValid(worldNotice))
        {
            worldNotice.QueueFree();
            worldNotice = null;
        }
        var target = playerModel.GlobalPosition + Vector3.Up * 1.0f;
        var forward = -playerModel.GlobalTransform.Basis.Z.Normalized();
        var right = playerModel.GlobalTransform.Basis.X.Normalized();
        var eye = target + forward * 1.05f + right * 0.75f + Vector3.Up * 0.05f;
        if (xrActive)
        {
            SetPresentationCameraBase(eye, target, Vector3.Up, 45.0f);
        }
        else
        {
            camera.Current = false;
            var inspectionCamera = new Camera3D
            {
                Name = "EquipmentInspectionCamera",
                Current = true,
                Near = 0.05f,
                Far = 1000.0f,
                Fov = 45.0f
            };
            AddChild(inspectionCamera);
            inspectionCamera.GlobalPosition = eye;
            inspectionCamera.LookAt(target, Vector3.Up);
        }
        GD.Print($"NIKAMI_AURORA_PLAYER_CAMERA status=active mode=equipment-closeup " +
                 $"fov=45.000 position={eye} xr={xrActive}");
    }

    private void FrameChairCloseup()
    {
        var chairs = materializedPlaceables.Where(placeable =>
                placeable.Source.Template.Equals(
                    "plc_chair2", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (chairs.Length != 3) return;
        if (worldNotice is not null && GodotObject.IsInstanceValid(worldNotice))
        {
            worldNotice.QueueFree();
            worldNotice = null;
        }
        var target = chairs.Aggregate(Vector3.Zero,
            (sum, chair) => sum + chair.Model.GlobalPosition) / chairs.Length +
                     Vector3.Up * 0.42f;
        var eye = target + Vector3.Back * 5.5f + Vector3.Up * 1.8f;
        camera.Current = false;
        var inspectionCamera = new Camera3D
        {
            Name = "ChairInspectionCamera",
            Current = true,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = 55.0f
        };
        AddChild(inspectionCamera);
        inspectionCamera.GlobalPosition = eye;
        inspectionCamera.LookAt(target, Vector3.Up);
        GD.Print($"NIKAMI_AURORA_PLACEABLE_CAMERA status=active mode=chair-closeup " +
                 $"count={chairs.Length} template=plc_chair2 " +
                 $"fov=55.000 position={eye}");
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
        playerManifestDirectory = manifestDirectory;
        openingEquipmentVariant = source.EquipmentVariants?.SingleOrDefault(variant =>
            variant.Id.Equals("opening-clothing-short-sword", StringComparison.OrdinalIgnoreCase));
        if (openingEquipmentVariant is not null &&
            openingEquipmentVariant.Schema != "nikami-aurora-kotor-player-equipment-v1")
            throw new InvalidDataException(
                $"Unsupported player equipment variant: {openingEquipmentVariant.Schema}");
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

    private int LoadDoorModels(IReadOnlyList<DoorRecord> doors, string manifestDirectory)
    {
        interactiveDoors.Clear();
        for (var index = 0; index < doors.Count; index++)
        {
            var door = doors[index];
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
            var instanceId = DoorInstanceId(index);
            interactiveDoors.Add(new InteractiveDoor(instanceId, door, model, model.Position));
            GD.Print($"NIKAMI_AURORA_DOOR status=ready id={instanceId} " +
                     $"tag={door.Tag} model={door.Model} " +
                     $"conversation={door.Conversation} nativeOnOpen={door.OnOpen}");
        }
        return interactiveDoors.Count;
    }

    private int LoadPlaceableModels(
        IReadOnlyList<PlaceableRecord> placeables,
        string manifestDirectory)
    {
        materializedPlaceables.Clear();
        var loaded = 0;
        for (var index = 0; index < placeables.Count; index++)
        {
            var placeable = placeables[index];
            if (string.IsNullOrWhiteSpace(placeable.Glb)) continue;
            var path = Path.GetFullPath(Path.Combine(manifestDirectory,
                placeable.Glb.Replace('/', Path.DirectorySeparatorChar)));
            var document = new GltfDocument();
            var state = new GltfState();
            if (document.AppendFromFile(path, state) != Error.Ok ||
                document.GenerateScene(state) is not Node3D model)
                throw new InvalidDataException(
                    $"Godot could not import placeable {placeable.Tag}: {path}");
            var instanceId = PlaceableInstanceId(index);
            model.Name = $"Placeable_{instanceId}_{placeable.Template}";
            model.Position = ToGodot(placeable.Position);
            model.Rotation = new Vector3(0, placeable.Bearing, 0);
            AddChild(model);
            materializedPlaceables.Add(new MaterializedPlaceable(instanceId, placeable, model));
            loaded++;
            GD.Print($"NIKAMI_AURORA_PLACEABLE status=ready id={instanceId} tag={placeable.Tag} " +
                     $"template={placeable.Template} model={placeable.Model} " +
                     $"static={placeable.Static} useable={placeable.Useable} " +
                     $"nativeOnInventory={placeable.OnInventory}");
        }
        return loaded;
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
            interactionPrompt.Text = IsPlaceableOpened(placeable)
                ? "LOCKER OPENED"
                : "E  OPEN FOOTLOCKER";
            return;
        }
        var door = NearestDoor(2.6f);
        interactionPrompt.Visible = door is not null;
        if (door is not null)
            interactionPrompt.Text = IsDoorOpen(door)
                ? "E  CLOSE LOCKDOWN DOOR"
                : "E  OPEN LOCKDOWN DOOR";
    }

    private MaterializedPlaceable? NearestPlaceable(float maximumDistance)
    {
        MaterializedPlaceable? nearest = null;
        var best = maximumDistance;
        foreach (var placeable in materializedPlaceables)
        {
            if (!placeable.Source.Useable) continue;
            var delta = placeable.Model.Position - playerBody.GlobalPosition;
            delta.Y = 0;
            var distance = delta.Length();
            if (distance >= best) continue;
            best = distance;
            nearest = placeable;
        }
        return nearest;
    }

    private void UsePlaceable(MaterializedPlaceable placeable)
    {
        ApplyGameplayTransition(
            RequireGameplaySimulation().UsePlaceable(placeable.InstanceId));
    }

    private KotorGameplaySimulation RequireGameplaySimulation() =>
        gameplaySimulation ?? throw new InvalidOperationException("KOTOR gameplay state is not initialized");

    private bool IsDoorOpen(InteractiveDoor door) =>
        gameplaySimulation?.IsDoorOpen(door.InstanceId) ?? false;

    private bool IsPlaceableOpened(MaterializedPlaceable placeable) =>
        gameplaySimulation?.IsPlaceableOpened(placeable.InstanceId) ?? false;

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
        ApplyGameplayTransition(
            RequireGameplaySimulation().ToggleDoor(door.InstanceId));
    }

    private void ExecuteScript(string? resref)
    {
        ApplyGameplayTransition(
            RequireGameplaySimulation().ExecuteScript(resref));
    }

    private void ApplyGameplayTransition(KotorGameplayTransition transition)
    {
        var equipmentChanged = false;
        foreach (var gameplayEvent in transition.Events)
        {
            switch (gameplayEvent)
            {
                case KotorDoorStateChanged doorState:
                    PresentDoorState(doorState);
                    break;
                case KotorPlaceableOpened placeableOpened:
                    PresentPlaceableOpened(placeableOpened.Placeable);
                    break;
                case KotorPlaceableAlreadyOpened alreadyOpened:
                    GD.Print($"NIKAMI_AURORA_PLACEABLE status=already-open " +
                             $"id={alreadyOpened.Placeable.InstanceId} " +
                             $"tag={alreadyOpened.Placeable.Tag}");
                    break;
                case KotorItemsTransferred transferred:
                    PresentItemsTransferred(transferred);
                    break;
                case KotorEquipmentChanged equipped:
                    equipmentChanged = true;
                    GD.Print($"NIKAMI_AURORA_EQUIPMENT status=equipped " +
                             $"slot={equipped.Slot} item={equipped.Item.Resref} " +
                             $"previous={equipped.PreviousResref}");
                    break;
                case KotorExperienceAwarded experience:
                    PresentExperienceAward(experience);
                    break;
                case KotorScriptExecuted executed:
                    PresentScriptExecution(executed.Contract);
                    break;
                case KotorScriptSkipped skipped:
                    GD.Print($"NIKAMI_AURORA_NCS status=skipped script={skipped.Contract.Resref} " +
                             $"kind={skipped.Contract.KindName} " +
                             $"requiredXp={skipped.Contract.RequiredPlayerExperience} " +
                             $"actualXp={skipped.ActualPlayerExperience}");
                    break;
                case KotorScriptUnsupported unsupported:
                    if (reportedUnsupportedScripts.Add(unsupported.Resref))
                        GD.Print($"NIKAMI_AURORA_NCS status=unsupported script={unsupported.Resref}");
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported KOTOR gameplay event: {gameplayEvent.GetType().Name}");
            }
        }
        if (equipmentChanged)
            PresentEquipment(transition.After);
    }

    private void PresentDoorState(KotorDoorStateChanged state)
    {
        var door = interactiveDoors.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(state.Door.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve door instance {state.Door.InstanceId}");
        var destination = state.Open
            ? door.ClosedPosition + Vector3.Up * 2.8f
            : door.ClosedPosition;
        CreateTween().SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut)
            .TweenProperty(door.Model, "position", destination, 0.7);
        GD.Print($"NIKAMI_AURORA_DOOR status={(state.Open ? "opened" : "closed")} " +
                 $"id={door.InstanceId} tag={door.Source.Tag} model={door.Source.Model} " +
                 $"conversation={door.Source.Conversation} nativeOnOpen={door.Source.OnOpen}");
    }

    private void PresentPlaceableOpened(KotorPlaceableDefinition state)
    {
        var placeable = materializedPlaceables.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(state.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve placeable instance {state.InstanceId}");
        GD.Print($"NIKAMI_AURORA_PLACEABLE status=opened id={placeable.InstanceId} " +
                 $"tag={placeable.Source.Tag} " +
                 $"model={placeable.Source.Model} " +
                 $"nativeOnInventory={placeable.Source.OnInventory}");
    }

    private void PresentItemsTransferred(KotorItemsTransferred transferred)
    {
        var placeable = materializedPlaceables.FirstOrDefault(candidate =>
            candidate.InstanceId.Equals(
                transferred.Placeable.InstanceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException(
                $"Gameplay state could not resolve loot source {transferred.Placeable.InstanceId}");
        var summary = string.Join(", ", transferred.Items.Select(stack =>
            $"{stack.Quantity}x {stack.Item.DisplayName}"));
        GD.Print($"NIKAMI_AURORA_INVENTORY status=transferred " +
                 $"source={placeable.InstanceId} items={summary}");

        ShowWorldNotice("LOOT ACQUIRED", transferred.Items.Select(stack =>
            $"{stack.Quantity}x  {stack.Item.DisplayName}").Concat(
            ["Q / B-Y  EQUIP GEAR"]));

        if (xrActive)
            (activeInteractionController ?? xrRightHand)
                .TriggerHapticPulse("haptic", 0.0, 0.35, 0.08, 0.0);
    }

    private void ShowWorldNotice(string title, IEnumerable<string> lines)
    {
        if (worldNotice is not null && GodotObject.IsInstanceValid(worldNotice))
            worldNotice.QueueFree();
        var label = new Label3D
        {
            Name = "WorldNotice",
            Text = title + '\n' + string.Join("\n", lines),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
            DoubleSided = true,
            NoDepthTest = true,
            FixedSize = false,
            PixelSize = 0.002f,
            FontSize = 32,
            OutlineSize = 6,
            Modulate = new Color(0.45f, 0.88f, 1.0f),
            OutlineModulate = new Color(0.01f, 0.02f, 0.04f, 0.95f)
        };
        worldNotice = label;
        AddChild(label);
        var activeView = xrActive ? (Node3D)xrCamera : camera;
        var viewForward = -activeView.GlobalTransform.Basis.Z.Normalized();
        var viewRight = activeView.GlobalTransform.Basis.X.Normalized();
        label.GlobalPosition = activeView.GlobalPosition + viewForward * 1.8f +
                               viewRight * 0.48f + Vector3.Up * 0.1f;
        var tween = CreateTween();
        tween.TweenInterval(3.0);
        tween.TweenProperty(label, "modulate:a", 0.0f, 0.8);
        tween.TweenCallback(Callable.From(() =>
        {
            if (worldNotice == label)
                worldNotice = null;
            label.QueueFree();
        }));
    }

    private void PresentEquipment(KotorGameplaySnapshot snapshot)
    {
        var variant = openingEquipmentVariant
            ?? throw new InvalidDataException("Opening player equipment variant is unavailable");
        if (!snapshot.Equipment.TryGetValue(KotorEquipmentSlot.Armor, out var armor) ||
            !armor.Equals(variant.ArmorResref, StringComparison.OrdinalIgnoreCase) ||
            !snapshot.Equipment.TryGetValue(KotorEquipmentSlot.RightHand, out var rightHand) ||
            !rightHand.Equals(variant.RightHandResref, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("No player model matches the profile equipment snapshot");

        var path = Path.GetFullPath(Path.Combine(playerManifestDirectory,
            variant.Glb.Replace('/', Path.DirectorySeparatorChar)));
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D model)
            throw new InvalidDataException($"Godot could not import equipped player model: {path}");
        model.Name = "PlayerModelEquipped";
        var animationPlayer = FindDescendant<AnimationPlayer>(model)
            ?? throw new InvalidDataException("Equipped player model has no animation player");
        foreach (var animationName in animationPlayer.GetAnimationList())
        {
            var animation = animationPlayer.GetAnimation(animationName);
            if (animation is not null)
                animation.LoopMode = Animation.LoopModeEnum.Linear;
        }
        foreach (var expected in variant.Animation.Animations)
            _ = FindAnimationName(animationPlayer, expected);

        var requestedAnimation = string.IsNullOrWhiteSpace(currentPlayerAnimation)
            ? "pause1"
            : currentPlayerAnimation;
        if (playerModel is not null)
        {
            playerModel.Visible = false;
            playerModel.QueueFree();
        }
        playerBody.AddChild(model);
        playerModel = model;
        playerAnimationPlayer = animationPlayer;
        currentPlayerAnimation = "";
        if (variant.CameraOffset is { Count: >= 3 })
        {
            cameraPivot.Position = ToGodot(variant.CameraOffset);
            xrOrigin.Position = cameraPivot.Position;
        }
        PlayPlayerAnimation(requestedAnimation);
        ShowWorldNotice("EQUIPPED", ["Clothing", "Short Sword"]);
        if (xrActive)
            (activeInteractionController ?? xrRightHand)
                .TriggerHapticPulse("haptic", 0.0, 0.5, 0.12, 0.0);
        GD.Print($"NIKAMI_AURORA_PLAYER_EQUIPMENT status=ready variant={variant.Id} " +
                 $"body={variant.BodyModel} texture={variant.BodyTexture} " +
                 $"head={variant.HeadModel} weapon={variant.WeaponModel} " +
                 $"skins={variant.Animation.SkinCount} " +
                 $"headSkins={variant.Animation.HeadSkinCount} " +
                 $"animations={string.Join(',', variant.Animation.Animations)}");
    }

    private static void PresentExperienceAward(KotorExperienceAwarded experience)
    {
        var contract = experience.Contract;
        GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                 $"kind={contract.KindName} plot={contract.PlotLabel} " +
                 $"percentage={contract.PlotPercentage} base={contract.PlotBaseExperience} " +
                 $"awarded={experience.Awarded} xp={experience.Before}->{experience.After}");
    }

    private static void PresentScriptExecution(KotorScriptContract contract)
    {
        GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                 $"kind={contract.KindName} door={contract.DoorTag} " +
                 $"pause={contract.PauseConversation} moveTarget={contract.MoveTargetTag} " +
                 $"run={contract.MoveRun} range={contract.MoveRange:F3} " +
                 $"resume={contract.ResumeConversation}");
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

    private static StaticMaterialReport ConfigureStaticRoomMaterials(Node node)
    {
        var lightmappedOpaque = 0;
        var baseOpaque = 0;
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
                    lightmappedOpaque++;
                    continue;
                }
                var material = (BaseMaterial3D)source.Duplicate();
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Transparency = BaseMaterial3D.TransparencyEnum.Disabled;
                material.DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
                material.NoDepthTest = false;
                var albedo = material.AlbedoColor;
                albedo.A = 1.0f;
                material.AlbedoColor = albedo;
                material.Metallic = 0;
                material.Roughness = 1;
                instance.SetSurfaceOverrideMaterial(surface, material);
                baseOpaque++;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var childReport = ConfigureStaticRoomMaterials(child);
            lightmappedOpaque += childReport.LightmappedOpaque;
            baseOpaque += childReport.BaseOpaque;
        }
        return new StaticMaterialReport(lightmappedOpaque, baseOpaque);
    }

    private void BuildNavigation(IEnumerable<RoomRecord> rooms)
    {
        navigationTriangles.Clear();
        var profileTriangles = new List<KotorNavigationTriangle>();
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
                profileTriangles.Add(new KotorNavigationTriangle(
                    ToNumericsWithOffset(triangle[0], room.Position),
                    ToNumericsWithOffset(triangle[1], room.Position),
                    ToNumericsWithOffset(triangle[2], room.Position)));
            }
        }
        movementSimulation = new KotorMovementSimulation(
            profileTriangles,
            new KotorMovementConfiguration(playerWalkSpeed, playerRunSpeed));
    }

    private bool MovePlayer(Vector3 displacement)
    {
        if (movementSimulation is null) return false;
        var nativeDisplacement = new NumericsVector3(displacement.X, -displacement.Z, displacement.Y);
        var result = movementSimulation.TryDisplace(
            simulationPlayerPosition, nativeDisplacement, CurrentDoorObstacles());
        ApplyMovementResult(result);
        return result.Accepted;
    }

    private KotorMovementResult StepPlayer(KotorMovementIntent intent, float deltaSeconds)
    {
        if (movementSimulation is null)
            return new KotorMovementResult(
                simulationPlayerPosition, false, false, KotorLocomotionMode.Idle);
        var result = movementSimulation.Step(
            simulationPlayerPosition, yaw, intent, deltaSeconds, CurrentDoorObstacles());
        ApplyMovementResult(result);
        return result;
    }

    private IReadOnlyList<KotorDoorObstacle> CurrentDoorObstacles() =>
        interactiveDoors.Select(door => new KotorDoorObstacle(
            ToNumerics(door.Source.Position), IsDoorOpen(door))).ToArray();

    private void ApplyMovementResult(KotorMovementResult result)
    {
        if (!result.Accepted) return;
        simulationPlayerPosition = result.Position;
        playerBody.GlobalPosition = ToGodot(result.Position);
    }

    private bool TryProjectToWalkmesh(Vector3 position, out float ground)
    {
        if (movementSimulation is not null &&
            movementSimulation.TryProjectToWalkmesh(ToNumerics(position), out var nativeGround))
        {
            ground = nativeGround;
            return true;
        }
        ground = 0;
        return false;
    }

    private static Vector3 ToGodot(IReadOnlyList<float> source) =>
        new(source[0], source[2], -source[1]);

    private static Vector3 ToGodot(NumericsVector3 source) =>
        new(source.X, source.Z, -source.Y);

    private static NumericsVector3 ToNumerics(IReadOnlyList<float> source) =>
        new(source[0], source[1], source[2]);

    private static NumericsVector3 ToNumerics(Vector3 source) =>
        new(source.X, -source.Z, source.Y);

    private static NumericsVector3 ToNumericsWithOffset(
        IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        new(source[0] + offset[0], source[1] + offset[1], source[2] + offset[2]);

    private static Color ToColor(IReadOnlyList<float> source) =>
        new(source[0], source[1], source[2]);

    private static Vector3 ToGodotWithOffset(IReadOnlyList<float> source, IReadOnlyList<float> offset) =>
        ToGodot(new[] { source[0] + offset[0], source[1] + offset[1], source[2] + offset[2] });

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
                contract.ResumeConversation);
        });
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
        return new KotorGameplaySimulation(
            contracts, doors, placeables, initialPlayerExperience);
    }

    private static void ValidatePlayerEquipmentVariants(ModuleManifest manifest)
    {
        var itemSources = manifest.Placeables.SelectMany(placeable =>
            (placeable.Inventory ?? []).Select(item =>
                (Item: item, BaseItemsSha256: placeable.BaseItemsSha256)));
        foreach (var variant in manifest.Player.EquipmentVariants ?? [])
        {
            if (variant.Schema != "nikami-aurora-kotor-player-equipment-v1" ||
                string.IsNullOrWhiteSpace(variant.Glb) ||
                variant.Animation.SkinCount <= 0 ||
                variant.Animation.HeadSkinCount <= 0 ||
                !variant.Animation.Animations.Contains("pause1", StringComparer.OrdinalIgnoreCase) ||
                !variant.Animation.Animations.Contains("walk", StringComparer.OrdinalIgnoreCase) ||
                !variant.Animation.Animations.Contains("run", StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Player equipment variant is incomplete: {variant.Id}");
            var armor = itemSources.SingleOrDefault(source =>
                source.Item.Resref.Equals(
                    variant.ArmorResref, StringComparison.OrdinalIgnoreCase));
            var rightHand = itemSources.SingleOrDefault(source =>
                source.Item.Resref.Equals(
                    variant.RightHandResref, StringComparison.OrdinalIgnoreCase));
            if (armor.Item is null || rightHand.Item is null ||
                !armor.Item.UtiSha256.Equals(
                    variant.ArmorUtiSha256, StringComparison.OrdinalIgnoreCase) ||
                !rightHand.Item.UtiSha256.Equals(
                    variant.RightHandUtiSha256, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    armor.BaseItemsSha256, variant.BaseItemsSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(
                    rightHand.BaseItemsSha256, variant.BaseItemsSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Player equipment variant sources drifted: {variant.Id}");
        }
    }

    private static string DoorInstanceId(int index) => $"door:{index:D4}";

    private static string PlaceableInstanceId(int index) => $"placeable:{index:D4}";

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
        PlayerAnimationRecord Animation,
        IReadOnlyList<PlayerEquipmentVariantRecord>? EquipmentVariants);
    private sealed record PlayerAnimationRecord(int MeshCount, int VertexCount, int TriangleCount,
        int SkinCount, int HeadSkinCount, IReadOnlyList<string> Animations);
    private sealed record PlayerEquipmentVariantRecord(
        string Schema,
        string Id,
        string Glb,
        string ArmorResref,
        string RightHandResref,
        string BodyModel,
        string BodyTexture,
        string HeadModel,
        string WeaponModel,
        IReadOnlyList<float>? TalkOffset,
        IReadOnlyList<float>? CameraOffset,
        PlayerAnimationRecord Animation,
        string ArmorUtiSha256,
        string RightHandUtiSha256,
        string BaseItemsSha256);
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
        string? OnInventory, bool Locked, bool Static, bool Useable, bool HasInventory,
        int AnimationState,
        string? BaseItemsSha256,
        IReadOnlyList<ItemStackRecord>? Inventory);
    private sealed record ItemStackRecord(
        string Resref,
        string DisplayName,
        string Tag,
        int BaseItem,
        int Charges,
        int StackSize,
        int ModelVariation,
        int BodyVariation,
        int TextureVariation,
        int EquipableSlots,
        string ItemClass,
        int ModelType,
        string DefaultModel,
        string DefaultIcon,
        string UtiSha256,
        int Quantity,
        bool Droppable,
        bool Infinite);
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
    private readonly record struct StaticMaterialReport(int LightmappedOpaque, int BaseOpaque);
    private sealed class InteractiveDoor(
        string instanceId,
        DoorRecord source,
        Node3D model,
        Vector3 closedPosition)
    {
        public string InstanceId { get; } = instanceId;
        public DoorRecord Source { get; } = source;
        public Node3D Model { get; } = model;
        public Vector3 ClosedPosition { get; } = closedPosition;
    }
    private sealed class MaterializedPlaceable(
        string instanceId,
        PlaceableRecord source,
        Node3D model)
    {
        public string InstanceId { get; } = instanceId;
        public PlaceableRecord Source { get; } = source;
        public Node3D Model { get; } = model;
    }
}
