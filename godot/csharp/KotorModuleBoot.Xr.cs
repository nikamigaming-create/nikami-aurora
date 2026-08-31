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
    private void BindXrPlayerRig(Node3D model)
    {
        xrLeftArm = null;
        xrRightArm = null;
        xrRigAcceptanceReported = false;
        if (!xrActive) return;

        var armMeshes = FindDescendants<MeshInstance3D>(model).Where(mesh =>
            mesh.Name.ToString().Contains("_LArm_", StringComparison.OrdinalIgnoreCase) ||
            mesh.Name.ToString().Contains("_RArm_", StringComparison.OrdinalIgnoreCase)).ToArray();
        var leftMesh = armMeshes.Single(mesh => mesh.Name.ToString().Contains(
            "_LArm_", StringComparison.OrdinalIgnoreCase));
        var rightMesh = armMeshes.Single(mesh => mesh.Name.ToString().Contains(
            "_RArm_", StringComparison.OrdinalIgnoreCase));
        var leftSkeleton = ResolveArmSkeleton(
            model, leftMesh, "lbicep_g", "Lforearm_g", "Lhand_g", "lhand");
        var rightSkeleton = ResolveArmSkeleton(
            model, rightMesh, "rbicep_g", "Rforearm_g", "Rhand_g", "rhand");

        xrLeftArm = CreateXrTrackedArmBinding(
            leftSkeleton, xrLeftHand, xrLeftGripTarget, leftMesh, true,
            "lbicep_g", "Lforearm_g", "Lhand_g", "lhand");
        xrRightArm = CreateXrTrackedArmBinding(
            rightSkeleton, xrRightHand, xrRightGripTarget, rightMesh, false,
            "rbicep_g", "Rforearm_g", "Rhand_g", "rhand");
        leftMesh.Visible = true;
        rightMesh.Visible = true;
        GD.Print("NIKAMI_AURORA_XR_RIG status=bound " +
                 "source=owned-kotor-player partitions=LArm,RArm " +
                 "chains=lbicep_g->lhand,rbicep_g->rhand " +
                 "socketAuthority=authored-grip proceduralFallback=none");
    }

    private static Skeleton3D ResolveArmSkeleton(
        Node3D model,
        MeshInstance3D mesh,
        params string[] requiredBones)
    {
        var linked = mesh.Skeleton.IsEmpty
            ? null
            : mesh.GetNodeOrNull(mesh.Skeleton) as Skeleton3D;
        if (linked is not null && requiredBones.All(name => linked.FindBone(name) >= 0))
            return linked;

        var matching = FindDescendants<Skeleton3D>(model).Where(candidate =>
            requiredBones.All(name => candidate.FindBone(name) >= 0)).ToArray();
        if (matching.Length != 1)
        {
            var inventory = string.Join(";", FindDescendants<Skeleton3D>(model).Select(
                candidate => $"{candidate.GetPath()}=[{string.Join(',',
                    Enumerable.Range(0, candidate.GetBoneCount()).Select(
                        candidate.GetBoneName))}]"));
            throw new InvalidDataException(
                $"XR arm skin {mesh.Name} expected one skeleton with " +
                $"[{string.Join(',', requiredBones)}], found {matching.Length}; " +
                $"meshSkeleton={mesh.Skeleton}; skeletons={inventory}");
        }
        return matching[0];
    }

    private static XrTrackedArmBinding CreateXrTrackedArmBinding(
        Skeleton3D skeleton,
        XRController3D controller,
        Node3D target,
        MeshInstance3D mesh,
        bool left,
        string shoulderName,
        string elbowName,
        string handName,
        string socketName)
    {
        var shoulder = RequireBone(skeleton, shoulderName);
        var elbow = RequireBone(skeleton, elbowName);
        var hand = RequireBone(skeleton, handName);
        var socket = RequireBone(skeleton, socketName);
        var upperLength = skeleton.GetBoneGlobalRest(shoulder).Origin.DistanceTo(
            skeleton.GetBoneGlobalRest(elbow).Origin);
        var lowerLength = skeleton.GetBoneGlobalRest(elbow).Origin.DistanceTo(
            skeleton.GetBoneGlobalRest(socket).Origin);
        if (upperLength is < 0.15f or > 0.55f || lowerLength is < 0.15f or > 0.55f)
            throw new InvalidDataException(
                $"XR {side(left)} arm has invalid authored lengths {upperLength:F4}/{lowerLength:F4}");

        var ik = new Fabrik3D
        {
            Name = left ? "LeftTrackedArmIK" : "RightTrackedArmIK",
            SettingCount = 1,
            MinDistance = 0.001f,
            MaxIterations = 48,
            Deterministic = true,
            Influence = 0.0f
        };
        skeleton.AddChild(ik);
        ik.SetRootBone(0, shoulder);
        ik.SetEndBone(0, socket);
        ik.SetExtendEndBone(0, false);
        ik.SetTargetNode(0, ik.GetPathTo(target));
        return new XrTrackedArmBinding(
            left, controller, target, mesh, skeleton, ik,
            shoulder, elbow, hand, socket, upperLength, lowerLength);

        static string side(bool isLeft) => isLeft ? "left" : "right";
    }

    private static int RequireBone(Skeleton3D skeleton, string name)
    {
        var index = skeleton.FindBone(name);
        if (index < 0)
            throw new InvalidDataException($"XR player rig bone is missing: {name}");
        return index;
    }

    private void UpdateXrTrackedPlayerRig()
    {
        if (!xrActive || xrLeftArm is null || xrRightArm is null) return;
        var leftReady = UpdateXrTrackedArm(xrLeftArm);
        var rightReady = UpdateXrTrackedArm(xrRightArm);
        if (!leftReady || !rightReady || xrRigAcceptanceReported ||
            xrLeftArm.StableFrames < 8 || xrRightArm.StableFrames < 8)
            return;

        var controllerVisuals = FindDescendants<MeshInstance3D>(xrLeftHand).Count() +
                                FindDescendants<MeshInstance3D>(xrRightHand).Count();
        if (controllerVisuals != 0 || !xrLeftArm.Mesh.Visible || !xrRightArm.Mesh.Visible)
            throw new InvalidDataException(
                "XR rig provider contract drifted: expected only two owned skinned arm partitions");
        xrRigAcceptanceReported = true;
        GD.Print($"NIKAMI_AURORA_XR_RIG status=pass " +
                 $"provider=owned-kotor-skinned-rig controllerVisuals={controllerVisuals} " +
                 $"leftTracked={xrLeftArm.Controller.GetHasTrackingData()} " +
                 $"rightTracked={xrRightArm.Controller.GetHasTrackingData()} " +
                 $"leftSocketError={xrLeftArm.SocketError:F6} " +
                 $"rightSocketError={xrRightArm.SocketError:F6} " +
                 $"leftClamped={xrLeftArm.TargetClamped} " +
                 $"rightClamped={xrRightArm.TargetClamped}");
    }

    private bool UpdateXrTrackedArm(XrTrackedArmBinding arm)
    {
        var tracked = arm.Controller.GetIsActive() && arm.Controller.GetHasTrackingData();
        if (!tracked)
        {
            arm.TrackingFrames = 0;
            arm.StableFrames = 0;
            arm.Ik.Influence = 0.0f;
            return false;
        }
        arm.TrackingFrames++;

        var controllerWorld = arm.Controller.GlobalTransform;
        if (!IsFinite(controllerWorld.Origin))
            return false;
        if (!arm.Calibrated && arm.TrackingFrames >= 5)
        {
            var socketWorld = arm.Skeleton.GlobalTransform *
                              arm.Skeleton.GetBoneGlobalPose(arm.SocketBone);
            arm.ControllerToSocketBasis =
                controllerWorld.Basis.Orthonormalized().Inverse() *
                socketWorld.Basis.Orthonormalized();
            arm.Calibrated = true;
            GD.Print($"NIKAMI_AURORA_XR_RIG status=calibrated " +
                     $"side={(arm.Left ? "left" : "right")} " +
                     $"tracker={arm.Controller.Tracker} pose={arm.Controller.Pose} " +
                     $"upper={arm.UpperLength:F4} lower={arm.LowerLength:F4} " +
                     "positionAnchor=authored-grip-socket orientation=stock-relative");
        }
        if (!arm.Calibrated) return false;

        var shoulderWorld = arm.Skeleton.GlobalTransform *
                            arm.Skeleton.GetBoneGlobalPose(arm.ShoulderBone);
        var targetPosition = controllerWorld.Origin;
        if (launchEnvironment.Get(
                "NIKAMI_AURORA_TEST_XR_TRACKED_RIG") == "1")
        {
            var viewForward = -xrCamera.GlobalBasis.Z.Normalized();
            var viewRight = xrCamera.GlobalBasis.X.Normalized();
            var phase = readyFrames * 0.045f + (arm.Left ? 0.0f : Mathf.Pi);
            targetPosition = xrCamera.GlobalPosition + viewForward * 0.62f +
                             viewRight * (arm.Left ? -0.23f : 0.23f) +
                             Vector3.Down * (0.32f + 0.055f * Mathf.Sin(phase));
        }

        var shoulderToTarget = targetPosition - shoulderWorld.Origin;
        var maximumReach = (arm.UpperLength + arm.LowerLength) * 0.985f;
        arm.TargetClamped = shoulderToTarget.Length() > maximumReach;
        if (arm.TargetClamped)
            targetPosition = shoulderWorld.Origin + shoulderToTarget.Normalized() * maximumReach;
        var targetBasis = (controllerWorld.Basis.Orthonormalized() *
                           arm.ControllerToSocketBasis).Orthonormalized();
        arm.Target.GlobalTransform = new Transform3D(targetBasis, targetPosition);

        arm.Ik.Influence = 1.0f;

        var resolvedSocket = arm.Skeleton.GlobalTransform *
                             arm.Skeleton.GetBoneGlobalPose(arm.SocketBone);
        arm.SocketError = resolvedSocket.Origin.DistanceTo(arm.Target.GlobalPosition);
        arm.StableFrames = arm.SocketError <= 0.035f
            ? arm.StableFrames + 1
            : 0;
        return true;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void UpdateXrSpectatorCamera()
    {
        UpdateXrLocalAvatarVisibility();
        if (xrActive)
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
        var shouldShowHead = !xrActive;
        if (xrLocalPlayerHeadVisible == shouldShowHead) return;
        var allMeshes = FindDescendants<MeshInstance3D>(playerModel).ToArray();
        // The importer flattens some Odyssey model nodes, so the authored head
        // hook is not a reliable runtime parent. Mask only the separately named
        // PMHA head meshes; this leaves PMB body geometry and the weapon intact.
        var headMeshes = allMeshes.Where(mesh => mesh.Name.ToString().StartsWith(
            "mesh__PMHA", StringComparison.OrdinalIgnoreCase)).ToArray();
        var armMeshes = allMeshes.Where(mesh =>
            KotorRigIdentityPolicy.IsArmMeshName(mesh.Name.ToString())).ToArray();
        var bodyMeshes = allMeshes.Count(mesh => mesh.Name.ToString().StartsWith(
            "mesh__PMB", StringComparison.OrdinalIgnoreCase));
        var hasLeftHand = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().Contains("lhand", StringComparison.OrdinalIgnoreCase));
        var hasRightHand = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().Contains("rhand", StringComparison.OrdinalIgnoreCase));
        if (headMeshes.Length != 8 || armMeshes.Length != 2 || bodyMeshes < 3 ||
            !hasLeftHand || !hasRightHand)
            throw new InvalidDataException(
                "Local player head/body/hand visibility contract drifted");
        foreach (var headMesh in headMeshes)
            headMesh.Visible = shouldShowHead;
        foreach (var armMesh in armMeshes)
            armMesh.Visible = true;
        xrLocalPlayerHeadVisible = shouldShowHead;
        var weapon = FindDescendants<Node3D>(playerModel).Any(node =>
            node.Name.ToString().StartsWith(
                "weapon__", StringComparison.OrdinalIgnoreCase));
        GD.Print($"NIKAMI_AURORA_XR_LOCAL_AVATAR status=" +
                 $"{(shouldShowHead ? "desktop-head-visible" : "xr-head-hidden")} " +
                 $"headMeshes={headMeshes.Length} bodyMeshes={bodyMeshes} " +
                 $"localArms=visible " +
                 $"handProvider={(xrActive ? "owned-kotor-skinned-rig" : "desktop-avatar")} " +
                  $"weapon={(weapon ? "present" : "none")}");
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
        if (launchEnvironment.Get("NIKAMI_AURORA_OPENXR") != "1")
        {
            GD.Print("NIKAMI_AURORA_OPENXR status=disabled");
            return;
        }
        var openXr = XRServer.FindInterface("OpenXR");
        if (openXr is null || (!openXr.IsInitialized() && !openXr.Initialize()))
        {
            GD.PushWarning("NIKAMI_AURORA_OPENXR status=unavailable fallback=desktop");
            if (launchEnvironment.Get(
                    "NIKAMI_AURORA_OPENXR_EXPECT_ACTIVE") == "1")
            {
                GD.PushError("NIKAMI_AURORA_OPENXR status=fail expected=active");
                RequestCleanExit(2);
            }
            return;
        }
        xrActive = true;
        AttachXrControllers();
        xrSpectatorActive = launchEnvironment.Get(
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
                Fov = xrSpectatorFieldOfView,
                CullMask = RuntimeCameraCullMask
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
        GD.Print("NIKAMI_AURORA_OPENXR status=ready worldScale=1.000 " +
                 "authority=hmd-relative-to-player-body " +
                 $"spectator={xrSpectatorActive}");
        GD.Print("NIKAMI_AURORA_XR_RIG status=provider-selected " +
                 "provider=owned-kotor-skinned-rig runtimeControllerModels=disabled " +
                 "proceduralFallback=removed");
    }

    private Control CreateXrHudSurface()
    {
        xrHudViewport = new SubViewport
        {
            Name = "OpenXRHudViewport",
            Size = new Vector2I(1200, 600),
            TransparentBg = true,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            GuiDisableInput = true
        };
        AddChild(xrHudViewport);
        xrHudRoot = new Control
        {
            Name = "OpenXRHudRoot",
            Size = new Vector2(1200, 600),
            CustomMinimumSize = new Vector2(1200, 600),
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        xrHudViewport.AddChild(xrHudRoot);
        var material = new StandardMaterial3D
        {
            AlbedoTexture = xrHudViewport.GetTexture(),
            AlbedoColor = Colors.White,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Mix,
            DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            NoDepthTest = true,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear
        };
        xrHudQuad = new MeshInstance3D
        {
            Name = "OpenXRHudQuad",
            Mesh = new QuadMesh
            {
                Size = new Vector2(1.6f, 0.8f),
                Material = material
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Visible = false
        };
        AddChild(xrHudQuad);
        GD.Print("NIKAMI_AURORA_XR_HUD status=ready mode=world-locked-quad " +
                 "pixels=1200x600 size=1.600x0.800");
        return xrHudRoot;
    }

    private void UpdateXrHudVisibility()
    {
        if (xrHudQuad is null) return;
        var dialogueVisible = dialoguePanel is { Visible: true };
        var visible = dialogueVisible || interactionPrompt is { Visible: true };
        if (visible && !xrHudWasVisible && !dialogueVisible)
            AnchorXrHudInFrontOfViewer();
        xrHudQuad.Visible = visible;
        xrHudWasVisible = visible;
    }

    private void AnchorXrDialogueHud(string? speakerActor)
    {
        if (xrHudQuad is null) return;
        if (speakerActor is null || !actorModels.TryGetValue(speakerActor, out var speaker))
        {
            if (!xrHudWasVisible)
                AnchorXrHudInFrontOfViewer();
            return;
        }
        var speakerPoint = actorTalkOffsets.TryGetValue(speakerActor, out var talkOffset)
            ? speaker.GlobalTransform * talkOffset
            : speaker.GlobalPosition + Vector3.Up * 1.45f;
        var eye = xrCamera.GlobalPosition;
        var towardViewer = eye - speakerPoint;
        towardViewer.Y = 0;
        towardViewer = towardViewer.LengthSquared() > 0.000001f
            ? towardViewer.Normalized()
            : Vector3.Back;
        var viewerRight = Vector3.Up.Cross(towardViewer).Normalized();
        var anchor = speakerPoint + Vector3.Down * 0.55f +
                     viewerRight * 0.95f + towardViewer * 0.12f;
        PositionXrHud(anchor, eye);
        GD.Print($"NIKAMI_AURORA_XR_HUD status=anchored mode=speaker-world " +
                 $"speaker={speakerActor} position={anchor}");
    }

    private void AnchorXrHudInFrontOfViewer()
    {
        if (xrHudQuad is null) return;
        var eye = xrCamera.GlobalPosition;
        var forward = -xrCamera.GlobalBasis.Z.Normalized();
        var anchor = eye + forward * 1.6f + Vector3.Down * 0.25f;
        PositionXrHud(anchor, eye);
        GD.Print($"NIKAMI_AURORA_XR_HUD status=anchored mode=viewer-initial " +
                 $"position={anchor}");
    }

    private void PositionXrHud(Vector3 position, Vector3 viewer)
    {
        if (xrHudQuad is null) return;
        xrHudQuad.GlobalPosition = position;
        xrHudQuad.LookAt(viewer, Vector3.Up);
        xrHudQuad.RotateY(Mathf.Pi);
    }
}
