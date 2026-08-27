using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.Story;
using OpenDAO.Infrastructure.World;
using OpenDAO.Infrastructure.Configuration;

namespace OpenDAO.Presentation.Cinematics;

/// <summary>
/// Executes the installed game's DLG graph and each line's embedded CUT block.
/// The extractor resolves text, voice, FaceFX, actors, animations, cameras, and
/// the owning ARE stage placement once; playback performs no archive traversal.
/// </summary>
internal sealed partial class AuthoredDialogueController : Node
{
    private const string FaceActorsPath =
        "res://assets/generated/cutscenes/start_wake/facefx-actors.json";
    private readonly Dictionary<int, DialogueNode> nodes = [];
    private readonly Dictionary<string, Node3D> actors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LayeredAnimationPlayback> actorAnimations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Node3D> hiddenWorldActors = [];
    private readonly Dictionary<string, int> actorPoses = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> pendingVisualTransitions = [];
    private readonly HashSet<string> loggedDialogueCameras =
        new(StringComparer.OrdinalIgnoreCase);
    private Node3D? bedPlayerActor;
    private Node3D? standingPlayerActor;
    private LayeredAnimationPlayback? standingPlayerAnimations;
    private Camera3D gameplayCamera = null!;
    private Camera3D dialogueCamera = null!;
    private CanvasLayer choiceLayer = null!;
    private VBoxContainer choiceList = null!;
    private FaceFxRuntime faceFx = null!;
    private StoryState story = null!;
    private Transform3D stageTransform = Transform3D.Identity;
    private DialogueManifest manifest = null!;
    private TaskCompletionSource<int>? pendingChoice;
    private bool capturedChoices;
    private bool capturedLine;
    private int automatedChoiceStep;

    internal bool IsPlaying { get; private set; }
    internal bool CompletedSuccessfully { get; private set; }
    internal string DialogueId { get; init; } = string.Empty;

    private string ManifestPath =>
        $"res://assets/generated/dialogues/{DialogueId}/dialogue-manifest.json";

    internal async Task PlayAsync(Camera3D playerCamera, IGodotModelCache modelCache,
        FaceFxRuntime facialRuntime, ICinematicActorModelResolver actorModelResolver,
        StoryState storyState, Transform3D initialCameraTransform,
        float initialCameraFieldOfView, IReadOnlyDictionary<string, Transform3D> initialActorTransforms,
        IReadOnlyDictionary<string, Node3D> initialActorNodes,
        IReadOnlyDictionary<string, LayeredAnimationState> initialActorAnimations,
        IReadOnlyList<Node3D> initialHiddenWorldActors,
        string playerGender, string playerAppearanceModelPath, string playerBedAppearanceModelPath,
        Action? cameraAcquired,
        CancellationToken cancellationToken)
    {
        if (!LoadManifest(actorModelResolver, playerGender, playerAppearanceModelPath,
                playerBedAppearanceModelPath))
        {
            GD.PushError($"OPENDAO_DIALOGUE_FAIL id={DialogueId} reason=manifest-invalid");
            return;
        }

        gameplayCamera = playerCamera;
        faceFx = facialRuntime;
        story = storyState;
        await modelCache.WarmAsync(manifest.Actors.Values.SelectMany(value =>
                new[] { value.ModelPath, value.AppearanceModelPath, value.BedAppearanceModelPath }
                    .Where(path => path.Length > 0)),
            this, cancellationToken);
        if (!BuildPresentation(modelCache, initialActorNodes, initialHiddenWorldActors))
        {
            GD.PushError($"OPENDAO_DIALOGUE_FAIL id={DialogueId} reason=presentation-invalid");
            Cleanup();
            return;
        }
        dialogueCamera.GlobalTransform = initialCameraTransform;
        dialogueCamera.Fov = initialCameraFieldOfView;
        foreach (var (mapping, transform) in initialActorTransforms)
            if (actors.TryGetValue(mapping, out var actor)) actor.GlobalTransform = transform;
        foreach (var (mapping, actorManifest) in manifest.Actors)
        {
            if (initialActorAnimations.TryGetValue(mapping, out var animation) &&
                actorAnimations.TryGetValue(mapping, out var mixer) && mixer.Restore(animation))
            {
                var restoredPose = manifest.Poses.FirstOrDefault(value =>
                    NormalizeAnimationName(value.Value).Equals(
                        NormalizeAnimationName(animation.Resource),
                        StringComparison.OrdinalIgnoreCase));
                if (restoredPose.Value is not null)
                {
                    actorPoses[mapping] = restoredPose.Key;
                    GD.Print($"OPENDAO_DIALOGUE_POSE_HANDOFF actor={mapping} " +
                             $"pose={restoredPose.Key} resource={animation.Resource}");
                }
                continue;
            }
            PlayInitialPose(mapping, actorManifest.BaseAnimation, actorManifest.PoseSpeed);
        }
        PlayInitialOverlay("OWNER", "mh_o.eyejitter");

        IsPlaying = true;
        gameplayCamera.Current = false;
        dialogueCamera.Current = true;
        cameraAcquired?.Invoke();
        GD.Print($"OPENDAO_DIALOGUE_STARTED id={DialogueId} start={manifest.StartNodeId} " +
                 $"nodes={nodes.Count} actors={actors.Count}");

        try
        {
            var cursor = nodes[manifest.StartNodeId];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidates = cursor.Children.Select(id => nodes.GetValueOrDefault(id))
                    .Where(node => node is not null && IsEligible(node)).Cast<DialogueNode>().ToArray();
                if (candidates.Length == 0) break;

                var playerChoices = candidates.Where(node =>
                    node.Speaker.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                    node.Text.Length > 0).ToArray();
                cursor = playerChoices.Length > 0
                    ? await PresentChoices(playerChoices, cancellationToken)
                    : candidates[0];
                if (!cursor.Speaker.Equals("PLAYER", StringComparison.OrdinalIgnoreCase))
                    await PlayLine(cursor, cancellationToken);
            }
            if (pendingVisualTransitions.Count > 0)
                await Task.WhenAll(pendingVisualTransitions);
            CompletedSuccessfully = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            GD.PushError($"OPENDAO_DIALOGUE_FAIL id={DialogueId} reason={exception.Message}");
        }
        finally
        {
            IsPlaying = false;
            Cleanup();
        }

