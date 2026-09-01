using Godot;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Infrastructure.Serialization;
using Nikami.Aurora.GodotRuntime.Domain.Sessions;
using Nikami.Aurora.GodotRuntime.Domain.Story;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;
using Nikami.Aurora.GodotRuntime.Launcher;
using Nikami.Aurora.GodotRuntime.Presentation.Player;
using Nikami.Aurora.Core;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Presentation.World;

public partial class OpenDaoWorld
{
    private async Task RunCityElfPlayableSmoke(CancellationToken cancellationToken)
    {
        await WaitForProcessFrames(
            ConfiguredFrameCount(PlayableSmokeStartupFramesVariable), cancellationToken);
        var stage = System.Environment.GetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE") ??
                    string.Empty;
        if (profile?.AreaId.Equals("bec110ar_players_house", StringComparison.OrdinalIgnoreCase) == true &&
            stage.Length == 0)
        {
            var capturePath = System.Environment.GetEnvironmentVariable("OPENDAO_GAME_START_CAPTURE") ?? string.Empty;
            var gameStartImage = await CaptureVisibleGameplayFrame(cancellationToken);
            var frameEvidence = gameStartImage.Evidence;
            var capture = capturePath.Length == 0
                ? Error.Ok
                : gameStartImage.Image.SavePng(capturePath);
            PrintGameplayFrameVisibility(frameEvidence, capturePath);
            var crate = FindPlaceable("bec110ip_pc_possessions");
            var door = FindPlaceable("bec110ip_to_alienage");
            if (crate is not null) OnPlaceableUseRequested(crate);
            var story = services!.GetRequired<StoryState>();
            var crateHandle = crate?.GetMeta("dao_story_handle", 0).AsInt32() ?? 0;
            var crateUsePassed = crate is not null && crateHandle > 0 &&
                                 Convert.ToInt32(story.GetLocal(crateHandle, "PLC_DO_ONCE_A", "int") ?? 0) == 1;
            var locomotionCapture = System.Environment.GetEnvironmentVariable(
                "OPENDAO_LOCOMOTION_CAPTURE") ?? string.Empty;
            var continuous = await RunContinuousFollowCamera(
                door, "house-to-door", locomotionCapture, cancellationToken);
            var locomotionPassed = continuous.Passed;
            var gameStartPassed = capture == Error.Ok && frameEvidence.Passed &&
                                  player.IsPhysicsProcessing() && locomotionPassed &&
                                  crateUsePassed && door is not null;
            GD.Print($"OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status={(gameStartPassed ? "pass" : "fail")} " +
                     $"character={character.Name} area={profile.AreaId} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"opening_cutscene=start_wake locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"capture={capturePath}");
            if (!gameStartPassed)
            {
                GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=house " +
                         $"crate={(crate is null ? 0 : 1)} door={(door is null ? 0 : 1)} " +
                         $"crate_use={(crateUsePassed ? "pass" : "fail")} " +
                         $"locomotion={(locomotionPassed ? "pass" : "fail")}");
                GetTree().Quit(61);
                return;
            }
            System.Environment.SetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE", "crate-used");
            if (door is null)
            {
                GD.Print("OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=transition");
                GetTree().Quit(62);
                return;
            }
            OnPlaceableUseRequested(door);
            if (!loading)
            {
                GD.Print("OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=transition");
                GetTree().Quit(62);
            }
            return;
        }
        if (profile?.AreaId.Equals("bec100ar_elven_alienage", StringComparison.OrdinalIgnoreCase) == true &&
            stage == "crate-used")
        {
            await WaitForProcessFrames(
                ConfiguredFrameCount(AlienageArrivalWarmupFramesVariable), cancellationToken);
            var exteriorTarget = FindContinuousExteriorTarget();
            var exteriorContinuous = await RunContinuousFollowCamera(
                exteriorTarget, "alienage-gameplay", string.Empty, cancellationToken);
            var locomotionPassed = exteriorContinuous.Passed;
            await WaitForProcessFrames(ConfiguredFrameCount(GameplayHoldFramesVariable), cancellationToken);
            var destinationCapture = System.Environment.GetEnvironmentVariable(
                "OPENDAO_PLAYABLE_DESTINATION_CAPTURE") ?? string.Empty;
            var destinationImage = GetViewport().GetTexture().GetImage();
            var captured = destinationCapture.Length == 0
                ? Error.Ok
                : destinationImage.SavePng(destinationCapture);
            var visibility = MeasureWorldVisibility(destinationImage);
            var visibilityPassed = visibility.VisibleRatio >= 0.15f;
            var skyCaptured = await CaptureAlienageSkyIfRequested(cancellationToken);
            var passed = player.IsPhysicsProcessing() && locomotionPassed &&
                         captured == Error.Ok && visibilityPassed && skyCaptured;
            GD.Print($"OPENDAO_CITY_ELF_EXTERIOR_GAMEPLAY status={(passed ? "pass" : "fail")} " +
                     $"area={profile.AreaId} waypoint=bec100wp_from_home " +
                     $"locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"world_visible={(visibilityPassed ? "pass" : "fail")}");
            GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status={(passed ? "pass" : "fail")} " +
                     "crate_use=pass transition=pass " +
                     $"destination={profile.AreaId} waypoint=bec100wp_from_home " +
                     $"locomotion={(locomotionPassed ? "pass" : "fail")} " +
                     $"player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                     $"world_visible={(visibilityPassed ? "pass" : "fail")} " +
                     $"visible_ratio={visibility.VisibleRatio:0.####} " +
                     $"mean_luminance={visibility.MeanLuminance:0.####} capture={destinationCapture}");
            System.Environment.SetEnvironmentVariable("OPENDAO_CITY_ELF_PLAYABLE_SMOKE_STAGE", string.Empty);
            GetTree().Quit(passed ? 0 : 63);
            return;
        }
        GD.Print($"OPENDAO_CITY_ELF_PLAYABLE_SMOKE status=fail stage=unexpected " +
                 $"area={profile?.AreaId ?? string.Empty} marker={stage}");
        GetTree().Quit(64);
    }

    private Node3D? FindContinuousExteriorTarget()
    {
        var origin = player.GlobalPosition;
        return GetNode<Node3D>("DAOScene")
            .FindChildren("*", "Node3D", true, false).OfType<Node3D>()
            .Where(node => node.HasMeta("dao_placeable"))
            .Select(node => (Node: node, Distance: new Vector2(
                node.GlobalPosition.X - origin.X,
                node.GlobalPosition.Z - origin.Z).Length()))
            .Where(candidate => candidate.Distance is >= 6 and <= 24 &&
                                player.BuildAuthoredNavigationPath(
                                    candidate.Node.GlobalPosition).Count >= 2)
            .OrderBy(candidate => candidate.Distance)
            .Select(candidate => candidate.Node)
            .FirstOrDefault();
    }

    private async Task<ContinuousGameplayEvidence> RunContinuousFollowCamera(
        Node3D? target,
        string segment,
        string capturePath,
        CancellationToken cancellationToken)
    {
        const int maximumFrames = 1800;
        const int maximumFramesPerNode = 300;
        const float waypointTolerance = .35f;
        var targetIdentity = target?.GetMeta("dao_tag", target.Name).AsString() ?? "absent";
        var path = target is null
            ? Array.Empty<Vector3>()
            : player.BuildAuthoredNavigationPath(target.GlobalPosition).ToArray();
        if (target is null || path.Length == 0)
        {
            var unavailable = new ContinuousGameplayEvidence(segment, targetIdentity,
                false, false, path.Length, 0, 0, float.PositiveInfinity,
                0, 0, 0, 0, false, false, null);
            PrintContinuousGameplayEvidence(unavailable, capturePath);
            return unavailable;
        }

        var frames = 0;
        var travelDistance = 0.0f;
        var previousPosition = player.GlobalPosition;
        var cameraSamples = 0;
        var cameraFailures = 0;
        var minimumActualArm = float.PositiveInfinity;
        var minimumPredictedArm = float.PositiveInfinity;
        var authoredWalk = false;
        var captured = false;
        Image? captureImage = null;
        DaoGameplayCameraAcceptance? captureCamera = null;
        var stableCameraFrames = 0;
        var reached = true;
        try
        {
            foreach (var waypoint in path)
            {
                var nodeFrames = 0;
                while (new Vector2(waypoint.X - player.GlobalPosition.X,
                           waypoint.Z - player.GlobalPosition.Z).Length() > waypointTolerance)
                {
                    if (frames >= maximumFrames || nodeFrames >= maximumFramesPerNode)
                    {
                        reached = false;
                        break;
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    var direction = waypoint - player.GlobalPosition;
                    direction.Y = 0;
                    player.FaceGameplayTarget(waypoint);
                    if (!player.PrepareGameplayCameraForMovement(direction))
                    {
                        reached = false;
                        break;
                    }
                    player.SetWorldMovementDirectionOverride(direction.Normalized());
                    await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                    await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                    frames++;
                    nodeFrames++;
                    var currentPosition = player.GlobalPosition;
                    travelDistance += new Vector2(currentPosition.X - previousPosition.X,
                        currentPosition.Z - previousPosition.Z).Length();
                    previousPosition = currentPosition;
                    authoredWalk |= player.CurrentLocomotionState ==
                                    PlayerController.LocomotionState.Walk &&
                                    player.IsAvatarAnimationPlaying;
                    var camera = player.SampleGameplayCameraForAcceptance();
                    cameraSamples++;
                    if (!camera.Passed) cameraFailures++;
                    stableCameraFrames = camera.Passed ? stableCameraFrames + 1 : 0;
                    minimumActualArm = Math.Min(minimumActualArm, camera.ActualArmLength);
                    minimumPredictedArm = Math.Min(minimumPredictedArm,
                        camera.PredictedArmLength);
                    if (!captured && travelDistance >= 1.5f &&
                        stableCameraFrames >= GameplayCameraStableFrames)
                    {
                        captured = true;
                        captureCamera = camera;
                        captureImage = GetViewport().GetTexture().GetImage();
                    }
                }
                if (!reached) break;
            }
        }
        finally
        {
            player.SetWorldMovementDirectionOverride(null);
            player.ResetLocomotionState();
        }

        if (!captured)
        {
            captureCamera = player.SettleGameplayCameraForAcceptance();
            stableCameraFrames = captureCamera.Passed ? GameplayCameraStableFrames : 0;
            captureImage = GetViewport().GetTexture().GetImage();
        }
        var save = capturePath.Length == 0
            ? Error.Ok
            : captureImage!.SavePng(capturePath);
        if (capturePath.Length > 0)
            GD.Print($"OPENDAO_LOCOMOTION_CAPTURE status={(save == Error.Ok ? "pass" : "fail")} " +
                     $"path={capturePath}");
        var selectedArmLength = player.GetNode<SpringArm3D>("Head").SpringLength;
        var frameEvidence = MeasureGameplayFrameEvidence(captureImage!, captureCamera!,
            stableCameraFrames, frames, player.AuthoredGameplayCameraArmLength,
            selectedArmLength);
        var targetDistance = new Vector2(target.GlobalPosition.X - player.GlobalPosition.X,
            target.GlobalPosition.Z - player.GlobalPosition.Z).Length();
        reached &= targetDistance <= 1.5f;
        var evidence = new ContinuousGameplayEvidence(segment, targetIdentity,
            true, reached, path.Length, frames, travelDistance, targetDistance,
            cameraSamples, cameraFailures,
            float.IsPositiveInfinity(minimumActualArm) ? 0 : minimumActualArm,
            float.IsPositiveInfinity(minimumPredictedArm) ? 0 : minimumPredictedArm,
            player.HasPlayableWalkAnimation && authoredWalk, save == Error.Ok,
            frameEvidence);
        PrintContinuousGameplayEvidence(evidence, capturePath);
        return evidence;
    }

    private void PrintContinuousGameplayEvidence(
        ContinuousGameplayEvidence evidence,
        string capturePath)
    {
        GD.Print("OPENDAO_CONTINUOUS_FOLLOW_CAMERA " +
                 $"status={(evidence.Passed ? "pass" : "fail")} " +
                 $"segment={evidence.Segment} target={evidence.Target} " +
                 $"path_ready={(evidence.PathReady ? 1 : 0)} " +
                 $"reached={(evidence.Reached ? 1 : 0)} " +
                 $"path_nodes={evidence.PathNodes} frames={evidence.Frames} " +
                 $"travel={evidence.TravelDistance:0.####} " +
                 $"target_distance={evidence.TargetDistance:0.####} " +
                 $"camera_samples={evidence.CameraSamples} " +
                 $"camera_failures={evidence.CameraFailures} " +
                 $"minimum_actual_arm={evidence.MinimumActualArm:0.####} " +
                 $"minimum_predicted_arm={evidence.MinimumPredictedArm:0.####} " +
                 $"authored_walk={(evidence.AuthoredWalk ? 1 : 0)} " +
                 $"frame_visibility={(evidence.CaptureFrame?.Passed == true ? "pass" : "fail")} " +
                 $"capture={capturePath} area={profile?.AreaId ?? "unknown"} " +
                 "parity_claim=none");
    }

    private async Task<bool> CaptureAlienageSkyIfRequested(
        CancellationToken cancellationToken)
    {
        var path = System.Environment.GetEnvironmentVariable(AlienageSkyCaptureVariable)?.Trim() ??
                   string.Empty;
        if (path.Length == 0) return true;

        var head = player.GetNode<Node3D>("Head");
        var camera = player.GetNode<Camera3D>("Head/Camera3D");
        var playerTransform = player.Transform;
        var headTransform = head.Transform;
        var cameraTransform = camera.Transform;
        var velocity = player.Velocity;
        var physicsProcessing = player.IsPhysicsProcessing();
        var hudVisible = hud?.Visible == true;
        Camera3D? diagnosticCamera = null;
        try
        {
            player.SetPhysicsProcess(false);
            player.Velocity = Vector3.Zero;
            var horizontalForward = -camera.GlobalBasis.Z;
            horizontalForward.Y = 0;
            if (horizontalForward.LengthSquared() < 0.01f)
            {
                horizontalForward = -player.GlobalBasis.Z;
                horizontalForward.Y = 0;
            }
            horizontalForward = horizontalForward.Normalized();
            player.LookAtTarget(player.GlobalPosition + horizontalForward * 3.0f +
                                Vector3.Up * 10.0f);
            diagnosticCamera = new Camera3D
            {
                Name = "SkyOnlyDiagnosticCamera",
                CullMask = 0,
                Fov = camera.Fov,
                Near = camera.Near,
                Far = camera.Far,
                KeepAspect = camera.KeepAspect,
                Current = true
            };
            AddChild(diagnosticCamera);
            diagnosticCamera.GlobalTransform = camera.GlobalTransform;
            if (hud is not null) hud.Visible = false;
            // Forward+ volumetrics reproject over multiple frames. This wait
            // is acceptance-only and never changes normal gameplay timing.
            // World geometry and HUD are excluded so the facet metric cannot
            // mistake a roof edge or tree branch for a cloud discontinuity.
            await WaitForProcessFrames(16, cancellationToken);
            var image = GetViewport().GetTexture().GetImage();
            var save = image.SavePng(path);
            var metrics = MeasureSkyVariation(image);
            var nonblankPassed = metrics.NonblankRatio >= 0.75f;
            var variationPassed = metrics.StandardDeviation >= 0.002f &&
                                  metrics.LuminanceRange >= 0.01f;
            // The capture framing deliberately reserves a mostly-sky band.
            // Large procedural cloud wedges produce long, high-contrast facet
            // edges there. This objective gate caught both broken planar-noise
            // implementations while leaving the seamless direction-space
            // result margin for normal cloud gradients and scene silhouettes.
            var facetPassed = metrics.FacetEdgeRatio <= 0.0025f;
            var passed = save == Error.Ok && nonblankPassed && variationPassed && facetPassed;
            GD.Print($"OPENDAO_CITY_ELF_SKY_CAPTURE status={(passed ? "pass" : "fail")} " +
                     $"nonblank_ratio={metrics.NonblankRatio:0.####} " +
                     $"mean_luminance={metrics.MeanLuminance:0.####} " +
                     $"luminance_stddev={metrics.StandardDeviation:0.####} " +
                     $"luminance_range={metrics.LuminanceRange:0.####} " +
                     $"facet_edge_ratio={metrics.FacetEdgeRatio:0.####} " +
                     $"facet_gate={(facetPassed ? "pass" : "fail")} " +
                     $"reprojection_frames=16 isolation=sky-only-world-cull-mask-zero " +
                     $"hud=hidden " +
                     $"capture={path}");
            return passed;
        }
        finally
        {
            diagnosticCamera?.QueueFree();
            player.Transform = playerTransform;
            head.Transform = headTransform;
            camera.Transform = cameraTransform;
            camera.MakeCurrent();
            if (hud is not null) hud.Visible = hudVisible;
            player.Velocity = velocity;
            player.SetPhysicsProcess(physicsProcessing);
        }
    }

    private async Task WaitForProcessFrames(int frameCount, CancellationToken cancellationToken)
    {
        for (var frame = 0; frame < frameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    private static int ConfiguredFrameCount(string variableName) => int.TryParse(
        System.Environment.GetEnvironmentVariable(variableName), out var configuredFrames)
        ? Math.Max(0, configuredFrames)
        : 0;

    private async Task<(Image Image, DaoGameplayCameraAcceptance Camera,
        int StableFrames, int NeighboringFrames)> CaptureSettledGameplayFrame(
        CancellationToken cancellationToken)
    {
        var camera = player.SettleGameplayCameraForAcceptance();
        var stableFrames = 0;
        var neighboringFrames = 0;
        while (neighboringFrames < GameplayCameraMaximumSettleFrames &&
               stableFrames < GameplayCameraStableFrames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            neighboringFrames++;
            camera = player.SettleGameplayCameraForAcceptance();
            stableFrames = camera.Passed ? stableFrames + 1 : 0;
        }
        return (GetViewport().GetTexture().GetImage(), camera,
            stableFrames, neighboringFrames);
    }

    private async Task<(Image Image, GameplayFrameEvidence Evidence)>
        CaptureVisibleGameplayFrame(CancellationToken cancellationToken)
    {
        const float armCompressionStep = .65f;
        const float minimumProbeAllowance = .4f;
        var authoredArmLength = player.AuthoredGameplayCameraArmLength;
        var selectedArmLength = authoredArmLength;
        GameplayFrameEvidence? latestEvidence = null;
        Image? latestImage = null;
        while (true)
        {
            player.SetGameplayCameraArmForAcceptance(selectedArmLength);
            var capture = await CaptureSettledGameplayFrame(cancellationToken);
            latestImage = capture.Image;
            latestEvidence = MeasureGameplayFrameEvidence(capture.Image,
                capture.Camera, capture.StableFrames, capture.NeighboringFrames,
                authoredArmLength, selectedArmLength);
            if (latestEvidence.Passed) return (latestImage, latestEvidence);
            var minimumArmLength = capture.Camera.MinimumArmLength + minimumProbeAllowance;
            if (selectedArmLength <= minimumArmLength) break;
            selectedArmLength = Math.Max(minimumArmLength,
                selectedArmLength - armCompressionStep);
        }
        return (latestImage!, latestEvidence!);
    }

    private static GameplayFrameEvidence MeasureGameplayFrameEvidence(
        Image image,
        DaoGameplayCameraAcceptance camera,
        int stableFrames,
        int neighboringFrames,
        float authoredArmLength,
        float selectedArmLength)
    {
        if (image.IsEmpty() || image.GetWidth() < 4 || image.GetHeight() < 4)
            return new GameplayFrameEvidence(camera, stableFrames, neighboringFrames,
                authoredArmLength, selectedArmLength, 0, 0, 0, 0, 1);
        const int columns = 48;
        const int rows = 32;
        const float clearColorDistanceSquared = .04f * .04f;
        var clearColor = RenderingServer.GetDefaultClearColor();
        var width = image.GetWidth();
        var height = image.GetHeight();
        var luminanceValues = new List<float>(columns * rows);
        var quantizedColors = new Dictionary<int, int>();
        var nonClear = 0;
        var sum = 0.0;
        var sumSquares = 0.0;
        for (var row = 0; row < rows; row++)
        {
            var y = (int)Math.Round(height * (.08f + .80f * row / (rows - 1)));
            for (var column = 0; column < columns; column++)
            {
                var x = (int)Math.Round(width * (.08f + .84f * column / (columns - 1)));
                var color = image.GetPixel(Math.Clamp(x, 0, width - 1),
                    Math.Clamp(y, 0, height - 1));
                var redDelta = color.R - clearColor.R;
                var greenDelta = color.G - clearColor.G;
                var blueDelta = color.B - clearColor.B;
                if (redDelta * redDelta + greenDelta * greenDelta + blueDelta * blueDelta >
                    clearColorDistanceSquared)
                    nonClear++;
                var luminance = SkyLuminance(color);
                luminanceValues.Add(luminance);
                sum += luminance;
                sumSquares += luminance * luminance;
                var red = Math.Clamp((int)Math.Round(color.R * 31), 0, 31);
                var green = Math.Clamp((int)Math.Round(color.G * 31), 0, 31);
                var blue = Math.Clamp((int)Math.Round(color.B * 31), 0, 31);
                var key = red << 10 | green << 5 | blue;
                quantizedColors[key] = quantizedColors.GetValueOrDefault(key) + 1;
            }
        }
        luminanceValues.Sort();
        var samples = luminanceValues.Count;
        var mean = sum / samples;
        var variance = Math.Max(0, sumSquares / samples - mean * mean);
        var lower = luminanceValues[(int)Math.Floor((samples - 1) * .01)];
        var upper = luminanceValues[(int)Math.Ceiling((samples - 1) * .99)];
        return new GameplayFrameEvidence(camera, stableFrames, neighboringFrames,
            authoredArmLength, selectedArmLength, (float)nonClear / samples,
            (float)mean, (float)Math.Sqrt(variance), upper - lower,
            (float)quantizedColors.Values.Max() / samples);
    }

    private void PrintGameplayFrameVisibility(
        GameplayFrameEvidence evidence,
        string capturePath)
    {
        var camera = evidence.Camera;
        GD.Print("OPENDAO_GAMEPLAY_FRAME_VISIBILITY " +
                 $"status={(evidence.Passed ? "pass" : "fail")} " +
                 $"active_camera={(camera.ActiveCamera ? 1 : 0)} " +
                 $"subject_projected={(camera.SubjectProjected ? 1 : 0)} " +
                 $"subject_los={(camera.SubjectLineOfSight ? 1 : 0)} " +
                 $"collision_safe={(evidence.CollisionSafe ? 1 : 0)} " +
                 $"stable_frames={evidence.StableFrames} " +
                 $"neighboring_frames={evidence.NeighboringFrames} " +
                 $"actual_arm={camera.ActualArmLength:0.####} " +
                 $"predicted_arm={camera.PredictedArmLength:0.####} " +
                 $"minimum_arm={camera.MinimumArmLength:0.####} " +
                 $"authored_arm={evidence.AuthoredArmLength:0.####} " +
                 $"selected_arm={evidence.SelectedArmLength:0.####} " +
                 $"yaw_degrees={camera.SelectedYawDegrees:0.####} " +
                 $"subject_screen=({camera.SubjectScreenPosition.X:0.##}," +
                 $"{camera.SubjectScreenPosition.Y:0.##}) " +
                 $"non_clear_coverage={(evidence.NonClearCoverage ? "pass" : "fail")} " +
                 $"non_clear_ratio={evidence.NonClearRatio:0.####} " +
                 $"image_detail={(evidence.ImageDetail ? "pass" : "fail")} " +
                 $"mean_luminance={evidence.MeanLuminance:0.####} " +
                 $"luminance_stddev={evidence.LuminanceStandardDeviation:0.####} " +
                 $"luminance_range={evidence.LuminanceRange:0.####} " +
                 $"dominant_color_ratio={evidence.DominantColorRatio:0.####} " +
                 $"capture={capturePath} area={profile?.AreaId ?? "unknown"}");
    }

    private async Task<bool> CaptureAreaRuntimeEvidenceIfRequested(
        WorldLoadResult world,
        CancellationToken cancellationToken)
    {
        var requestedRoot = System.Environment.GetEnvironmentVariable(
            AreaRuntimeEvidenceRootVariable)?.Trim() ?? string.Empty;
        if (requestedRoot.Length == 0) return false;
        try
        {
            if (profile is null || currentWorldArrival is null)
                throw new InvalidDataException(
                    "selected profile has no source-authored arrival; camera evidence is unverified");
            var renderingBackend = RenderingQualityPolicy.ParseBackend(
                RenderingServer.GetCurrentRenderingMethod().ToString());
            var tier = RenderingQualityPolicy.ParseTier(
                System.Environment.GetEnvironmentVariable("NIKAMI_AURORA_PRESENTATION_TIER"),
                renderingBackend);
            if (renderingBackend != RenderingBackend.ForwardPlus ||
                tier != RenderingPresentationTier.Enhanced)
                throw new InvalidDataException(
                    "all-area evidence requires enhanced Forward+ presentation");

            var sourceKey = profile.SourceKey.Trim();
            if (sourceKey.Length == 0 || profile.AreaId.Length == 0 ||
                profile.LayoutName.Length == 0)
                throw new InvalidDataException(
                    "selected profile has incomplete source-key/area/layout identity");
            var profilePath = DaoRuntimePaths.ResolveSourcePath(profile.SourcePath);
            var worldManifestPath = DaoRuntimePaths.ResolveSourcePath(profile.AreaFile);
            var profileSha256 = HashEvidenceFile(profilePath);
            var worldManifestSha256 = HashEvidenceFile(worldManifestPath);
            if (profileSha256.Length == 0 || worldManifestSha256.Length == 0)
                throw new InvalidDataException(
                    "selected profile or world manifest could not be hash-bound");

            var evidenceRoot = Path.GetFullPath(requestedRoot);
            Directory.CreateDirectory(evidenceRoot);
            var areaRoot = Path.Combine(evidenceRoot, SanitizeEvidenceFileComponent(
                $"{sourceKey}-{profile.AreaId}-{profile.LayoutName}"));
            if (Directory.Exists(areaRoot) && Directory.EnumerateFileSystemEntries(areaRoot).Any())
                throw new IOException(
                    $"refusing to overwrite existing area evidence: {areaRoot}");
            Directory.CreateDirectory(areaRoot);

            player.SetPhysicsProcess(false);
            player.Velocity = Vector3.Zero;
            if (hud is not null) hud.Visible = false;
            status.Visible = false;
            await WaitForProcessFrames(12, cancellationToken);

            var environmentCapture = await CaptureVisibleGameplayFrame(cancellationToken);
            var environmentPath = Path.Combine(areaRoot, "environment.png");
            var environmentSave = environmentCapture.Image.SavePng(environmentPath);
            PrintGameplayFrameVisibility(environmentCapture.Evidence, environmentPath);
            if (environmentSave != Error.Ok || !environmentCapture.Evidence.Passed)
                throw new InvalidDataException(
                    "authored-arrival environment frame failed visibility/image-detail acceptance");
            var environmentSha256 = HashEvidenceFile(environmentPath);
            if (environmentSha256.Length == 0)
                throw new InvalidDataException("environment frame could not be hash-bound");

            var camera = player.LocomotionCamera;
            var actorNodes = GetNode<Node3D>("DAOScene")
                .FindChildren("*", "Node3D", true, false)
                .OfType<Node3D>()
                .Where(actor => actor.HasMeta("dao_actor"))
                .OrderBy(actor => actor.GetMeta("dao_placement_ordinal", int.MaxValue).AsInt32())
                .ThenBy(actor => actor.Name.ToString(), StringComparer.Ordinal)
                .ToArray();
            var unshadedFallbackSurfaces = CountUnshadedActorSurfaces(
                actorNodes.Append(player.GetNode<Node3D>("AvatarRoot")));
            if (unshadedFallbackSurfaces != 0)
                throw new InvalidDataException(
                    $"enhanced actor material path retained {unshadedFallbackSurfaces} unshaded surfaces");
            var modelHashes = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);
            var placements = new List<InWorldCreatureFrameEvidence>(actorNodes.Length);
            var capturedCrops = new List<(InWorldCreatureFrameEvidence Evidence, Image Crop)>();
            foreach (var actor in actorNodes)
            {
                var creature = CaptureInWorldCreatureFrame(actor, camera,
                    environmentCapture.Image, environmentPath, environmentSha256,
                    areaRoot, modelHashes);
                placements.Add(creature.Evidence);
                if (creature.Crop is not null)
                    capturedCrops.Add((creature.Evidence, creature.Crop));
                GD.Print("OPENDAO_IN_WORLD_CREATURE_FRAME " +
                         $"status={creature.Evidence.Status} " +
                         $"actor={SanitizeTelemetryValue(creature.Evidence.ActorIdentity)} " +
                         $"tag={SanitizeTelemetryValue(creature.Evidence.ActorTag)} " +
                         $"placement_ordinal={creature.Evidence.PlacementOrdinal} " +
                         $"authored_transform_sha256={creature.Evidence.AuthoredTransformSha256} " +
                         $"model_sha256={creature.Evidence.ModelSha256} " +
                         $"projected_height={creature.Evidence.ProjectedHeight:0.####} " +
                         $"screen_coverage={creature.Evidence.ScreenCoverage:0.####} " +
                         $"los_probes={creature.Evidence.ClearLineOfSightProbes}/3 " +
                         $"crop_luminance_stddev={creature.Evidence.CropLuminanceStandardDeviation:0.####} " +
                         $"crop_luminance_range={creature.Evidence.CropLuminanceRange:0.####} " +
                         $"crop_dominant_color_ratio={creature.Evidence.CropDominantColorRatio:0.####} " +
                         $"reason={creature.Evidence.Reason} " +
                         $"capture={creature.Evidence.CapturePath} area={profile.AreaId}");
            }

            if (actorNodes.Length is > 0 and <= 12 &&
                System.Environment.GetEnvironmentVariable(
                    "OPENDAO_AREA_RUNTIME_EVIDENCE_WALK") != "0")
            {
                for (var index = 0; index < actorNodes.Length; index++)
                {
                    if (placements[index].Status == "pass") continue;
                    var actor = actorNodes[index];
                    player.SetPhysicsProcess(true);
                    var route = await RunContinuousFollowCamera(actor,
                        $"gallery-{placements[index].PlacementOrdinal:D4}", string.Empty,
                        cancellationToken);
                    var backedAway = route.Passed && await BackAwayForCreatureFraming(
                        actor, cancellationToken);
                    player.SetPhysicsProcess(false);
                    if (!backedAway) continue;
                    await WaitForProcessFrames(6, cancellationToken);
                    var actorEnvironment = await CaptureVisibleGameplayFrame(cancellationToken);
                    if (!actorEnvironment.Evidence.Passed) continue;
                    var actorEnvironmentPath = Path.Combine(areaRoot,
                        $"creature-{placements[index].PlacementOrdinal:D4}-environment.png");
                    if (actorEnvironment.Image.SavePng(actorEnvironmentPath) != Error.Ok) continue;
                    var actorEnvironmentSha256 = HashEvidenceFile(actorEnvironmentPath);
                    if (actorEnvironmentSha256.Length == 0) continue;
                    var creature = CaptureInWorldCreatureFrame(actor, camera,
                        actorEnvironment.Image, actorEnvironmentPath, actorEnvironmentSha256,
                        areaRoot, modelHashes);
                    if (creature.Evidence.Status != "pass" || creature.Crop is null) continue;
                    placements[index] = creature.Evidence;
                    capturedCrops.Add((creature.Evidence, creature.Crop));
                    GD.Print("OPENDAO_IN_WORLD_CREATURE_ROUTE status=pass " +
                             $"actor={SanitizeTelemetryValue(creature.Evidence.ActorIdentity)} " +
                             $"placement_ordinal={creature.Evidence.PlacementOrdinal} " +
                             $"environment={actorEnvironmentPath} " +
                             $"environment_sha256={actorEnvironmentSha256} " +
                             $"crop={creature.Evidence.CapturePath} area={profile.AreaId} " +
                             "authored_navigation=1 teleport=0 camera_repositioned=0 parity_claim=none");
                }
            }

            var galleryStatus = placements.Count == 0
                ? "unverified"
                : capturedCrops.Count == placements.Count
                    ? "pass"
                    : capturedCrops.Count > 0 ? "partial" : "unverified";
            var contactSheetPath = capturedCrops.Count == 0
                ? string.Empty
                : Path.Combine(areaRoot, "creatures-contact-sheet.png");
            var contactSheetSha256 = string.Empty;
            if (capturedCrops.Count > 0)
            {
                var sheet = BuildCreatureContactSheet(capturedCrops.Select(value => value.Crop));
                if (sheet.SavePng(contactSheetPath) != Error.Ok)
                    throw new IOException("in-world creature contact sheet could not be saved");
                contactSheetSha256 = HashEvidenceFile(contactSheetPath);
            }

            var manifestPath = Path.Combine(areaRoot, "opendao-area-runtime-evidence-v1.json");
            var manifest = new
            {
                schema = "opendao-area-runtime-evidence-v1",
                sourceKey,
                areaId = profile.AreaId,
                layout = profile.LayoutName,
                profileSha256,
                worldManifestSha256,
                renderer = new
                {
                    status = "partial",
                    backend = "forward-plus",
                    tier = "enhanced",
                    worldPbrContract = "strict-runtime-ready",
                    sourceMaoSemantics = "unsupported",
                    unshadedFallback = unshadedFallbackSurfaces,
                    parityClaim = "none"
                },
                authoredArrival = new
                {
                    status = "pass",
                    waypoint = currentWorldArrival.Waypoint,
                    source = currentWorldArrival.Source,
                    position = VectorChannels(currentWorldArrival.Transform.Origin)
                },
                cameraSpawnVisibility = new
                {
                    status = "pass",
                    reason = "source-authored-arrival-and-runtime-visibility-pass",
                    actualArmLength = environmentCapture.Evidence.Camera.ActualArmLength,
                    predictedArmLength = environmentCapture.Evidence.Camera.PredictedArmLength,
                    minimumArmLength = environmentCapture.Evidence.Camera.MinimumArmLength,
                    nonClearRatio = environmentCapture.Evidence.NonClearRatio,
                    luminanceStandardDeviation =
                        environmentCapture.Evidence.LuminanceStandardDeviation,
                    luminanceRange = environmentCapture.Evidence.LuminanceRange,
                    dominantColorRatio = environmentCapture.Evidence.DominantColorRatio
                },
                playabilityTransition = new
                {
                    status = "unverified",
                    reason = "environment-capture-does-not-prove-transition-traversal"
                },
                environmentFrame = new
                {
                    status = "pass",
                    path = environmentPath,
                    sha256 = environmentSha256,
                    aestheticStatus = "manual-review-required"
                },
                creatureGallery = new
                {
                    status = galleryStatus,
                    expected = world.Actors,
                    discovered = placements.Count,
                    rendered = capturedCrops.Count,
                    missing = Math.Max(0, world.Actors - placements.Count),
                    unsupported = placements.Count(value => value.Status != "pass"),
                    environmentFrameStatus = "pass",
                    creatureFrameStatus = galleryStatus,
                    contactSheetPath = contactSheetPath.Length == 0 ? null : contactSheetPath,
                    contactSheetSha256 = contactSheetSha256.Length == 0
                        ? null
                        : contactSheetSha256,
                    placements
                },
                aesthetics = new
                {
                    status = "unreviewed",
                    automaticPromotion = false,
                    reason = "visual-review-required-for-sky-water-material-and-framing-quality"
                }
            };
            File.WriteAllText(manifestPath,
                JsonSerializer.Serialize(manifest, RuntimeJsonOptions.IndentedCamelCase),
                new UTF8Encoding(false));
            var manifestSha256 = HashEvidenceFile(manifestPath);
            if (manifestSha256.Length == 0)
                throw new IOException("runtime evidence manifest could not be hash-bound");
            GD.Print("OPENDAO_AREA_ENVIRONMENT_FRAME status=pass " +
                     $"source_key={SanitizeTelemetryValue(sourceKey)} area={profile.AreaId} " +
                     $"layout={profile.LayoutName} pbr=strict-runtime-ready " +
                     $"unshaded_fallback={unshadedFallbackSurfaces} authored_arrival=1 " +
                     $"capture={environmentPath} sha256={environmentSha256} " +
                     "aesthetic_status=manual-review-required parity_claim=none");
            GD.Print("OPENDAO_IN_WORLD_CREATURE_GALLERY " +
                     $"status={galleryStatus} expected={world.Actors} " +
                     $"discovered={placements.Count} rendered={capturedCrops.Count} " +
                     $"unverified={placements.Count(value => value.Status != "pass") + Math.Max(0, world.Actors - placements.Count)} " +
                     $"contact_sheet={contactSheetPath} area={profile.AreaId} " +
                     "camera_repositioned=0 actor_repositioned=0 preview_viewport=0 parity_claim=none");
            GD.Print("OPENDAO_AREA_RUNTIME_EVIDENCE status=partial " +
                     $"source_key={SanitizeTelemetryValue(sourceKey)} area={profile.AreaId} " +
                     $"layout={profile.LayoutName} manifest={manifestPath} " +
                     $"sha256={manifestSha256} aesthetic_status=manual-review-required");
            GetTree().Quit(0);
        }
        catch (Exception error)
        {
            GD.PushError("OPENDAO_AREA_RUNTIME_EVIDENCE status=fail reason=" +
                         SanitizeTelemetryValue(error.Message));
            GetTree().Quit(67);
        }
        return true;
    }

    private (InWorldCreatureFrameEvidence Evidence, Image? Crop)
        CaptureInWorldCreatureFrame(Node3D actor, Camera3D camera, Image environment,
            string environmentFramePath, string environmentFrameSha256,
            string outputRoot, IDictionary<string, string> modelHashes)
    {
        var actorIdentity = actor.GetMeta("dao_actor_identity", "").AsString();
        var actorTag = actor.GetMeta("dao_actor_tag",
            actor.GetMeta("dao_resref", "")).AsString();
        var placementOrdinal = actor.GetMeta("dao_placement_ordinal", -1).AsInt32();
        var authoredPosition = actor.GetMeta("dao_authored_position", "").AsString();
        var authoredRotation = actor.GetMeta("dao_authored_rotation", "").AsString();
        var authoredTransformSha256 = actor.GetMeta(
            "dao_authored_transform_sha256", "").AsString();
        var modelRelative = actor.GetMeta("dao_actor_model_relative", "").AsString();
        var modelPath = ResolveActorModelEvidencePath(modelRelative);
        if (!modelHashes.TryGetValue(modelPath, out var modelSha256))
        {
            modelSha256 = HashEvidenceFile(modelPath);
            modelHashes[modelPath] = modelSha256;
        }

        (InWorldCreatureFrameEvidence Evidence, Image? Crop) Reject(string reason,
            float projectedHeight = 0, float screenCoverage = 0, int clearProbes = 0,
            float cropStandardDeviation = 0, float cropRange = 0,
            float cropDominantColorRatio = 1) =>
            (new InWorldCreatureFrameEvidence(actorIdentity, actorTag, placementOrdinal,
                authoredPosition, authoredRotation, authoredTransformSha256, modelSha256,
                "unverified", reason, projectedHeight, screenCoverage, clearProbes,
                cropStandardDeviation, cropRange, cropDominantColorRatio,
                environmentFramePath, environmentFrameSha256,
                string.Empty, string.Empty), null);

        if (actorIdentity.Length == 0 || placementOrdinal < 0 ||
            authoredPosition.Length == 0 || authoredRotation.Length == 0 ||
            authoredTransformSha256.Length != 64)
            return Reject("source-placement-identity-unavailable");
        if (modelSha256.Length != 64) return Reject("source-model-hash-unavailable");
        if (!actor.IsVisibleInTree()) return Reject("source-actor-not-visible-in-tree");
        var localBounds = SceneBounds.Calculate(actor);
        if (localBounds.Size.IsZeroApprox() || !localBounds.Position.IsFinite() ||
            !localBounds.Size.IsFinite()) return Reject("source-actor-bounds-unavailable");
        var worldBounds = actor.GlobalTransform * localBounds;
        var corners = EvidenceBoundsCorners(worldBounds);
        if (corners.Any(camera.IsPositionBehind)) return Reject("source-actor-behind-camera");
        var projected = corners.Select(camera.UnprojectPosition).ToArray();
        var minimum = new Vector2(projected.Min(value => value.X),
            projected.Min(value => value.Y));
        var maximum = new Vector2(projected.Max(value => value.X),
            projected.Max(value => value.Y));
        var fullSize = maximum - minimum;
        var viewportSize = GetViewport().GetVisibleRect().Size;
        if (fullSize.X <= 1 || fullSize.Y <= 1 || viewportSize.X <= 1 || viewportSize.Y <= 1)
            return Reject("source-actor-projection-degenerate");
        var clippedMinimum = new Vector2(Math.Clamp(minimum.X, 0, viewportSize.X),
            Math.Clamp(minimum.Y, 0, viewportSize.Y));
        var clippedMaximum = new Vector2(Math.Clamp(maximum.X, 0, viewportSize.X),
            Math.Clamp(maximum.Y, 0, viewportSize.Y));
        var clippedSize = clippedMaximum - clippedMinimum;
        var screenCoverage = clippedSize.X <= 0 || clippedSize.Y <= 0
            ? 0
            : clippedSize.X * clippedSize.Y / (fullSize.X * fullSize.Y);
        var projectedHeight = clippedSize.Y / viewportSize.Y;
        if (screenCoverage < .70f || projectedHeight < .05f || projectedHeight > .95f)
            return Reject("source-actor-framing-rejected", projectedHeight, screenCoverage);

        var center = worldBounds.GetCenter();
        var probes = new[]
        {
            center,
            center + Vector3.Up * worldBounds.Size.Y * .32f,
            center - Vector3.Up * worldBounds.Size.Y * .32f
        };
        var clearProbes = probes.Count(point => HasCreatureLineOfSight(camera, point));
        if (clearProbes < 2)
            return Reject("source-actor-line-of-sight-blocked", projectedHeight,
                screenCoverage, clearProbes);

        const int padding = 8;
        var left = Math.Clamp((int)Math.Floor(clippedMinimum.X) - padding,
            0, environment.GetWidth() - 1);
        var top = Math.Clamp((int)Math.Floor(clippedMinimum.Y) - padding,
            0, environment.GetHeight() - 1);
        var right = Math.Clamp((int)Math.Ceiling(clippedMaximum.X) + padding,
            left + 1, environment.GetWidth());
        var bottom = Math.Clamp((int)Math.Ceiling(clippedMaximum.Y) + padding,
            top + 1, environment.GetHeight());
        var crop = environment.GetRegion(new Rect2I(left, top, right - left, bottom - top));
        var cropMetrics = MeasureCropImageDetail(crop);
        if (cropMetrics.StandardDeviation < .04f || cropMetrics.Range < .16f ||
            cropMetrics.DominantColorRatio > .45f)
            return Reject("source-actor-crop-image-detail-rejected", projectedHeight,
                screenCoverage, clearProbes, cropMetrics.StandardDeviation,
                cropMetrics.Range, cropMetrics.DominantColorRatio);
        if (crop.GetHeight() < 360)
        {
            var scale = 360f / crop.GetHeight();
            crop.Resize(Math.Max(1, (int)Math.Round(crop.GetWidth() * scale)), 360,
                Image.Interpolation.Lanczos);
        }
        var capturePath = Path.Combine(outputRoot,
            $"creature-{placementOrdinal:D4}-{SanitizeEvidenceFileComponent(actorIdentity)}.png");
        if (crop.SavePng(capturePath) != Error.Ok)
            return Reject("source-actor-crop-save-failed", projectedHeight,
                screenCoverage, clearProbes);
        var captureSha256 = HashEvidenceFile(capturePath);
        if (captureSha256.Length == 0)
            return Reject("source-actor-crop-hash-failed", projectedHeight,
                screenCoverage, clearProbes);
        return (new InWorldCreatureFrameEvidence(actorIdentity, actorTag, placementOrdinal,
            authoredPosition, authoredRotation, authoredTransformSha256, modelSha256,
            "pass", "source-authored-in-world-visible", projectedHeight, screenCoverage,
            clearProbes, cropMetrics.StandardDeviation, cropMetrics.Range,
            cropMetrics.DominantColorRatio, environmentFramePath,
            environmentFrameSha256, capturePath, captureSha256), crop);
    }

    private bool HasCreatureLineOfSight(Camera3D camera, Vector3 point)
    {
        var towardCamera = camera.GlobalPosition - point;
        if (towardCamera.LengthSquared() < .001f) return false;
        var end = point + towardCamera.Normalized() *
            Math.Min(.2f, Math.Max(.05f, towardCamera.Length() * .02f));
        var query = PhysicsRayQueryParameters3D.Create(camera.GlobalPosition, end, 3);
        query.HitFromInside = true;
        query.Exclude = [player.GetRid()];
        return GetWorld3D().DirectSpaceState.IntersectRay(query).Count == 0;
    }

    private async Task<bool> BackAwayForCreatureFraming(Node3D actor,
        CancellationToken cancellationToken)
    {
        const float desiredDistance = 4.0f;
        const int maximumFrames = 180;
        var frames = 0;
        var cameraFailures = 0;
        try
        {
            while (new Vector2(actor.GlobalPosition.X - player.GlobalPosition.X,
                       actor.GlobalPosition.Z - player.GlobalPosition.Z).Length() < desiredDistance &&
                   frames < maximumFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var away = player.GlobalPosition - actor.GlobalPosition;
                away.Y = 0;
                if (away.LengthSquared() < .001f) away = player.GlobalBasis.Z;
                player.FaceGameplayTarget(actor.GlobalPosition);
                if (!player.PrepareGameplayCameraForMovement(away, .25f)) return false;
                player.SetWorldMovementDirectionOverride(away.Normalized());
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                frames++;
                if (!player.SampleGameplayCameraForAcceptance().Passed) cameraFailures++;
            }
        }
        finally
        {
            player.SetWorldMovementDirectionOverride(null);
            player.ResetLocomotionState();
        }
        var distance = new Vector2(actor.GlobalPosition.X - player.GlobalPosition.X,
            actor.GlobalPosition.Z - player.GlobalPosition.Z).Length();
        var passed = distance >= desiredDistance && cameraFailures == 0;
        GD.Print("OPENDAO_IN_WORLD_CREATURE_BACKAWAY " +
                 $"status={(passed ? "pass" : "fail")} actor=" +
                 $"{SanitizeTelemetryValue(actor.GetMeta("dao_actor_identity", actor.Name).AsString())} " +
                 $"frames={frames} distance={distance:0.####} camera_failures={cameraFailures} " +
                 "authored_walk=1 teleport=0 camera_repositioned=0 parity_claim=none");
        return passed;
    }

    private static int CountUnshadedActorSurfaces(IEnumerable<Node3D> roots)
    {
        var unshaded = 0;
        foreach (var mesh in roots.SelectMany(root =>
                     root.FindChildren("*", "MeshInstance3D", true, false)
                         .OfType<MeshInstance3D>()).Where(mesh => mesh.Visible && mesh.Mesh is not null))
            for (var surface = 0; surface < mesh.Mesh!.GetSurfaceCount(); surface++)
            {
                var material = mesh.GetActiveMaterial(surface);
                if (material is BaseMaterial3D
                    {
                        ShadingMode: BaseMaterial3D.ShadingModeEnum.Unshaded
                    } || material is ShaderMaterial { Shader: not null } shader &&
                    shader.Shader.Code.Contains("unshaded", StringComparison.OrdinalIgnoreCase))
                    unshaded++;
            }
        return unshaded;
    }

    private static (float StandardDeviation, float Range, float DominantColorRatio)
        MeasureCropImageDetail(Image image)
    {
        const int columns = 32;
        const int rows = 32;
        var luminance = new List<float>(columns * rows);
        var colors = new Dictionary<int, int>();
        var sum = 0f;
        var sumSquares = 0f;
        for (var row = 0; row < rows; row++)
            for (var column = 0; column < columns; column++)
            {
                var x = Math.Clamp((int)Math.Round((image.GetWidth() - 1f) * column /
                                                   (columns - 1)), 0, image.GetWidth() - 1);
                var y = Math.Clamp((int)Math.Round((image.GetHeight() - 1f) * row /
                                                   (rows - 1)), 0, image.GetHeight() - 1);
                var color = image.GetPixel(x, y);
                var value = SkyLuminance(color);
                luminance.Add(value);
                sum += value;
                sumSquares += value * value;
                var red = Math.Clamp((int)Math.Round(color.R * 15), 0, 15);
                var green = Math.Clamp((int)Math.Round(color.G * 15), 0, 15);
                var blue = Math.Clamp((int)Math.Round(color.B * 15), 0, 15);
                var key = red << 8 | green << 4 | blue;
                colors[key] = colors.GetValueOrDefault(key) + 1;
            }
        luminance.Sort();
        var count = luminance.Count;
        var mean = sum / count;
        var variance = Math.Max(0, sumSquares / count - mean * mean);
        var lower = luminance[(int)Math.Floor((count - 1) * .01f)];
        var upper = luminance[(int)Math.Ceiling((count - 1) * .99f)];
        return ((float)Math.Sqrt(variance), upper - lower,
            (float)colors.Values.Max() / count);
    }

    private string ResolveActorModelEvidencePath(string relative)
    {
        if (profile is null || relative.Length == 0) return string.Empty;
        var path = Path.IsPathRooted(relative)
            ? relative
            : Path.Combine(profile.ActorRoot,
                relative.Replace('/', Path.DirectorySeparatorChar));
        return DaoRuntimePaths.ResolveSourcePath(path);
    }

    private static Vector3[] EvidenceBoundsCorners(Aabb bounds)
    {
        var start = bounds.Position;
        var end = bounds.End;
        return
        [
            new Vector3(start.X, start.Y, start.Z),
            new Vector3(end.X, start.Y, start.Z),
            new Vector3(start.X, end.Y, start.Z),
            new Vector3(end.X, end.Y, start.Z),
            new Vector3(start.X, start.Y, end.Z),
            new Vector3(end.X, start.Y, end.Z),
            new Vector3(start.X, end.Y, end.Z),
            new Vector3(end.X, end.Y, end.Z)
        ];
    }

    private static Image BuildCreatureContactSheet(IEnumerable<Image> sourceCrops)
    {
        const int tileWidth = 240;
        const int tileHeight = 240;
        var crops = sourceCrops.ToArray();
        var columns = Math.Min(6, Math.Max(1, (int)Math.Ceiling(Math.Sqrt(crops.Length))));
        var rows = (int)Math.Ceiling((double)crops.Length / columns);
        var sheet = Image.CreateEmpty(columns * tileWidth, rows * tileHeight,
            false, Image.Format.Rgba8);
        sheet.Fill(new Color(.018f, .021f, .025f, 1));
        for (var index = 0; index < crops.Length; index++)
        {
            var source = crops[index];
            var scale = Math.Min((tileWidth - 8f) / source.GetWidth(),
                (tileHeight - 8f) / source.GetHeight());
            var width = Math.Max(1, (int)Math.Round(source.GetWidth() * scale));
            var height = Math.Max(1, (int)Math.Round(source.GetHeight() * scale));
            var tile = source.Duplicate() as Image ?? source;
            if (tile.GetFormat() != Image.Format.Rgba8) tile.Convert(Image.Format.Rgba8);
            tile.Resize(width, height, Image.Interpolation.Lanczos);
            var column = index % columns;
            var row = index / columns;
            var destination = new Vector2I(column * tileWidth + (tileWidth - width) / 2,
                row * tileHeight + (tileHeight - height) / 2);
            sheet.BlitRect(tile, new Rect2I(0, 0, width, height), destination);
        }
        return sheet;
    }

    private static string HashEvidenceFile(string path)
    {
        if (path.Length == 0 || !File.Exists(path)) return string.Empty;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string SanitizeEvidenceFileComponent(string value)
    {
        var safe = new string(value.Select(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_'
                ? char.ToLowerInvariant(character)
                : '-').ToArray()).Trim('-');
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string SanitizeTelemetryValue(string value) =>
        value.Replace(' ', '_').Replace('\r', '_').Replace('\n', '_');

    private static float[] VectorChannels(Vector3 value) => [value.X, value.Y, value.Z];

    private static (float VisibleRatio, float MeanLuminance) MeasureWorldVisibility(Image image)
    {
        if (image.IsEmpty() || image.GetWidth() < 4 || image.GetHeight() < 4) return (0, 0);
        const int columnsPerBand = 32;
        const int rows = 36;
        var width = image.GetWidth();
        var height = image.GetHeight();
        var visible = 0;
        var samples = 0;
        var luminanceSum = 0.0f;
        for (var row = 0; row < rows; row++)
        {
            var y = (int)Math.Round(height * (0.18f + 0.60f * row / (rows - 1)));
            for (var band = 0; band < 2; band++)
            {
                var xStart = band == 0 ? 0.05f : 0.62f;
                var xEnd = band == 0 ? 0.38f : 0.95f;
                for (var column = 0; column < columnsPerBand; column++)
                {
                    var x = (int)Math.Round(width *
                        (xStart + (xEnd - xStart) * column / (columnsPerBand - 1)));
                    var color = image.GetPixel(Math.Clamp(x, 0, width - 1),
                        Math.Clamp(y, 0, height - 1));
                    var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
                    luminanceSum += luminance;
                    if (luminance >= 0.03f) visible++;
                    samples++;
                }
            }
        }
        return samples == 0 ? (0, 0) : ((float)visible / samples, luminanceSum / samples);
    }

    private static (float NonblankRatio, float MeanLuminance,
        float StandardDeviation, float LuminanceRange, float FacetEdgeRatio)
        MeasureSkyVariation(Image image)
    {
        if (image.IsEmpty() || image.GetWidth() < 4 || image.GetHeight() < 4)
            return (0, 0, 0, 0, 1);
        const int columns = 48;
        const int rows = 32;
        var width = image.GetWidth();
        var height = image.GetHeight();
        var samples = 0;
        var nonblank = 0;
        var sum = 0.0;
        var sumSquares = 0.0;
        var minimum = float.MaxValue;
        var maximum = float.MinValue;
        for (var row = 0; row < rows; row++)
        {
            var y = (int)Math.Round(height * (0.08f + 0.64f * row / (rows - 1)));
            for (var column = 0; column < columns; column++)
            {
                var x = (int)Math.Round(width * (0.12f + 0.68f * column / (columns - 1)));
                var color = image.GetPixel(Math.Clamp(x, 0, width - 1),
                    Math.Clamp(y, 0, height - 1));
                var luminance = 0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;
                if (luminance >= 0.03f) nonblank++;
                sum += luminance;
                sumSquares += luminance * luminance;
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                samples++;
            }
        }
        if (samples == 0) return (0, 0, 0, 0, 1);
        var mean = sum / samples;
        var variance = Math.Max(0, sumSquares / samples - mean * mean);
        var facetEdges = 0;
        var edgeSamples = 0;
        var xStart = (int)Math.Round(width * 0.22f);
        var xEnd = (int)Math.Round(width * 0.82f);
        var yStart = (int)Math.Round(height * 0.10f);
        var yEnd = (int)Math.Round(height * 0.42f);
        for (var y = yStart; y + 2 < yEnd; y += 2)
            for (var x = xStart; x + 2 < xEnd; x += 2)
            {
                var center = SkyLuminance(image.GetPixel(x, y));
                if (Math.Abs(center - SkyLuminance(image.GetPixel(x + 2, y))) > 0.08f)
                    facetEdges++;
                if (Math.Abs(center - SkyLuminance(image.GetPixel(x, y + 2))) > 0.08f)
                    facetEdges++;
                edgeSamples += 2;
            }
        return ((float)nonblank / samples, (float)mean,
            (float)Math.Sqrt(variance), maximum - minimum,
            edgeSamples == 0 ? 1 : (float)facetEdges / edgeSamples);
    }

    private static float SkyLuminance(Color color) =>
        0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B;

    private Node3D? FindPlaceable(string tag) => GetNode<Node3D>("DAOScene")
        .FindChildren("*", "Node3D", true, false).OfType<Node3D>()
        .FirstOrDefault(node => node.HasMeta("dao_placeable") &&
                                node.GetMeta("dao_tag").AsString()
                                    .Equals(tag, StringComparison.OrdinalIgnoreCase));

    private async Task RunCharacterGameStartAcceptance(CancellationToken cancellationToken)
    {
        for (var frame = 0; frame < 12; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var expectedOrigin = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_ORIGIN");
        if (string.IsNullOrWhiteSpace(expectedOrigin)) expectedOrigin = "city-elf";
        var expectedRace = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_RACE");
        if (string.IsNullOrWhiteSpace(expectedRace)) expectedRace = character.Race;
        var expectedGender = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_GENDER");
        if (expectedGender is not ("male" or "female")) expectedGender = "female";
        var expectedName = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_NAME");
        if (string.IsNullOrWhiteSpace(expectedName)) expectedName = "Automation Warden";
        var expectedClass = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_CLASS");
        if (string.IsNullOrWhiteSpace(expectedClass)) expectedClass = character.Class;
        var expectedAppearance = System.Environment.GetEnvironmentVariable("OPENDAO_ACCEPTANCE_APPEARANCE");
        if (string.IsNullOrWhiteSpace(expectedAppearance)) expectedAppearance = "preset-3";
        var expected = character.Name == expectedName && character.Race == expectedRace &&
                       character.Gender == expectedGender &&
                       character.Origin.Equals(expectedOrigin, StringComparison.OrdinalIgnoreCase) &&
                       character.Class.Equals(expectedClass, StringComparison.OrdinalIgnoreCase) &&
                       character.Appearance.Equals(expectedAppearance, StringComparison.OrdinalIgnoreCase);
        var origin = Nikami.Aurora.GodotRuntime.MainMenu.CharacterProfileRules.OriginFor(character.Origin);
        var correctArea = origin is not null && profile is not null &&
                          profile.AreaId.Equals(origin.AreaId, StringComparison.OrdinalIgnoreCase);
        var correctCutscenePolicy = origin is not null &&
            (origin.OpeningCutscene.Length == 0
                ? openingCutscene is null
                : openingCutscene?.CompletedSuccessfully == true);
        var gameplayCamera = player.GetNode<Camera3D>("Head/Camera3D");
        GD.Print($"OPENDAO_GAMEPLAY_CAMERA player={player.GlobalPosition} camera={gameplayCamera.GlobalPosition} " +
                 $"forward={-gameplayCamera.GlobalBasis.Z} spring={player.GetNode<SpringArm3D>("Head").SpringLength:F2}");
        var capturePath = System.Environment.GetEnvironmentVariable("OPENDAO_GAME_START_CAPTURE") ?? string.Empty;
        var gameStartImage = await CaptureVisibleGameplayFrame(cancellationToken);
        var frameEvidence = gameStartImage.Evidence;
        var capture = capturePath.Length == 0
            ? Error.Ok
            : gameStartImage.Image.SavePng(capturePath);
        PrintGameplayFrameVisibility(frameEvidence, capturePath);
        var locomotionPassed = System.Environment.GetEnvironmentVariable("OPENDAO_FLOW_LOCOMOTION") != "1" ||
                               await LocomotionSmoke.RunAsync(player, cancellationToken);
        var passed = expected && correctArea && correctCutscenePolicy && capture == Error.Ok &&
                     frameEvidence.Passed && player.IsPhysicsProcessing() && locomotionPassed;
        GD.Print($"OPENDAO_CHARACTER_GAME_START_ACCEPTANCE status={(passed ? "pass" : "fail")} " +
                 $"character={character.Name} area={profile?.AreaId} player_control={(player.IsPhysicsProcessing() ? 1 : 0)} " +
                 $"opening_cutscene={(origin?.OpeningCutscene.Length > 0 ? origin.OpeningCutscene : "not-authored-for-area")} " +
                 $"locomotion={(locomotionPassed ? "pass" : "fail")} capture={capturePath}");
        await WaitForProcessFrames(ConfiguredFrameCount(GameplayHoldFramesVariable), cancellationToken);
        GetTree().Quit(passed ? 0 : 59);
    }

    private void RestoreSession()
    {
        if (services is null || profile is null ||
            System.Environment.GetEnvironmentVariable("OPENDAO_CONTINUE") != "1") return;
        var session = services.GetRequired<IPlayerSessionRepository>().Load();
        if (session is null || (session.AreaId.Length > 0 && !string.Equals(session.AreaId, profile.AreaId,
            StringComparison.OrdinalIgnoreCase))) return;
        player.GlobalPosition = session.Position;
        player.Rotation = player.Rotation with { Y = session.Yaw };
        var head = player.GetNode<Node3D>("Head");
        head.Rotation = head.Rotation with { X = Mathf.Clamp(session.Pitch, Mathf.DegToRad(-85), Mathf.DegToRad(85)) };
        GD.Print($"OPENDAO_SESSION restored area={session.AreaId} position={session.Position}");
    }

    private void SaveSession()
    {
        if (services is null || profile is null || !IsInstanceValid(player) ||
            System.Environment.GetEnvironmentVariable("OPENDAO_TEST_NO_PERSIST") == "1") return;
        var head = player.GetNodeOrNull<Node3D>("Head");
        services.GetRequired<IPlayerSessionRepository>().Save(new PlayerSession(string.Empty,
            profile.AreaId, player.GlobalPosition, player.Rotation.Y, head?.Rotation.X ?? 0,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(), progression?.Experience ?? 0));
    }

    private async Task CaptureIfRequested(CancellationToken cancellationToken)
    {
        var path = System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE")?.Trim() ?? string.Empty;
        if (path.Length == 0) return;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError("OPENDAO_CAPTURE status=fail reason=headless-display-server");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1") GetTree().Quit(1);
            return;
        }
        for (var i = 0; i < 24; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(path);
        GD.Print($"OPENDAO_CAPTURE path={path} status={(error == Error.Ok ? "pass" : "fail")}");
        if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
            GetTree().Quit(error == Error.Ok ? 0 : 1);
    }

    private async Task<bool> CaptureCharacterPbrCloseIfRequested(
        CancellationToken cancellationToken)
    {
        var path = System.Environment.GetEnvironmentVariable(
            CharacterPbrCloseCaptureVariable)?.Trim() ?? string.Empty;
        if (path.Length == 0) return false;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError("OPENDAO_CHARACTER_PBR_CLOSE_CAPTURE status=fail " +
                         "reason=headless-display-server");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
            {
                GetTree().Quit(1);
                return true;
            }
            return false;
        }

        var avatarRoot = player.GetNode<Node3D>("AvatarRoot");
        var gameplayCamera = player.GetNode<Camera3D>("Head/Camera3D");
        var avatarVisible = avatarRoot.Visible;
        var hudVisible = hud?.Visible == true;
        var playerProcessing = player.IsProcessing();
        var physicsProcessing = player.IsPhysicsProcessing();
        var gameplayCameraCurrent = gameplayCamera.Current;
        Camera3D? inspectionCamera = null;
        var passed = false;
        try
        {
            player.SetProcess(false);
            player.SetPhysicsProcess(false);
            player.SetCameraClearanceCaptureOverride(true);
            player.Velocity = Vector3.Zero;
            avatarRoot.Visible = true;
            if (hud is not null) hud.Visible = false;
            gameplayCamera.Current = false;

            var bounds = SceneBounds.Calculate(avatarRoot);
            if (bounds.Size.IsZeroApprox() || !bounds.Size.IsFinite())
                throw new InvalidDataException(
                    "Player avatar bounds are unavailable for close capture.");
            var targetLocal = bounds.Position + new Vector3(
                bounds.Size.X * .5f, bounds.Size.Y * .76f, bounds.Size.Z * .5f);
            var target = avatarRoot.ToGlobal(targetLocal);
            var actorForward = -player.GlobalBasis.Z;
            actorForward.Y = 0;
            if (actorForward.LengthSquared() < .001f) actorForward = Vector3.Forward;
            actorForward = actorForward.Normalized();
            var actorRight = player.GlobalBasis.X;
            actorRight.Y = 0;
            actorRight = actorRight.Normalized();

            inspectionCamera = new Camera3D
            {
                Name = "CharacterPbrCloseCaptureCamera",
                Current = true,
                Fov = 30,
                Near = .03f,
                Far = gameplayCamera.Far,
                KeepAspect = Camera3D.KeepAspectEnum.Height
            };
            AddChild(inspectionCamera);
            inspectionCamera.GlobalPosition = target + actorForward * 2.15f +
                                              actorRight * .34f + Vector3.Up * .05f;
            inspectionCamera.LookAt(target, Vector3.Up);

            var query = PhysicsRayQueryParameters3D.Create(
                inspectionCamera.GlobalPosition, target, 3);
            query.Exclude = [player.GetRid()];
            var lineOfSightClear = GetWorld3D().DirectSpaceState.IntersectRay(query).Count == 0;
            var viewportSize = GetViewport().GetVisibleRect().Size;
            var framing = CinematicFramingGate.Evaluate(
                new CinematicFramingSample(
                    ToNumerics(inspectionCamera.GlobalPosition),
                    ToNumerics(-inspectionCamera.GlobalBasis.Z),
                    ToNumerics(inspectionCamera.GlobalBasis.Y),
                    inspectionCamera.Fov,
                    viewportSize.X / Math.Max(1, viewportSize.Y),
                    inspectionCamera.Near,
                    ToNumerics(target),
                    Math.Clamp(bounds.Size.Y * .22f, .25f, .55f),
                    lineOfSightClear),
                new CinematicFramingRequirements(.03f, .35f, .9f));

            await WaitForProcessFrames(16, cancellationToken);
            var image = GetViewport().GetTexture().GetImage();
            var save = image.SavePng(path);
            var metrics = MeasureWorldVisibility(image);
            var imagePassed = metrics.VisibleRatio >= .35f &&
                              metrics.MeanLuminance >= .02f;
            passed = save == Error.Ok && imagePassed && framing.Accepted;
            GD.Print("OPENDAO_CHARACTER_PBR_CLOSE_CAPTURE " +
                     $"status={(passed ? "pass" : "fail")} " +
                     $"framing={(framing.Accepted ? "pass" : "fail")} " +
                     $"projected_height={framing.ProjectedHeight:0.####} " +
                     $"camera_depth={framing.CameraDepth:0.####} " +
                     $"line_of_sight={(lineOfSightClear ? "clear" : "blocked")} " +
                     $"visible_ratio={metrics.VisibleRatio:0.####} " +
                     $"mean_luminance={metrics.MeanLuminance:0.####} " +
                     "view=front-three-quarter shader_tier=enhanced " +
                     $"capture={path}");
        }
        finally
        {
            if (IsInstanceValid(inspectionCamera)) inspectionCamera!.QueueFree();
            gameplayCamera.Current = gameplayCameraCurrent;
            avatarRoot.Visible = avatarVisible;
            if (hud is not null) hud.Visible = hudVisible;
            player.SetProcess(playerProcessing);
            player.SetPhysicsProcess(physicsProcessing);
            player.SetCameraClearanceCaptureOverride(false);
        }

        if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
        {
            GetTree().Quit(passed ? 0 : 1);
            return true;
        }
        return false;
    }

    private async Task<bool> CaptureEffectCloseIfRequested(
        CancellationToken cancellationToken)
    {
        var path = System.Environment.GetEnvironmentVariable(
            EffectCloseCaptureVariable)?.Trim() ?? string.Empty;
        if (path.Length == 0) return false;
        var requested = Path.GetFileNameWithoutExtension(
            System.Environment.GetEnvironmentVariable(
                EffectCloseCaptureResRefVariable)?.Trim() ?? string.Empty);
        if (requested.Length == 0 || !requested.StartsWith(
                "fxe_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{EffectCloseCaptureResRefVariable} must name one source effect resref.");
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            GD.PushError("OPENDAO_EFFECT_CLOSE_CAPTURE status=fail " +
                         "reason=headless-display-server");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
                GetTree().Quit(1);
            return true;
        }

        var matching = new List<Node3D>();
        CollectSourceEffectRoots(GetNode<Node3D>("DAOScene"), requested, matching);
        if (matching.Count == 0)
        {
            GD.PushError("OPENDAO_EFFECT_CLOSE_CAPTURE status=fail " +
                         $"reason=requested-source-effect-absent resref={requested}");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
                GetTree().Quit(1);
            return true;
        }

        await WaitForProcessFrames(24, cancellationToken);
        const float captureFov = 34;
        Node3D? selected = null;
        GpuParticles3D[] emitters = [];
        var graphEmitterCount = 0;
        var particleBounds = new Aabb();
        var target = Vector3.Zero;
        var cameraPosition = Vector3.Zero;
        var cameraDistance = 0f;
        var projectedHeight = 0f;
        var visibleBoundsProbes = 0;
        var space = GetWorld3D().DirectSpaceState;
        var clearanceShape = new SphereShape3D { Radius = .4f };
        foreach (var effect in matching.OrderBy(candidate =>
                     candidate.GlobalPosition.DistanceSquaredTo(player.GlobalPosition)))
        {
            var effectEmitters = effect.GetChildren().OfType<GpuParticles3D>().ToArray();
            var focusEmitter = effectEmitters
                .OrderByDescending(emitter => emitter.HasMeta(
                    "dao_effect_scale_axis_contract") && emitter.GetMeta(
                    "dao_effect_scale_axis_contract").AsString().Equals(
                    "source-independent-x-y", StringComparison.Ordinal))
                .ThenByDescending(emitter => emitter.Amount)
                .FirstOrDefault();
            if (focusEmitter is null ||
                !TryCaptureParticleBounds([focusEmitter], out var bounds)) continue;
            var center = bounds.GetCenter();
            // Particle graphs rooted at terrain commonly straddle the authored
            // surface. Aim into the upper half of the live particle volume so
            // the diagnostic ray does not terminate under the floor.
            center.Y = bounds.Position.Y + bounds.Size.Y * .6f;
            var halfHeight = Math.Max(.15f, bounds.Size.Y * .5f);
            var halfWidthAtAspect = Math.Max(.15f, bounds.Size.X * .5f / (16f / 9f));
            var framingRadius = Math.Max(halfHeight, halfWidthAtAspect);
            var distance = Math.Clamp(
                framingRadius / Mathf.Tan(Mathf.DegToRad(captureFov * .3f)) +
                bounds.Size.Z * .5f, 1.1f, 30f);
            var towardPlayer = player.GlobalPosition - center;
            towardPlayer.Y = 0;
            if (towardPlayer.LengthSquared() < .001f) towardPlayer = Vector3.Back;
            towardPlayer = towardPlayer.Normalized();
            for (var index = 0; index < 12; index++)
            {
                var direction = towardPlayer.Rotated(Vector3.Up,
                    index * Mathf.Tau / 12f);
                var candidate = center + direction * distance +
                                Vector3.Up * Math.Clamp(framingRadius * .3f, .25f, 2f);
                var clearance = new PhysicsShapeQueryParameters3D
                {
                    Shape = clearanceShape,
                    Transform = new Transform3D(Basis.Identity, candidate),
                    CollisionMask = 3,
                    CollideWithAreas = true,
                    CollideWithBodies = true
                };
                if (space.IntersectShape(clearance, 1).Count != 0) continue;
                var probeCount = CountVisibleParticleBoundsProbes(
                    space, candidate, center, bounds, direction);
                if (probeCount < 4) continue;
                selected = effect;
                emitters = [focusEmitter];
                graphEmitterCount = effectEmitters.Length;
                particleBounds = bounds;
                target = center;
                cameraPosition = candidate;
                cameraDistance = candidate.DistanceTo(center);
                projectedHeight = Mathf.RadToDeg(2 * Mathf.Atan(
                    halfHeight / Math.Max(.001f, cameraDistance))) / captureFov;
                visibleBoundsProbes = probeCount;
                break;
            }
            if (selected is not null) break;
        }
        if (selected is null)
        {
            GD.PushError("OPENDAO_EFFECT_CLOSE_CAPTURE status=fail " +
                         $"reason=no-unoccluded-active-particle-bounds resref={requested} " +
                         $"instances={matching.Count}");
            if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
                GetTree().Quit(1);
            return true;
        }

        var nextPath = Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(path))!,
            Path.GetFileNameWithoutExtension(path) + "-next" +
            (Path.GetExtension(path).Length == 0 ? ".png" : Path.GetExtension(path)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var avatarRoot = player.GetNode<Node3D>("AvatarRoot");
        var gameplayCamera = player.GetNode<Camera3D>("Head/Camera3D");
        var avatarVisible = avatarRoot.Visible;
        var hudVisible = hud?.Visible == true;
        var statusVisible = status.Visible;
        var playerProcessing = player.IsProcessing();
        var playerPhysicsProcessing = player.IsPhysicsProcessing();
        var gameplayCameraCurrent = gameplayCamera.Current;
        var diagnosticHidden = new List<(Node3D Node, bool Visible)>();
        CollectDiagnosticOccluders(GetNode<Node3D>("DAOScene"), selected,
            diagnosticHidden);
        const uint diagnosticRenderLayer = 1u << 19;
        var emitterLayers = emitters.Select(emitter => emitter.Layers).ToArray();
        Camera3D? inspectionCamera = null;
        var passed = false;
        try
        {
            player.SetProcess(false);
            player.SetPhysicsProcess(false);
            avatarRoot.Visible = false;
            if (hud is not null) hud.Visible = false;
            status.Visible = false;
            foreach (var (node, _) in diagnosticHidden) node.Visible = false;
            foreach (var emitter in emitters) emitter.Layers = diagnosticRenderLayer;
            gameplayCamera.Current = false;
            inspectionCamera = new Camera3D
            {
                Name = "EffectCloseCaptureCamera",
                Current = true,
                CullMask = diagnosticRenderLayer,
                Fov = captureFov,
                Near = .03f,
                Far = gameplayCamera.Far,
                KeepAspect = Camera3D.KeepAspectEnum.Height,
                Environment = new Godot.Environment
                {
                    BackgroundMode = Godot.Environment.BGMode.Color,
                    // A neutral slate background keeps isolated source pixels
                    // legible without presenting a nearly black frame as
                    // visual-quality evidence.
                    BackgroundColor = new Color(.12f, .14f, .17f),
                    BackgroundEnergyMultiplier = 1
                }
            };
            AddChild(inspectionCamera);
            inspectionCamera.GlobalPosition = cameraPosition;
            inspectionCamera.LookAt(target, Vector3.Up);

            await WaitForProcessFrames(24, cancellationToken);
            var first = GetViewport().GetTexture().GetImage();
            var firstSave = first.SavePng(path);
            await WaitForProcessFrames(8, cancellationToken);
            var next = GetViewport().GetTexture().GetImage();
            var nextSave = next.SavePng(nextPath);
            var evidence = MeasureCentralEffectEvidence(first, next);
            passed = firstSave == Error.Ok && nextSave == Error.Ok &&
                     projectedHeight is >= .3f and <= .9f &&
                     evidence.PeakLuminance >= .03f &&
                     evidence.MotionRatio >= .0002f;
            GD.Print("OPENDAO_EFFECT_CLOSE_CAPTURE " +
                     $"status={(passed ? "pass" : "fail")} resref={requested.ToLowerInvariant()} " +
                     $"instances={matching.Count} graph_emitters={graphEmitterCount} " +
                     $"captured_emitters={emitters.Length} " +
                     $"captured_emitter={emitters[0].Name} " +
                     $"scale_axis_contract={emitters[0].GetMeta("dao_effect_scale_axis_contract").AsString()} " +
                     $"scale_age_keys={emitters[0].GetMeta("dao_effect_scale_age_keys").AsInt32()} " +
                     $"contract_kind={selected.GetMeta("dao_effect_contract_kind").AsString()} " +
                     $"source_mmh_sha256={selected.GetMeta("dao_effect_mmh_sha256").AsString()} " +
                     $"effect_position={selected.GlobalPosition} target={target} " +
                     $"camera={cameraPosition} " +
                     $"active_bounds={particleBounds} camera_distance={cameraDistance:0.####} " +
                     $"projected_height={projectedHeight:0.####} " +
                     $"bounds_visibility={visibleBoundsProbes}/5 " +
                     "line_of_sight=clear camera_clearance=clear " +
                     $"central_peak_luminance={evidence.PeakLuminance:0.####} " +
                     $"neighbor_frame_motion_ratio={evidence.MotionRatio:0.####} " +
                     $"capture={path} next_capture={nextPath} " +
                     "capture_mode=isolated-source-emitter " +
                     "selection=source-resref-only renderer_policy=unchanged parity_claim=none");
        }
        finally
        {
            if (IsInstanceValid(inspectionCamera)) inspectionCamera!.QueueFree();
            gameplayCamera.Current = gameplayCameraCurrent;
            avatarRoot.Visible = avatarVisible;
            if (hud is not null) hud.Visible = hudVisible;
            status.Visible = statusVisible;
            foreach (var (node, visible) in diagnosticHidden) node.Visible = visible;
            for (var index = 0; index < emitters.Length; index++)
                emitters[index].Layers = emitterLayers[index];
            player.SetProcess(playerProcessing);
            player.SetPhysicsProcess(playerPhysicsProcessing);
        }

        if (System.Environment.GetEnvironmentVariable("DAOPEN_CAPTURE_EXIT") == "1")
        {
            GetTree().Quit(passed ? 0 : 1);
            return true;
        }
        return false;
    }

    private static void CollectSourceEffectRoots(Node node, string requested,
        ICollection<Node3D> matches)
    {
        if (node is Node3D effect && effect.HasMeta("dao_effect") &&
            effect.GetMeta("dao_effect_source").AsString().Equals(
                "installed-mmh-mao-dds", StringComparison.Ordinal) &&
            effect.GetMeta("dao_effect_resref").AsString().Equals(
                requested, StringComparison.OrdinalIgnoreCase))
            matches.Add(effect);
        foreach (var child in node.GetChildren())
            CollectSourceEffectRoots(child, requested, matches);
    }

    private static bool TryCaptureParticleBounds(
        IReadOnlyCollection<GpuParticles3D> emitters, out Aabb bounds)
    {
        bounds = new Aabb();
        var ready = false;
        foreach (var emitter in emitters)
        {
            var local = emitter.CaptureAabb();
            if (!local.Position.IsFinite() || !local.Size.IsFinite() ||
                local.Size.IsZeroApprox()) continue;
            var world = emitter.GlobalTransform * local;
            bounds = ready ? bounds.Merge(world) : world;
            ready = true;
        }
        return ready && bounds.Size.X <= 100 && bounds.Size.Y <= 100 &&
               bounds.Size.Z <= 100;
    }

    private static int CountVisibleParticleBoundsProbes(
        PhysicsDirectSpaceState3D space, Vector3 camera, Vector3 center,
        Aabb bounds, Vector3 viewDirection)
    {
        var horizontalRadius = Math.Max(bounds.Size.X, bounds.Size.Z) * .35f;
        var viewRight = viewDirection.Cross(Vector3.Up);
        if (viewRight.LengthSquared() < .001f) viewRight = Vector3.Right;
        viewRight = viewRight.Normalized();
        var points = new[]
        {
            center,
            center + viewRight * horizontalRadius,
            center - viewRight * horizontalRadius,
            center + Vector3.Up * bounds.Size.Y * .3f,
            center - Vector3.Up * bounds.Size.Y * .3f
        };
        var clear = 0;
        foreach (var point in points)
        {
            var towardCamera = camera - point;
            var end = point + towardCamera.Normalized() *
                Math.Min(.2f, Math.Max(.05f, towardCamera.Length() * .02f));
            var query = PhysicsRayQueryParameters3D.Create(camera, end, 3);
            query.HitFromInside = true;
            if (space.IntersectRay(query).Count == 0) clear++;
        }
        return clear;
    }

    private static void CollectDiagnosticOccluders(Node node, Node3D selectedEffect,
        ICollection<(Node3D Node, bool Visible)> hidden)
    {
        if (node is Node3D node3D &&
            (node3D.HasMeta("dao_actor") ||
             node3D.HasMeta("dao_effect") && node3D != selectedEffect))
        {
            hidden.Add((node3D, node3D.Visible));
            return;
        }
        foreach (var child in node.GetChildren())
            CollectDiagnosticOccluders(child, selectedEffect, hidden);
    }

    private static (float PeakLuminance, float MotionRatio)
        MeasureCentralEffectEvidence(Image first, Image next)
    {
        if (first.IsEmpty() || next.IsEmpty() || first.GetSize() != next.GetSize())
            return default;
        var width = first.GetWidth();
        var height = first.GetHeight();
        var x0 = width / 5;
        var x1 = width - x0;
        var y0 = height / 5;
        var y1 = height - y0;
        var peak = 0f;
        var changed = 0L;
        var samples = 0L;
        for (var y = y0; y < y1; y += 2)
            for (var x = x0; x < x1; x += 2)
            {
                var a = first.GetPixel(x, y);
                var b = next.GetPixel(x, y);
                peak = Math.Max(peak, Math.Max(SkyLuminance(a), SkyLuminance(b)));
                if (Math.Max(Math.Abs(a.R - b.R),
                        Math.Max(Math.Abs(a.G - b.G), Math.Abs(a.B - b.B))) >= .015f)
                    changed++;
                samples++;
            }
        return (peak, samples == 0 ? 0 : (float)changed / samples);
    }

    private static System.Numerics.Vector3 ToNumerics(Vector3 value) =>
        new(value.X, value.Y, value.Z);
}
