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
            render_mode depth_draw_opaque, diffuse_lambert, specular_disabled;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            void fragment() {
                vec4 base = texture(albedo_texture, UV);
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                // Odyssey combines its baked atlas with dynamic area lighting.
                // Keep most of the baked response emissive, while a restrained
                // diffuse term lets authored point lights and dynamic ambient
                // reach atlas regions that contain no baked contribution.
                ALBEDO = base.rgb * 0.55;
                vec3 baked = min(vec3(1.0), lightmap) * 0.82;
                EMISSION = base.rgb * max(baked, dynamic_ambient * 0.85);
                ROUGHNESS = 1.0;
            }
            """
    };
    private static readonly Shader OdysseyTransparentLightmapShader = new()
    {
        Code = """
            shader_type spatial;
            render_mode depth_draw_never, cull_disabled, diffuse_lambert, specular_disabled;
            uniform sampler2D albedo_texture : source_color, repeat_enable, filter_linear_mipmap_anisotropic;
            uniform sampler2D lightmap_texture : source_color, repeat_disable, filter_linear_mipmap_anisotropic;
            uniform vec3 dynamic_ambient;
            void fragment() {
                vec4 base = texture(albedo_texture, UV);
                vec3 lightmap = texture(lightmap_texture, UV2).rgb;
                ALBEDO = base.rgb * 0.55;
                vec3 baked = min(vec3(1.0), lightmap) * 0.82;
                EMISSION = base.rgb * max(baked, dynamic_ambient * 0.85);
                ROUGHNESS = 1.0;
                ALPHA = base.a;
            }
            """
    };
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
    private readonly HashSet<string> playedDialogueMedia = new(StringComparer.OrdinalIgnoreCase);
    private KotorRuntimeConfiguration runtimeConfiguration = null!;
    private KotorGameplaySimulation? gameplaySimulation;
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
    private bool automatedCorridorTrigger;
    private bool automatedCorridorTriggerVerified;
    private bool automatedCorridorTransmissionVerified;
    private bool automatedFirstEncounterVerified;
    private bool showcaseRouteEnabled;
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
    private int encounterImpactCount;
    private int roomSmokeEmitterCount;
    private int roomSparkEmitterCount;
    private bool damagedEndSmokeReady;
    private string currentMusicResref = "";
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
        if (int.TryParse(System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_FRAME"),
                out var configuredCaptureFrame))
            captureTargetFrame = Math.Max(1, configuredCaptureFrame);
        captureDialogueNode =
            System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_DIALOGUE_NODE") ?? "";
        showcaseRouteEnabled = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_SHOWCASE_ROUTE") == "1";
        showcasePhase = showcaseRouteEnabled
            ? ShowcasePhase.OpeningDialogue
            : ShowcasePhase.Disabled;
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
        var movementResult = !dialoguePanel.Visible && !cinematicSequenceActive
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
            if (!automatedDoorApplied &&
                readyFrames >= runtimeConfiguration.Automation.DoorFrame &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_DOOR") == "1" &&
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
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_OPEN_LOCKER") == "1" &&
                materializedPlaceables.Count > 0)
            {
                automatedLockerApplied = true;
                UsePlaceable(materializedPlaceables[0]);
            }
            if (!automatedInventoryOpened &&
                readyFrames >= runtimeConfiguration.Automation.MenuOpenFrame &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_TEST_INVENTORY_SCREEN") == "1")
            {
                automatedInventoryOpened = true;
                ShowInventory();
            }
            if (System.Environment.GetEnvironmentVariable(
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
                System.Environment.GetEnvironmentVariable(
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
                System.Environment.GetEnvironmentVariable(
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
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_TEST_EQUIPMENT_SCREEN") == "1")
            {
                automatedEquipmentScreenOpened = true;
                ShowEquipment();
            }
            if (System.Environment.GetEnvironmentVariable(
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
            if (System.Environment.GetEnvironmentVariable(
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
            if (!automatedEquipmentApplied &&
                readyFrames >= runtimeConfiguration.Automation.MenuOpenFrame &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_TEST_EQUIP_OPENING_GEAR") == "1")
            {
                automatedEquipmentApplied = true;
                EquipOpeningGear(null);
            }
            if (!automatedCorridorTrigger &&
                readyFrames >= runtimeConfiguration.Automation.StateFrame &&
                System.Environment.GetEnvironmentVariable(
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
                System.Environment.GetEnvironmentVariable(
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
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1")
                StartFirstEncounter();
            if (firstEncounterCombatReady && !automatedFirstEncounterVerified &&
                (System.Environment.GetEnvironmentVariable(
                     "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1" ||
                 showcaseRouteEnabled))
            {
                automatedFirstEncounterVerified = true;
                var snapshot = RequireGameplaySimulation().CaptureSnapshot();
                var encounterDoor = RequireInteractiveDoor("end_door02");
                if (!snapshot.GlobalNumbers.TryGetValue("END_TRASK_DLG", out var traskGlobal) ||
                    traskGlobal != 1 || !IsDoorOpen(encounterDoor) ||
                    dialoguePanel.Visible || !cinematicSequenceActive ||
                    encounterAttackSoundCount < 4 ||
                    encounterProjectileCount < 4 ||
                    encounterMuzzleFlashCount < 4 ||
                    encounterImpactCount < 3 ||
                    roomSmokeEmitterCount != 9 || roomSparkEmitterCount != 3 ||
                    !damagedEndSmokeReady ||
                    firstEncounter is null ||
                    !IsFirstEncounterEnvironmentReady(firstEncounter) ||
                    !currentMusicResref.Equals(
                        "mus_bat_sithbs", StringComparison.OrdinalIgnoreCase) ||
                    !playedDialogueMedia.Contains("nm01aaroom03000_") ||
                    !playedDialogueMedia.Contains("nm01aaroom03001_") ||
                    !currentDialogueNodeKey.Equals(
                        "encounter:combat-ready", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "First encounter did not reach its combat-ready state");
                GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=pass " +
                         "door=end_door02 cameras=26,19,20 dialogue=end_room3 " +
                         "global=END_TRASK_DLG:1 stage=combat-ready " +
                         $"voices=2 attacks={encounterAttackSoundCount} " +
                         $"projectiles={encounterProjectileCount} " +
                         $"muzzles={encounterMuzzleFlashCount} impacts={encounterImpactCount} " +
                         $"roomFx=smoke:{roomSmokeEmitterCount},sparks:{roomSparkEmitterCount} " +
                         $"environment={firstEncounter.EnvironmentPlaceables.Count} " +
                         $"music={currentMusicResref}");
            }
            var configuredChoice = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_DIALOGUE_CHOICE");
            if (!automatedChoiceApplied &&
                readyFrames >= runtimeConfiguration.Automation.ChoiceFrame &&
                int.TryParse(configuredChoice, out var choice) &&
                choice >= 0 && choice < activeChoiceButtons.Count)
            {
                automatedChoiceApplied = true;
                activeChoiceButtons[choice].EmitSignal(BaseButton.SignalName.Pressed);
                GD.Print($"NIKAMI_AURORA_DIALOGUE_CHOICE status=selected index={choice}");
            }
            var configuredMove = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_MOVE_METERS");
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
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_CLEAN") == "1")
                overlayLayer.Visible = false;
            if (readyFrames == runtimeConfiguration.Automation.CapturePreparationFrame &&
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_LIP_CLOSEUP") == "1")
                FrameLipSyncCloseup(dialogueOwnerActor);
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_CAPTURE_PLAYER_EQUIPMENT_CLOSEUP") == "1")
                FramePlayerEquipmentCloseup();
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_CAPTURE_CHAIR_CLOSEUP") == "1")
                FrameChairCloseup();
            if (readyFrames == runtimeConfiguration.Automation.SceneReadyFrame &&
                System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_CAPTURE_XR_BODY_LOOKDOWN") == "1")
                FrameXrBodyLookDown();
            if (showcaseRouteEnabled)
                AdvanceShowcaseRoute();
        }

        UpdateXrSpectatorCamera();
        UpdateFlatUiVisibility();
        var capturePath = System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE");
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
            if (System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
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
            runtimeConfiguration = manifest.RuntimeConfiguration.Validate(requireSourceHash: true);
            if (captureTargetFrame == 0)
                captureTargetFrame = runtimeConfiguration.Automation.SceneReadyFrame;

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
            ConfigureFlatPresentation(manifest.Ui, manifestDirectory);
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
            if (int.TryParse(System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_TEST_PLAYER_XP"),
                    out var configuredPlayerXp))
                initialPlayerExperience = Math.Max(0, configuredPlayerXp);
            gameplaySimulation = CreateGameplaySimulation(manifest, initialPlayerExperience);
            var supportedTriggers = gameplaySimulation.CaptureSnapshot().TriggerStates.Count;
            GD.Print($"NIKAMI_AURORA_GAMEPLAY_STATE status=ready scripts={manifest.ScriptContracts.Count} " +
                     $"doors={manifest.Doors.Count} placeables={manifest.Placeables.Count} " +
                     $"triggers={supportedTriggers}/{manifest.Triggers.Count} " +
                     $"xp={initialPlayerExperience}");
            ApplyAreaLighting(manifest.Lighting);
            var loadedRooms = 0;
            var lightmappedOpaqueMaterials = 0;
            var baseOpaqueMaterials = 0;
            var lightmappedTransparentMaterials = 0;
            var baseTransparentMaterials = 0;
            var sourceAdditiveMaterials = 0;
            roomSmokeEmitterCount = 0;
            roomSparkEmitterCount = 0;
            damagedEndSmokeReady = false;
            var roomEmitterTextures = new Dictionary<string, Texture2D>(
                StringComparer.OrdinalIgnoreCase);
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
                var materialReport = ConfigureStaticRoomMaterials(
                    imported, ToColor(manifest.Lighting.DynamicAmbient));
                lightmappedOpaqueMaterials += materialReport.LightmappedOpaque;
                baseOpaqueMaterials += materialReport.BaseOpaque;
                lightmappedTransparentMaterials += materialReport.LightmappedTransparent;
                baseTransparentMaterials += materialReport.BaseTransparent;
                sourceAdditiveMaterials += materialReport.SourceAdditive;
                AddChild(imported);
                var emitterReport = LoadRoomEmitters(
                    room, imported, manifestDirectory, roomEmitterTextures);
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
            if (lightmappedOpaqueMaterials == 0 || baseOpaqueMaterials == 0 ||
                sourceAdditiveMaterials == 0)
                throw new InvalidDataException("Room opacity audit found no configured materials");
            GD.Print($"NIKAMI_AURORA_OPACITY status=pass policy=source-opaque " +
                      $"lightmapped={lightmappedOpaqueMaterials} base={baseOpaqueMaterials} " +
                      $"sourceTransparentLightmapped={lightmappedTransparentMaterials} " +
                      $"sourceTransparentBase={baseTransparentMaterials} " +
                      $"sourceAdditive={sourceAdditiveMaterials} " +
                      "opaqueAlphaWrites=0 opaqueDepthWrite=opaque");
            if (roomSmokeEmitterCount + roomSparkEmitterCount !=
                    manifest.Counts.AuthoredEmitters ||
                roomSmokeEmitterCount != 9 || roomSparkEmitterCount != 3 ||
                !damagedEndSmokeReady)
                throw new InvalidDataException(
                    "Endar Spire room-emitter presentation contract drifted");
            GD.Print("NIKAMI_AURORA_ROOM_EMITTERS status=ready authored=12 " +
                     "materialized=12 smoke=9 sparks=3 " +
                     "damagedEnd=M01aa_03a/Object107/smoke044");

            var authoredLights = LoadAuthoredLights(manifest.Rooms, manifest.Lighting);
            var materializedPlayer = LoadPlayerModel(
                manifest.Player, manifest.CameraStyle, manifestDirectory);
            var materializedActors = LoadActorModels(manifest.Creatures, manifestDirectory);
            if (System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_DEBUG_CREATURE_MARKERS") == "1")
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

            UpdateLoadingProgress(
                runtimeConfiguration.Presentation.Loading.CompleteProgress);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            loadingBackdrop.Visible = false;
            StopRetailLoadingMusic();
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
            if (openingActor is not null)
                LoadOpeningDialogue(
                    openingActor,
                    manifestDirectory,
                    System.Environment.GetEnvironmentVariable(
                        "NIKAMI_AURORA_SKIP_OPENING_DIALOGUE") != "1");
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
            if (System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_CAPTURE_EXIT") == "1")
                RequestCleanExit(1);
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
        cinematicCamera = new Camera3D
        {
            Name = "CinematicCamera",
            Current = false,
            Near = 0.05f,
            Far = 1000.0f,
            Fov = DefaultGameplayFieldOfView
        };
        AddChild(cinematicCamera);
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
            MaterialOverride = material,
            Visible = false
        };
        container.AddChild(fallback);
        return (container, fallback);
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

    private static FirstEncounterAudioStreams LoadFirstEncounterAudio(
        FirstEncounterAudio source,
        string manifestDirectory) => new(
        LoadOwnedAudio(source.BlasterShot, manifestDirectory),
        LoadOwnedAudio(source.BlasterImpact, manifestDirectory),
        LoadOwnedAudio(source.BackgroundMusic, manifestDirectory),
        LoadOwnedAudio(source.BattleMusic, manifestDirectory));

    private static FirstEncounterEffectTextures LoadFirstEncounterEffects(
        FirstEncounterEffects source,
        string manifestDirectory)
    {
        if (source.Schema != "nikami-aurora-kotor-first-encounter-effects-v1" ||
            Math.Abs(source.ProjectileSize - 0.09f) > 0.0001f ||
            Math.Abs(source.MuzzleSize - 0.3f) > 0.0001f ||
            Math.Abs(source.MuzzleLifetime - 0.02f) > 0.0001f)
            throw new InvalidDataException("First-encounter effect contract drifted");
        return new FirstEncounterEffectTextures(
            LoadOwnedEffectTexture(source.LaserTexture, manifestDirectory),
            LoadOwnedEffectTexture(source.MuzzleTexture, manifestDirectory),
            LoadOwnedEffectTexture(source.FlareTexture, manifestDirectory),
            source.ProjectileSize,
            source.MuzzleSize,
            source.MuzzleLifetime);
    }

    private static Texture2D LoadOwnedEffectTexture(
        FirstEncounterEffectTexture source,
        string manifestDirectory)
    {
        var root = Path.GetFullPath(manifestDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            manifestDirectory, source.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter effect path escapes the bundle: {source.Path}");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (bytes.Length != source.ByteCount ||
            !hash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter effect payload drifted: {source.Resref}");
        var image = new Godot.Image();
        if (image.LoadPngFromBuffer(bytes) != Error.Ok || image.IsEmpty())
            throw new InvalidDataException($"Encounter effect texture is not playable: {source.Resref}");
        var texture = ImageTexture.CreateFromImage(image);
        GD.Print($"NIKAMI_AURORA_EFFECT_TEXTURE status=validated resref={source.Resref} " +
                 $"size={image.GetWidth()}x{image.GetHeight()}");
        return texture;
    }

    private static RoomEmitterReport LoadRoomEmitters(
        RoomRecord room,
        Node3D roomRoot,
        string manifestDirectory,
        IDictionary<string, Texture2D> textureCache)
    {
        var smoke = 0;
        var spark = 0;
        var damagedEnd = false;
        foreach (var source in room.Emitters ?? [])
        {
            var isSmoke = source.Texture.Resref.Equals(
                "fx_Smoke", StringComparison.OrdinalIgnoreCase);
            var isSpark = source.Texture.Resref.Equals(
                "fx_Spark", StringComparison.OrdinalIgnoreCase);
            if (source.Schema != "nikami-aurora-kotor-room-emitter-v1" ||
                (!isSmoke && !isSpark) ||
                !source.Update.Equals("Fountain", StringComparison.OrdinalIgnoreCase) ||
                source.BirthRate <= 0 || source.LifeExpectancy <= 0 ||
                source.Direction.Count < 3 || source.Position.Count < 3 ||
                source.ColorStart.Count < 3 || source.ColorMid.Count < 3 ||
                source.ColorEnd.Count < 3 ||
                source.PercentStart < 0 || source.PercentEnd > 1 ||
                source.PercentStart > source.PercentMid ||
                source.PercentMid > source.PercentEnd ||
                (isSmoke && (source.XGrid != 4 || source.YGrid != 4 ||
                             !source.Blend.Equals(
                                 "Normal", StringComparison.OrdinalIgnoreCase) ||
                             !source.Render.Equals(
                                 "Normal", StringComparison.OrdinalIgnoreCase))) ||
                (isSpark && (source.XGrid != 2 || source.YGrid != 2 ||
                             !source.Blend.Equals(
                                 "Lighten", StringComparison.OrdinalIgnoreCase) ||
                             !source.Render.Equals(
                                 "Motion_Blur", StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException(
                    $"Unsupported room emitter: {room.Model}/{source.NodePath}");

            if (!textureCache.TryGetValue(source.Texture.PayloadSha256, out var texture))
            {
                texture = LoadOwnedEffectTexture(source.Texture, manifestDirectory);
                textureCache[source.Texture.PayloadSha256] = texture;
            }

            var colorMidOffset = Mathf.Clamp(
                source.PercentMid,
                source.PercentStart + 0.0001f,
                source.PercentEnd - 0.0001f);
            var gradient = new Gradient
            {
                Offsets = [source.PercentStart, colorMidOffset, source.PercentEnd],
                Colors =
                [
                    ToEmitterColor(source.ColorStart, source.AlphaStart),
                    ToEmitterColor(source.ColorMid, source.AlphaMid),
                    ToEmitterColor(source.ColorEnd, source.AlphaEnd)
                ]
            };
            var colorRamp = new GradientTexture1D { Gradient = gradient };
            var scaleCurve = new Curve
            {
                MinValue = 0,
                MaxValue = Math.Max(
                    1.0f, Math.Max(source.SizeStart,
                        Math.Max(source.SizeMid, source.SizeEnd)))
            };
            scaleCurve.AddPoint(new Vector2(source.PercentStart, source.SizeStart));
            scaleCurve.AddPoint(new Vector2(colorMidOffset, source.SizeMid));
            scaleCurve.AddPoint(new Vector2(source.PercentEnd, source.SizeEnd));
            var frameCount = Math.Max(1, source.XGrid * source.YGrid);
            var frameDivisor = Math.Max(1, frameCount - 1);
            var frameStart = Mathf.Clamp(source.FrameStart / frameDivisor, 0, 1);
            var frameEnd = Mathf.Clamp(source.FrameEnd / frameDivisor, 0, 1);
            var animationCycles = source.Fps > 0
                ? source.Fps * source.LifeExpectancy / frameCount
                : 0.0f;
            var processMaterial = new ParticleProcessMaterial
            {
                Direction = ToGodot(source.Direction).Normalized(),
                Spread = Mathf.RadToDeg(source.SpreadRadians),
                InitialVelocityMin = Math.Max(0, source.Velocity - source.RandomVelocity),
                InitialVelocityMax = source.Velocity + source.RandomVelocity,
                AngularVelocityMin = Mathf.RadToDeg(source.ParticleRotation),
                AngularVelocityMax = Mathf.RadToDeg(source.ParticleRotation),
                Gravity = Vector3.Up * -source.Mass,
                ScaleMin = 1,
                ScaleMax = 1,
                ScaleCurve = new CurveTexture { Curve = scaleCurve },
                ColorRamp = colorRamp,
                AnimSpeedMin = animationCycles,
                AnimSpeedMax = animationCycles,
                AnimOffsetMin = source.Fps > 0 ? frameStart : Math.Min(frameStart, frameEnd),
                AnimOffsetMax = source.Fps > 0 ? frameStart : Math.Max(frameStart, frameEnd)
            };
            var material = new StandardMaterial3D
            {
                AlbedoTexture = texture,
                AlbedoColor = Colors.White,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                BlendMode = isSpark
                    ? BaseMaterial3D.BlendModeEnum.Add
                    : BaseMaterial3D.BlendModeEnum.Mix,
                DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
                ParticlesAnimHFrames = source.XGrid,
                ParticlesAnimVFrames = source.YGrid,
                ParticlesAnimLoop = true
            };
            if (isSpark)
            {
                material.EmissionEnabled = true;
                material.Emission = Colors.White;
                material.EmissionTexture = texture;
                material.EmissionEnergyMultiplier = 2.0f;
            }
            var travel = source.Velocity * source.LifeExpectancy + source.SizeEnd * 2;
            var boundsExtent = Math.Max(8.0f, Math.Min(64.0f, travel));
            var particles = new GpuParticles3D
            {
                Name = "Emitter_" + source.NodePath.Replace('/', '_'),
                Position = ToGodot(source.Position),
                Amount = Math.Max(1, (int)Math.Ceiling(
                    source.BirthRate * source.LifeExpectancy)),
                Lifetime = source.LifeExpectancy,
                Preprocess = Math.Min(source.LifeExpectancy, 6.0f),
                Randomness = Mathf.Clamp(
                    source.RandomBirthRate / Math.Max(1.0f, source.BirthRate), 0, 1),
                FixedFps = 30,
                Interpolate = true,
                LocalCoords = false,
                DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
                ProcessMaterial = processMaterial,
                DrawPass1 = new QuadMesh
                {
                    Size = isSpark
                        ? new Vector2(
                            1.0f,
                            Math.Max(1.0f, source.BlurLength /
                                Math.Max(0.001f, source.SizeStart)))
                        : Vector2.One,
                    Material = material
                },
                VisibilityAabb = new Aabb(
                    Vector3.One * -boundsExtent,
                    Vector3.One * boundsExtent * 2),
                Emitting = true
            };
            roomRoot.AddChild(particles);
            smoke += isSmoke ? 1 : 0;
            spark += isSpark ? 1 : 0;
            damagedEnd |= room.Model.Equals(
                              "M01aa_03a", StringComparison.OrdinalIgnoreCase) &&
                          source.NodePath.EndsWith(
                              "Object107/smoke044", StringComparison.OrdinalIgnoreCase) &&
                          Math.Abs(source.BirthRate - 40.0f) < 0.0001f &&
                          Math.Abs(source.LifeExpectancy - 6.0f) < 0.0001f;
        }
        return new RoomEmitterReport(smoke, spark, damagedEnd);
    }

    private static Color ToEmitterColor(IReadOnlyList<float> source, float alpha) =>
        new(source[0], source[1], source[2], alpha);

    private static AudioStream LoadOwnedAudio(
        FirstEncounterAudioSource source,
        string manifestDirectory)
    {
        var root = Path.GetFullPath(manifestDirectory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(
            manifestDirectory, source.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter audio path escapes the bundle: {source.Path}");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        if (bytes.Length != source.ByteCount ||
            !hash.Equals(source.PayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Encounter audio payload drifted: {source.Resref}");
        AudioStream stream = source.Format.ToLowerInvariant() switch
        {
            "wav" => AudioStreamWav.LoadFromBuffer(bytes, new Godot.Collections.Dictionary()),
            "mp3" => AudioStreamMP3.LoadFromBuffer(bytes),
            _ => throw new InvalidDataException(
                $"Unsupported encounter audio format: {source.Format}")
        };
        if (stream.GetLength() <= 0.0)
            throw new InvalidDataException(
                $"Encounter audio decoded with no playable duration: {source.Resref}");
        GD.Print($"NIKAMI_AURORA_AUDIO status=validated resref={source.Resref} " +
                 $"source={source.SourceEncoding} payload={source.PayloadEncoding} " +
                 $"duration={stream.GetLength():F3}");
        return stream;
    }

    private void PlayOneShot(AudioStream stream, float volumeDb = -3.0f)
    {
        var player = new AudioStreamPlayer
        {
            Stream = stream,
            VolumeDb = volumeDb
        };
        player.Finished += player.QueueFree;
        AddChild(player);
        player.Play();
    }

    private void FireEncounterBlaster(string attackerTag, string targetTag)
    {
        var audio = firstEncounterAudio;
        var effects = firstEncounterEffectTextures;
        if (audio is null || effects is null ||
            !actorModels.TryGetValue(attackerTag, out var attacker) ||
            !actorModels.TryGetValue(targetTag, out var target))
            return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var muzzle = FindDescendantBySuffix<Node3D>(attacker, "bullethook")?.GlobalPosition ??
                     attacker.GlobalPosition + Vector3.Up *
                     presentation.FallbackMuzzleHeightMeters;
        var destination = actorTalkOffsets.TryGetValue(targetTag, out var talkOffset)
            ? target.GlobalTransform * talkOffset
            : target.GlobalPosition + Vector3.Up *
              presentation.FallbackTargetHeightMeters;
        SpawnMuzzleFlash(muzzle);
        var bolt = new MeshInstance3D
        {
            Name = $"BlasterBolt_{encounterProjectileCount:D3}",
            Mesh = new BoxMesh
            {
                Size = new Vector3(
                    effects.ProjectileSize, effects.ProjectileSize,
                    presentation.ProjectileLengthMeters)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.ProjectileColor), effects.Laser, false)
        };
        AddChild(bolt);
        bolt.GlobalPosition = muzzle;
        bolt.LookAt(destination, Vector3.Up);
        encounterProjectileCount++;
        PlayOneShot(audio.BlasterShot, presentation.ShotVolumeDb);
        encounterAttackSoundCount++;
        var duration = Math.Max(
            presentation.MinimumProjectileTravelSeconds,
            muzzle.DistanceTo(destination) /
            presentation.ProjectileSpeedMetersPerSecond);
        var tween = CreateTween();
        tween.TweenProperty(bolt, "global_position", destination, duration);
        tween.TweenCallback(Callable.From(() =>
        {
            bolt.QueueFree();
            SpawnImpactFlash(destination);
            PlayOneShot(audio.BlasterImpact, presentation.ImpactVolumeDb);
        }));
        GD.Print($"NIKAMI_AURORA_PROJECTILE status=fired attacker={attackerTag} " +
                 $"target={targetTag} from={muzzle} to={destination} duration={duration:F3}");
    }

    private static StandardMaterial3D CreateEffectMaterial(
        Color color, Texture2D? texture, bool billboard) => new()
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            AlbedoColor = color,
            AlbedoTexture = texture,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            BillboardMode = billboard
            ? BaseMaterial3D.BillboardModeEnum.Enabled
            : BaseMaterial3D.BillboardModeEnum.Disabled,
            BillboardKeepScale = billboard
        };

    private void SpawnMuzzleFlash(Vector3 position)
    {
        var effects = firstEncounterEffectTextures;
        if (effects is null) return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var flash = new Node3D
        {
            Name = $"MuzzleFlash_{encounterMuzzleFlashCount:D3}"
        };
        AddChild(flash);
        flash.GlobalPosition = position;
        var authoredFlash = new MeshInstance3D
        {
            Name = "AuthoredMuzzleBillboard",
            Mesh = new QuadMesh
            {
                Size = new Vector2(effects.MuzzleSize, effects.MuzzleSize)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.MuzzleColor), effects.Muzzle, true)
        };
        flash.AddChild(authoredFlash);
        var authoredFlare = new MeshInstance3D
        {
            Name = "AuthoredMuzzleFlare",
            Mesh = new QuadMesh
            {
                Size = new Vector2(
                    effects.MuzzleSize * presentation.MuzzleFlareScale,
                    effects.MuzzleSize * presentation.MuzzleFlareScale)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.MuzzleFlareColor), effects.Flare, true)
        };
        flash.AddChild(authoredFlare);
        encounterMuzzleFlashCount++;
        var tween = CreateTween();
        tween.TweenProperty(
            flash, "scale", Vector3.Zero, effects.MuzzleLifetime);
        tween.TweenCallback(Callable.From(flash.QueueFree));
    }

    private void SpawnImpactFlash(Vector3 position)
    {
        var effects = firstEncounterEffectTextures;
        if (effects is null) return;
        var presentation = runtimeConfiguration.Presentation.FirstEncounter;
        var impact = new MeshInstance3D
        {
            Name = $"ImpactFlash_{encounterImpactCount:D3}",
            Mesh = new QuadMesh
            {
                Size = new Vector2(
                    presentation.ImpactSizeMeters,
                    presentation.ImpactSizeMeters)
            },
            MaterialOverride = CreateEffectMaterial(
                RuntimeColor(presentation.ImpactColor), effects.Flare, true)
        };
        AddChild(impact);
        impact.GlobalPosition = position;
        encounterImpactCount++;
        var tween = CreateTween();
        tween.TweenProperty(
            impact, "scale", Vector3.Zero,
            presentation.ImpactLifetimeSeconds);
        tween.TweenCallback(Callable.From(impact.QueueFree));
    }

    private void SwitchAreaMusic(AudioStream stream, string resref)
    {
        areaMusic.Stop();
        areaMusic.Stream = stream;
        areaMusic.Play();
        currentMusicResref = resref;
        GD.Print($"NIKAMI_AURORA_MUSIC status=playing resref={resref}");
    }

    private void UpdateXrSpectatorCamera()
    {
        UpdateXrLocalAvatarVisibility();
        if (xrActive && !dialogueCameraActive)
        {
            if (!xrGameplayOriginCalibrated)
                RecenterXrGameplayBase();
            else
                ApplyXrGameplayBase();
        }
        if (!xrSpectatorActive || xrSpectatorCamera is null) return;
        xrSpectatorCamera.GlobalTransform = xrCamera.GlobalTransform;
        xrSpectatorCamera.Fov = xrSpectatorFieldOfView;
    }

    private void UpdateXrLocalAvatarVisibility()
    {
        if (playerModel is null) return;
        var shouldShowHead = !xrActive || dialogueCameraActive;
        if (xrLocalPlayerHeadVisible == shouldShowHead) return;
        var allMeshes = FindDescendants<MeshInstance3D>(playerModel).ToArray();
        // The importer flattens some Odyssey model nodes, so the authored head
        // hook is not a reliable runtime parent. Mask only the separately named
        // PMHA head meshes; this leaves PMB body geometry and the weapon intact.
        var headMeshes = allMeshes.Where(mesh => mesh.Name.ToString().StartsWith(
            "mesh__PMHA", StringComparison.OrdinalIgnoreCase)).ToArray();
        var bodyMeshes = allMeshes.Count(mesh => mesh.Name.ToString().StartsWith(
            "mesh__PMB", StringComparison.OrdinalIgnoreCase));
        var hasLeftHand = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().Contains("lhand", StringComparison.OrdinalIgnoreCase));
        var hasRightHand = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().Contains("rhand", StringComparison.OrdinalIgnoreCase));
        if (headMeshes.Length != 8 || bodyMeshes < 3 || !hasLeftHand || !hasRightHand)
            throw new InvalidDataException(
                "Local player head/body/hand visibility contract drifted");
        foreach (var headMesh in headMeshes)
            headMesh.Visible = shouldShowHead;
        xrLocalPlayerHeadVisible = shouldShowHead;
        var weapon = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().StartsWith(
                "weapon__", StringComparison.OrdinalIgnoreCase));
        GD.Print($"NIKAMI_AURORA_XR_LOCAL_AVATAR status=" +
                 $"{(shouldShowHead ? "cinematic-head-visible" : "gameplay-head-hidden")} " +
                 $"headMeshes={headMeshes.Length} bodyMeshes={bodyMeshes} " +
                 $"hands=left,right weapon={(weapon ? "present" : "none")}");
    }

    private void RecenterXrGameplayBase()
    {
        if (!xrActive) return;
        var desiredLocalHead = new Transform3D(Basis.Identity, cameraPivot.Position);
        xrGameplayOriginOffset = desiredLocalHead * xrCamera.Transform.AffineInverse();
        xrGameplayOriginCalibrated = true;
        ApplyXrGameplayBase();
        var desiredHead = playerBody.GlobalTransform * desiredLocalHead;
        var error = xrCamera.GlobalPosition.DistanceTo(desiredHead.Origin);
        var forwardDot = (-xrCamera.GlobalBasis.Z).Normalized()
            .Dot((-desiredHead.Basis.Z).Normalized());
        if (error > 0.002f || forwardDot < 0.999f)
            throw new InvalidDataException(
                $"XR gameplay camera alignment drifted by {error:F6} m / " +
                $"forward dot {forwardDot:F6}");
        GD.Print($"NIKAMI_AURORA_XR_GAMEPLAY_BASE status=recentered " +
                 $"desired={desiredHead.Origin} actual={xrCamera.GlobalPosition} " +
                 $"error={error:F6} forwardDot={forwardDot:F6}");
    }

    private void ApplyXrGameplayBase()
    {
        if (xrSpectatorActive)
        {
            xrOrigin.TopLevel = true;
            xrOrigin.GlobalTransform = playerBody.GlobalTransform * xrGameplayOriginOffset;
        }
        else
        {
            xrOrigin.TopLevel = false;
            xrOrigin.Transform = xrGameplayOriginOffset;
        }
    }

    private static bool HasVisibleCapturePixels(Godot.Image image)
    {
        if (image.IsEmpty()) return false;
        var xStep = Math.Max(1, image.GetWidth() / 8);
        var yStep = Math.Max(1, image.GetHeight() / 8);
        for (var y = yStep / 2; y < image.GetHeight(); y += yStep)
            for (var x = xStep / 2; x < image.GetWidth(); x += xStep)
            {
                var sample = image.GetPixel(x, y);
                if (sample.R + sample.G + sample.B > 0.075f)
                    return true;
            }
        return false;
    }

    private async void RequestCleanExit(int exitCode)
    {
        if (cleanExitRequested) return;
        cleanExitRequested = true;
        if (xrActive)
        {
            await ToSignal(
                RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            xrActive = false;
            GD.Print("NIKAMI_AURORA_OPENXR status=shutdown-requested " +
                     "boundary=frame-post-draw");
        }
        GetTree().Quit(exitCode);
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
            if (System.Environment.GetEnvironmentVariable(
                    "NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE") == "1")
            {
                GD.PushError("NIKAMI_AURORA_OPENXR status=fail expected=active");
                RequestCleanExit(2);
            }
            return;
        }
        xrActive = true;
        xrSpectatorActive = System.Environment.GetEnvironmentVariable(
            "NIKAMI_AURORA_XR_SPECTATOR") == "1";
        xrOrigin.Current = true;
        xrOrigin.WorldScale = 1.0f;
        if (xrSpectatorActive)
        {
            var sourceViewport = GetViewport();
            xrRenderViewport = new SubViewport
            {
                Name = "OpenXRRenderViewport",
                Size = new Vector2I(1280, 720),
                OwnWorld3D = false,
                RenderTargetUpdateMode =
                    SubViewport.UpdateMode.Always
            };
            AddChild(xrRenderViewport);
            xrRenderViewport.World3D = sourceViewport.World3D;
            xrOrigin.Reparent(xrRenderViewport, true);
            xrCamera.Current = true;
            xrRenderViewport.UseXR = true;
            sourceViewport.UseXR = false;
            camera.Current = false;
            cinematicCamera.Current = false;
            xrSpectatorFieldOfView = gameplayFieldOfView;
            xrSpectatorCamera = new Camera3D
            {
                Name = "OpenXRSpectatorCamera",
                Current = true,
                Near = 0.05f,
                Far = 1000.0f,
                Fov = xrSpectatorFieldOfView
            };
            AddChild(xrSpectatorCamera);
            UpdateXrSpectatorCamera();
            GD.Print("NIKAMI_AURORA_XR_SPECTATOR status=ready " +
                     "source=hmd world=shared output=root");
        }
        else
        {
            camera.Current = false;
            xrCamera.Current = true;
            GetViewport().UseXR = true;
        }
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        xrLeftModelManager = CreatePortableRenderModel(xrLeftModelContainer, true);
        xrRightModelManager = CreatePortableRenderModel(xrRightModelContainer, false);
        xrLeftVendorModel = TryCreateMetaRenderModel(xrLeftModelContainer, true);
        xrRightVendorModel = TryCreateMetaRenderModel(xrRightModelContainer, false);
        UpdateControllerModelFallbacks();
        GD.Print("NIKAMI_AURORA_OPENXR status=ready worldScale=1.000 " +
                 "authority=hmd-relative-to-game-camera " +
                 $"spectator={xrSpectatorActive}");
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
        var dialogueLayer = new CanvasLayer { Name = "DialogueLayer" };
        AddChild(dialogueLayer);
        dialogueLayer.AddChild(dialoguePanel);
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

    private void LoadOpeningDialogue(
        CreatureRecord actor,
        string manifestDirectory,
        bool present)
    {
        if (actor.Dialogue is null) return;
        var graph = ReadDialogueGraph(actor.Dialogue, manifestDirectory);
        dialogueOwnerActor = actor.Template;
        dialogueManifestDirectory = manifestDirectory;
        openingDialogueConversation = actor.Conversation ?? "";
        openingDialogueGraph = graph;
        currentDialogueConversation = openingDialogueConversation;
        if (present)
            PresentDialogueStarter(graph, graph.OpeningStarter);
    }

    private static DialogueGraph ReadDialogueGraph(
        DialogueReference reference,
        string manifestDirectory)
    {
        var path = Path.GetFullPath(Path.Combine(manifestDirectory,
            reference.Path.Replace('/', Path.DirectorySeparatorChar)));
        var graph = JsonSerializer.Deserialize<DialogueGraph>(File.ReadAllText(path), JsonOptions())
                    ?? throw new InvalidDataException($"Dialogue graph is empty: {path}");
        if (graph.Schema != "nikami-aurora-kotor-dialogue-v1" || graph.Starters.Count == 0)
            throw new InvalidDataException($"Unsupported dialogue graph: {path}");
        return graph;
    }

    private void PresentDialogueStarter(DialogueGraph graph, int starterIndex)
    {
        if (starterIndex < 0 || starterIndex >= graph.Starters.Count)
            throw new InvalidDataException($"Dialogue starter is out of range: {starterIndex}");
        PresentDialogueNode(
            graph, graph.Starters[starterIndex].Target, new HashSet<string>(), 0);
    }

    private void PlayActorAnimation(string actor, string requested, bool loop = true)
    {
        if (!actorAnimations.TryGetValue(actor, out var player)) return;
        var match = player.GetAnimationList().FirstOrDefault(name =>
            name.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase) ||
            name.ToString().EndsWith('/' + requested, StringComparison.OrdinalIgnoreCase));
        if (match == default) return;
        var animation = player.GetAnimation(match);
        if (animation is not null)
            animation.LoopMode = loop
                ? Animation.LoopModeEnum.Linear
                : Animation.LoopModeEnum.None;
        player.Play(match);
        GD.Print($"NIKAMI_AURORA_ACTOR_ANIMATION status=playing actor={actor} animation={match}");
    }

    private void ApplyDialogueAnimations(DialogueNode node)
    {
        foreach (var animation in node.Animations)
        {
            if (string.IsNullOrWhiteSpace(animation.AnimationName) ||
                string.IsNullOrWhiteSpace(animation.Participant))
                continue;
            if (animation.Participant.Equals("PLAYER", StringComparison.OrdinalIgnoreCase))
                PlayPlayerAnimation(animation.AnimationName);
            else
                PlayActorAnimation(
                    animation.Participant,
                    animation.AnimationName,
                    animation.Looping && !animation.FireForget);
            GD.Print($"NIKAMI_AURORA_DIALOGUE_ANIMATION status=applied " +
                     $"participant={animation.Participant} id={animation.AnimationId} " +
                     $"name={animation.AnimationName}");
        }
    }

    private void SetPresentationCameraBase(Vector3 position, Vector3 target, Vector3 up, float fov)
    {
        if (xrActive)
        {
            xrOrigin.TopLevel = true;
            var desiredHeadTransform = new Transform3D(Basis.Identity, position)
                .LookingAt(target, up);
            xrOrigin.GlobalTransform = desiredHeadTransform *
                                       xrCamera.Transform.AffineInverse();
            xrSpectatorFieldOfView = fov;
            var alignmentError = xrCamera.GlobalPosition.DistanceTo(position);
            var forwardDot = (-xrCamera.GlobalBasis.Z).Normalized()
                .Dot((-desiredHeadTransform.Basis.Z).Normalized());
            if (alignmentError > 0.002f || forwardDot < 0.999f)
                throw new InvalidDataException(
                    $"XR presentation camera alignment drifted by {alignmentError:F6} m / " +
                    $"forward dot {forwardDot:F6}");
            GD.Print($"NIKAMI_AURORA_XR_CAMERA_BASE status=recentered " +
                     $"desired={position} actual={xrCamera.GlobalPosition} " +
                     $"error={alignmentError:F6} forwardDot={forwardDot:F6}");
        }
        else
        {
            camera.Current = false;
            cinematicCamera.Current = true;
            cinematicCamera.GlobalPosition = position;
            cinematicCamera.LookAt(target, up);
            cinematicCamera.Fov = fov;
        }
        dialogueCameraActive = true;
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
    }

    private void ApplyDialogueCamera(DialogueNode node)
    {
        if (node.CameraId is int cameraId && cameraId > 0 &&
            dialogueCameras.ContainsKey(cameraId))
        {
            ApplyStaticDialogueCamera(cameraId, node.CameraFov);
            if (!xrActive && cameraId == 1)
            {
                var carthTarget = actorModels.TryGetValue("Carth", out var carthModel)
                    ? actorTalkOffsets.TryGetValue("Carth", out var carthTalkOffset)
                        ? carthModel.GlobalTransform * carthTalkOffset
                        : carthModel.GlobalPosition + Vector3.Up * 0.85f
                    : FindDescendantBySuffix<Node3D>(this, "Authored_p_carth001")?.GlobalPosition;
                if (carthTarget is not Vector3 targetPosition) return;
                GD.Print($"NIKAMI_AURORA_CAMERA_TARGET status=projected camera=1 " +
                         $"target=Carth world={targetPosition} " +
                         $"screen={cinematicCamera.UnprojectPosition(targetPosition)} " +
                         $"behind={cinematicCamera.IsPositionBehind(targetPosition)}");
            }
            return;
        }
        var speakerActor = ResolveDialogueActor(node);
        if (string.IsNullOrWhiteSpace(node.Text) || speakerActor is null ||
            !actorModels.TryGetValue(speakerActor, out var speaker))
            return;

        var listenerPosition = playerBody.GlobalPosition + Vector3.Up * 1.55f;
        FaceModelToward(speaker, listenerPosition);
        if (playerModel is not null)
            FaceModelToward(playerModel, speaker.GlobalPosition + Vector3.Up * 1.0f);
        var talkDummy = FindDescendantBySuffix<Node3D>(speaker, "talkdummy");
        var speakerPosition = talkDummy?.GlobalPosition ??
            (actorTalkOffsets.TryGetValue(speakerActor, out var talkOffset)
                ? speaker.GlobalTransform * talkOffset
                : speaker.GlobalPosition + Vector3.Up * 1.55f);
        var listenerToSpeaker = speakerPosition - listenerPosition;
        var distance = listenerToSpeaker.Length();
        if (distance < 0.01f) return;
        if (node.CameraAngle == 0 && dialogueCameraWasDynamic &&
            lastDynamicDialogueActor.Equals(speakerActor, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=preserved mode=dynamic " +
                     $"actor={speakerActor} angle=0 xr={xrActive}");
            return;
        }
        var direction = listenerToSpeaker / distance;
        var side = direction.Cross(Vector3.Down).Normalized();
        var center = 0.5f * (listenerPosition + speakerPosition);
        var tightSpeaker = node.CameraAngle is 1 or 2 or 4;
        Vector3 eye;
        if (tightSpeaker)
        {
            var forwardOffset = Math.Min(0.325f * distance, 0.875f);
            var sideOffset = Math.Min(0.11f * distance, 0.293f);
            eye = speakerPosition - forwardOffset * direction +
                  sideOffset * side + 0.1f * Vector3.Up;
        }
        else
        {
            var offset = Math.Min(0.25f * distance, 1.0f);
            eye = center - offset * direction + offset * side + 0.1f * Vector3.Up;
        }
        var targetHeight = tightSpeaker ? -0.1f : 0.1f;
        var target = speakerPosition - 0.1f * distance * side +
                     targetHeight * Vector3.Up;
        SetPresentationCameraBase(eye, target, Vector3.Up, dialogueFieldOfView);
        dialogueCameraWasDynamic = true;
        lastDynamicDialogueActor = speakerActor;
        GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active " +
                 $"mode={(tightSpeaker ? "speaker-tight" : "speaker")} actor={speakerActor} " +
                 $"angle={node.CameraAngle} fov={dialogueFieldOfView:F3} position={eye} xr={xrActive}");
    }

    private void ApplyStaticDialogueCamera(int cameraId, float? overrideFov = null)
    {
        if (!dialogueCameras.TryGetValue(cameraId, out var source))
            throw new InvalidDataException($"Authored camera was not found: {cameraId}");
        var position = ToGodot(source.Position) + Vector3.Up * source.Height;
        var forward = ToGodot(source.Forward).Normalized();
        var up = ToGodot(source.Up).Normalized();
        if (forward.LengthSquared() < 0.99f || up.LengthSquared() < 0.99f)
            throw new InvalidDataException($"Authored camera {cameraId} has an invalid basis");
        var fov = overrideFov is > 0 ? overrideFov.Value : source.Fov;
        SetPresentationCameraBase(position, position + forward, up, fov);
        dialogueCameraWasDynamic = false;
        lastDynamicDialogueActor = "";
        GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active mode=static id={cameraId} " +
                 $"fov={fov:F3} position={position} xr={xrActive}");
    }

    private static void FaceModelToward(Node3D model, Vector3 target)
    {
        target.Y = model.GlobalPosition.Y;
        if (model.GlobalPosition.DistanceSquaredTo(target) < 0.0001f) return;
        model.LookAt(target, Vector3.Up);
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
        currentLipRig?.Modifier.SetNeutral();
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
        var mediaResref = string.IsNullOrWhiteSpace(node.Sound) ? node.Voice : node.Sound;
        playedDialogueMedia.Add(mediaResref);
        GD.Print($"NIKAMI_AURORA_DIALOGUE_AUDIO status=playing actor={actor} sound={mediaResref} " +
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
        if (pendingAutomaticDialogueGraph is not null &&
            !string.IsNullOrWhiteSpace(pendingAutomaticDialogueTarget))
        {
            AdvanceAutomaticDialogue();
            return;
        }
        foreach (var button in activeChoiceButtons)
            button.Disabled = false;
    }

    private void AdvanceAutomaticDialogue()
    {
        var graph = pendingAutomaticDialogueGraph;
        var target = pendingAutomaticDialogueTarget;
        pendingAutomaticDialogueGraph = null;
        pendingAutomaticDialogueTarget = "";
        if (graph is null || string.IsNullOrWhiteSpace(target)) return;
        automaticDialogueTransitionCount++;
        GD.Print($"NIKAMI_AURORA_DIALOGUE_AUTO status=advanced " +
                 $"target={target} count={automaticDialogueTransitionCount}");
        PresentDialogueNode(graph, target, new HashSet<string>(), 0);
    }

    private void StopDialoguePerformance()
    {
        var actor = currentVoiceActor;
        dialogueVoice.Stop();
        ClearLipPose();
        currentLipTrack = null;
        currentLipSegment = -1;
        currentVoiceActor = "";
        pendingAutomaticDialogueGraph = null;
        pendingAutomaticDialogueTarget = "";
        currentDialogueNodeKey = "";
        if (!string.IsNullOrWhiteSpace(actor))
            PlayActorAnimation(actor, "pause1");
    }

    private void RestoreGameplayCamera()
    {
        if (!dialogueCameraActive) return;
        dialogueCameraActive = false;
        dialogueCameraWasDynamic = false;
        lastDynamicDialogueActor = "";
        if (xrActive)
        {
            xrSpectatorFieldOfView = gameplayFieldOfView;
            RecenterXrGameplayBase();
        }
        else
        {
            cinematicCamera.Current = false;
            camera.Current = true;
            camera.TopLevel = false;
            camera.Transform = Transform3D.Identity;
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            camera.Fov = gameplayFieldOfView;
        }
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
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
        var target = playerModel.GlobalPosition + Vector3.Up * 1.1f;
        var forward = -playerModel.GlobalTransform.Basis.Z.Normalized();
        var right = playerModel.GlobalTransform.Basis.X.Normalized();
        var snapshot = gameplaySimulation?.CaptureSnapshot();
        var inspectLeftHand = snapshot?.Equipment.ContainsKey(
            KotorEquipmentSlot.LeftHand) == true &&
            snapshot.Equipment.ContainsKey(KotorEquipmentSlot.RightHand) == false;
        var eye = target + forward * 1.8f +
                  right * (inspectLeftHand ? -1.1f : 1.1f) +
                  Vector3.Up * 0.1f;
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
                 $"hand={(inspectLeftHand ? "left" : "right")} " +
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

    private void FrameXrBodyLookDown()
    {
        if (!xrActive || playerModel is null)
            throw new InvalidDataException(
                "XR body look-down gate requires an active local XR avatar");
        dialogueCameraActive = false;
        var desiredEye = playerBody.GlobalTransform * cameraPivot.Position;
        var playerForward = -playerBody.GlobalBasis.Z.Normalized();
        var leftHand = FindDescendants<Node3D>(playerModel).Single(node =>
            node.Name.ToString().Equals("lhand", StringComparison.OrdinalIgnoreCase));
        var rightHand = FindDescendants<Node3D>(playerModel).Single(node =>
            node.Name.ToString().Equals("rhand", StringComparison.OrdinalIgnoreCase));
        var handMidpoint = (leftHand.GlobalPosition + rightHand.GlobalPosition) * 0.5f;
        var target = handMidpoint + playerForward * 0.15f;
        var desiredHead = new Transform3D(Basis.Identity, desiredEye)
            .LookingAt(target, Vector3.Up);
        xrGameplayOriginOffset = playerBody.GlobalTransform.AffineInverse() *
                                 desiredHead * xrCamera.Transform.AffineInverse();
        xrGameplayOriginCalibrated = true;
        ApplyXrGameplayBase();
        // A wide spectator lens shows the same downward HMD pose together with
        // the avatar's shoulders, arms, hands, and right-hand weapon.
        xrSpectatorFieldOfView = 90.0f;
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
        currentDialogueNodeKey = "xr:body-lookdown";
        GD.Print($"NIKAMI_AURORA_XR_BODY_VIEW status=ready eye={desiredEye} " +
                 $"leftHand={leftHand.GlobalPosition} rightHand={rightHand.GlobalPosition} " +
                 "head=hidden body=visible hands=left,right");
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
        if (!animationPlayer.HasAnimation("talk")) return null;
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
        currentLipRig?.Modifier.SetNeutral();
        currentLipRig = null;
    }

    private void PresentDialogueNode(DialogueGraph graph, string key, HashSet<string> visited, int depth)
    {
        if (depth > 32 || !visited.Add(key) || !graph.Nodes.TryGetValue(key, out var node))
        {
            FinishDialogue();
            return;
        }
        currentDialogueNodeKey = key;
        GD.Print($"NIKAMI_AURORA_DIALOGUE_NODE status=presented key={key} " +
                 $"kind={node.Kind} speaker={node.Speaker} sound={node.Sound}");
        ExecuteScript(node.Script1);
        ExecuteScript(node.Script2);
        ApplyDialogueAnimations(node);
        ApplyDialogueCamera(node);
        if (string.IsNullOrWhiteSpace(node.Text))
        {
            if (node.Links.Count > 0)
                PresentDialogueNode(graph, node.Links[0].Target, visited, depth + 1);
            else
                FinishDialogue();
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

        if (TryGetAutomaticDialogueTarget(graph, node, out var automaticTarget))
        {
            pendingAutomaticDialogueGraph = graph;
            pendingAutomaticDialogueTarget = automaticTarget;
            GD.Print($"NIKAMI_AURORA_DIALOGUE_AUTO status=armed " +
                     $"source={key} target={automaticTarget} voice={dialogueVoice.Playing}");
            if (!dialogueVoice.Playing)
                Callable.From(AdvanceAutomaticDialogue).CallDeferred();
            return;
        }

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
            close.Pressed += FinishDialogue;
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

    private static bool TryGetAutomaticDialogueTarget(
        DialogueGraph graph,
        DialogueNode node,
        out string target)
    {
        target = "";
        if (!node.Kind.Equals("entry", StringComparison.OrdinalIgnoreCase) ||
            node.Links.Count != 1)
            return false;
        var link = node.Links[0];
        if (!string.IsNullOrWhiteSpace(link.Condition1) ||
            !string.IsNullOrWhiteSpace(link.Condition2) ||
            !graph.Nodes.TryGetValue(link.Target, out var reply) ||
            !reply.Kind.Equals("reply", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(reply.Text))
            return false;
        target = link.Target;
        return true;
    }

    private void FollowDialogueChoice(DialogueGraph graph, string key)
    {
        var visible = ResolveVisibleNode(graph, key, new HashSet<string>(), 0);
        if (visible is null)
        {
            FinishDialogue();
            return;
        }
        if (visible.Kind == "reply" && visible.Links.Count > 0)
            PresentDialogueNode(graph, visible.Links[0].Target, new HashSet<string>(), 0);
        else
            PresentDialogueNode(graph, key, new HashSet<string>(), 0);
    }

    private void FinishDialogue()
    {
        var completedConversation = currentDialogueConversation;
        dialoguePanel.Visible = false;
        StopDialoguePerformance();
        RestoreGameplayCamera();
        Input.MouseMode = Input.MouseModeEnum.Captured;
        currentDialogueConversation = "";
        if (completedConversation.Equals("end_room3", StringComparison.OrdinalIgnoreCase))
            FinishFirstEncounter();
    }

    private void AdvanceShowcaseRoute()
    {
        showcaseRouteFrames++;
        showcasePhaseFrames++;
        switch (showcasePhase)
        {
            case ShowcasePhase.OpeningDialogue:
                if (TryApplyShowcaseChoice(true)) return;
                if (!dialoguePanel.Visible && string.IsNullOrWhiteSpace(currentDialogueConversation) &&
                    showcaseOpeningChoiceCount == 5)
                {
                    if (!IsDoorOpen(RequireInteractiveDoor("end_door01")))
                        throw new InvalidDataException(
                            "Showcase opening dialogue did not open end_door01");
                    SetShowcasePhase(ShowcasePhase.Gear);
                }
                break;
            case ShowcasePhase.Gear:
                if (showcasePhaseFrames < 30) return;
                var locker = materializedPlaceables.Single(placeable =>
                    placeable.Source.Template.Equals(
                        "footlker001", StringComparison.OrdinalIgnoreCase));
                UsePlaceable(locker);
                EquipOpeningGear(null);
                var gearSnapshot = RequireGameplaySimulation().CaptureSnapshot();
                if (gearSnapshot.PlayerExperience != 50 ||
                    !gearSnapshot.Equipment.TryGetValue(
                        KotorEquipmentSlot.Armor, out var armor) ||
                    !armor.Equals("g_a_clothes01", StringComparison.OrdinalIgnoreCase) ||
                    !gearSnapshot.Equipment.TryGetValue(
                        KotorEquipmentSlot.RightHand, out var weapon) ||
                    !weapon.Equals("g_w_shortswrd01", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Showcase gear phase did not equip Clothing and Short Sword at XP 50");
                SetShowcasePhase(ShowcasePhase.Corridor);
                break;
            case ShowcasePhase.Corridor:
                if (showcasePhaseFrames < 60) return;
                var basis = new Basis(Vector3.Up, yaw);
                if (!MovePlayer(-basis.Z * 10.0f))
                    throw new InvalidDataException(
                        "Showcase corridor traversal was rejected by navigation");
                SetShowcasePhase(ShowcasePhase.Transmission);
                break;
            case ShowcasePhase.Transmission:
                if (!showcaseTransmissionVerified &&
                    currentDialogueNodeKey.Equals(
                        "entry:35", StringComparison.OrdinalIgnoreCase))
                {
                    var transmissionEntry = RequireGameplaySimulation().CaptureSnapshot();
                    if (automaticDialogueTransitionCount -
                        showcaseTransmissionAutomaticBaseline != 3 ||
                        activeChoiceButtons.Count != 2 ||
                        !transmissionEntry.GlobalNumbers.TryGetValue(
                            "END_CARTH_DLG", out var entryCarthGlobal) ||
                        entryCarthGlobal != 1 ||
                        !transmissionEntry.GlobalNumbers.TryGetValue(
                            "END_TRASK_DLG", out var entryTraskGlobal) ||
                        entryTraskGlobal != 11 || !transmissionEntry.MapRevealed)
                        throw new InvalidDataException(
                            "Showcase transmission did not reach the journal choice exactly");
                    showcaseTransmissionVerified = true;
                    GD.Print("NIKAMI_AURORA_SHOWCASE_TRANSMISSION status=pass " +
                             "automatic=3 globals=END_CARTH_DLG:1,END_TRASK_DLG:11 " +
                             "map=revealed choices=2");
                }
                if (TryApplyShowcaseChoice(false)) return;
                if (!dialoguePanel.Visible && string.IsNullOrWhiteSpace(currentDialogueConversation) &&
                    showcaseTransmissionChoiceCount == 2)
                {
                    var transmission = RequireGameplaySimulation().CaptureSnapshot();
                    if (!transmission.GlobalNumbers.TryGetValue(
                            "END_CARTH_DLG", out var carthGlobal) || carthGlobal != 1 ||
                        !transmission.GlobalNumbers.TryGetValue(
                            "END_TRASK_DLG", out var traskGlobal) || traskGlobal != 11 ||
                        !transmission.MapRevealed)
                        throw new InvalidDataException(
                            "Showcase corridor transmission state drifted");
                    SetShowcasePhase(ShowcasePhase.EncounterLeadIn);
                }
                break;
            case ShowcasePhase.EncounterLeadIn:
                if (showcasePhaseFrames < 60) return;
                StartFirstEncounter();
                SetShowcasePhase(ShowcasePhase.Encounter);
                break;
            case ShowcasePhase.Encounter:
                if (automatedFirstEncounterVerified &&
                    currentDialogueNodeKey.Equals(
                        "encounter:gameplay-ready", StringComparison.OrdinalIgnoreCase))
                    SetShowcasePhase(ShowcasePhase.FinalHold);
                break;
            case ShowcasePhase.FinalHold:
                if (showcasePhaseFrames < 120) return;
                VerifyShowcaseCompletion();
                SetShowcasePhase(ShowcasePhase.Complete);
                currentDialogueNodeKey = "showcase:complete";
                if (System.Environment.GetEnvironmentVariable(
                        "NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE") == "1")
                    Callable.From(() => RequestCleanExit(0)).CallDeferred();
                break;
            case ShowcasePhase.Disabled:
            case ShowcasePhase.Complete:
            default:
                break;
        }
    }

    private bool TryApplyShowcaseChoice(bool opening)
    {
        if (!dialoguePanel.Visible || dialogueVoice.Playing || activeChoiceButtons.Count == 0)
            return false;
        int? choice = opening
            ? currentDialogueNodeKey switch
            {
                "entry:55" or "entry:58" or "entry:71" or "entry:73" or "reply:92" => 0,
                _ => null
            }
            : currentDialogueNodeKey switch
            {
                "entry:35" or "reply:50" => 0,
                _ => null
            };
        if (choice is null || choice.Value >= activeChoiceButtons.Count)
            throw new InvalidDataException(
                $"Showcase reached an unsupported choice node: {currentDialogueNodeKey}");
        if (!showcaseChoiceNode.Equals(
                currentDialogueNodeKey, StringComparison.OrdinalIgnoreCase))
        {
            showcaseChoiceNode = currentDialogueNodeKey;
            showcaseChoiceHoldFrames = 0;
        }
        if (++showcaseChoiceHoldFrames < 30 || activeChoiceButtons[choice.Value].Disabled)
            return false;
        activeChoiceButtons[choice.Value].EmitSignal(BaseButton.SignalName.Pressed);
        if (opening)
            showcaseOpeningChoiceCount++;
        else
            showcaseTransmissionChoiceCount++;
        GD.Print($"NIKAMI_AURORA_SHOWCASE_CHOICE status=selected " +
                 $"phase={showcasePhase} node={showcaseChoiceNode} index={choice.Value}");
        showcaseChoiceNode = "";
        showcaseChoiceHoldFrames = 0;
        return true;
    }

    private void SetShowcasePhase(ShowcasePhase phase)
    {
        showcasePhase = phase;
        showcasePhaseFrames = 0;
        showcaseChoiceNode = "";
        showcaseChoiceHoldFrames = 0;
        if (phase == ShowcasePhase.Transmission)
            showcaseTransmissionAutomaticBaseline = automaticDialogueTransitionCount;
        GD.Print($"NIKAMI_AURORA_SHOWCASE status=phase phase={phase} " +
                 $"frame={showcaseRouteFrames}");
    }

    private void VerifyShowcaseCompletion()
    {
        var snapshot = RequireGameplaySimulation().CaptureSnapshot();
        var firstDoor = RequireInteractiveDoor("end_door01");
        var encounterDoor = RequireInteractiveDoor("end_door02");
        if (!automatedFirstEncounterVerified || cinematicSequenceActive ||
            !IsDoorOpen(firstDoor) || !IsDoorOpen(encounterDoor) ||
            snapshot.PlayerExperience != 50 || !snapshot.MapRevealed ||
            !snapshot.GlobalNumbers.TryGetValue("END_CARTH_DLG", out var carthGlobal) ||
            carthGlobal != 1 ||
            !snapshot.GlobalNumbers.TryGetValue("END_TRASK_DLG", out var traskGlobal) ||
            traskGlobal != 1 ||
            !snapshot.Equipment.ContainsKey(KotorEquipmentSlot.Armor) ||
            !snapshot.Equipment.ContainsKey(KotorEquipmentSlot.RightHand) ||
            (xrActive && xrLocalPlayerHeadVisible != false) ||
            showcaseOpeningChoiceCount != 5 || showcaseTransmissionChoiceCount != 2 ||
            !showcaseTransmissionVerified ||
            playedDialogueMedia.Count < 15 ||
            !currentMusicResref.Equals(
                "mus_theme_sith", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "Showcase route did not preserve its complete startup-to-action state");
        GD.Print("NIKAMI_AURORA_SHOWCASE status=pass " +
                 "route=boot->opening->gear->corridor->transmission->encounter->gameplay " +
                 $"frames={showcaseRouteFrames} choices=5+2 voices={playedDialogueMedia.Count} " +
                 $"xp={snapshot.PlayerExperience} music={currentMusicResref}");
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
        basePlayerRecord = source;
        playerEquipmentVariants = source.EquipmentVariants ?? [];
        openingEquipmentVariant = playerEquipmentVariants.SingleOrDefault(variant =>
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
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
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
        var playerFaceRig = BuildLipRig(model, playerAnimationPlayer);
        playerFaceRig?.Modifier.SetNeutral();
        GD.Print($"NIKAMI_AURORA_PLAYER_FACE status=" +
                 $"{(playerFaceRig is null ? "unavailable" : "neutralized")}");
        cameraPivot.Position = source.CameraOffset is { Count: >= 3 }
            ? ToGodot(source.CameraOffset)
            : Vector3.Up * source.Height;
        xrGameplayOriginCalibrated = false;
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
            var actorKeys = new[] { creature.Template, creature.Tag }
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var key in actorKeys)
                actorModels[key!] = actor;
            if (creature.TalkOffset is { Count: >= 3 })
            {
                foreach (var key in actorKeys)
                    actorTalkOffsets[key!] = ToGodot(creature.TalkOffset);
            }
            var animationPlayer = FindDescendant<AnimationPlayer>(actor);
            if (animationPlayer is not null)
            {
                foreach (var key in actorKeys)
                    actorAnimations[key!] = animationPlayer;
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
                    foreach (var key in actorKeys)
                        actorLipRigs[key!] = lipRig;
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

    private bool IsFirstEncounterEnvironmentReady(FirstEncounterRecord encounter) =>
        encounter.EnvironmentPlaceables.All(expected =>
            materializedPlaceables.Any(actual =>
                actual.Source.Template.Equals(
                    expected.Template, StringComparison.OrdinalIgnoreCase) &&
                (ToGodot(actual.Source.Position) - ToGodot(expected.Position))
                .LengthSquared() < 0.0001f));

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

    private InteractiveDoor RequireInteractiveDoor(string tag) =>
        interactiveDoors.FirstOrDefault(candidate =>
            candidate.Source.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidDataException($"Interactive door was not materialized: {tag}");

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
                case KotorEquipmentRemoved removed:
                    equipmentChanged = true;
                    GD.Print($"NIKAMI_AURORA_EQUIPMENT status=removed " +
                             $"slot={removed.Slot} item={removed.Item.Resref}");
                    break;
                case KotorItemUsed used:
                    GD.Print($"NIKAMI_AURORA_ITEM status=used item={used.Item.Resref} " +
                             $"quantity={used.QuantityBefore}->{used.QuantityAfter} " +
                             $"target={used.PartyMemberId} " +
                             $"vitality={used.VitalityBefore}->{used.VitalityAfter}");
                    break;
                case KotorPartyMemberSelected selected:
                    GD.Print($"NIKAMI_AURORA_PARTY status=selected " +
                             $"member={selected.BeforeId}->{selected.AfterId}");
                    break;
                case KotorTriggerEntered entered:
                    GD.Print($"NIKAMI_AURORA_TRIGGER status=entered " +
                             $"id={entered.Trigger.InstanceId} " +
                             $"template={entered.Trigger.Template} " +
                             $"onEnter={entered.Trigger.OnEnterScript}");
                    break;
                case KotorGlobalNumberChanged global:
                    GD.Print($"NIKAMI_AURORA_GLOBAL status=changed name={global.Name} " +
                             $"value={global.Before}->{global.After}");
                    break;
                case KotorMapRevealed map:
                    GD.Print($"NIKAMI_AURORA_MAP status=revealed value={map.Before}->{map.After}");
                    break;
                case KotorDialogueRequested requested:
                    PresentRequestedDialogue(requested);
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
            if (GodotObject.IsInstanceValid(label))
                label.QueueFree();
        }));
    }

    private void PresentEquipment(KotorGameplaySnapshot snapshot)
    {
        var unsupportedSlots = snapshot.Equipment.Keys.Where(slot =>
            slot is not KotorEquipmentSlot.Armor and
            not KotorEquipmentSlot.LeftHand and
            not KotorEquipmentSlot.RightHand).ToArray();
        if (unsupportedSlots.Length > 0)
            throw new InvalidDataException(
                $"Player equipment has no visual variant coverage: " +
                string.Join(',', unsupportedSlots));
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.Armor, out var armor);
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.LeftHand, out var leftHand);
        snapshot.Equipment.TryGetValue(KotorEquipmentSlot.RightHand, out var rightHand);
        var basePlayer = basePlayerRecord
            ?? throw new InvalidDataException("Base player model is unavailable");
        var isBaseAppearance = armor is null && leftHand is null && rightHand is null;
        var variant = isBaseAppearance
            ? null
            : playerEquipmentVariants.SingleOrDefault(candidate =>
                string.Equals(
                    candidate.ArmorResref, armor, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.LeftHandResref, leftHand, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    candidate.RightHandResref, rightHand, StringComparison.OrdinalIgnoreCase));
        if (!isBaseAppearance && variant is null)
            throw new InvalidDataException(
                $"No player model matches Armor={armor ?? "none"}, " +
                $"LeftHand={leftHand ?? "none"}, " +
                $"RightHand={rightHand ?? "none"}");

        var glb = variant?.Glb ?? basePlayer.Glb;
        var animationContract = variant?.Animation ?? basePlayer.Animation;
        var cameraOffset = variant?.CameraOffset ?? basePlayer.CameraOffset;
        var variantId = variant?.Id ?? "opening-base";
        var path = Path.GetFullPath(Path.Combine(playerManifestDirectory,
            glb.Replace('/', Path.DirectorySeparatorChar)));
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D model)
            throw new InvalidDataException($"Godot could not import player equipment model: {path}");
        model.Name = $"PlayerModel_{variantId}";
        var weaponNodes = FindDescendants<Node3D>(model).Where(node =>
            node.Name.ToString().StartsWith(
                "weapon__", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (variant?.WeaponHook is { Length: > 0 } expectedWeaponHook)
        {
            var weaponRoots = weaponNodes.Where(node =>
                node.GetParent() is not Node3D parent ||
                !parent.Name.ToString().StartsWith(
                    "weapon__", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (weaponRoots.Length != 1 ||
                !weaponRoots[0].GetParent().Name.ToString().Equals(
                    expectedWeaponHook, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Player weapon hierarchy does not attach to {expectedWeaponHook}");
            GD.Print($"NIKAMI_AURORA_PLAYER_WEAPON status=attached " +
                     $"variant={variantId} hook={expectedWeaponHook} " +
                     $"nodes={weaponNodes.Length}");
        }
        else if (weaponNodes.Length != 0)
        {
            throw new InvalidDataException(
                $"Unarmed player variant contains weapon nodes: {variantId}");
        }
        var animationPlayer = FindDescendant<AnimationPlayer>(model)
            ?? throw new InvalidDataException("Player equipment model has no animation player");
        foreach (var animationName in animationPlayer.GetAnimationList())
        {
            var clip = animationPlayer.GetAnimation(animationName);
            if (clip is not null)
                clip.LoopMode = Animation.LoopModeEnum.Linear;
        }
        foreach (var expected in animationContract.Animations)
            _ = FindAnimationName(animationPlayer, expected);
        var playerFaceRig = BuildLipRig(model, animationPlayer);
        playerFaceRig?.Modifier.SetNeutral();
        GD.Print($"NIKAMI_AURORA_PLAYER_FACE status=" +
                 $"{(playerFaceRig is null ? "unavailable" : "neutralized")} " +
                 $"variant={variantId}");

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
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
        playerAnimationPlayer = animationPlayer;
        currentPlayerAnimation = "";
        var walkAnimation = animationPlayer.GetAnimation(
            FindAnimationName(animationPlayer, "walk"));
        var runAnimation = animationPlayer.GetAnimation(
            FindAnimationName(animationPlayer, "run"));
        if (walkAnimation is null || runAnimation is null)
            throw new InvalidDataException("Player equipment movement animations are missing");
        playerWalkSpeed = basePlayer.WalkDistance / (float)walkAnimation.GetLength();
        playerRunSpeed = basePlayer.RunDistance / (float)runAnimation.GetLength();
        if (cameraOffset is { Count: >= 3 })
        {
            cameraPivot.Position = ToGodot(cameraOffset);
            xrGameplayOriginCalibrated = false;
            if (xrActive && !dialogueCameraActive)
                RecenterXrGameplayBase();
        }
        PlayPlayerAnimation(requestedAnimation);
        if (equipmentScreen?.Visible != true)
            ShowWorldNotice(
                snapshot.Equipment.Count == 0 ? "UNEQUIPPED" : "EQUIPPED",
                snapshot.Equipment.Values.ToArray());
        if (xrActive)
            (activeInteractionController ?? xrRightHand)
                .TriggerHapticPulse("haptic", 0.0, 0.5, 0.12, 0.0);
        GD.Print($"NIKAMI_AURORA_PLAYER_EQUIPMENT status=ready variant={variantId} " +
                 $"armor={armor ?? "none"} leftHand={leftHand ?? "none"} " +
                 $"rightHand={rightHand ?? "none"} " +
                 $"body={variant?.BodyModel ?? basePlayer.BodyModel} " +
                 $"texture={variant?.BodyTexture ?? basePlayer.BodyTexture} " +
                 $"head={variant?.HeadModel ?? basePlayer.HeadModel} " +
                 $"weapon={variant?.WeaponModel ?? "none"} " +
                 $"skins={animationContract.SkinCount} " +
                 $"headSkins={animationContract.HeadSkinCount} " +
                 $"animations={string.Join(',', animationContract.Animations)}");
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
        if (contract.Kind == KotorScriptContractKind.TriggerDialogue &&
            contract.TriggerDialogue is { } trigger)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName} trigger={trigger.TriggerTemplate} " +
                     $"global={trigger.GlobalName}:{trigger.GlobalValue} " +
                     $"actor={trigger.ActorTag} event={trigger.UserEvent} " +
                     $"conversation={trigger.Conversation} starter={trigger.DialogueStarter}");
            return;
        }
        if (contract.Kind is KotorScriptContractKind.GlobalNumberAdd or
            KotorScriptContractKind.GlobalNumberSet)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName} global={contract.GlobalName} " +
                     $"value={contract.GlobalValue}");
            return;
        }
        if (contract.Kind == KotorScriptContractKind.RevealMap)
        {
            GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                     $"kind={contract.KindName}");
            return;
        }
        GD.Print($"NIKAMI_AURORA_NCS status=executed script={contract.Resref} " +
                 $"kind={contract.KindName} door={contract.DoorTag} " +
                 $"pause={contract.PauseConversation} moveTarget={contract.MoveTargetTag} " +
                 $"run={contract.MoveRun} range={contract.MoveRange:F3} " +
                 $"resume={contract.ResumeConversation}");
    }

    private async void PresentRequestedDialogue(KotorDialogueRequested request)
    {
        try
        {
            inputLockedUntilMsec = Math.Max(
                inputLockedUntilMsec,
                Time.GetTicksMsec() + (ulong)Math.Ceiling(request.InputLockSeconds * 1000.0f));
            GD.Print($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=queued " +
                     $"actor={request.ActorTag} event={request.UserEvent} " +
                     $"conversation={request.Conversation} starter={request.StarterIndex} " +
                     $"delay={request.DelaySeconds:F3} inputLock={request.InputLockSeconds:F3}");
            if (request.DelaySeconds > 0)
            {
                var timer = GetTree().CreateTimer(request.DelaySeconds);
                await ToSignal(timer, SceneTreeTimer.SignalName.Timeout);
            }
            if (openingDialogueGraph is null ||
                !request.ActorTag.Equals(dialogueOwnerActor, StringComparison.OrdinalIgnoreCase) ||
                !request.Conversation.Equals(
                    openingDialogueConversation, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Triggered dialogue could not resolve {request.ActorTag}:{request.Conversation}");
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            RestoreGameplayCamera();
            currentDialogueConversation = request.Conversation;
            PresentDialogueStarter(openingDialogueGraph, request.StarterIndex);
            GD.Print($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=started " +
                     $"conversation={request.Conversation} starter={request.StarterIndex}");
        }
        catch (Exception exception)
        {
            GD.PushError($"NIKAMI_AURORA_DIALOGUE_TRIGGER status=fail error={exception}");
        }
    }

    private async void StartFirstEncounter()
    {
        if (firstEncounterStarted) return;
        firstEncounterStarted = true;
        try
        {
            var encounter = firstEncounter
                ?? throw new InvalidDataException("First encounter is unavailable");
            var graph = firstEncounterGraph
                ?? throw new InvalidDataException("First encounter dialogue is unavailable");
            cinematicSequenceActive = true;
            dialoguePanel.Visible = false;
            StopDialoguePerformance();
            var door = RequireInteractiveDoor(encounter.DoorTag);
            if (!IsDoorOpen(door))
                ToggleDoor(door);

            var playerWaypoint = encounter.PartyWaypoints.Single(item =>
                item.Tag.Equals("wp_end_room3_1", StringComparison.OrdinalIgnoreCase));
            var traskWaypoint = encounter.PartyWaypoints.Single(item =>
                item.Tag.Equals("wp_end_room3_2", StringComparison.OrdinalIgnoreCase));
            simulationPlayerPosition = ToNumerics(playerWaypoint.Position);
            playerBody.GlobalPosition = ToGodot(playerWaypoint.Position);
            yaw = playerWaypoint.Bearing;
            playerBody.Rotation = new Vector3(0, yaw, 0);
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            if (!actorModels.TryGetValue("end_trask", out var trask))
                throw new InvalidDataException("First encounter could not resolve Trask");
            trask.GlobalPosition = ToGodot(traskWaypoint.Position);
            trask.Rotation = new Vector3(0, traskWaypoint.Bearing, 0);

            var openingControl = graph.Nodes["entry:0"];
            ApplyDialogueAnimations(openingControl);
            ApplyStaticDialogueCamera(26);
            currentDialogueNodeKey = "encounter:camera26";
            GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=started " +
                     "door=end_door02 dialogue=end_room3 camera=26");

            await WaitSeconds(encounter.TimingSeconds.CameraSwitch);
            ApplyStaticDialogueCamera(19);
            currentDialogueNodeKey = "encounter:camera19";
            PlayActorAnimation("end_sith2", "b7a1", false);
            PlayActorAnimation("end_soldier2", "c3d4", false);
            FireEncounterBlaster("end_sith2", "end_soldier2");

            await WaitSeconds(encounter.TimingSeconds.SecondAttack);
            PlayActorAnimation("end_sith3", "b7a1", false);
            PlayActorAnimation("end_soldier2", "c3d4", false);
            FireEncounterBlaster("end_sith3", "end_soldier2");

            var elapsedBeforeBattleMusic =
                encounter.TimingSeconds.CameraSwitch + encounter.TimingSeconds.SecondAttack;
            await WaitSeconds(Math.Max(
                0.0f,
                encounter.TimingSeconds.BattleMusic - elapsedBeforeBattleMusic));
            var audio = firstEncounterAudio
                ?? throw new InvalidDataException("First encounter audio is unavailable");
            SwitchAreaMusic(audio.BattleMusic, encounter.Audio.BattleMusic.Resref);
            await WaitSeconds(Math.Max(
                0.0f,
                encounter.TimingSeconds.FirstControlResume -
                encounter.TimingSeconds.BattleMusic));
            var secondControl = graph.Nodes["entry:1"];
            ApplyDialogueAnimations(secondControl);
            ApplyStaticDialogueCamera(20);
            currentDialogueNodeKey = "encounter:camera20";
            PlayActorAnimation("end_sith2", "b7a1", false);
            FireEncounterBlaster("end_sith2", "end_soldier2");

            await WaitSeconds(encounter.TimingSeconds.ThirdAttack);
            PlayActorAnimation("end_soldier2", "die", false);
            await WaitSeconds(0.5f);

            currentDialogueConversation = encounter.SceneObject.Conversation;
            currentDialogueNodeKey = "entry:4";
            PresentDialogueNode(graph, "entry:4", new HashSet<string>(), 0);
        }
        catch (Exception exception)
        {
            cinematicSequenceActive = false;
            GD.PushError($"NIKAMI_AURORA_FIRST_ENCOUNTER status=fail error={exception}");
        }
    }

    private async System.Threading.Tasks.Task WaitSeconds(float seconds)
    {
        if (seconds <= 0) return;
        await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);
    }

    private void FinishFirstEncounter()
    {
        cinematicSequenceActive = true;
        firstEncounterCombatReady = true;
        if (actorModels.TryGetValue("end_sith2", out var sith2))
            FaceModelToward(sith2, playerBody.GlobalPosition + Vector3.Up * 1.0f);
        if (actorModels.TryGetValue("end_sith3", out var sith3))
            FaceModelToward(sith3, playerBody.GlobalPosition + Vector3.Up * 1.0f);
        PlayActorAnimation("end_soldier2", "dead");
        PlayActorAnimation("end_sith2", "b7a1", false);
        PlayActorAnimation("end_sith3", "b7a1", false);
        FireEncounterBlaster("end_sith3", "end_trask");
        ApplyStaticDialogueCamera(20);
        currentDialogueNodeKey = "encounter:combat-ready";
        GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=combat-ready " +
                 "hostiles=end_sith2,end_sith3 soldier=end_soldier2:dead");
        Callable.From(ReleaseFirstEncounterToGameplay).CallDeferred();
    }

    private async void ReleaseFirstEncounterToGameplay()
    {
        await WaitSeconds(3.0f);
        cinematicSequenceActive = false;
        RestoreGameplayCamera();
        if (firstEncounterAudio is { } audio && firstEncounter is { } encounter)
            SwitchAreaMusic(audio.BackgroundMusic, encounter.Audio.BackgroundMusic.Resref);
        currentDialogueNodeKey = "encounter:gameplay-ready";
        GD.Print("NIKAMI_AURORA_FIRST_ENCOUNTER status=gameplay-ready");
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

    private static StaticMaterialReport ConfigureStaticRoomMaterials(
        Node node, Color dynamicAmbient)
    {
        var lightmappedOpaque = 0;
        var baseOpaque = 0;
        var lightmappedTransparent = 0;
        var baseTransparent = 0;
        var sourceAdditiveCount = 0;
        if (node is MeshInstance3D instance && instance.Mesh is not null)
        {
            for (var surface = 0; surface < instance.Mesh.GetSurfaceCount(); surface++)
            {
                if (instance.GetActiveMaterial(surface) is not BaseMaterial3D source) continue;
                var sourceAdditive = source.ResourceName.ToString().EndsWith(
                    "__aurora_additive", StringComparison.OrdinalIgnoreCase);
                var sourceTransparent =
                    source.Transparency != BaseMaterial3D.TransparencyEnum.Disabled;
                if (source.AlbedoTexture is not null && source.EmissionTexture is not null)
                {
                    if (sourceAdditive)
                        throw new InvalidDataException(
                            "Additive lightmapped room material is unsupported");
                    var lightmapped = new ShaderMaterial
                    {
                        Shader = sourceTransparent
                            ? OdysseyTransparentLightmapShader
                            : OdysseyLightmapShader
                    };
                    lightmapped.SetShaderParameter("albedo_texture", source.AlbedoTexture);
                    lightmapped.SetShaderParameter("lightmap_texture", source.EmissionTexture);
                    lightmapped.SetShaderParameter("dynamic_ambient", new Vector3(
                        dynamicAmbient.R, dynamicAmbient.G, dynamicAmbient.B));
                    instance.SetSurfaceOverrideMaterial(surface, lightmapped);
                    if (sourceTransparent)
                        lightmappedTransparent++;
                    else
                        lightmappedOpaque++;
                    continue;
                }
                var material = (BaseMaterial3D)source.Duplicate();
                material.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                material.Transparency = sourceTransparent
                    ? BaseMaterial3D.TransparencyEnum.Alpha
                    : BaseMaterial3D.TransparencyEnum.Disabled;
                material.BlendMode = sourceAdditive
                    ? BaseMaterial3D.BlendModeEnum.Add
                    : BaseMaterial3D.BlendModeEnum.Mix;
                material.DepthDrawMode = sourceTransparent
                    ? BaseMaterial3D.DepthDrawModeEnum.Disabled
                    : BaseMaterial3D.DepthDrawModeEnum.OpaqueOnly;
                material.NoDepthTest = false;
                var albedo = material.AlbedoColor;
                if (!sourceTransparent)
                    albedo.A = 1.0f;
                material.AlbedoColor = albedo;
                material.Metallic = 0;
                material.Roughness = 1;
                instance.SetSurfaceOverrideMaterial(surface, material);
                if (sourceTransparent)
                    baseTransparent++;
                else
                    baseOpaque++;
                if (sourceAdditive)
                    sourceAdditiveCount++;
            }
        }
        foreach (var child in node.GetChildren())
        {
            var childReport = ConfigureStaticRoomMaterials(child, dynamicAmbient);
            lightmappedOpaque += childReport.LightmappedOpaque;
            baseOpaque += childReport.BaseOpaque;
            lightmappedTransparent += childReport.LightmappedTransparent;
            baseTransparent += childReport.BaseTransparent;
            sourceAdditiveCount += childReport.SourceAdditive;
        }
        return new StaticMaterialReport(
            lightmappedOpaque, baseOpaque, lightmappedTransparent, baseTransparent,
            sourceAdditiveCount);
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
        var previous = simulationPlayerPosition;
        simulationPlayerPosition = result.Position;
        playerBody.GlobalPosition = ToGodot(result.Position);
        if (result.Moved && gameplaySimulation is not null)
            ApplyGameplayTransition(
                gameplaySimulation.UpdateTriggers(previous, result.Position));
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
                "trigger-dialogue" => KotorScriptContractKind.TriggerDialogue,
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
                        contract.GlobalName
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no global"),
                        contract.GlobalValue
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no global value"),
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
                        contract.ActorScriptSourceSha256
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no actor-script hash"),
                        contract.ActorScriptInstructionCount
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no actor-script count"),
                        contract.ConditionScriptSourceSha256
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no condition hash"),
                        contract.ConditionScriptInstructionCount
                            ?? throw new InvalidDataException(
                                $"Trigger contract {contract.Resref} has no condition count"))
                    : null,
                contract.GlobalName,
                contract.GlobalValue);
        }).ToArray();
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
            .Where(trigger => contracts.Any(contract =>
                contract.Resref.Equals(
                    trigger.OnEnterScript, StringComparison.OrdinalIgnoreCase)))
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

    private static string DoorInstanceId(int index) => $"door:{index:D4}";

    private static string PlaceableInstanceId(int index) => $"placeable:{index:D4}";

    private static string TriggerInstanceId(int index) => $"trigger:{index:D4}";

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

    private sealed record ModuleManifest(
        string Schema,
        string Module,
        EntryRecord Entry,
        TargetRecord Target,
        AreaLightingRecord Lighting,
        CameraStyleRecord CameraStyle,
        KotorRuntimeConfiguration RuntimeConfiguration,
        KotorUiRecord Ui,
        PlayerRecord Player,
        IReadOnlyList<RoomRecord> Rooms,
        IReadOnlyList<CreatureRecord> Creatures,
        IReadOnlyList<DoorRecord> Doors,
        IReadOnlyList<PlaceableRecord> Placeables,
        IReadOnlyList<TriggerRecord> Triggers,
        IReadOnlyList<CameraRecord> Cameras,
        FirstEncounterRecord? FirstEncounter,
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
        string? ArmorResref,
        string? LeftHandResref,
        string? RightHandResref,
        string BodyModel,
        string BodyTexture,
        string HeadModel,
        string? WeaponModel,
        string? WeaponHook,
        IReadOnlyList<float>? TalkOffset,
        IReadOnlyList<float>? CameraOffset,
        PlayerAnimationRecord Animation,
        string? ArmorUtiSha256,
        string? LeftHandUtiSha256,
        string? RightHandUtiSha256,
        string BaseItemsSha256);
    private sealed record RoomRecord(string Model, string? Glb, IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<IReadOnlyList<float>>>? WalkmeshTriangles,
        IReadOnlyList<LightRecord>? Lights,
        IReadOnlyList<RoomEmitterRecord>? Emitters);
    private sealed record RoomEmitterRecord(
        string Schema,
        string NodePath,
        IReadOnlyList<float> AuthoredPosition,
        IReadOnlyList<float> Position,
        IReadOnlyList<float> Direction,
        FirstEncounterEffectTexture Texture,
        string Update,
        string Render,
        string Blend,
        int Flags,
        int XGrid,
        int YGrid,
        float BirthRate,
        float RandomBirthRate,
        float Velocity,
        float RandomVelocity,
        float Mass,
        float ParticleRotation,
        float SpreadRadians,
        float LifeExpectancy,
        IReadOnlyList<float> ColorStart,
        IReadOnlyList<float> ColorMid,
        IReadOnlyList<float> ColorEnd,
        float PercentStart,
        float PercentMid,
        float PercentEnd,
        float AlphaStart,
        float AlphaMid,
        float AlphaEnd,
        float SizeStart,
        float SizeMid,
        float SizeEnd,
        float FrameStart,
        float FrameEnd,
        float Fps,
        float BlurLength);
    private sealed record LightRecord(string Name, IReadOnlyList<float> Position,
        IReadOnlyList<float> Color, float Radius, float Multiplier, bool AmbientOnly,
        int DynamicType, bool AffectDynamic, bool Shadow, int Priority);
    private sealed record CameraRecord(int Id, IReadOnlyList<float> Position, float Height, float Fov,
        float PitchDegrees, IReadOnlyList<float> OrientationWxyz, IReadOnlyList<float> Forward,
        IReadOnlyList<float> Up);
    private sealed record FirstEncounterRecord(
        string Schema,
        string DoorTag,
        FirstEncounterSceneObject SceneObject,
        IReadOnlyList<FirstEncounterParticipant> Participants,
        IReadOnlyList<PlaceableRecord> EnvironmentPlaceables,
        IReadOnlyList<FirstEncounterWaypoint> PartyWaypoints,
        IReadOnlyList<int> CameraIds,
        FirstEncounterAnimationIds AnimationIds,
        FirstEncounterEffects Effects,
        FirstEncounterTiming TimingSeconds,
        FirstEncounterAudio Audio,
        IReadOnlyList<FirstEncounterScript> Scripts);
    private sealed record FirstEncounterSceneObject(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing,
        string Conversation,
        string OnUserDefined,
        string UtpSha256,
        DialogueReference Dialogue);
    private sealed record FirstEncounterParticipant(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing,
        string Glb,
        int FactionId,
        int HitPoints,
        int CurrentHitPoints,
        int MaxHitPoints,
        bool MinimumOneHitPoint,
        bool NoPermanentDeath,
        PlayerAnimationRecord Animation);
    private sealed record FirstEncounterWaypoint(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        float Bearing);
    private sealed record FirstEncounterAnimationIds(
        int Damage,
        int CutsceneAttack,
        int TraskFirstLine,
        int TraskCharge);
    private sealed record FirstEncounterTiming(
        float CameraSwitch,
        float BattleMusic,
        float FirstControlResume,
        float SecondAttack,
        float ThirdAttack);
    private sealed record FirstEncounterEffects(
        string Schema,
        string ProjectileModel,
        string ProjectileMdlSha256,
        string ProjectileMdxSha256,
        string MuzzleModel,
        string MuzzleMdlSha256,
        string MuzzleMdxSha256,
        float ProjectileSize,
        float MuzzleSize,
        float MuzzleLifetime,
        FirstEncounterEffectTexture LaserTexture,
        FirstEncounterEffectTexture MuzzleTexture,
        FirstEncounterEffectTexture FlareTexture);
    private sealed record FirstEncounterEffectTexture(
        string Resref,
        string Path,
        string SourceSha256,
        int SourceByteCount,
        string SourceType,
        string SourceTxi,
        string PayloadSha256,
        int ByteCount);
    private sealed record FirstEncounterAudio(
        string AmmunitionTypesSha256,
        string AmbientMusicSha256,
        int StandardMusicId,
        int BattleMusicId,
        int MusicDelayMilliseconds,
        FirstEncounterAudioSource BlasterShot,
        FirstEncounterAudioSource BlasterImpact,
        FirstEncounterAudioSource BackgroundMusic,
        FirstEncounterAudioSource BattleMusic);
    private sealed record FirstEncounterAudioSource(
        string Resref,
        string Path,
        string Format,
        string SourceSha256,
        int SourceByteCount,
        string SourceEncoding,
        string PayloadSha256,
        int ByteCount,
        string PayloadEncoding);
    private sealed record FirstEncounterScript(
        string Resref,
        string SourceSha256,
        int InstructionCount);
    private sealed record FirstEncounterAudioStreams(
        AudioStream BlasterShot,
        AudioStream BlasterImpact,
        AudioStream BackgroundMusic,
        AudioStream BattleMusic);
    private sealed record FirstEncounterEffectTextures(
        Texture2D Laser,
        Texture2D Muzzle,
        Texture2D Flare,
        float ProjectileSize,
        float MuzzleSize,
        float MuzzleLifetime);
    private sealed record CreatureRecord(string Template, string? Tag,
        IReadOnlyList<float> Position, float Bearing,
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
    private sealed record TriggerRecord(
        string Template,
        string Tag,
        IReadOnlyList<float> Position,
        IReadOnlyList<IReadOnlyList<float>> Geometry,
        string? OnEnter,
        float HighlightHeight,
        string UttSha256);
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
        string Voice,
        string Script1, string Script2,
        int CameraAngle, int? CameraId, float? CameraFov, float? CameraHeight,
        IReadOnlyList<DialogueAnimation> Animations, DialogueMedia? Media,
        IReadOnlyList<DialogueLink> Links);
    private sealed record DialogueAnimation(
        int AnimationId,
        string AnimationName,
        bool Looping,
        bool FireForget,
        string Participant);
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
        bool? ResumeConversation, string? TriggerTemplate, string? GlobalName, int? GlobalValue,
        string? ActorTag, int? UserEvent, float? InputLockSeconds, float? DelaySeconds,
        string? Conversation, int? DialogueStarter, string? ActorScriptSourceSha256,
        int? ActorScriptInstructionCount, string? ConditionScriptSourceSha256,
        int? ConditionScriptInstructionCount);
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
        int Placeables, int Triggers, int WalkmeshTriangles, int AuthoredLights,
        int AuthoredEmitters);
    private readonly record struct NavigationTriangle(Vector3 A, Vector3 B, Vector3 C);
    private readonly record struct StaticMaterialReport(
        int LightmappedOpaque,
        int BaseOpaque,
        int LightmappedTransparent,
        int BaseTransparent,
        int SourceAdditive);
    private readonly record struct RoomEmitterReport(int Smoke, int Spark, bool DamagedEnd);
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