        GD.Print($"OPENDAO_DIALOGUE_FINISHED id={DialogueId} " +
                 $"status={(CompletedSuccessfully ? "pass" : "fail")}");
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (pendingChoice is null || @event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        var index = key.Keycode switch
        {
            Key.Key1 or Key.Kp1 => 0,
            Key.Key2 or Key.Kp2 => 1,
            Key.Key3 or Key.Kp3 => 2,
            Key.Key4 or Key.Kp4 => 3,
            Key.Key5 or Key.Kp5 => 4,
            Key.Key6 or Key.Kp6 => 5,
            Key.Key7 or Key.Kp7 => 6,
            Key.Key8 or Key.Kp8 => 7,
            Key.Key9 or Key.Kp9 => 8,
            _ => -1
        };
        if (index >= 0 && index < choiceList.GetChildCount())
        {
            SelectChoice(index);
            GetViewport().SetInputAsHandled();
        }
    }

    private async Task<DialogueNode> PresentChoices(DialogueNode[] choices,
        CancellationToken cancellationToken)
    {
        foreach (var child in choiceList.GetChildren()) child.QueueFree();
        pendingChoice = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (var index = 0; index < choices.Length; index++)
        {
            var choiceIndex = index;
            var button = new Button
            {
                Name = $"Choice{index + 1}",
                Text = $"{index + 1}. {FormatPlayerText(choices[index].Text)}",
                Alignment = HorizontalAlignment.Left,
                CustomMinimumSize = new Vector2(560, 15),
                FocusMode = Control.FocusModeEnum.None,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand
            };
            foreach (var stateName in new[] { "normal", "hover", "pressed", "focus", "disabled" })
                button.AddThemeStyleboxOverride(stateName, new StyleBoxEmpty());
            button.AddThemeColorOverride("font_color", new Color(0.86f, 0.77f, 0.43f));
            button.AddThemeColorOverride("font_hover_color", new Color(1, 0.94f, 0.63f));
            button.AddThemeColorOverride("font_pressed_color", Colors.White);
            button.AddThemeColorOverride("font_outline_color", new Color(0, 0, 0, 0.98f));
            button.AddThemeConstantOverride("outline_size", 2);
            button.AddThemeFontSizeOverride("font_size", 12);
            if (LoadDragonTextFont() is { } font) button.AddThemeFontOverride("font", font);
            button.Pressed += () => SelectChoice(choiceIndex);
            choiceList.AddChild(button);
        }
        choiceLayer.Visible = true;

        if (OS.GetEnvironment("OPENDAO_CHARACTER_CREATION_ACCEPTANCE") == "1")
        {
            for (var frame = 0; frame < 8; frame++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (!capturedChoices)
            {
                capturedChoices = true;
                SaveViewport("OPENDAO_DIALOGUE_CAPTURE", "OPENDAO_DIALOGUE_CAPTURE");
            }
            var configuredChoices = ReadIntegerSequence("OPENDAO_DIALOGUE_CHOICES");
            var configuredHolds = ReadNumberSequence("OPENDAO_DIALOGUE_CHOICE_HOLD_SECONDS");
            var selected = configuredChoices.Length > 0
                ? configuredChoices[Math.Min(automatedChoiceStep, configuredChoices.Length - 1)]
                : int.TryParse(OS.GetEnvironment("OPENDAO_DIALOGUE_CHOICE"), out var legacyChoice)
                    ? legacyChoice
                    : 1;
            var automated = Math.Clamp(selected - 1, 0, choices.Length - 1);
            var hold = configuredHolds.Length > 0
                ? configuredHolds[Math.Min(automatedChoiceStep, configuredHolds.Length - 1)]
                : 0;
            GD.Print($"OPENDAO_DIALOGUE_AUTOMATION step={automatedChoiceStep + 1} " +
                     $"choice={automated + 1}/{choices.Length} hold={hold:F3}");
            automatedChoiceStep++;
            if (hold > 0)
                await ToSignal(GetTree().CreateTimer(hold), SceneTreeTimer.SignalName.Timeout);
            SelectChoice(automated);
        }

        using var registration = cancellationToken.Register(() => pendingChoice?.TrySetCanceled(cancellationToken));
        var result = await pendingChoice.Task;
        pendingChoice = null;
        choiceLayer.Visible = false;
        return choices[result];
    }

    private async Task PlayLine(DialogueNode node, CancellationToken cancellationToken)
    {
        var cutscene = node.Cutscene;
        GD.Print($"OPENDAO_DIALOGUE_LINE_STARTED id={node.Id} speaker={node.Speaker} " +
                 $"external_cut={(node.ResolvedExternalCutscene ? 1 : 0)}");
        if (cutscene is not null)
        {
            var cameraCount = cutscene.Actors.Count(value =>
                value.Role.Equals("camera", StringComparison.OrdinalIgnoreCase));
            var switchCount = cutscene.Actions.Count(value => value.Type == 11);
            if (node.ResolvedExternalCutscene)
                GD.Print($"OPENDAO_DIALOGUE_EXTERNAL_CUT status=ready line={node.Id} " +
                         $"ref={node.CutsceneReference} cameras={cameraCount} " +
                         $"switches={switchCount}");
            PlaceActors(cutscene);
            PrepareCamera(cutscene);
        }

        AudioStreamPlayer? audio = null;
        if (node.Media is not null)
        {
            var stream = LoadWav(node.Media.WavPath);
            if (stream is not null)
            {
                audio = new AudioStreamPlayer { Stream = stream };
                AddChild(audio);
                audio.Play();
            }
            if (actors.TryGetValue(node.Speaker, out var speaker))
            {
                var faceReference = node.Media.FaceReference.EndsWith("_m", StringComparison.OrdinalIgnoreCase)
                    ? node.Media.FaceReference[..^2]
                    : node.Media.FaceReference;
                if (!faceFx.StartLine(faceReference, ActorResref(node.Speaker)))
                    GD.PushError("OPENDAO_FACEFX_FAIL reason=" + faceFx.FailureReason);
            }
        }

        var timeScale = Math.Max(0.1, ReadNumber("OPENDAO_CUTSCENE_TIME_SCALE", 1));
        var duration = Math.Max(cutscene?.Runtime ?? 0, node.Media?.Duration ?? 0);
        var startedAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stoppedAnimations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elapsed = 0.0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cutscene is not null)
            {
                ApplyCamera(node.Id, cutscene, elapsed);
                DispatchAnimations(cutscene, elapsed, startedAnimations, stoppedAnimations);
                foreach (var mixer in actorAnimations.Values)
                    mixer.AdvanceOverlays(GetProcessDeltaTime() * timeScale);
            }
            if (node.Media is not null && !faceFx.Advance(elapsed))
                GD.PushError("OPENDAO_FACEFX_FAIL reason=" + faceFx.FailureReason);
            if (!capturedLine && elapsed >= Math.Min(1.2, duration * 0.45))
            {
                capturedLine = true;
                SaveViewport("OPENDAO_DIALOGUE_LINE_CAPTURE", "OPENDAO_DIALOGUE_LINE_CAPTURE");
            }
            if (elapsed >= duration) break;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            elapsed += GetProcessDeltaTime() * timeScale;
        }
        var clearedActions = ClearCutsceneActions(cutscene, startedAnimations);
        if (clearedActions != startedAnimations.Count)
            throw new InvalidOperationException($"dialogue-action-scope-leak:" +
                                                $"{clearedActions}/{startedAnimations.Count}");
        if (clearedActions > 0)
            GD.Print($"OPENDAO_DIALOGUE_ACTION_SCOPE state=cleared line={node.Id} " +
                     $"actions={clearedActions}");
        faceFx.Stop();
        if (audio is not null)
        {
            audio.Stop();
            audio.QueueFree();
        }
    }

    private bool BuildPresentation(IGodotModelCache modelCache,
        IReadOnlyDictionary<string, Node3D> initialActorNodes,
        IReadOnlyList<Node3D> initialHiddenWorldActors)
    {
        dialogueCamera = new Camera3D
        {
            Name = "AuthoredDialogueCamera",
            Near = 0.05f,
            Far = 1000,
            Fov = 45,
            KeepAspect = Camera3D.KeepAspectEnum.Width
        };
        GetParent().AddChild(dialogueCamera);
        choiceLayer = new CanvasLayer { Name = "AuthoredDialogueChoices", Layer = 70 };
        AddChild(choiceLayer);
        choiceList = new VBoxContainer
        {
            Name = "Choices",
            Position = new Vector2(76, 665),
            Size = new Vector2(600, 52),
            MouseFilter = Control.MouseFilterEnum.Pass
        };
        choiceList.AddThemeConstantOverride("separation", -1);
        choiceLayer.AddChild(choiceList);
        choiceLayer.Visible = false;

        foreach (var worldActor in initialHiddenWorldActors.Where(IsInstanceValid))
        {
            worldActor.Visible = false;
            hiddenWorldActors.Add(worldActor);
        }
        if (initialHiddenWorldActors.Count > 0)
            GD.Print($"OPENDAO_CINEMATIC_ACTOR_HANDOFF status=adopted " +
                     $"source=cut target=dialogue hidden_world_actors={hiddenWorldActors.Count}");

        foreach (var (mapping, actorManifest) in manifest.Actors)
        {
            if (initialActorNodes.TryGetValue(mapping, out var retainedActor) &&
                IsInstanceValid(retainedActor))
            {
                var dialogueAnimationBank = modelCache.Instantiate(actorManifest.ModelPath);
                var bankFailure = "model-missing";
                if (dialogueAnimationBank is null ||
                    !CinematicPlayerAppearance.MergeAnimationBank(retainedActor,
                        dialogueAnimationBank, out bankFailure))
                {
                    GD.PushError("OPENDAO_DIALOGUE_FAIL reason=animation-bank:" + bankFailure);
                    dialogueAnimationBank?.Free();
                    return false;
                }
                if (mapping.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                    actorManifest.BedAppearanceModelPath.Length > 0 &&
                    !PrepareStandingPlayer(modelCache, actorManifest, retainedActor,
                        dialogueAnimationBank, out bankFailure))
                {
                    GD.PushError("OPENDAO_DIALOGUE_FAIL reason=standing-player:" + bankFailure);
                    dialogueAnimationBank.Free();
                    return false;
                }
                dialogueAnimationBank.Free();
                retainedActor.Name = "DialogueActor_" + mapping;
                actors[mapping] = retainedActor;
                if (LayeredAnimationPlayback.Create(retainedActor) is not { } retainedMixer)
                    return false;
                actorAnimations[mapping] = retainedMixer;
                continue;
            }
            if (modelCache.Instantiate(actorManifest.ModelPath) is not { } authoredActor)
                return false;
            var actor = authoredActor;
            if (actorManifest.AppearanceModelPath.Length > 0)
            {
                var selectedPath = actorManifest.BedAppearanceModelPath.Length > 0
                    ? actorManifest.BedAppearanceModelPath
                    : actorManifest.AppearanceModelPath;
                var selectedAppearance = modelCache.Instantiate(selectedPath);
                var appearanceFailure = "model-missing";
                if (selectedAppearance is null ||
                    !CinematicPlayerAppearance.AdoptSelectedActor(selectedAppearance, authoredActor,
                        actorManifest.BedAppearanceModelPath.Length > 0, out appearanceFailure))
                {
                    GD.PushError("OPENDAO_DIALOGUE_FAIL reason=player-appearance:" +
                                 appearanceFailure);
                    selectedAppearance?.Free();
                    authoredActor.Free();
                    return false;
                }
                actor = selectedAppearance;
            }
            var animationPlayer = actor.FindChildren("*", "AnimationPlayer", true, false)
                .OfType<AnimationPlayer>().FirstOrDefault();
            actor.Name = "DialogueActor_" + mapping;
            GetParent().AddChild(actor);
            actors[mapping] = actor;
            if (LayeredAnimationPlayback.Create(animationPlayer) is not { } mixer) return false;
            actorAnimations[mapping] = mixer;
            if (mapping.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                actorManifest.BedAppearanceModelPath.Length > 0 &&
                !PrepareStandingPlayer(modelCache, actorManifest, actor, authoredActor,
                    out var standingFailure))
            {
                GD.PushError("OPENDAO_DIALOGUE_FAIL reason=standing-player:" + standingFailure);
                return false;
            }
            if (actor != authoredActor) authoredActor.Free();
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

        if (!faceFx.Load(manifest.FacialCurvesPath, FaceActorsPath)) return false;
        if (!actors.TryGetValue("OWNER", out var owner)) return false;
        if (faceFx.BindSpeaker(ActorResref("OWNER"), owner, "elffemale")) return true;
        GD.PushError("OPENDAO_FACEFX_FAIL reason=" + faceFx.FailureReason);
        return false;
    }

    private void PlaceActors(EmbeddedCutscene cutscene)
    {
        var cutsceneTransform = stageTransform * ConvertTransform(
            cutscene.Position, cutscene.Orientation);
        foreach (var actorRecord in cutscene.Actors.Where(value =>
                     value.Role.Equals("character", StringComparison.OrdinalIgnoreCase)))
        {
            var mapping = actorRecord.MappingTag.ToUpperInvariant();
            if (!actors.TryGetValue(mapping, out var actor)) continue;
            actor.GlobalTransform = cutsceneTransform * ConvertTransform(
                actorRecord.FinalPosition, actorRecord.FinalOrientation);
            actor.Visible = true;
            if (!manifest.Poses.TryGetValue(actorRecord.Pose, out var pose) ||
                !actorAnimations.TryGetValue(mapping, out var mixer)) continue;
            var hadPreviousPose = actorPoses.TryGetValue(mapping, out var previousPose);
            if (hadPreviousPose && previousPose == actorRecord.Pose) continue;
            var transitionDuration = 0.0;
            var transition = string.Empty;
            var transitioned = hadPreviousPose &&
                manifest.PoseTransitions.TryGetValue((previousPose, actorRecord.Pose), out transition) &&
                mixer.TransitionBody(transition, pose, actorRecord.PoseSpeed, out transitionDuration);
            if (transitioned)
            {
                GD.Print($"OPENDAO_POSE_TRANSITION actor={mapping} from={previousPose} " +
                         $"to={actorRecord.Pose} clip={transition} duration={transitionDuration:F3}");
                if (mapping.Equals("PLAYER", StringComparison.OrdinalIgnoreCase) &&
                    previousPose == 32 && actorRecord.Pose == 0)
                {
                    pendingVisualTransitions.Add(CapturePoseTransitionAsync(transitionDuration * 0.5));
                    pendingVisualTransitions.Add(SwitchToStandingPlayerAsync(actor,
                        transitionDuration, pose, actorRecord.PoseSpeed));
                }
            }
            if (!transitioned && !mixer.PlayBody(pose, speed: actorRecord.PoseSpeed))
                GD.PushError($"OPENDAO_DIALOGUE_ANIMATION_FAIL actor={mapping} base={pose}");
            actorPoses[mapping] = actorRecord.Pose;
        }
    }

    private int ClearCutsceneActions(EmbeddedCutscene? cutscene, HashSet<string> started)
    {
        if (cutscene is null || started.Count == 0) return 0;
        var cleared = 0;
        for (var actionIndex = 0; actionIndex < cutscene.Actions.Length; actionIndex++)
        {
            var action = cutscene.Actions[actionIndex];
            if (action.Type != 4 || action.Animation.Length == 0) continue;
            var actorRecord = cutscene.Actors.FirstOrDefault(value => value.Id == action.ActorId);
            if (actorRecord is null ||
                !actorAnimations.TryGetValue(actorRecord.MappingTag, out var mixer)) continue;
            var resource = AnimationResourceName(action.Animation);
            var key = $"{action.ActorId}:{actionIndex}:{action.Start}:{resource}";
            if (!started.Contains(key)) continue;
            mixer.StopAction(key);
            cleared++;
        }
        return cleared;
    }

    private void PrepareCamera(EmbeddedCutscene cutscene)
    {
        var cameraActorId = cutscene.Actions.Where(value => value.Type == 11)
            .OrderBy(value => value.Start).Select(value => value.CameraActorId).FirstOrDefault(-1);
        var camera = cutscene.Actors.FirstOrDefault(value => value.Id == cameraActorId &&
            value.Role.Equals("camera", StringComparison.OrdinalIgnoreCase));
        if (camera is null) return;
        dialogueCamera.GlobalTransform = stageTransform *
            ConvertTransform(cutscene.Position, cutscene.Orientation) *
            ConvertCameraTransform(camera.FinalPosition, camera.FinalOrientation);
        var fov = cutscene.Actions.FirstOrDefault(value => value.ActorId == cameraActorId && value.Type == 13);
        if (fov?.Curves.Length > 0) dialogueCamera.Fov = (float)fov.Curves[0].Evaluate(0);
    }

    private void ApplyCamera(int lineId, EmbeddedCutscene cutscene, double elapsed)
    {
        var switches = cutscene.Actions.Where(value => value.Type == 11 && value.Start <= elapsed)
            .OrderBy(value => value.Start).ToArray();
        if (switches.Length == 0) return;
        var selectedSwitch = switches[^1];
        var cameraActorId = selectedSwitch.CameraActorId;
        var camera = cutscene.Actors.FirstOrDefault(value => value.Id == cameraActorId);
        if (camera is null) return;
        var localPosition = camera.FinalPosition;
        var position = cutscene.Actions.FirstOrDefault(value =>
            value.ActorId == cameraActorId && value.Type == 17 && value.Curves.Length >= 3);
        if (position is not null && position.Curves.Any(curve => curve.Points.Length > 0))
            localPosition = camera.OriginPosition + new Basis(camera.OriginOrientation) * new Vector3(
                (float)position.Curves[0].Evaluate(elapsed),
                (float)position.Curves[1].Evaluate(elapsed),
                (float)position.Curves[2].Evaluate(elapsed));
        var localOrientation = camera.FinalOrientation;
        var rotation = cutscene.Actions.FirstOrDefault(value =>
            value.ActorId == cameraActorId && value.Type == 18 && value.Curves.Length >= 3);
        if (rotation is not null && rotation.Curves.Any(curve => curve.Points.Length > 0))
        {
            var z = Mathf.DegToRad((float)rotation.Curves[0].Evaluate(elapsed));
            var x = Mathf.DegToRad((float)rotation.Curves[1].Evaluate(elapsed));
            var y = Mathf.DegToRad((float)rotation.Curves[2].Evaluate(elapsed));
            localOrientation = (camera.OriginOrientation * new Quaternion(Vector3.Back, z) *
                                new Quaternion(Vector3.Right, x) *
                                new Quaternion(Vector3.Up, y)).Normalized();
        }
        dialogueCamera.GlobalTransform = stageTransform *
                                         ConvertTransform(cutscene.Position, cutscene.Orientation) *
                                         ConvertCameraTransform(localPosition, localOrientation);
        var fov = cutscene.Actions.FirstOrDefault(value =>
            value.ActorId == cameraActorId && value.Type == 13 && value.Curves.Length > 0);
        if (fov is not null) dialogueCamera.Fov = (float)fov.Curves[0].Evaluate(elapsed);
        var switchKey = $"{lineId}:{selectedSwitch.Start:R}:{cameraActorId}";
        if (loggedDialogueCameras.Add(switchKey))
            GD.Print($"OPENDAO_DIALOGUE_CAMERA_SWITCH line={lineId} " +
                     $"time={selectedSwitch.Start:F3} camera={cameraActorId}");
    }

    private void DispatchAnimations(EmbeddedCutscene cutscene, double elapsed,
        HashSet<string> started, HashSet<string> stopped)
    {
        for (var actionIndex = 0; actionIndex < cutscene.Actions.Length; actionIndex++)
        {
            var action = cutscene.Actions[actionIndex];
            if (action.Type != 4 || action.Stop > elapsed || action.Animation.Length == 0) continue;
            var actorRecord = cutscene.Actors.FirstOrDefault(value => value.Id == action.ActorId);
            if (actorRecord is null ||
                !actorAnimations.TryGetValue(actorRecord.MappingTag, out var mixer)) continue;
            var resource = AnimationResourceName(action.Animation);
            var key = $"{action.ActorId}:{actionIndex}:{action.Start}:{resource}";
            if (!stopped.Add(key)) continue;
            mixer.StopAction(key);
        }
        for (var actionIndex = 0; actionIndex < cutscene.Actions.Length; actionIndex++)
        {
            var action = cutscene.Actions[actionIndex];
            if (action.Type != 4 || action.Start > elapsed || action.Stop <= elapsed ||
                action.Animation.Length == 0) continue;
            var actorRecord = cutscene.Actors.FirstOrDefault(value => value.Id == action.ActorId);
            if (actorRecord is null ||
                !actorAnimations.TryGetValue(actorRecord.MappingTag, out var mixer)) continue;
            var resource = AnimationResourceName(action.Animation);
            var key = $"{action.ActorId}:{actionIndex}:{action.Start}:{resource}";
            if (started.Add(key) && !mixer.PlayAction(key, resource,
                    Math.Max(0, elapsed - action.Start), action.Speed, action.StartOffset))
                GD.PushError($"OPENDAO_DIALOGUE_ANIMATION_FAIL actor={actorRecord.MappingTag} " +
                             $"clip={resource}");
            mixer.SetActionPosition(key, action.StartOffset +
                Math.Max(0, elapsed - action.Start) * action.Speed);
            var weight = action.Curves.Length > 0 ? action.Curves[0].Evaluate(elapsed) / 100.0 : 1;
            mixer.SetActionWeight(key, weight);
        }
    }

    private bool LoadManifest(ICinematicActorModelResolver actorModelResolver,
        string playerGender, string playerAppearanceModelPath,
        string playerBedAppearanceModelPath)
    {
        var path = DaoRuntimePaths.ResolveSourcePath(ManifestPath);
        if (!File.Exists(path)) return false;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.GetProperty("schema").GetString() != "opendao-dialogue-v1") return false;
        var actorsManifest = root.GetProperty("actors").EnumerateObject().ToDictionary(
            property => property.Name,
            property =>
            {
                var resref = property.Value.GetProperty("resref").GetString() ?? string.Empty;
                var configuredModelPath = property.Value.TryGetProperty("modelPathsByGender", out var genderModels) &&
                genderModels.TryGetProperty(playerGender.ToLowerInvariant(), out var genderModel)
                    ? genderModel.GetString() ?? string.Empty
                    : property.Value.GetProperty("modelPath").GetString() ?? string.Empty;
                var configuredStandingPath = property.Value.TryGetProperty("standingModelPathsByGender", out var standingGenderModels) &&
                standingGenderModels.TryGetProperty(playerGender.ToLowerInvariant(), out var standingGenderModel)
                    ? standingGenderModel.GetString() ?? string.Empty
                    : property.Value.TryGetProperty("standingModelPath", out var standingModel)
                        ? standingModel.GetString() ?? string.Empty
                        : configuredModelPath;
                return new ActorManifest(resref,
                configuredModelPath,
                configuredStandingPath,
                property.Name.Equals("PLAYER", StringComparison.OrdinalIgnoreCase)
                    ? playerAppearanceModelPath
                    : actorModelResolver.ResolveAppearanceModelPath(resref),
                property.Name.Equals("PLAYER", StringComparison.OrdinalIgnoreCase)
                    ? playerBedAppearanceModelPath
                    : string.Empty,
                property.Value.TryGetProperty("baseAnimation", out var baseAnimation)
                    ? baseAnimation.GetString() ?? string.Empty
                    : string.Empty);
            },
            StringComparer.OrdinalIgnoreCase);
        var stage = root.GetProperty("stage");
        stageTransform = ConvertTransform(ReadVector(stage.GetProperty("position")),
            ReadQuaternion(stage.GetProperty("orientation")));
        var poses = root.TryGetProperty("poses", out var poseManifest)
            ? poseManifest.EnumerateObject().ToDictionary(
                property => int.Parse(property.Name),
                property => property.Value.GetString() ?? string.Empty)
            : new Dictionary<int, string>();
        var poseTransitions = root.TryGetProperty("poseTransitions", out var transitionManifest)
            ? transitionManifest.EnumerateObject().Select(property =>
            {
                var parts = property.Name.Split(':');
                return new KeyValuePair<(int, int), string>(
                    (int.Parse(parts[0]), int.Parse(parts[1])),
                    property.Value.GetString() ?? string.Empty);
            }).ToDictionary(value => value.Key, value => value.Value)
            : new Dictionary<(int, int), string>();
        manifest = new DialogueManifest(root.GetProperty("startNodeId").GetInt32(),
            root.GetProperty("facialCurvesPath").GetString() ?? string.Empty,
            actorsManifest, poses, poseTransitions);
        foreach (var value in root.GetProperty("nodes").EnumerateArray())
        {
            var node = ParseNode(value);
            nodes[node.Id] = node;
        }
        return nodes.ContainsKey(manifest.StartNodeId) && manifest.Actors.Count > 0;
    }

    private static DialogueNode ParseNode(JsonElement value)
    {
        EmbeddedCutscene? cutscene = null;
        if (value.TryGetProperty("embeddedCutscene", out var embedded) &&
            embedded.TryGetProperty("structType", out var structType) &&
            structType.GetString() == "CUT ")
        {
            var actors = embedded.GetProperty("actors").EnumerateArray().Select(actor =>
                new CutsceneActor(actor.GetProperty("id").GetInt32(),
                    actor.GetProperty("role").GetString() ?? string.Empty,
                    actor.GetProperty("mappingTag").GetString() ?? string.Empty,
                    actor.TryGetProperty("pose", out var pose) ? pose.GetInt32() : 0,
                    actor.TryGetProperty("poseSpeed", out var poseSpeed) ? poseSpeed.GetDouble() : 1,
                    ReadVector(actor.GetProperty("originPosition")),
                    ReadQuaternion(actor.GetProperty("originOrientation")),
                    ReadVector(actor.GetProperty("finalPosition")),
                    ReadQuaternion(actor.GetProperty("finalOrientation")))).ToArray();
            var actions = embedded.GetProperty("timeline").EnumerateArray().Select(action =>
                new CutsceneAction(action.GetProperty("actorId").GetInt32(),
                    action.GetProperty("type").GetInt32(), action.GetProperty("start").GetDouble(),
                    action.GetProperty("stop").GetDouble(),
                    action.GetProperty("animation").GetString() ?? string.Empty,
                    action.TryGetProperty("animationSpeed", out var animationSpeed)
                        ? animationSpeed.GetDouble()
                        : 1,
                    action.TryGetProperty("animationStartOffset", out var animationStartOffset)
                        ? animationStartOffset.GetDouble()
                        : 0,
                    action.GetProperty("cameraActorId").GetInt32(),
                    action.GetProperty("curves").EnumerateArray().Select(ParseCurve).ToArray())).ToArray();
            var cutscenePosition = embedded.TryGetProperty("position", out var position) &&
                                   position.GetArrayLength() >= 3
                ? ReadVector(position)
                : Vector3.Zero;
            var cutsceneOrientation = embedded.TryGetProperty("orientation", out var orientation) &&
                                      orientation.GetArrayLength() >= 4
                ? ReadQuaternion(orientation)
                : Quaternion.Identity;
            cutscene = new EmbeddedCutscene(embedded.GetProperty("runtime").GetDouble(),
                cutscenePosition, cutsceneOrientation, actors, actions);
        }
        DialogueMedia? media = null;
        if (value.TryGetProperty("media", out var mediaValue))
            media = new DialogueMedia(mediaValue.GetProperty("wavPath").GetString() ?? string.Empty,
                mediaValue.GetProperty("duration").GetDouble(),
                mediaValue.GetProperty("faceReference").GetString() ?? string.Empty);
        return new DialogueNode(value.GetProperty("id").GetInt32(),
            value.GetProperty("speaker").GetString() ?? string.Empty,
            value.GetProperty("text").GetString() ?? string.Empty,
            value.GetProperty("children").EnumerateArray().Select(child => child.GetInt32()).ToArray(),
            value.GetProperty("plotGuid").GetString() ?? string.Empty,
            value.GetProperty("plotFlag").GetString() ?? string.Empty, cutscene, media,
            value.TryGetProperty("cutsceneResRef", out var cutsceneReference)
                ? cutsceneReference.GetString() ?? string.Empty
                : string.Empty,
            value.TryGetProperty("resolvedCutsceneProvenance", out _));
    }

    private bool IsEligible(DialogueNode node) => node.PlotGuid.Length == 0 ||
        int.TryParse(node.PlotFlag, out var flag) && story.GetPlotFlag(node.PlotGuid, flag);

    private static int[] ReadIntegerSequence(string environmentName) =>
        OS.GetEnvironment(environmentName).Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                      StringSplitOptions.TrimEntries)
            .Select(value => int.TryParse(value, out var parsed) ? parsed : 1).ToArray();

    private static double[] ReadNumberSequence(string environmentName) =>
        OS.GetEnvironment(environmentName).Split(',', StringSplitOptions.RemoveEmptyEntries |
                                                      StringSplitOptions.TrimEntries)
            .Select(value => double.TryParse(value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? Math.Max(0, parsed)
                : 0).ToArray();

    private void SelectChoice(int index) => pendingChoice?.TrySetResult(index);

    private void PlayInitialPose(string mapping, string resource, double speed)
    {
        if (resource.Length == 0 || !actorAnimations.TryGetValue(mapping, out var mixer)) return;
        if (!mixer.PlayBody(resource, speed: speed))
            GD.PushError($"OPENDAO_DIALOGUE_ANIMATION_FAIL actor={mapping} base={resource}");
    }

    private void PlayInitialOverlay(string mapping, string resource)
    {
        if (!actorAnimations.TryGetValue(mapping, out var mixer)) return;
        if (!mixer.PlayOverlay(resource))
            GD.PushError($"OPENDAO_DIALOGUE_ANIMATION_FAIL actor={mapping} clip={resource}");
    }

    private async Task CapturePoseTransitionAsync(double delay)
    {
        var path = OS.GetEnvironment("OPENDAO_POSE_TRANSITION_CAPTURE");
        if (path.Length == 0) return;
        await ToSignal(GetTree().CreateTimer(Math.Max(0.05, delay)),
            SceneTreeTimer.SignalName.Timeout);
        if (IsInsideTree()) SaveViewport("OPENDAO_POSE_TRANSITION_CAPTURE",
            "OPENDAO_POSE_TRANSITION_CAPTURE");
    }

    private bool PrepareStandingPlayer(IGodotModelCache modelCache, ActorManifest actorManifest,
        Node3D bedActor, Node3D animationBank, out string failure)
    {
        failure = string.Empty;
        if (standingPlayerActor is not null) return true;
        var standing = modelCache.Instantiate(actorManifest.AppearanceModelPath);
        if (standing is null)
        {
            failure = "standing-model-missing";
            return false;
        }
        if (!CinematicPlayerAppearance.AdoptSelectedActor(standing, animationBank, false,
                out failure))
        {
            standing.Free();
            return false;
        }
        standing.Name = "DialogueActor_PLAYER_Standing";
        GetParent().AddChild(standing);
        standing.Visible = false;
        if (LayeredAnimationPlayback.Create(standing) is not { } mixer)
        {
            failure = "standing-animation-mixer-missing";
            standing.Free();
            return false;
        }
        bedPlayerActor = bedActor;
        standingPlayerActor = standing;
        standingPlayerAnimations = mixer;
        GD.Print("OPENDAO_CINEMATIC_PLAYER_PAIR status=ready visible=bed hidden=standing " +
                 "identity=selected-character");
        return true;
    }

    private async Task SwitchToStandingPlayerAsync(Node3D bedActor, double delay, string pose,
        double poseSpeed)
    {
        await ToSignal(GetTree().CreateTimer(Math.Max(0.05, delay)),
            SceneTreeTimer.SignalName.Timeout);
        if (!IsInsideTree() || !IsInstanceValid(bedActor) || standingPlayerActor is null ||
            standingPlayerAnimations is null) return;
        if (!actors.TryGetValue("PLAYER", out var currentActor) || currentActor != bedActor ||
            !bedActor.Visible)
            throw new InvalidOperationException("player-appearance-continuity-lost");

        standingPlayerActor.GlobalTransform = bedActor.GlobalTransform;
        if (!standingPlayerAnimations.PlayBody(pose, speed: poseSpeed))
            throw new InvalidOperationException("standing-pose-missing:" + pose);

        // Let the queued standing pose evaluate while hidden, then transfer the
        // final get-up bone pose before the atomic visibility handoff. This keeps
        // limbs and face continuous while retail's equipped clothing becomes visible.
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (!CinematicPlayerAppearance.CopyPose(bedActor, standingPlayerActor,
                out var poseFailure))
            throw new InvalidOperationException("standing-pose-copy:" + poseFailure);
        bedActor.Visible = false;
        standingPlayerActor.Visible = true;
        actors["PLAYER"] = standingPlayerActor;
        actorAnimations["PLAYER"] = standingPlayerAnimations;
        GD.Print("OPENDAO_DIALOGUE_PLAYER_APPEARANCE state=standing-clothed " +
                 "transition=32:0 source=retail-area-equipment");
        SaveViewport("OPENDAO_STANDING_DIALOGUE_CAPTURE",
            "OPENDAO_STANDING_DIALOGUE_CAPTURE");
    }

    private void Cleanup()
    {
        pendingChoice?.TrySetCanceled();
        pendingChoice = null;
        faceFx?.Stop();
        foreach (var actor in actors.Values.Where(IsInstanceValid)) actor.QueueFree();
        actors.Clear();
        actorAnimations.Clear();
        actorPoses.Clear();
        pendingVisualTransitions.Clear();
        if (IsInstanceValid(bedPlayerActor) && !bedPlayerActor.IsQueuedForDeletion())
            bedPlayerActor.QueueFree();
        if (IsInstanceValid(standingPlayerActor) && !standingPlayerActor.IsQueuedForDeletion())
            standingPlayerActor.QueueFree();
        bedPlayerActor = null;
        standingPlayerActor = null;
        standingPlayerAnimations = null;
        foreach (var item in hiddenWorldActors.Where(IsInstanceValid)) item.Visible = true;
        hiddenWorldActors.Clear();
        if (IsInstanceValid(dialogueCamera)) dialogueCamera.QueueFree();
        if (IsInstanceValid(choiceLayer)) choiceLayer.QueueFree();
        if (IsInstanceValid(gameplayCamera)) gameplayCamera.Current = true;
    }

    private string ActorResref(string mapping) =>
        manifest.Actors.GetValueOrDefault(mapping)?.Resref ?? mapping;

    private static AudioStream? LoadWav(string path)
    {
        if (ResourceLoader.Exists(path)) return GD.Load<AudioStream>(path);
        var global = DaoRuntimePaths.ResolveSourcePath(path);
        return File.Exists(global) ? AudioStreamWav.LoadFromFile(global) : null;
    }

    private static FontFile? LoadDragonTextFont()
    {
        var path = ProjectSettings.GlobalizePath("user://fonts/dragontext.ttf");
        if (!File.Exists(path)) return null;
        var font = new FontFile();
        return font.LoadDynamicFont(path) == Error.Ok ? font : null;
    }


    private void SaveViewport(string variable, string marker)
    {
        var path = OS.GetEnvironment(variable);
        if (path.Length == 0) return;
        var error = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"{marker} status={(error == Error.Ok ? "pass" : "fail")} path={path}");
    }

    private static string FormatPlayerText(string value) =>
        Regex.Replace(Regex.Replace(value, "<desc>", "(", RegexOptions.IgnoreCase),
            "</desc>", ")", RegexOptions.IgnoreCase).Trim();
    private static double ReadNumber(string variable, double fallback) =>
        double.TryParse(OS.GetEnvironment(variable), out var value) ? value : fallback;
    private static Vector3 ReadVector(JsonElement value) => new((float)value[0].GetDouble(),
        (float)value[1].GetDouble(), (float)value[2].GetDouble());
    private static Quaternion ReadQuaternion(JsonElement value) => new((float)value[0].GetDouble(),
        (float)value[1].GetDouble(), (float)value[2].GetDouble(), (float)value[3].GetDouble());
    private static Transform3D ConvertTransform(Vector3 sourcePosition, Quaternion sourceRotation) =>
        new(ConvertBasis(sourceRotation), new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y));
    private static Transform3D ConvertCameraTransform(Vector3 sourcePosition, Quaternion sourceRotation) =>
        new(ConvertCameraBasis(new Basis(sourceRotation)),
            new Vector3(sourcePosition.X, sourcePosition.Z, -sourcePosition.Y));
    private static Basis ConvertBasis(Quaternion sourceRotation)
    {
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        return conversion * new Basis(sourceRotation) * conversion.Inverse();
    }
    private static Basis ConvertCameraBasis(Basis source)
    {
        var conversion = new Basis(Vector3.Right, new Vector3(0, 0, -1), new Vector3(0, 1, 0));
        var cameraFrame = new Basis(new Vector3(1, 0, 0), new Vector3(0, 0, 1),
            new Vector3(0, -1, 0));
        return conversion * source * cameraFrame;
    }
    private static string AnimationResourceName(string value)
    {
        var marker = value.LastIndexOf("__", StringComparison.Ordinal);
        return marker > 0 && int.TryParse(value[(marker + 2)..], out _) ? value[..marker] : value;
    }
    private static string NormalizeAnimationName(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character)
            ? char.ToLowerInvariant(character) : '_'));
    private static CurveRecord ParseCurve(JsonElement value) => new(
        value.GetProperty("baseValue").GetDouble(),
        value.GetProperty("vertices").EnumerateArray().Select(point =>
            new CurvePoint(point[0].GetDouble(), point[1].GetDouble())).ToArray(),
        value.GetProperty("transitionTypes").EnumerateArray().Select(item => item.GetInt32()).ToArray());

    private sealed record DialogueManifest(int StartNodeId, string FacialCurvesPath,
        IReadOnlyDictionary<string, ActorManifest> Actors,
        IReadOnlyDictionary<int, string> Poses,
        IReadOnlyDictionary<(int Source, int Target), string> PoseTransitions);
    private sealed record ActorManifest(string Resref, string ModelPath, string StandingModelPath,
        string AppearanceModelPath, string BedAppearanceModelPath, string BaseAnimation,
        double PoseSpeed = 1);
    private sealed record DialogueNode(int Id, string Speaker, string Text, int[] Children,
        string PlotGuid, string PlotFlag, EmbeddedCutscene? Cutscene, DialogueMedia? Media,
        string CutsceneReference, bool ResolvedExternalCutscene);
    private sealed record DialogueMedia(string WavPath, double Duration, string FaceReference);
    private sealed record EmbeddedCutscene(double Runtime, Vector3 Position, Quaternion Orientation,
        CutsceneActor[] Actors, CutsceneAction[] Actions);
    private sealed record CutsceneActor(int Id, string Role, string MappingTag, int Pose, double PoseSpeed,
        Vector3 OriginPosition,
        Quaternion OriginOrientation, Vector3 FinalPosition, Quaternion FinalOrientation);
    private sealed record CutsceneAction(int ActorId, int Type, double Start, double Stop, string Animation,
        double Speed, double StartOffset, int CameraActorId, CurveRecord[] Curves);
    private readonly record struct CurvePoint(double Time, double Value);
    private sealed record CurveRecord(double BaseValue, CurvePoint[] Points, int[] Transitions)
    {
        internal double Evaluate(double time)
        {
            if (Points.Length == 0 || time < Points[0].Time) return BaseValue;
            for (var index = 1; index < Points.Length; index++)
            {
                if (time > Points[index].Time) continue;
                var left = Points[index - 1];
                var right = Points[index];
                if (index - 1 < Transitions.Length && Transitions[index - 1] == 2) return left.Value;
                return Mathf.Lerp(left.Value, right.Value,
                    (time - left.Time) / Math.Max(right.Time - left.Time, double.Epsilon));
            }
            return Points[^1].Value;
        }
    }
}
