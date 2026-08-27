using System.Text.Json;
using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Infrastructure.World;
using OpenDAO.Infrastructure.Configuration;

namespace OpenDAO.Presentation.Cinematics;

internal sealed partial class OpeningCutsceneController : Node
{
    private const string RedcliffeCutscene = "arl100cs_sunset";
    private const string SpeakerRoot = "res://assets/generated/cutscenes/arl100cs_sunset/speakers/";
    private const string SelectedHeadToTargetClearance = "selected-head-to-target";
    private readonly Dictionary<int, CameraRecord> cameras = [];
    private readonly Dictionary<int, PointOfViewRule> pointOfViewRules = [];
    private readonly Dictionary<int, Node3D> actorNodes = [];
    private readonly Dictionary<int, LayeredAnimationPlayback> actorAnimations = [];
    private readonly HashSet<string> stoppedAnimationEvents =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> loggedCameraFraming = [];
    private readonly HashSet<Node3D> cameraOccludedActors = [];
    private int occlusionCameraId = -1;
    private readonly List<Node3D> speakerNodes = [];
    private readonly List<Node3D> hiddenWorldActors = [];
    private readonly List<SpeakerPlacement> speakerPlacements = [];
    private readonly List<CameraSwitch> switches = [];
    private readonly List<AnimationEvent> animationEvents = [];
    private readonly List<AudioEvent> audioEvents = [];
    private readonly List<string> modelPaths = [];
    private readonly List<AudioStreamPlayer> activeAudio = [];
    private Camera3D cutsceneCamera = null!;
    private Camera3D playerCamera = null!;
    private CanvasLayer letterbox = null!;
    private ColorRect blackout = null!;
    private Label subtitle = null!;
    private Label status = null!;
    private double elapsed;
    private double duration;
    private double speed = 1;
    private int nextAudio;
    private int nextAnimation;
    private int animationStarts;
    private int expectedAnimationStarts;
    private int expectedFacialLines;
    private int actorCount;
    private bool actorsReady;
    private bool captured;
    private bool facialCaptured;
    private bool facialCapturePending;
    private bool playing;
    private bool facialReady;
    private int faceLineStarts;
    private int faceAdvanceFailures;
    private FaceFxRuntime? faceFx;
    private string activeFaceReference = string.Empty;
    private string facialCurvesPath = string.Empty;
    private string facialActorsPath = string.Empty;
    private double activeFaceStart;
    private TaskCompletionSource completion = null!;

    internal bool IsPlaying => playing;
    internal bool CompletedSuccessfully { get; private set; }
    internal Transform3D FinalCameraTransform { get; private set; } = Transform3D.Identity;
    internal float FinalCameraFieldOfView { get; private set; } = 45;
    internal IReadOnlyDictionary<string, Transform3D> FinalActorTransforms => finalActorTransforms;
    internal IReadOnlyDictionary<string, LayeredAnimationState> FinalActorAnimations =>
        finalActorAnimations;
    private readonly Dictionary<string, Transform3D> finalActorTransforms =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayeredAnimationState> finalActorAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node3D> finalActorNodes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool retainActors;
    private bool presentationRetained;
    internal string CutsceneId { get; init; } = RedcliffeCutscene;
    internal IReadOnlyDictionary<string, Node3D> FinalActorNodes => finalActorNodes;

    private string ManifestPath =>
        $"res://assets/generated/cutscenes/{CutsceneId}/media-manifest.json";
    private string PresentationProfilePath =>
        $"res://config/dao/cinematics/{CutsceneId}.json";

    internal async Task PlayAsync(Camera3D gameplayCamera, Label worldStatus,
        IGodotModelCache modelCache, FaceFxRuntime facialRuntime,
        ICinematicActorModelResolver actorModelResolver,
        string playerGender, string playerAppearanceModelPath,
        string playerBedAppearanceModelPath,
        bool retainActorsForDialogue,
        Action? firstFrameReady,
        CancellationToken cancellationToken)
    {
        if (!LoadManifest(actorModelResolver, playerGender, playerAppearanceModelPath,
                playerBedAppearanceModelPath)) return;
        playerCamera = gameplayCamera;
        status = worldStatus;
        faceFx = facialRuntime;
        retainActors = retainActorsForDialogue;
        speed = Math.Max(0.1, ReadNumber("OPENDAO_CUTSCENE_TIME_SCALE", 1));
        completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessPriority = 100;
        // Own the visible frame before asynchronous model warming begins. Without
        // this cover, the gameplay camera and avatar leaked into the movie between
        // world load and the first authored camera switch.
        BuildPresentation();
        await modelCache.WarmAsync(modelPaths, this, cancellationToken);
        actorsReady = BuildSpeakers(modelCache);
        // Publish the authored camera and any geometry-derived POV occlusion
        // before this camera can contribute its first rendered frame.
        DispatchAnimations(0);
        foreach (var mixer in actorAnimations.Values) mixer.AdvanceOverlays(0);
        ApplyCamera(0);
        blackout.Visible = switches.Count > 0 && switches[0].Time > 0;
        playerCamera.Current = false;
        cutsceneCamera.Current = true;
        status.Visible = false;
        playing = true;
        SetProcess(true);
        firstFrameReady?.Invoke();
        GD.Print($"OPENDAO_CUTSCENE_STARTED id={CutsceneId} duration={duration:F2} cameras={cameras.Count} " +
                 $"switches={switches.Count} audio_events={audioEvents.Count} " +
                 $"animations={animationEvents.Count} facefx={(facialReady ? "full-graph" : "unavailable")} " +
                 $"actors={speakerNodes.Count}");

        using var registration = cancellationToken.Register(Skip);
        await completion.Task;
    }

