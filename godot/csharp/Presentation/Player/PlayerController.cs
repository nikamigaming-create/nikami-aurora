using Godot;
using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.World;
using OpenDAO.Presentation;
using OpenDAO.Presentation.Rigging;
using OpenDAO.Infrastructure.World;

namespace OpenDAO.Presentation.Player;

public partial class PlayerController : CharacterBody3D
{
    public enum LocomotionState
    {
        Idle,
        Walk,
        Run
    }

    private const float GroundClearance = 0.95f;
    private const float GroundRayUp = 0.1f;
    private const float GroundRayDown = 80.0f;
    private const float MaximumStepUp = 0.46f;
    private const float MaximumWalkableDrop = 0.72f;
    private const float MinimumStepUp = 0.025f;
    private const float StepProbeLead = 0.37f;
    private const float RecoveryRayDown = 160.0f;
    private const uint WorldCollisionMask = 3;

    [Export] public float WalkSpeed { get; set; } = 5.0f;
    [Export] public float SprintSpeed { get; set; } = 9.0f;
    [Export] public float MouseSensitivity { get; set; } = 0.0018f;
    [Export] public float JumpVelocity { get; set; } = 6.5f;
    [Export] public float Gravity { get; set; } = 18.0f;
    [Export] public float SwimSpeed { get; set; } = 4.2f;
    [Export] public float GroundAcceleration { get; set; } = 32.0f;
    [Export] public float GroundDeceleration { get; set; } = 40.0f;
    [Export] public float AirAcceleration { get; set; } = 9.0f;

    private readonly Dictionary<string, float> waterSurfaces = new(StringComparer.Ordinal);
    private Camera3D locomotionCamera = null!;
    private Node3D avatarRoot = null!;
    private Node3D? avatar;
    private PackedScene? avatarTemplate;
    private AnimationPlayer? avatarAnimations;
    private string currentAvatarAnimation = string.Empty;
    private LocomotionAnimationSet? locomotionAnimations;
    private XRController3D? leftController;
    private bool xrActive;
    private bool scriptedMotion;
    private bool grounded = true;
    private bool inWater;
    private float waterSurfaceY;
    private Vector2? movementInputOverride;
    private AuthoredNavigationGrid? authoredNavigation;

