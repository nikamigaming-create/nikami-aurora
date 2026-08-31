using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Nikami.Aurora.Core;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.Profiles.Kotor;
using NumericsVector3 = System.Numerics.Vector3;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorModuleBoot
{
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
        PlayActorEffects(actor, requested, loop);
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
            if (!xrGameplayOriginCalibrated)
                RecenterXrGameplayBase();
            else
                ApplyXrGameplayBase();
            xrSpectatorFieldOfView = gameplayFieldOfView;
            GD.Print($"NIKAMI_AURORA_XR_DIALOGUE_CAMERA status=preserved " +
                     $"mode=diegetic-first-person ignoredSourcePosition={position} " +
                     $"ignoredSourceFov={fov:F3} head={xrCamera.GlobalPosition}");
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

        var listenerPosition = ResolvePlayerTalkPoint();
        FaceModelToward(speaker, listenerPosition);
        if (playerModel is not null)
            FaceModelToward(playerModel, speaker.GlobalPosition + Vector3.Up * 1.0f);
        var talkDummy = FindDescendantBySuffix<Node3D>(speaker, "talkdummy");
        var speakerPosition = talkDummy?.GlobalPosition ??
            (actorTalkOffsets.TryGetValue(speakerActor, out var talkOffset)
                ? speaker.GlobalTransform * talkOffset
                : speaker.GlobalPosition + Vector3.Up * 1.55f);
        if (node.CameraAngle == 0 && dialogueCameraWasDynamic &&
            lastDynamicDialogueActor.Equals(speakerActor, StringComparison.OrdinalIgnoreCase))
        {
            GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=preserved mode=dynamic " +
                     $"actor={speakerActor} angle=0 xr={xrActive}");
            return;
        }
        var shot = KotorDialogueCameraComposer.ComposeSpeakerShot(
            ToNumericsPresentation(listenerPosition),
            ToNumericsPresentation(speakerPosition),
            node.CameraAngle,
            dialogueFieldOfView);
        var eye = ToGodotPresentation(shot.Position);
        var target = ToGodotPresentation(shot.Target);
        SetPresentationCameraBase(
            eye, target, ToGodotPresentation(shot.Up),
            shot.VerticalFieldOfViewDegrees);
        AssertDesktopCameraFraming(
            $"dialogue:{speakerActor}:angle{node.CameraAngle}",
            speakerPosition,
            0.16f,
            new CinematicFramingRequirements(0.01f, 0.12f, 0.62f));
        dialogueCameraWasDynamic = true;
        lastDynamicDialogueActor = speakerActor;
        GD.Print($"NIKAMI_AURORA_DIALOGUE_CAMERA status=active " +
                 $"mode={shot.Kind.ToString().ToLowerInvariant()} actor={speakerActor} " +
                 $"angle={node.CameraAngle} fov={dialogueFieldOfView:F3} position={eye} xr={xrActive}");
    }

    private Vector3 ResolvePlayerTalkPoint()
    {
        if (playerModel is not null && playerTalkOffset is Vector3 offset)
            return playerModel.GlobalTransform * offset;
        return playerBody.GlobalPosition + Vector3.Up * 1.55f;
    }

    private void AssertDesktopCameraFraming(
        string beat,
        Vector3 subjectCenter,
        float subjectRadius,
        CinematicFramingRequirements requirements,
        Camera3D? presentationCamera = null)
    {
        if (xrActive) return;
        presentationCamera ??= cinematicCamera;
        var viewportSize = GetViewport().GetVisibleRect().Size;
        if (viewportSize.Y <= 0)
            throw new InvalidDataException("Camera framing viewport has no height");
        var excludedBodies = new Godot.Collections.Array<Rid> { playerBody.GetRid() };
        var ray = PhysicsRayQueryParameters3D.Create(
            presentationCamera.GlobalPosition,
            subjectCenter,
            CameraVisibilityCollisionLayer,
            excludedBodies);
        ray.CollideWithAreas = false;
        ray.CollideWithBodies = true;
        var hit = presentationCamera.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        var lineOfSightClear = hit.Count == 0;
        var result = CinematicFramingGate.Evaluate(
            new CinematicFramingSample(
                ToNumericsPresentation(presentationCamera.GlobalPosition),
                ToNumericsPresentation(-presentationCamera.GlobalBasis.Z),
                ToNumericsPresentation(presentationCamera.GlobalBasis.Y),
                presentationCamera.Fov,
                viewportSize.X / viewportSize.Y,
                presentationCamera.Near,
                ToNumericsPresentation(subjectCenter),
                subjectRadius,
                LineOfSightClear: lineOfSightClear),
            requirements);
        var occluder = "none";
        if (hit.TryGetValue("collider", out var collider) &&
            collider.AsGodotObject() is Node colliderNode)
            occluder = colliderNode.Name.ToString();
        if (!result.Accepted)
        {
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_TEST_FIRST_ENCOUNTER") == "1" &&
                beat.StartsWith("encounter:", StringComparison.OrdinalIgnoreCase))
                GetTree().Quit(1);
            throw new InvalidDataException(
                $"Camera framing rejected {beat}: failures={result.Failures} " +
                $"center={result.NormalizedViewportCenter} " +
                $"height={result.ProjectedHeight:F4} depth={result.CameraDepth:F4} " +
                $"occluder={occluder}");
        }
        GD.Print($"NIKAMI_AURORA_CAMERA_FRAMING status=pass beat={beat} " +
                 $"center={result.NormalizedViewportCenter} " +
                 $"height={result.ProjectedHeight:F4} depth={result.CameraDepth:F4} " +
                 $"lineOfSight=clear occluder={occluder}");
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
        if (xrActive)
            FocusXrDialogueChoice(xrDialogueChoiceIndex);
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
            ApplyXrGameplayBase();
        }
        else
        {
            cinematicCamera.Current = false;
            camera.Current = true;
            camera.TopLevel = false;
            // SpringArm publishes its child offset later in the frame. Resolve
            // the same source-room collision path synchronously so a cinematic
            // handoff does not expose one first-person frame at the pivot/head.
            camera.Transform = new Transform3D(
                Basis.Identity,
                Vector3.Back * ResolveImmediateCameraArmLength());
            cameraPivot.Rotation = new Vector3(pitch, 0, 0);
            camera.Fov = gameplayFieldOfView;
        }
        xrLocalPlayerHeadVisible = null;
        UpdateXrLocalAvatarVisibility();
        GD.Print("NIKAMI_AURORA_DIALOGUE_CAMERA status=released");
    }

    private float ResolveImmediateCameraArmLength()
    {
        var start = cameraArm.GlobalPosition;
        var desired = start + cameraArm.GlobalBasis.Z.Normalized() *
            cameraArm.SpringLength;
        var ray = PhysicsRayQueryParameters3D.Create(
            start, desired, CameraVisibilityCollisionLayer,
            new Godot.Collections.Array<Rid> { playerBody.GetRid() });
        ray.CollideWithAreas = false;
        ray.CollideWithBodies = true;
        var hit = cameraArm.GetWorld3D().DirectSpaceState.IntersectRay(ray);
        if (!hit.TryGetValue("position", out var position))
            return cameraArm.SpringLength;
        var collisionDistance = start.DistanceTo(position.AsVector3());
        return Mathf.Clamp(
            collisionDistance - cameraArm.Margin,
            cameraArm.Margin,
            cameraArm.SpringLength);
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

    private void FrameCreatureInWorld(string identity)
    {
        if (!actorModels.TryGetValue(identity, out var model))
            throw new InvalidDataException(
                $"Creature capture identity is unavailable: {identity}");
        var minimum = new Vector3(float.PositiveInfinity, float.PositiveInfinity,
            float.PositiveInfinity);
        var maximum = new Vector3(float.NegativeInfinity, float.NegativeInfinity,
            float.NegativeInfinity);
        var meshCount = 0;
        foreach (var mesh in FindDescendants<MeshInstance3D>(model))
        {
            if (mesh.Mesh is null || !mesh.IsVisibleInTree()) continue;
            var bounds = mesh.GetAabb();
            for (var endpoint = 0; endpoint < 8; endpoint++)
            {
                var local = bounds.Position + new Vector3(
                    (endpoint & 1) == 0 ? 0 : bounds.Size.X,
                    (endpoint & 2) == 0 ? 0 : bounds.Size.Y,
                    (endpoint & 4) == 0 ? 0 : bounds.Size.Z);
                var world = mesh.GlobalTransform * local;
                minimum = minimum.Min(world);
                maximum = maximum.Max(world);
            }
            meshCount++;
        }
        if (meshCount == 0 || !minimum.IsFinite() || !maximum.IsFinite())
            throw new InvalidDataException(
                $"Creature capture has no finite render bounds: {identity}");
        var size = maximum - minimum;
        // Godot reports skinned surface bounds before the imported Odyssey
        // actor-basis rotation on some rigs.  The maximum extent remains the
        // source silhouette height, while treating reported Y as height can
        // put the lens at floor level and occlude half the actor.
        var sourceExtent = actorRecords.TryGetValue(identity, out var source) &&
                           source.RenderExtent is { Count: >= 3 }
            ? new Vector3(source.RenderExtent[0], source.RenderExtent[1],
                source.RenderExtent[2])
            : Vector3.Zero;
        var actorHeight = sourceExtent.Y > 0 && sourceExtent.IsFinite()
            ? sourceExtent.Y
            : Math.Max(0.5f, Math.Max(size.X, Math.Max(size.Y, size.Z)));
        var target = model.GlobalPosition + Vector3.Up * actorHeight * 0.52f;
        var forward = -model.GlobalBasis.Z;
        forward.Y = 0;
        forward = forward.LengthSquared() > 0.000001f
            ? forward.Normalized()
            : Vector3.Forward;
        var right = model.GlobalBasis.X;
        right.Y = 0;
        right = right.LengthSquared() > 0.000001f
            ? right.Normalized()
            : Vector3.Right;
        const float fieldOfView = 42.0f;
        var largestHalfExtent = Math.Max(0.35f, actorHeight * 0.5f);
        var distance = Math.Max(1.4f,
            largestHalfExtent / MathF.Tan(Mathf.DegToRad(fieldOfView * 0.5f)) * 1.35f);
        var cameraDirections = new[]
        {
            forward, -forward, right, -right,
            (forward + right).Normalized(), (forward - right).Normalized(),
            (-forward + right).Normalized(), (-forward - right).Normalized(),
        };
        var eye = target + forward * distance + Vector3.Up * actorHeight * 0.04f;
        var selectedDirection = "fallback";
        var bestClearance = -1.0f;
        for (var index = 0; index < cameraDirections.Length; index++)
        {
            var candidate = target + cameraDirections[index] * distance +
                            Vector3.Up * actorHeight * 0.04f;
            var query = PhysicsRayQueryParameters3D.Create(
                candidate, target, CameraVisibilityCollisionLayer,
                new Godot.Collections.Array<Rid> { playerBody.GetRid() });
            query.CollideWithAreas = false;
            query.CollideWithBodies = true;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (!hit.TryGetValue("position", out var hitPosition))
            {
                eye = candidate;
                selectedDirection = $"clear-{index}";
                bestClearance = distance;
                break;
            }
            var clearance = candidate.DistanceTo(hitPosition.AsVector3());
            if (clearance > bestClearance)
            {
                bestClearance = clearance;
                eye = candidate;
                selectedDirection = $"bounded-{index}";
            }
        }
        overlayLayer.Visible = false;
        SetPresentationCameraBase(eye, target, Vector3.Up, fieldOfView);
        GD.Print($"NIKAMI_AURORA_CREATURE_CAMERA status=ready module={loadedModuleId} " +
                 $"identity={identity} meshes={meshCount} size={size} " +
                 $"source_extent={sourceExtent} height={actorHeight:F3} " +
                 $"position={model.GlobalPosition} eye={eye} fov={fieldOfView:F3} " +
                 $"view={selectedDirection} clearance={bestClearance:F3} " +
                 "environment=module-world standins=0");
    }

    private void FramePointToPointEmitterCloseup()
    {
        var emitter = GetTree().GetNodesInGroup("kotor_p2p_emitters")
            .OfType<GpuParticles3D>()
            .OrderBy(candidate =>
                candidate.GlobalPosition.DistanceSquaredTo(playerBody.GlobalPosition))
            .FirstOrDefault();
        if (emitter is null || !emitter.HasMeta("source_target_global"))
            throw new InvalidDataException(
                "Point-to-point emitter closeup requires a materialized source target");
        var origin = emitter.GlobalPosition;
        var target = emitter.GetMeta("source_target_global").AsVector3();
        var sourceQuadMaximum = (float)emitter.GetMeta(
            "source_quad_max_meters").AsDouble();
        var path = target - origin;
        if (path.LengthSquared() <= 0.000001f)
            throw new InvalidDataException(
                "Point-to-point emitter closeup target collapsed onto its origin");
        var pathDirection = path.Normalized();
        var side = pathDirection.Cross(Vector3.Up);
        if (side.LengthSquared() <= 0.000001f) side = Vector3.Right;
        side = side.Normalized();
        var midpoint = (origin + target) * 0.5f;
        var gameplayDistance = Math.Max(3.2f, cameraArm.SpringLength);
        var eye = midpoint + side * gameplayDistance + Vector3.Up * 1.1f;
        SetPresentationCameraBase(
            eye, midpoint, Vector3.Up, gameplayFieldOfView);
        GD.Print($"NIKAMI_AURORA_P2P_CAMERA status=ready " +
                  $"emitter={emitter.Name} origin={origin} target={target} " +
                  $"distance={path.Length():F3} camera_distance={gameplayDistance:F3} " +
                  $"fov={gameplayFieldOfView:F3} quad_max_m={sourceQuadMaximum:F3} " +
                  $"mode=gameplay-distance");
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
                Fov = 45.0f,
                CullMask = RuntimeCameraCullMask
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
            Fov = 55.0f,
            CullMask = RuntimeCameraCullMask
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
        if (xrLeftArm is null || xrRightArm is null)
            throw new InvalidDataException(
                "XR body look-down gate requires the owned KOTOR arm rig");
        var playerForward = -playerBody.GlobalBasis.Z.Normalized();
        var leftSocket = xrLeftArm.Skeleton.GlobalTransform *
                         xrLeftArm.Skeleton.GetBoneGlobalPose(xrLeftArm.SocketBone);
        var rightSocket = xrRightArm.Skeleton.GlobalTransform *
                          xrRightArm.Skeleton.GetBoneGlobalPose(xrRightArm.SocketBone);
        var handMidpoint = (leftSocket.Origin + rightSocket.Origin) * 0.5f;
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
                 $"leftHand={leftSocket.Origin} rightHand={rightSocket.Origin} " +
                 "head=hidden body=visible hands=left,right " +
                 "provider=owned-kotor-skinned-rig");
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
        if (xrActive)
            AnchorXrDialogueHud(speakerActor);
        UpdateXrHudVisibility();
        if (xrActive)
            GD.Print($"NIKAMI_AURORA_XR_HUD status=dialogue-visible node={key}");
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
        xrDialogueChoiceIndex = 0;

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
            if (xrActive)
                FocusXrDialogueChoice(0);
            return;
        }
        foreach (var choice in choices)
        {
            var label = choice.Node.Kind == "reply" ? choice.Node.Text : "Continue";
            if (xrActive)
                label = label.Replace(
                    " [Left-click this answer to select highlighted response.]",
                    "", StringComparison.OrdinalIgnoreCase);
            var button = CreateChoiceButton(label);
            button.Disabled = dialogueVoice.Playing;
            var targetKey = choice.Target;
            button.Pressed += () => FollowDialogueChoice(graph, targetKey);
            dialogueChoices.AddChild(button);
            activeChoiceButtons.Add(button);
        }
        if (xrActive)
        {
            FocusXrDialogueChoice(0);
            GD.Print($"NIKAMI_AURORA_XR_HUD status=dialogue-visible " +
                     $"node={key} choices={activeChoiceButtons.Count}");
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
        UpdateXrHudVisibility();
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
                if (launchEnvironment.Get(
                        "NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE") == "1")
                    Callable.From(() => RequestCleanExit(0)).CallDeferred();
                break;
            case ShowcasePhase.Disabled:
            case ShowcasePhase.Complete:
            default:
                break;
        }
    }

    private void AdvanceGenericWorldShowcase(double delta)
    {
        if (moduleContentMode != KotorModuleContentMode.GenericWorld)
            throw new InvalidDataException(
                "Generic-world showcase automation reached a non-generic module.");
        if (readyFrames < runtimeConfiguration.Automation.SceneReadyFrame)
            return;

        if (!genericWorldShowcaseFramed)
        {
            genericWorldShowcaseFramed = true;
            overlayLayer.Visible = false;
            camera.Fov = 60.0f;
            cameraArm.SpringLength = Math.Max(cameraArm.SpringLength, 3.8f);
            pitch = -0.14f;
            GD.Print($"NIKAMI_AURORA_GENERIC_SHOWCASE status=framed " +
                     $"module={loadedModuleId} camera=third-person " +
                     $"fov={camera.Fov:0.0} arm={cameraArm.SpringLength:0.00}");
        }

        genericWorldShowcaseSeconds += Math.Max(0.0, delta);
        var normalized = (float)Math.Clamp(genericWorldShowcaseSeconds / 8.0, 0.0, 1.0);
        var eased = normalized * normalized * (3.0f - 2.0f * normalized);
        var orbit = Mathf.DegToRad(-12.0f + 24.0f * eased);
        cameraPivot.Rotation = new Vector3(pitch, orbit, 0);
        if (normalized < 0.82f)
        {
            var forward = -(new Basis(Vector3.Up, yaw).Z).Normalized();
            if (MovePlayer(forward * 0.45f * (float)Math.Max(0.0, delta)))
                PlayPlayerAnimation("walk");
        }
        else
        {
            PlayPlayerAnimation("pause1");
        }

        if (genericWorldShowcaseSeconds < 8.0)
            return;
        genericWorldShowcaseEnabled = false;
        GD.Print($"NIKAMI_AURORA_GENERIC_SHOWCASE status=pass " +
                 $"module={loadedModuleId} duration=8.000 camera=third-person " +
                 "motion=bounded-orbit+source-walkmesh renderer_scope=application");
        if (launchEnvironment.Get(
                "NIKAMI_AURORA_SHOWCASE_EXIT_ON_COMPLETE") == "1")
            Callable.From(() => RequestCleanExit(0)).CallDeferred();
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
                "mus_bat_sithbs", StringComparison.OrdinalIgnoreCase))
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

    private Button CreateChoiceButton(string text)
    {
        var button = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            CustomMinimumSize = new Vector2(0, xrActive ? 52 : 34)
        };
        button.AddThemeFontSizeOverride("font_size", xrActive ? 26 : 16);
        return button;
    }
}