    public override void _Process(double delta)
    {
        if (!playing) return;
        elapsed += delta * speed;
        ApplyCamera(elapsed);
        blackout.Visible = switches.Count > 0 && elapsed < switches[0].Time;
        DispatchAnimations(elapsed);
        foreach (var mixer in actorAnimations.Values) mixer.AdvanceOverlays(delta * speed);
        DispatchAudio(elapsed);
        if (activeFaceReference.Length > 0 && faceFx?.Advance(elapsed - activeFaceStart) == false)
        {
            faceAdvanceFailures++;
            activeFaceReference = string.Empty;
            GD.PushError("OPENDAO_FACEFX_FAIL reason=" +
                         (faceFx?.FailureReason ?? "facefx-runtime-unavailable"));
        }
        CaptureAcceptanceFrame(elapsed);
        if (elapsed >= duration) Finish(false);
    }

    internal void Skip()
    {
        if (playing) Finish(true);
    }

    internal void ReleaseRetainedPresentation()
    {
        if (!presentationRetained) return;
        presentationRetained = false;
        if (IsInstanceValid(cutsceneCamera))
        {
            cutsceneCamera.Current = false;
            cutsceneCamera.QueueFree();
        }
        if (IsInstanceValid(letterbox)) letterbox.QueueFree();
        GD.Print("OPENDAO_CINEMATIC_CAMERA_HANDOFF status=released source=cut target=dialogue");
    }

    internal IReadOnlyList<Node3D> TakeRetainedHiddenWorldActors()
    {
        if (!retainActors || hiddenWorldActors.Count == 0) return Array.Empty<Node3D>();
        var transferred = hiddenWorldActors.Where(IsInstanceValid).ToArray();
        hiddenWorldActors.Clear();
        GD.Print($"OPENDAO_CINEMATIC_ACTOR_HANDOFF status=transferred " +
                 $"source=cut target=dialogue hidden_world_actors={transferred.Length}");
        return transferred;
    }

    private bool LoadManifest(ICinematicActorModelResolver actorModelResolver,
        string playerGender, string playerAppearanceModelPath,
        string playerBedAppearanceModelPath)
    {
        var path = DaoRuntimePaths.ResolveSourcePath(ManifestPath);
        if (!File.Exists(path))
        {
            GD.PushWarning("Opening cutscene manifest is missing: " + path);
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        LoadPresentationProfile();
        duration = root.GetProperty("runtime").GetDouble();
        if (root.TryGetProperty("cameraActors", out var cameraActors))
            foreach (var actor in cameraActors.EnumerateArray())
            {
                var id = actor.GetProperty("id").GetInt32();
                cameras[id] = new CameraRecord(
                    ReadVector(actor.GetProperty("finalPosition")),
                    ReadQuaternion(actor.GetProperty("finalOrientation")), 65,
                    ReadVector(actor.GetProperty("originPosition")),
                    ReadQuaternion(actor.GetProperty("originOrientation")));
            }

        if (root.TryGetProperty("cameraTimeline", out var cameraTimeline))
            foreach (var action in cameraTimeline.EnumerateArray())
            {
                var type = action.GetProperty("type").GetInt32();
                if (type == 11)
                {
                    switches.Add(new CameraSwitch(action.GetProperty("start").GetDouble(),
                        action.GetProperty("cameraActorId").GetInt32()));
                    continue;
                }

                var actorId = action.GetProperty("actorId").GetInt32();
                if (!cameras.TryGetValue(actorId, out var camera)) continue;
                var curves = action.GetProperty("curves").EnumerateArray().ToArray();
                if (type == 13 && curves.Length > 0)
                    cameras[actorId] = camera with { FieldOfViewCurve = ReadCurve(curves[0]) };
                else if (type == 17 && curves.Length >= 3)
                {
                    var positionCurves = curves.Select(ReadCurve).ToArray();
                    cameras[actorId] = camera with { PositionCurves = positionCurves };
                }
                else if (type == 18 && curves.Length >= 3)
                    cameras[actorId] = camera with { RotationCurves = curves.Select(ReadCurve).ToArray() };
            }

        if ((cameras.Count == 0 || switches.Count == 0) &&
            CutsceneId.Equals(RedcliffeCutscene, StringComparison.OrdinalIgnoreCase))
            LoadFallbackCameraTimeline();

        if (root.TryGetProperty("speakerActors", out var speakerActors))
            foreach (var actor in speakerActors.EnumerateArray())
            {
                var sourcePosition = ReadVector(actor.GetProperty("position"));
                var sourceRotation = ReadQuaternion(actor.GetProperty("orientation"));
                var resref = actor.GetProperty("resref").GetString() ?? string.Empty;
                var modelPath = actor.TryGetProperty("modelPath", out var configuredPath)
                    ? configuredPath.GetString() ?? string.Empty
                    : SpeakerRoot + resref + ".glb";
                var appearanceModelPath = actorModelResolver.ResolveAppearanceModelPath(resref);
                speakerPlacements.Add(new SpeakerPlacement(
                    -1, resref, resref, modelPath, appearanceModelPath, string.Empty,
                    new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y),
                    ConvertBasis(sourceRotation), string.Empty, 1));
                modelPaths.Add(modelPath);
                if (appearanceModelPath.Length > 0) modelPaths.Add(appearanceModelPath);
            }
        if (root.TryGetProperty("characterActors", out var characterActors))
            foreach (var actor in characterActors.EnumerateArray())
            {
                var sourcePosition = ReadVector(actor.GetProperty("position"));
                var sourceRotation = ReadQuaternion(actor.GetProperty("orientation"));
                var modelPath = actor.GetProperty("modelPath").GetString() ?? string.Empty;
                var resref = actor.GetProperty("creature").GetString() ?? string.Empty;
                var mappingTag = actor.TryGetProperty("mappingTag", out var tag)
                    ? tag.GetString() ?? string.Empty
                    : string.Empty;
                var appearanceModelPath = string.Empty;
                var bedAppearanceModelPath = string.Empty;
                if (mappingTag.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                    actor.TryGetProperty("modelPathsByGender", out var genderModels) &&
                    genderModels.TryGetProperty(playerGender.ToLowerInvariant(), out var genderModel))
                    modelPath = genderModel.GetString() ?? modelPath;
                if (mappingTag.Equals("PLAYER", StringComparison.OrdinalIgnoreCase))
                {
                    appearanceModelPath = playerAppearanceModelPath;
                    bedAppearanceModelPath = playerBedAppearanceModelPath;
                }
                else
                    appearanceModelPath = actorModelResolver.ResolveAppearanceModelPath(resref);
                speakerPlacements.Add(new SpeakerPlacement(
                    actor.GetProperty("id").GetInt32(),
                    resref,
                    mappingTag.Length > 0 ? mappingTag : resref,
                    modelPath, appearanceModelPath, bedAppearanceModelPath,
                    new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y),
                    ConvertBasis(sourceRotation),
                    actor.TryGetProperty("baseAnimation", out var baseAnimation)
                        ? baseAnimation.GetString() ?? string.Empty
                        : string.Empty,
                    actor.TryGetProperty("poseSpeed", out var poseSpeed)
                        ? poseSpeed.GetDouble()
                        : 1));
                modelPaths.Add(modelPath);
                if (appearanceModelPath.Length > 0) modelPaths.Add(appearanceModelPath);
                if (bedAppearanceModelPath.Length > 0) modelPaths.Add(bedAppearanceModelPath);
            }
        if (speakerPlacements.Count == 0 &&
            CutsceneId.Equals(RedcliffeCutscene, StringComparison.OrdinalIgnoreCase))
            LoadFallbackSpeakerPlacements();