    public int GroundRecoveryCount { get; private set; }
    public int GroundGuardBlockCount { get; private set; }
    public int StepUpCount { get; private set; }
    public LocomotionState CurrentLocomotionState { get; private set; } = LocomotionState.Idle;
    public string CurrentAvatarAnimation => currentAvatarAnimation;
    public bool IsAvatarAnimationPlaying => avatarAnimations?.IsPlaying() == true;
    public bool HasPlayableWalkAnimation => locomotionAnimations is not null &&
        ResolveAnimationName(locomotionAnimations.Walk).Length > 0;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        FloorSnapLength = 0.55f;
        FloorMaxAngle = Mathf.DegToRad(50.0f);
        FloorConstantSpeed = true;
        FloorStopOnSlope = true;
        FloorBlockOnWall = true;
        MaxSlides = 8;
        SafeMargin = 0.025f;
        CollisionMask = WorldCollisionMask;
        locomotionCamera = GetNode<Camera3D>("Head/Camera3D");
        avatarRoot = GetNode<Node3D>("AvatarRoot");
        grounded = IsOnFloor();
    }

    public Camera3D LocomotionCamera => locomotionCamera;

    public Node3D? DuplicateAvatarForPortrait() =>
        avatarTemplate?.Instantiate<Node3D>() ?? avatar?.Duplicate() as Node3D;

    public Node3D? DuplicateAvatarForCinematics() =>
        avatarTemplate?.Instantiate<Node3D>() ?? avatar?.Duplicate() as Node3D;

    public void SetAvatar(string modelPath, LocomotionAnimationSet? animationSet,
        IGodotModelPostprocessor modelPostprocessor)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            GD.PushWarning("OPENDAO_PLAYER_AVATAR status=missing path=" + modelPath);
            return;
        }

        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(modelPath, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D imported)
        {
            GD.PushWarning("OPENDAO_PLAYER_AVATAR status=import-failed path=" + modelPath);
            return;
        }
        modelPostprocessor.Process(imported, state);

        locomotionAnimations = null;
        if (animationSet is not null && TryLoadModel(animationSet.BankPath) is { } animationBank)
        {
            if (RigAnimationBank.Merge(imported, animationBank, out var failure))
                locomotionAnimations = animationSet;
            else
                GD.PushWarning("OPENDAO_LOCOMOTION_BANK status=merge-failed reason=" + failure);
            animationBank.Free();
        }
        var packed = new PackedScene();
        avatarTemplate = packed.Pack(imported) == Error.Ok ? packed : null;
        avatar?.QueueFree();
        avatar = imported;
        avatarRoot.AddChild(imported);
        var bounds = SceneBounds.Calculate(imported);
        if (!bounds.Size.IsZeroApprox())
        {
            var center = bounds.GetCenter();
            imported.Position = new Vector3(-center.X, -GroundClearance - bounds.Position.Y, -center.Z);
        }
        avatarAnimations = RigAnimationBank.FindAnimationPlayer(imported);
        ConfigureLocomotionLoops();
        PlayAvatarAnimation(LocomotionState.Idle);
        GD.Print($"OPENDAO_PLAYER_AVATAR status=ready path={modelPath} " +
                 $"animations={avatarAnimations?.GetAnimationList().Length ?? 0} " +
                 $"locomotion={(HasPlayableWalkAnimation ? "authored" : "unavailable")} " +
                 $"animation_names={string.Join(',', avatarAnimations?.GetAnimationList() ?? [])}");
    }

    public void SetXrActive(bool enabled, Camera3D xrCamera, XRController3D controller)
    {
        xrActive = enabled;
        locomotionCamera = enabled ? xrCamera : GetNode<Camera3D>("Head/Camera3D");
        leftController = controller;
        Input.MouseMode = enabled ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
    }

    public void SetScriptedMotion(bool enabled)
    {
        scriptedMotion = enabled;
        Velocity = Vector3.Zero;
    }

    public void SetMovementInputOverride(Vector2? input) => movementInputOverride = input;

    public void ResetLocomotionState()
    {
        movementInputOverride = null;
        scriptedMotion = false;
        Velocity = Vector3.Zero;
        PlayAvatarAnimation(LocomotionState.Idle);
        GD.Print($"OPENDAO_LOCOMOTION_HANDOFF state={CurrentLocomotionState} " +
                 $"animation={CurrentAvatarAnimation} velocity={Velocity}");
    }

    public void SetAuthoredNavigation(AuthoredNavigationGrid? navigation) =>
        authoredNavigation = navigation;

    public void ResetThirdPersonView()
    {
        var head = GetNode<SpringArm3D>("Head");
        head.Rotation = new Vector3(Mathf.DegToRad(-10), 0, 0);
        locomotionCamera.Rotation = Vector3.Zero;
    }

    public void SetThirdPersonDistance(float distance) =>
        GetNode<SpringArm3D>("Head").SpringLength = Math.Max(0.8f, distance);

    public void SetWaterState(bool enabled, float surfaceY) =>
        SetWaterVolumeState("scripted", enabled, surfaceY);

    public void SetWaterVolumeState(string volumeId, bool enabled, float surfaceY)
    {
        if (enabled) waterSurfaces[volumeId] = surfaceY;
        else waterSurfaces.Remove(volumeId);
        inWater = waterSurfaces.Count > 0;
        waterSurfaceY = inWater ? waterSurfaces.Values.Max() : 0;
        CollisionMask = WorldCollisionMask;
        Velocity = Vector3.Zero;
        GD.Print($"OPENDAO_WATER_STATE active={inWater} volumes={waterSurfaces.Count} " +
                 $"surface_y={waterSurfaceY:F3} position={GlobalPosition}");
    }

    public void ClearWaterVolumes()
    {
        waterSurfaces.Clear();
        inWater = false;
        waterSurfaceY = 0;
        CollisionMask = WorldCollisionMask;
        Velocity = Vector3.Zero;
    }

    public bool SnapToWalkableGround(Vector3 requestedPosition, string context, bool emitFailure = true)
    {
        var hit = FindGround(requestedPosition + Vector3.Up * GroundClearance, false);
        if (hit.Count == 0) hit = FindGround(requestedPosition + Vector3.Up * GroundClearance, true);
        if (hit.Count == 0)
        {
            grounded = false;
            if (emitFailure) GD.Print($"OPENDAO_INDOOR_GROUND status=fail context={context} requested={requestedPosition}");
            return false;
        }
        var point = hit["position"].AsVector3();
        var groundNode = hit["collider"].AsGodotObject() as Node;
        GlobalPosition = new Vector3(requestedPosition.X, point.Y + GroundClearance, requestedPosition.Z);
        Velocity = Vector3.Zero;
        grounded = true;
        GD.Print($"OPENDAO_INDOOR_GROUND status=pass context={context} requested={requestedPosition} " +
                 $"position={GlobalPosition} collider={groundNode?.Name ?? "unknown"}");
        return true;
    }

    public void LookAtTarget(Vector3 target)
    {
        var camera = GetNode<Camera3D>("Head/Camera3D");
        camera.Rotation = Vector3.Zero;
        var direction = (target - camera.GlobalPosition).Normalized();
        Rotation = Rotation with { Y = Mathf.Atan2(-direction.X, -direction.Z) };
        var head = GetNode<Node3D>("Head");
        head.Rotation = new Vector3(Mathf.Asin(Mathf.Clamp(direction.Y, -1, 1)), 0, 0);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!IsPhysicsProcessing() || scriptedMotion) return;
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            RotateY(-motion.Relative.X * MouseSensitivity);
            var head = GetNode<Node3D>("Head");
            head.RotateX(-motion.Relative.Y * MouseSensitivity);
            head.Rotation = head.Rotation with
            {
                X = Mathf.Clamp(head.Rotation.X, Mathf.DegToRad(-85), Mathf.DegToRad(85))
            };
        }
        else if (@event is InputEventKey { Pressed: true, Keycode: Key.Escape })
            Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (@event is InputEventMouseButton { Pressed: true })
            Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (scriptedMotion) { Velocity = Vector3.Zero; return; }
        var input = movementInputOverride ??
                    Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        if (xrActive && leftController is not null)
        {
            var stick = leftController.GetVector2("primary");
            if (stick.Length() > input.Length()) input = stick;
        }

        var forward = -locomotionCamera.GlobalBasis.Z;
        var right = locomotionCamera.GlobalBasis.X;
        forward.Y = right.Y = 0;
        forward = forward.Normalized();
        right = right.Normalized();
        var direction = (right * input.X + forward * -input.Y).Normalized();
        if (inWater)
        {
            var buoyancy = Mathf.Clamp((waterSurfaceY - 0.22f - GlobalPosition.Y) * 3.6f, -2.2f, 2.2f);
            Velocity = direction * SwimSpeed + Vector3.Up * (buoyancy + (Input.IsActionPressed("jump") ? 2.4f : 0));
            MoveAndSlide();
            grounded = false;
            return;
        }

        var sprinting = Input.IsActionPressed("sprint");
        var speed = sprinting ? SprintSpeed : WalkSpeed;
        if (!direction.IsZeroApprox())
        {
            var localDirection = GlobalBasis.Inverse() * direction;
            var targetYaw = Mathf.Atan2(-localDirection.X, -localDirection.Z);
            avatarRoot.Rotation = avatarRoot.Rotation with
            {
                Y = Mathf.LerpAngle(avatarRoot.Rotation.Y, targetYaw,
                    1.0f - Mathf.Exp(-14.0f * (float)delta))
            };
        }
        PlayAvatarAnimation(direction.IsZeroApprox()
            ? LocomotionState.Idle
            : sprinting ? LocomotionState.Run : LocomotionState.Walk);
        var velocity = Velocity;
        var targetVelocity = direction * speed;
        var acceleration = grounded
            ? (direction.IsZeroApprox() ? GroundDeceleration : GroundAcceleration)
            : AirAcceleration;
        velocity.X = Mathf.MoveToward(velocity.X, targetVelocity.X, acceleration * (float)delta);
        velocity.Z = Mathf.MoveToward(velocity.Z, targetVelocity.Z, acceleration * (float)delta);
        var wasGrounded = IsOnFloor() || grounded;
        var previousPosition = GlobalPosition;
        var startedOnAuthoredGrid = authoredNavigation?.IsWalkableWorld(
            previousPosition.X, previousPosition.Z) == true;
        if (wasGrounded && startedOnAuthoredGrid && !direction.IsZeroApprox())
            ConstrainToAuthoredNavigation(ref velocity, (float)delta);
        if (wasGrounded && !direction.IsZeroApprox())
            TryStepUp(direction, new Vector3(velocity.X, 0, velocity.Z).Length() * (float)delta);
        if (IsOnFloor()) velocity.Y = Input.IsActionJustPressed("jump") ? JumpVelocity : 0;
        else velocity.Y -= Gravity * (float)delta;
        Velocity = velocity;
        MoveAndSlide();
        if (startedOnAuthoredGrid && authoredNavigation?.IsWalkableWorld(
                GlobalPosition.X, GlobalPosition.Z) != true)
        {
            GlobalPosition = GlobalPosition with { X = previousPosition.X, Z = previousPosition.Z };
            Velocity = Velocity with { X = 0, Z = 0 };
            GroundGuardBlockCount++;
        }
        if (!IsOnFloor() && Velocity.Y <= 0) ApplyFloorSnap();
        grounded = IsOnFloor();
        if (!grounded && wasGrounded && Velocity.Y <= 0)
        {
            grounded = TrySnapToNearbyGround(MaximumWalkableDrop);
            if (grounded) Velocity = Velocity with { Y = 0 };
        }
        if (grounded) return;
    }

    private void TryStepUp(Vector3 direction, float travelDistance)
    {
        if (travelDistance <= 0 || !TestMove(GlobalTransform, direction * travelDistance)) return;
        var probe = GlobalPosition + direction * (StepProbeLead + travelDistance) +
                    Vector3.Up * MaximumStepUp;
        var hit = FindGround(probe, false);
        if (hit.Count == 0) return;
        var stepHeight = hit["position"].AsVector3().Y + GroundClearance - GlobalPosition.Y;
        if (stepHeight < MinimumStepUp || stepHeight > MaximumStepUp) return;

        var lift = Vector3.Up * (stepHeight + SafeMargin);
        if (TestMove(GlobalTransform, lift)) return;
        GlobalPosition += lift;
        StepUpCount++;
    }

    private void ConstrainToAuthoredNavigation(ref Vector3 velocity, float delta)
    {
        if (authoredNavigation is null) return;
        var targetX = GlobalPosition.X + velocity.X * delta;
        var targetZ = GlobalPosition.Z + velocity.Z * delta;
        if (authoredNavigation.IsWalkableWorld(targetX, targetZ)) return;

        var xAllowed = authoredNavigation.IsWalkableWorld(targetX, GlobalPosition.Z);
        var zAllowed = authoredNavigation.IsWalkableWorld(GlobalPosition.X, targetZ);
        if (xAllowed && zAllowed)
        {
            if (Mathf.Abs(velocity.X) >= Mathf.Abs(velocity.Z)) velocity.Z = 0;
            else velocity.X = 0;
        }
        else
        {
            if (!xAllowed) velocity.X = 0;
            if (!zAllowed) velocity.Z = 0;
        }
        GroundGuardBlockCount++;
    }

    private bool TrySnapToNearbyGround(float maximumDrop)
    {
        var hit = FindGround(GlobalPosition + Vector3.Up * GroundRayUp, false);
        if (hit.Count == 0) return false;
        var targetY = hit["position"].AsVector3().Y + GroundClearance;
        var drop = GlobalPosition.Y - targetY;
        if (drop < -MaximumStepUp || drop > maximumDrop) return false;
        GlobalPosition = GlobalPosition with { Y = targetY };
        return true;
    }

    private Godot.Collections.Dictionary FindGround(Vector3 position, bool deep)
    {
        var start = position + Vector3.Up * (deep ? 0.2f : GroundRayUp);
        var end = position - Vector3.Up * (deep ? RecoveryRayDown : GroundRayDown);
        for (var attempt = 0; attempt < (deep ? 24 : 12); attempt++)
        {
            var query = PhysicsRayQueryParameters3D.Create(start, end, WorldCollisionMask,
                new Godot.Collections.Array<Rid> { GetRid() });
            query.HitBackFaces = true;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            if (hit.Count == 0) return hit;
            var normal = hit["normal"].AsVector3();
            if (normal.Y < 0) normal = -normal;
            var point = hit["position"].AsVector3();
            if (normal.AngleTo(Vector3.Up) <= FloorMaxAngle && (deep || point.Y + GroundClearance - position.Y <= MaximumStepUp))
            {
                hit["normal"] = normal;
                return hit;
            }
            start = point - Vector3.Up * 0.03f;
        }
        return [];
    }

    private void PlayAvatarAnimation(LocomotionState state)
    {
        if (avatarAnimations is null || locomotionAnimations is null) return;
        var desired = state switch
        {
            LocomotionState.Walk => locomotionAnimations.Walk,
            LocomotionState.Run => locomotionAnimations.Run,
            _ => locomotionAnimations.Idle
        };
        var qualifiedName = ResolveAnimationName(desired);
        if (qualifiedName.Length == 0)
        {
            GD.PushWarning($"OPENDAO_LOCOMOTION_ANIMATION status=missing state={state} name={desired}");
            return;
        }
        if (currentAvatarAnimation == qualifiedName && avatarAnimations.IsPlaying()) return;
        CurrentLocomotionState = state;
        currentAvatarAnimation = qualifiedName;
        avatarAnimations.Play(qualifiedName, customBlend: 0.16);
    }

    private void ConfigureLocomotionLoops()
    {
        if (avatarAnimations is null || locomotionAnimations is null) return;
        foreach (var name in new[]
                 {
                     locomotionAnimations.Idle,
                     locomotionAnimations.Walk,
                     locomotionAnimations.Run
                 })
        {
            var qualifiedName = ResolveAnimationName(name);
            if (qualifiedName.Length > 0)
                avatarAnimations.GetAnimation(qualifiedName).LoopMode = Godot.Animation.LoopModeEnum.Linear;
        }
    }

    private string ResolveAnimationName(string resourceName)
    {
        if (avatarAnimations is null) return string.Empty;
        foreach (var name in avatarAnimations.GetAnimationList())
        {
            var candidate = name.ToString();
            var separator = candidate.LastIndexOf('/');
            var localName = separator >= 0 ? candidate[(separator + 1)..] : candidate;
            if (NormalizeAnimationIdentity(localName).Equals(
                    NormalizeAnimationIdentity(resourceName), StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return string.Empty;
    }

    private static string NormalizeAnimationIdentity(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character)
            ? char.ToLowerInvariant(character) : '_'));

    private static Node3D? TryLoadModel(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var document = new GltfDocument();
        var state = new GltfState();
        return document.AppendFromFile(path, state) == Error.Ok
            ? document.GenerateScene(state) as Node3D
            : null;
    }

}