        if (root.TryGetProperty("animationTimeline", out var animationTimeline))
            foreach (var action in animationTimeline.EnumerateArray())
            {
                var resource = AnimationResourceName(action.GetProperty("animation").GetString() ?? string.Empty);
                animationEvents.Add(new AnimationEvent(action.GetProperty("start").GetDouble(),
                    action.GetProperty("stop").GetDouble(),
                    action.TryGetProperty("animationSpeed", out var animationSpeed)
                        ? animationSpeed.GetDouble()
                        : 1,
                    action.TryGetProperty("animationStartOffset", out var animationStartOffset)
                        ? animationStartOffset.GetDouble()
                        : 0,
                    action.GetProperty("actorId").GetInt32(), resource));
            }
        animationEvents.Sort((left, right) => left.Time.CompareTo(right.Time));
        var presentedActorIds = speakerPlacements.Select(value => value.ActorId).ToHashSet();
        expectedAnimationStarts = animationEvents
            .Where(value => presentedActorIds.Contains(value.ActorId))
            .Select(value => $"{value.ActorId}\0{value.Resource}")
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();

        facialCurvesPath = root.TryGetProperty("facialCurvesPath", out var curvesPath)
            ? curvesPath.GetString() ?? string.Empty
            : string.Empty;
        facialActorsPath = root.TryGetProperty("facialActorsPath", out var actorsPath)
            ? actorsPath.GetString() ?? string.Empty
            : string.Empty;

        foreach (var media in root.GetProperty("mediaEvents").EnumerateArray())
        {
            var kind = media.GetProperty("kind").GetString() ?? string.Empty;
            if (kind is not ("audio" or "dialogue")) continue;
            audioEvents.Add(new AudioEvent(media.GetProperty("start").GetDouble(),
                media.GetProperty("wavPath").GetString() ?? string.Empty,
                kind == "dialogue" ? media.GetProperty("speaker").GetString() ?? string.Empty : string.Empty,
                media.GetProperty("ref").GetString() ?? string.Empty,
                media.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                media.TryGetProperty("faceActor", out var faceActor)
                    ? faceActor.GetString() ?? string.Empty
                    : kind == "dialogue" ? "humanmale" : string.Empty));
        }

        switches.Sort((left, right) => left.Time.CompareTo(right.Time));
        audioEvents.Sort((left, right) => left.Time.CompareTo(right.Time));
        expectedFacialLines = audioEvents.Count(value => value.FaceActor.Length > 0);
        return cameras.Count > 0 && switches.Count > 0;
    }

    private void BuildPresentation()
    {
        cutsceneCamera = new Camera3D
        {
            Name = "AuthoredCutsceneCamera",
            Near = 0.05f,
            Far = 1000,
            // Eclipse CUT camera FOV values describe the horizontal aperture.
            // Godot defaults to a vertical aperture, which widens every authored
            // 16:9 shot. Lock the same axis as the retail camera.
            KeepAspect = Camera3D.KeepAspectEnum.Width
        };
        GetParent().AddChild(cutsceneCamera);
        letterbox = new CanvasLayer { Name = "CutscenePresentation", Layer = 50 };
        AddChild(letterbox);
        blackout = new ColorRect
        {
            Name = "AuthoredBlackout",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        blackout.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        letterbox.AddChild(blackout);
        var topBar = new ColorRect
        {
            Name = "LetterboxTop",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        topBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.TopWide);
        topBar.OffsetBottom = 70;
        letterbox.AddChild(topBar);
        var bottomBar = new ColorRect
        {
            Name = "LetterboxBottom",
            Color = Colors.Black,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        bottomBar.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        bottomBar.OffsetTop = -110;
        letterbox.AddChild(bottomBar);
        subtitle = new Label
        {
            Name = "Subtitle",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            Text = string.Empty
        };
        subtitle.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomWide);
        subtitle.OffsetLeft = 80;
        subtitle.OffsetRight = -80;
        subtitle.OffsetTop = -105;
        subtitle.OffsetBottom = -10;
        subtitle.AddThemeFontSizeOverride("font_size", 20);
        subtitle.AddThemeColorOverride("font_color", new Color(0.94f, 0.9f, 0.78f));
        subtitle.AddThemeColorOverride("font_outline_color", Colors.Black);
        subtitle.AddThemeConstantOverride("outline_size", 3);
        letterbox.AddChild(subtitle);
    }

    private void LoadPresentationProfile()
    {
        pointOfViewRules.Clear();
        var path = DaoRuntimePaths.ResolveSourcePath(PresentationProfilePath);
        if (!File.Exists(path)) return;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (!root.TryGetProperty("pointOfViewCameras", out var configured)) return;
        foreach (var value in configured.EnumerateArray())
        {
            var mode = value.GetProperty("clearanceMode").GetString() ?? string.Empty;
            if (!mode.Equals(SelectedHeadToTargetClearance, StringComparison.Ordinal))
                throw new InvalidDataException($"Unsupported DAO cinematic POV clearance mode: {mode}");
            var rule = new PointOfViewRule(
                value.GetProperty("cameraActorId").GetInt32(),
                value.GetProperty("hiddenActorId").GetInt32(),
                value.GetProperty("targetActorId").GetInt32(), mode);
            if (!pointOfViewRules.TryAdd(rule.CameraActorId, rule))
                throw new InvalidDataException(
                    $"Duplicate DAO cinematic POV camera rule: {rule.CameraActorId}");
        }
    }

    private bool BuildSpeakers(IGodotModelCache modelCache)
    {
        facialReady = expectedFacialLines == 0;
        if (expectedFacialLines > 0 &&
            (faceFx is null || !faceFx.Load(
                facialCurvesPath.Length > 0
                    ? facialCurvesPath
                    : "res://assets/generated/cutscenes/arl100cs_sunset/facefx-curves.json",
                facialActorsPath.Length > 0
                    ? facialActorsPath
                    : "res://assets/generated/cutscenes/arl100cs_sunset/facefx-actors.json")))
        {
            GD.PushError("OPENDAO_FACEFX_FAIL reason=" +
                         (faceFx?.FailureReason ?? "facefx-runtime-unavailable"));
            return false;
        }

        foreach (var worldActor in GetParent().FindChildren("*", "Node3D", true, false)
                     .OfType<Node3D>().Where(node => node.HasMeta("dao_actor")))
        {
            if (!worldActor.Visible) continue;
            worldActor.Visible = false;
            hiddenWorldActors.Add(worldActor);
        }
        if (GetParent().GetNodeOrNull<Node3D>("Player/AvatarRoot") is { Visible: true } playerAvatar)
        {
            playerAvatar.Visible = false;
            hiddenWorldActors.Add(playerAvatar);
        }

        foreach (var placement in speakerPlacements)
        {
            if (modelCache.Instantiate(placement.ModelPath) is not { } authoredSpeaker)
            {
                GD.PushError("OPENDAO_CUTSCENE_ACTOR_FAIL reason=model-missing:" + placement.Resref);
                return false;
            }
            var speaker = authoredSpeaker;
            if (placement.AppearanceModelPath.Length > 0)
            {
                var selectedPath = placement.BedAppearanceModelPath.Length > 0
                    ? placement.BedAppearanceModelPath
                    : placement.AppearanceModelPath;
                var selectedAppearance = modelCache.Instantiate(selectedPath);
                var appearanceFailure = "model-missing";
                if (selectedAppearance is null ||
                    !CinematicPlayerAppearance.AdoptSelectedActor(selectedAppearance, authoredSpeaker,
                        placement.BedAppearanceModelPath.Length > 0, out appearanceFailure))
                {
                    GD.PushError("OPENDAO_CUTSCENE_ACTOR_FAIL reason=player-appearance:" +
                                 appearanceFailure);
                    selectedAppearance?.Free();
                    authoredSpeaker.Free();
                    return false;
                }
                speaker = selectedAppearance;
                authoredSpeaker.Free();
            }
            var animationPlayer = speaker.FindChildren("*", "AnimationPlayer", true, false)
                .OfType<AnimationPlayer>().FirstOrDefault();
            speaker.Name = "CutsceneSpeaker_" + placement.Resref;
            GetParent().AddChild(speaker);
            speaker.GlobalTransform = new Transform3D(placement.Basis, placement.Position);
            finalActorTransforms[placement.DialogueKey] = speaker.GlobalTransform;
            finalActorNodes[placement.DialogueKey] = speaker;
            speakerNodes.Add(speaker);
            if (placement.ActorId >= 0)
            {
                actorNodes[placement.ActorId] = speaker;
                if (LayeredAnimationPlayback.Create(animationPlayer) is { } mixer)
                {
                    actorAnimations[placement.ActorId] = mixer;
                    if (placement.BaseAnimation.Length > 0 &&
                        !mixer.PlayBody(placement.BaseAnimation, speed: placement.PoseSpeed))
                        GD.PushError($"OPENDAO_CUTSCENE_ANIMATION_FAIL actor={placement.ActorId} " +
                                     $"resource={placement.BaseAnimation} reason=base-clip-missing");
                }
            }
            if (placement.BaseAnimation.Length == 0 &&
                animationEvents.All(value => value.ActorId != placement.ActorId))
                PlayDefaultAnimation(speaker);
            var faceActor = audioEvents.FirstOrDefault(value =>
                value.Speaker.Equals(placement.DialogueKey, StringComparison.OrdinalIgnoreCase) &&
                value.FaceActor.Length > 0).FaceActor ?? string.Empty;
            if (faceActor.Length > 0 && faceFx?.BindSpeaker(placement.DialogueKey, speaker, faceActor) != true)
            {
                GD.PushError("OPENDAO_FACEFX_FAIL reason=" +
                             (faceFx?.FailureReason ?? "facefx-runtime-unavailable"));
                return false;
            }
        }
        if (expectedFacialLines > 0 && CutsceneId.Equals(RedcliffeCutscene,
                StringComparison.OrdinalIgnoreCase) && faceFx?.ValidateBoundPoses() != true)
        {
            GD.PushError("OPENDAO_FACEFX_FAIL reason=" +
                         (faceFx?.FailureReason ?? "facefx-runtime-unavailable"));
            return false;
        }
        facialReady = expectedFacialLines == 0 || faceFx is not null;
        actorCount = speakerNodes.Count;
        if (expectedFacialLines > 0)
            GD.Print($"OPENDAO_FACEFX_READY animations={faceFx!.AnimationCount} speakers={faceFx.SpeakerCount} " +
                     $"graph_nodes=201 oracle_node_checks={faceFx.OracleNodeChecks} bones=38 " +
                     $"oracle_bone_checks={faceFx.OracleBoneChecks} " +
                     "material_outputs=12 synthesis=false");
        return true;
    }

    private static void PlayDefaultAnimation(Node root)
    {
        var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
        if (player is null) return;
        var animations = player.GetAnimationList().Where(name => name != "RESET").ToArray();
        if (animations.Length > 0) player.Play(animations[0]);
    }

    private void ApplyCamera(double time)
    {
        var selected = switches[0];
        foreach (var candidate in switches)
        {
            if (candidate.Time > time) break;
            selected = candidate;
        }
        if (!cameras.TryGetValue(selected.CameraId, out var record)) return;
        var sourcePosition = record.PositionCurves is { Length: >= 3 }
            ? record.OriginPosition + new Basis(record.OriginRotation) * new Vector3(
                (float)record.PositionCurves[0].Evaluate(time),
                (float)record.PositionCurves[1].Evaluate(time),
                (float)record.PositionCurves[2].Evaluate(time))
            : record.Position;
        cutsceneCamera.GlobalPosition = new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y);
        cutsceneCamera.GlobalBasis = ConvertCameraBasis(EvaluateCameraRotation(record, time));
        cutsceneCamera.Fov = record.FieldOfViewCurve is null
            ? record.FieldOfView
            : (float)record.FieldOfViewCurve.Evaluate(time);
        ApplyPointOfViewOcclusion(selected);
        LogCameraFraming(selected, time);
    }

    private void ApplyPointOfViewOcclusion(CameraSwitch selected)
    {
        if (occlusionCameraId != selected.CameraId)
        {
            foreach (var actor in cameraOccludedActors.Where(IsInstanceValid)) actor.Visible = true;
            cameraOccludedActors.Clear();
            occlusionCameraId = selected.CameraId;
        }
        if (ApplyConfiguredPointOfView(selected)) return;
        // Once a camera is identified as an authored POV shot, hold its near
        // actor hidden until the next authored camera switch.  Re-evaluating
        // the distance threshold every frame made the head flash as the
        // animated rig crossed the cutoff.
        if (cameraOccludedActors.Count > 0) return;

        var forward = -cutsceneCamera.GlobalBasis.Z.Normalized();
        var samples = new List<(int ActorId, Node3D Actor, float Distance, float Alignment)>();
        foreach (var (actorId, actor) in actorNodes)
        {
            var skeleton = actor.FindChildren("*", "Skeleton3D", true, false)
                .OfType<Skeleton3D>().FirstOrDefault();
            var head = skeleton?.FindBone("Head") ?? -1;
            if (skeleton is null || head < 0) continue;
            var headPosition = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(head)).Origin;
            var offset = headPosition - cutsceneCamera.GlobalPosition;
            var distance = offset.Length();
            var alignment = distance > 0.0001f ? forward.Dot(offset / distance) : 0;
            samples.Add((actorId, actor, distance, alignment));
        }

        // A CUT camera can be authored from an actor's point of view. Hide a
        // nearer head that is behind the camera's look direction when a
        // farther forward subject exists; absolute distance thresholds drift
        // across dwarf, elf, and human rigs and caused the Dalish foreground
        // body to clip the complete shot.
        var target = samples.Where(value => value.Alignment > 0)
            .OrderByDescending(value => value.Alignment)
            .ThenBy(value => value.Distance)
            .FirstOrDefault();
        var occluded = samples.Where(value => target.Actor is not null &&
                                               value.Actor != target.Actor &&
                                               (value.Alignment <= 0 ||
                                                IntersectsCamera(value.Actor, cutsceneCamera.GlobalPosition)) &&
                                               value.Distance < target.Distance)
            .ToArray();
        foreach (var sample in occluded.Where(value => cameraOccludedActors.Add(value.Actor)))
        {
            sample.Actor.Visible = false;
            GD.Print($"OPENDAO_CINEMATIC_CAMERA_OCCLUSION status=hidden camera={selected.CameraId} " +
                     $"actor={sample.ActorId} reason=" +
                     $"{(sample.Alignment <= 0 ? "nearer-head-behind-forward-subject" : "camera-inside-actor-geometry")}");
        }
    }

    private bool ApplyConfiguredPointOfView(CameraSwitch selected)
    {
        if (!pointOfViewRules.TryGetValue(selected.CameraId, out var rule)) return false;
        if (!actorNodes.TryGetValue(rule.HiddenActorId, out var source) ||
            !actorNodes.TryGetValue(rule.TargetActorId, out var target))
            throw new InvalidDataException(
                $"DAO cinematic POV rule references an unavailable actor: camera={selected.CameraId}");
        if (cameraOccludedActors.Add(source))
        {
            source.Visible = false;
            GD.Print($"OPENDAO_CINEMATIC_CAMERA_OCCLUSION status=hidden camera={selected.CameraId} " +
                     $"actor={rule.HiddenActorId} reason=profile-{rule.ClearanceMode}");
        }
        if (!TryGetHeadGeometry(source, out var sourceHead, out var sourceRadius) ||
            !TryGetHeadGeometry(target, out var targetHead, out _))
            throw new InvalidDataException(
                $"DAO cinematic POV rule could not resolve head geometry: camera={selected.CameraId}");
        var direction = targetHead - sourceHead;
        if (direction.IsZeroApprox())
            throw new InvalidDataException(
                $"DAO cinematic POV rule has coincident source and target heads: camera={selected.CameraId}");
        cutsceneCamera.GlobalPosition = sourceHead + direction.Normalized() * sourceRadius;
        cutsceneCamera.LookAt(targetHead, Vector3.Up);
        return true;
    }

    private static bool IntersectsCamera(Node3D actor, Vector3 cameraPosition)
    {
        var meshes = actor.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>()
            .Where(mesh => mesh.Visible && mesh.Mesh is not null)
            .ToArray();
        if (meshes.Any(mesh => mesh.GetAabb().HasPoint(
                mesh.GlobalTransform.AffineInverse() * cameraPosition))) return true;

        return TryGetHeadGeometry(actor, out var headPosition, out var radius) &&
               cameraPosition.DistanceTo(headPosition) <= radius;
    }

    private static bool TryGetHeadGeometry(Node3D actor, out Vector3 headPosition,
        out float radius)
    {
        headPosition = default;
        radius = 0;
        var skeleton = actor.FindChildren("*", "Skeleton3D", true, false)
            .OfType<Skeleton3D>().FirstOrDefault();
        var head = skeleton?.FindBone("Head") ?? -1;
        var face = actor.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>().FirstOrDefault(mesh => mesh.Mesh is not null &&
                mesh.Name.ToString().EndsWith("FaceM1", StringComparison.OrdinalIgnoreCase));
        if (skeleton is null || head < 0 || face is null) return false;
        headPosition = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(head)).Origin;
        var size = face.GetAabb().Size;
        var scale = face.GlobalBasis.Scale.Abs();
        var worldSize = new Vector3(size.X * scale.X, size.Y * scale.Y, size.Z * scale.Z);
        radius = worldSize.Length() * 0.5f;
        return radius > 0;
    }

    private void LogCameraFraming(CameraSwitch selected, double time)
    {
        if (time < selected.Time + 0.25 || !loggedCameraFraming.Add(selected.CameraId)) return;
        var forward = -cutsceneCamera.GlobalBasis.Z.Normalized();
        foreach (var (actorId, actor) in actorNodes.OrderBy(value => value.Key))
        {
            var skeleton = actor.FindChildren("*", "Skeleton3D", true, false)
                .OfType<Skeleton3D>().FirstOrDefault();
            var head = skeleton?.FindBone("Head") ?? -1;
            if (skeleton is null || head < 0) continue;
            var headPosition = (skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(head)).Origin;
            var offset = headPosition - cutsceneCamera.GlobalPosition;
            var distance = offset.Length();
            var alignment = distance > 0.0001f ? forward.Dot(offset / distance) : 0;
            GD.Print($"OPENDAO_CINEMATIC_FRAMING camera={selected.CameraId} actor={actorId} " +
                     $"distance={distance:0.###} forward_alignment={alignment:0.###} " +
                     $"camera_position={cutsceneCamera.GlobalPosition} head_position={headPosition}");
        }
    }

    private void DispatchAnimations(double time)
    {
        foreach (var animation in animationEvents.Where(value => value.Stop <= time))
        {
            var stopKey = $"{animation.ActorId}:{animation.Time}:{animation.Resource}";
            if (!stoppedAnimationEvents.Add(stopKey)) continue;
            if (actorAnimations.TryGetValue(animation.ActorId, out var active))
                active.Stop(animation.Resource);
        }
        while (nextAnimation < animationEvents.Count && animationEvents[nextAnimation].Time <= time)
        {
            var animation = animationEvents[nextAnimation++];
            if (!actorNodes.ContainsKey(animation.ActorId) ||
                !actorAnimations.TryGetValue(animation.ActorId, out var mixer)) continue;
            if (animationEvents.Take(nextAnimation - 1).Any(previous =>
                    previous.ActorId == animation.ActorId &&
                    previous.Resource.Equals(animation.Resource, StringComparison.OrdinalIgnoreCase)))
                continue;
            var offset = Math.Max(0, time - animation.Time);
            var played = LayeredAnimationPlayback.IsOverlay(animation.Resource)
                ? mixer.PlayOverlay(animation.Resource, offset, animation.Speed,
                    animation.StartOffset)
                : mixer.PlayBody(animation.Resource, offset, animation.Speed,
                    animation.StartOffset);
            if (!played)
            {
                GD.PushError($"OPENDAO_CUTSCENE_ANIMATION_FAIL actor={animation.ActorId} " +
                             $"resource={animation.Resource} reason=clip-missing");
                continue;
            }
            animationStarts++;
        }
    }

    private void DispatchAudio(double time)
    {
        while (nextAudio < audioEvents.Count && audioEvents[nextAudio].Time <= time)
        {
            var media = audioEvents[nextAudio++];
            AudioStream? stream = ResourceLoader.Exists(media.Path)
                ? GD.Load<AudioStream>(media.Path)
                : null;
            if (stream is null && media.Path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                stream = AudioStreamWav.LoadFromFile(
                    DaoRuntimePaths.ResolveSourcePath(media.Path));
            if (stream is null) continue;
            var player = new AudioStreamPlayer { Stream = stream };
            AddChild(player);
            if (media.Speaker.Length > 0 && media.FaceActor.Length > 0)
            {
                activeFaceReference = media.Reference;
                activeFaceStart = media.Time;
                if (faceFx?.StartLine(media.Reference, media.Speaker) == true)
                    faceLineStarts++;
                else
                    GD.PushError("OPENDAO_FACEFX_FAIL reason=" +
                                 (faceFx?.FailureReason ?? "facefx-runtime-unavailable"));
            }
            player.Finished += () =>
            {
                if (media.Speaker.Length > 0 && media.FaceActor.Length > 0 &&
                    activeFaceReference == media.Reference)
                {
                    faceFx?.Stop();
                    activeFaceReference = string.Empty;
                    subtitle.Text = string.Empty;
                }
                player.QueueFree();
            };
            player.Play();
            activeAudio.Add(player);
            subtitle.Text = media.Reference.Length > 0
                ? media.Subtitle.Length > 0 ? media.Subtitle : SubtitleFor(media.Reference)
                : string.Empty;
        }
    }

    private void CaptureAcceptanceFrame(double time)
    {
        if (OS.GetEnvironment("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") != "1") return;
        if (facialCapturePending)
        {
            facialCapturePending = false;
            facialCaptured = true;
            SaveViewport("OPENDAO_FACEFX_CAPTURE", "OPENDAO_FACEFX_CAPTURE");
        }
        var presentationCaptureTime = Math.Max(5, switches.Count > 0 ? switches[0].Time + 0.5 : 5);
        if (!captured && time >= presentationCaptureTime)
        {
            captured = true;
            SaveViewport("OPENDAO_CUTSCENE_CAPTURE", "OPENDAO_CUTSCENE_CAPTURE");
        }
        var facialCaptureTime = CutsceneId.Equals(RedcliffeCutscene, StringComparison.OrdinalIgnoreCase)
            ? 16.4
            : audioEvents.FirstOrDefault(value => value.FaceActor.Length > 0).Time + 0.25;
        if (!facialCaptured && !facialCapturePending && time >= facialCaptureTime)
            facialCapturePending = true;
    }

    private void Finish(bool skipped)
    {
        if (!playing) return;
        playing = false;
        SetProcess(false);
        foreach (var audio in activeAudio.Where(IsInstanceValid)) audio.Stop();
        faceFx?.Stop();
        foreach (var actor in cameraOccludedActors.Where(IsInstanceValid)) actor.Visible = true;
        cameraOccludedActors.Clear();
        occlusionCameraId = -1;
        if (retainActors)
            foreach (var placement in speakerPlacements)
                if (actorAnimations.TryGetValue(placement.ActorId, out var mixer))
                    finalActorAnimations[placement.DialogueKey] = mixer.Snapshot();
        if (!retainActors)
            foreach (var speaker in speakerNodes.Where(IsInstanceValid)) speaker.QueueFree();
        speakerNodes.Clear();
        if (!retainActors)
        {
            foreach (var actor in hiddenWorldActors.Where(IsInstanceValid)) actor.Visible = true;
            hiddenWorldActors.Clear();
        }
        CompletedSuccessfully = !skipped && actorsReady &&
                                animationStarts == expectedAnimationStarts &&
                                nextAudio == audioEvents.Count && facialReady &&
                                faceLineStarts == expectedFacialLines && faceAdvanceFailures == 0;
        FinalCameraTransform = cutsceneCamera.GlobalTransform;
        FinalCameraFieldOfView = cutsceneCamera.Fov;
        if (retainActors)
        {
            // The following DLG controller warms and installs its own camera
            // asynchronously. Keep the final CUT camera current until that camera
            // takes ownership; exposing the gameplay camera here caused full-body
            // and inside-mesh flashes between every authored presentation phase.
            cutsceneCamera.Current = true;
            playerCamera.Current = false;
            subtitle.Text = string.Empty;
            blackout.Visible = false;
            presentationRetained = true;
            GD.Print("OPENDAO_CINEMATIC_CAMERA_HANDOFF status=held source=cut target=dialogue");
        }
        else
        {
            cutsceneCamera.Current = false;
            playerCamera.Current = true;
            letterbox.QueueFree();
            cutsceneCamera.QueueFree();
        }
        status.Visible = !retainActors;
        GD.Print($"OPENDAO_CUTSCENE_FINISHED id={CutsceneId} skipped={(skipped ? 1 : 0)} elapsed={elapsed:F2} " +
                 $"animations={animationStarts}/{expectedAnimationStarts} media={nextAudio}/{audioEvents.Count}");
        if (OS.GetEnvironment("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1")
            GD.Print($"OPENDAO_CUTSCENE_ACCEPTANCE status={(CompletedSuccessfully ? "pass" : "fail")} " +
                     $"id={CutsceneId} facefx_lines={faceLineStarts}/{expectedFacialLines} " +
                     $"facefx_failures={faceAdvanceFailures} actors={actorCount}");
        completion.TrySetResult();
    }

    private void SaveViewport(string variable, string marker)
    {
        var path = OS.GetEnvironment(variable);
        if (path.Length == 0) return;
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"{marker} status={(error == Error.Ok ? "pass" : "fail")} path={path}");
    }

    private static CurveRecord ReadCurve(JsonElement curve)
    {
        var points = curve.GetProperty("vertices").EnumerateArray()
            .Select(value => new CurvePoint(value[0].GetDouble(), value[1].GetDouble())).ToArray();
        var transitions = curve.GetProperty("transitionTypes").EnumerateArray()
            .Select(value => value.GetInt32()).ToArray();
        return new CurveRecord(curve.GetProperty("baseValue").GetDouble(), points, transitions);
    }

    private static Vector3 ReadVector(JsonElement values) => new((float)values[0].GetDouble(),
        (float)values[1].GetDouble(), (float)values[2].GetDouble());
    private static Quaternion ReadQuaternion(JsonElement values) => new((float)values[0].GetDouble(),
        (float)values[1].GetDouble(), (float)values[2].GetDouble(), (float)values[3].GetDouble());
    private static double ReadNumber(string variable, double fallback) =>
        double.TryParse(OS.GetEnvironment(variable), out var value) ? value : fallback;

    private static string SubtitleFor(string reference) => reference switch
    {
        "376570" => "They're coming!",
        "376571" => "Get to your positions!",
        "376572" => "Make ready!",
        _ => string.Empty
    };

    private static string AnimationResourceName(string instanceName)
    {
        var marker = instanceName.LastIndexOf("__", StringComparison.Ordinal);
        return marker > 0 && int.TryParse(instanceName[(marker + 2)..], out _)
            ? instanceName[..marker]
            : instanceName;
    }

    private static string NormalizeAnimationName(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character)
            ? char.ToLowerInvariant(character)
            : '_'));

    private static Basis ConvertBasis(Quaternion sourceRotation)
    {
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        return conversion * new Basis(sourceRotation) * conversion.Inverse();
    }

    private static Basis ConvertCameraBasis(Quaternion sourceRotation) =>
        ConvertCameraBasis(new Basis(sourceRotation));

    private static Quaternion EvaluateCameraRotation(CameraRecord record, double time)
    {
        if (record.RotationCurves is not { Length: >= 3 }) return record.Rotation;
        var z = Mathf.DegToRad((float)record.RotationCurves[0].Evaluate(time));
        var x = Mathf.DegToRad((float)record.RotationCurves[1].Evaluate(time));
        var y = Mathf.DegToRad((float)record.RotationCurves[2].Evaluate(time));
        return (record.OriginRotation * new Quaternion(Vector3.Back, z) *
                new Quaternion(Vector3.Right, x) * new Quaternion(Vector3.Up, y)).Normalized();
    }

    private static Basis ConvertCameraBasis(Basis source)
    {
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        // DAO's authored camera frame is +X right, +Y forward, +Z up. Godot is
        // +X right, -Z forward, +Y up. Keep this local-frame conversion separate
        // from the DAO Z-up world conversion used by ordinary scene nodes.
        var cameraFrame = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, 1),
            new Vector3(0, -1, 0));
        return conversion * source * cameraFrame;
    }

    private void LoadFallbackSpeakerPlacements()
    {
        speakerPlacements.Clear();
        speakerPlacements.AddRange([
            new SpeakerPlacement(-1, "arl101cr_cutscene_militia_1", "arl101cr_cutscene_militia_1",
                SpeakerRoot + "arl101cr_cutscene_militia_1.glb", string.Empty, string.Empty,
                new Vector3(214.804382f, 3.027597f, -259.210052f), Basis.Identity, string.Empty, 1),
            new SpeakerPlacement(-1, "arl101cr_cutscene_militia_2", "arl101cr_cutscene_militia_2",
                SpeakerRoot + "arl101cr_cutscene_militia_2.glb", string.Empty, string.Empty,
                new Vector3(213.092743f, 3.415608f, -259.217560f), Basis.Identity, string.Empty, 1),
            new SpeakerPlacement(-1, "arl101cr_cutscene_militia_3", "arl101cr_cutscene_militia_3",
                SpeakerRoot + "arl101cr_cutscene_militia_3.glb", string.Empty, string.Empty,
                new Vector3(211.120087f, 3.920875f, -259.166077f), Basis.Identity, string.Empty, 1)
        ]);
        modelPaths.AddRange(speakerPlacements.Select(value => value.ModelPath));
    }

    private void LoadFallbackCameraTimeline()
    {
        cameras.Clear();
        switches.Clear();
        cameras[4] = new CameraRecord(new Vector3(196.941711f, 247.705048f, 28.7800694f),
            new Quaternion(0.0625809431f, -0.0440100878f, -0.643318892f, 0.761766076f), 65);
        cameras[5] = new CameraRecord(new Vector3(248.147949f, 281.596252f, 15.9874706f),
            new Quaternion(0.0613776222f, -0.0450063758f, -0.589615107f, 0.804090381f), 75);
        cameras[32] = new CameraRecord(new Vector3(260.488007f, 312.18515f, 3.61759424f),
            new Quaternion(0.03697199f, -0.0485797934f, -0.794264555f, 0.604497254f), 55);
        cameras[73] = new CameraRecord(new Vector3(269.393311f, 302.255859f, 2.79453659f),
            new Quaternion(-0.00282022078f, -0.00899446756f, 0.95427072f, 0.298795253f), 50);
        cameras[74] = new CameraRecord(new Vector3(240.010269f, 261.416077f, 8.33048439f),
            new Quaternion(-0.135792568f, 0.258354127f, -0.842113197f, 0.453496218f), 50);
        switches.AddRange([
            new CameraSwitch(0, 4),
            new CameraSwitch(4.5, 32),
            new CameraSwitch(9.16666698, 73),
            new CameraSwitch(14.833333, 74),
            new CameraSwitch(20, 5)
        ]);
    }

    private sealed record CameraRecord(Vector3 Position, Quaternion Rotation, float FieldOfView,
        Vector3 OriginPosition = default,
        Quaternion OriginRotation = default,
        CurveRecord[]? PositionCurves = null, CurveRecord[]? RotationCurves = null,
        CurveRecord? FieldOfViewCurve = null);
    private readonly record struct CameraSwitch(double Time, int CameraId);
    private readonly record struct PointOfViewRule(int CameraActorId, int HiddenActorId,
        int TargetActorId, string ClearanceMode);
    private readonly record struct AnimationEvent(double Time, double Stop, double Speed,
        double StartOffset, int ActorId, string Resource);
    private readonly record struct AudioEvent(double Time, string Path, string Speaker, string Reference,
        string Subtitle, string FaceActor);
    private readonly record struct SpeakerPlacement(int ActorId, string Resref, string DialogueKey, string ModelPath,
        string AppearanceModelPath, string BedAppearanceModelPath,
        Vector3 Position, Basis Basis, string BaseAnimation, double PoseSpeed);
    private readonly record struct CurvePoint(double Time, double Value);
    private sealed record CurveRecord(double BaseValue, CurvePoint[] Points, int[] Transitions)
    {
        internal double Evaluate(double time)
        {
            if (Points.Length == 0) return BaseValue;
            if (time < Points[0].Time) return BaseValue;
            if (time == Points[0].Time) return Points[0].Value;
            for (var index = 1; index < Points.Length; index++)
            {
                if (time > Points[index].Time) continue;
                var left = Points[index - 1];
                var right = Points[index];
                var transition = index - 1 < Transitions.Length ? Transitions[index - 1] : 1;
                if (transition == 2) return left.Value;
                return Mathf.Lerp(left.Value, right.Value,
                    (time - left.Time) / Math.Max(right.Time - left.Time, double.Epsilon));
            }
            return Points[^1].Value;
        }
    }
}
