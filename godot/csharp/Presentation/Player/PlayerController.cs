using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Presentation;
using Nikami.Aurora.GodotRuntime.Presentation.Rigging;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;
using Nikami.Aurora.GodotRuntime.Infrastructure.World;

namespace Nikami.Aurora.GodotRuntime.Presentation.Player;

public sealed record DaoGameplayCameraAcceptance(
    bool ActiveCamera,
    bool SubjectProjected,
    bool SubjectLineOfSight,
    float ActualArmLength,
    float PredictedArmLength,
    float MinimumArmLength,
    float SelectedYawDegrees,
    Vector2 SubjectScreenPosition)
{
    public bool Passed => ActiveCamera && SubjectProjected && SubjectLineOfSight &&
                          ActualArmLength >= MinimumArmLength &&
                          PredictedArmLength >= MinimumArmLength;
}

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
    private bool enhancedCameraClearance;
    private bool avatarFadedForCameraClearance;
    private bool cameraClearanceCaptureOverride;
    private float minimumAvatarCameraClearance;
    private float avatarCameraClearanceHysteresis;
    private float enhancedPivotHeight;
    private float compressedPivotHeight;
    private float compressedPivotLateral;
    private float cameraProbeRadius;
    private float avatarBodyTransparency;
    private float avatarHeadTransparency;
    private float selectedObstructionYaw;
    private float authoredCameraArmLength = 2.7f;
    private const float ObstructionSearchSwitchHysteresis = .25f;
    private const float ObstructionSearchYawPenaltyPerDegree = .03f;
    private static readonly float[] ObstructionSearchYawDegrees =
        [0, 35, -35, 70, -70, 90, -90];
    private readonly Dictionary<MeshInstance3D, float> avatarBaseTransparency = [];
    private readonly Dictionary<MeshInstance3D, bool> avatarBaseVisibility = [];
    private float waterSurfaceY;
    private Vector2? movementInputOverride;
    private Vector3? movementWorldDirectionOverride;
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
    public float AuthoredGameplayCameraArmLength => authoredCameraArmLength;

    public void SetGameplayCameraArmForAcceptance(float requestedArmLength)
    {
        var head = GetNode<SpringArm3D>("Head");
        var minimum = enhancedCameraClearance ? minimumAvatarCameraClearance : 0;
        head.SpringLength = Math.Clamp(requestedArmLength, minimum,
            authoredCameraArmLength);
        selectedObstructionYaw = 0;
        head.Rotation = head.Rotation with { Y = 0 };
    }

    /// <summary>
    /// Makes the authored gameplay camera current after cinematic release and
    /// advances the same collision-safe orbit policy used during live play.
    /// Call on neighboring physics/process frames before accepting a capture.
    /// </summary>
    public DaoGameplayCameraAcceptance SettleGameplayCameraForAcceptance()
        => EvaluateGameplayCameraForAcceptance(true);

    public DaoGameplayCameraAcceptance SampleGameplayCameraForAcceptance()
        => EvaluateGameplayCameraForAcceptance(false);

    private DaoGameplayCameraAcceptance EvaluateGameplayCameraForAcceptance(
        bool advanceObstructionSearch)
    {
        if (!IsInsideTree() || !IsInstanceValid(locomotionCamera))
            return new DaoGameplayCameraAcceptance(false, false, false,
                0, 0, minimumAvatarCameraClearance, 0, Vector2.Zero);
        locomotionCamera.MakeCurrent();
        var head = GetNode<SpringArm3D>("Head");
        var actualArmLength = head.GlobalPosition.DistanceTo(
            locomotionCamera.GlobalPosition);
        if (enhancedCameraClearance && advanceObstructionSearch)
            UpdateObstructionSearch(head, actualArmLength);
        actualArmLength = head.GlobalPosition.DistanceTo(locomotionCamera.GlobalPosition);
        var predictedArmLength = enhancedCameraClearance
            ? PredictCameraClearance(head.Rotation.Y, head.SpringLength)
            : actualArmLength;
        var subject = GlobalPosition + GlobalBasis * new Vector3(0, 1.05f, 0);
        var viewportSize = GetViewport().GetVisibleRect().Size;
        var behind = locomotionCamera.IsPositionBehind(subject);
        var screen = behind ? new Vector2(-1, -1) :
            locomotionCamera.UnprojectPosition(subject);
        var margin = viewportSize * .04f;
        var projected = !behind && screen.X >= margin.X && screen.Y >= margin.Y &&
                        screen.X <= viewportSize.X - margin.X &&
                        screen.Y <= viewportSize.Y - margin.Y;
        var ray = PhysicsRayQueryParameters3D.Create(
            locomotionCamera.GlobalPosition, subject, WorldCollisionMask, [GetRid()]);
        ray.HitFromInside = true;
        var lineOfSight = GetWorld3D().DirectSpaceState.IntersectRay(ray).Count == 0;
        var active = GetViewport().GetCamera3D() == locomotionCamera;
        return new DaoGameplayCameraAcceptance(active, projected, lineOfSight,
            actualArmLength, predictedArmLength,
            enhancedCameraClearance ? minimumAvatarCameraClearance : 0,
            Mathf.RadToDeg(head.Rotation.Y), screen);
    }

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
        modelPostprocessor.Process(imported, state, modelPath);

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
        avatarBaseTransparency.Clear();
        avatarBaseVisibility.Clear();
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
    public void SetWorldMovementDirectionOverride(Vector3? direction) =>
        movementWorldDirectionOverride = direction;

    public void ResetLocomotionState()
    {
        movementInputOverride = null;
        movementWorldDirectionOverride = null;
        scriptedMotion = false;
        Velocity = Vector3.Zero;
        PlayAvatarAnimation(LocomotionState.Idle);
        GD.Print($"OPENDAO_LOCOMOTION_HANDOFF state={CurrentLocomotionState} " +
                 $"animation={CurrentAvatarAnimation} velocity={Velocity}");
    }

    public void SetAuthoredNavigation(AuthoredNavigationGrid? navigation) =>
        authoredNavigation = navigation;

    public IReadOnlyList<Vector3> BuildAuthoredNavigationPath(Vector3 target)
    {
        if (authoredNavigation is null) return [];
        var navigation = authoredNavigation;
        var start = ClosestWalkableCell(navigation, GlobalPosition);
        var goal = ClosestWalkableCell(navigation, target);
        if (start < 0 || goal < 0) return [];

        var frontier = new PriorityQueue<int, float>();
        var costs = new float[navigation.Accessibility.Length];
        Array.Fill(costs, float.PositiveInfinity);
        var previous = new Dictionary<int, int>();
        costs[start] = 0;
        frontier.Enqueue(start, 0);
        (int Column, int Row)[] offsets = [(1, 0), (-1, 0), (0, 1), (0, -1)];
        while (frontier.TryDequeue(out var current, out _))
        {
            if (current == goal) break;
            var column = current % navigation.Columns;
            var row = current / navigation.Columns;
            foreach (var offset in offsets)
            {
                var nextColumn = column + offset.Column;
                var nextRow = row + offset.Row;
                if (!navigation.IsWalkable(nextColumn, nextRow)) continue;
                var next = nextRow * navigation.Columns + nextColumn;
                var nextCost = costs[current] + 1;
                if (nextCost >= costs[next]) continue;
                costs[next] = nextCost;
                previous[next] = current;
                var goalColumn = goal % navigation.Columns;
                var goalRow = goal / navigation.Columns;
                var heuristic = Math.Abs(nextColumn - goalColumn) +
                                Math.Abs(nextRow - goalRow);
                frontier.Enqueue(next, nextCost + heuristic);
            }
        }
        if (start != goal && !previous.ContainsKey(goal)) return [];

        var cells = new List<int> { goal };
        while (cells[^1] != start) cells.Add(previous[cells[^1]]);
        cells.Reverse();
        var points = new List<Vector3>();
        var previousDirection = (Column: 0, Row: 0);
        for (var index = 1; index < cells.Count; index++)
        {
            var previousCell = cells[index - 1];
            var cell = cells[index];
            var direction = (Column: cell % navigation.Columns -
                                     previousCell % navigation.Columns,
                Row: cell / navigation.Columns - previousCell / navigation.Columns);
            if (index > 1 && direction != previousDirection)
                points.Add(NavigationCellPosition(navigation, previousCell));
            previousDirection = direction;
        }
        points.Add(NavigationCellPosition(navigation, goal));
        return points;
    }

    public void FaceGameplayTarget(Vector3 target)
    {
        var direction = target - GlobalPosition;
        direction.Y = 0;
        if (direction.LengthSquared() < .0001f) return;
        direction = direction.Normalized();
        Rotation = Rotation with { Y = Mathf.Atan2(-direction.X, -direction.Z) };
    }

    public bool PrepareGameplayCameraForMovement(Vector3 worldDirection,
        float lookaheadMeters = .65f)
    {
        if (!enhancedCameraClearance) return true;
        worldDirection.Y = 0;
        if (worldDirection.LengthSquared() < .0001f) return true;
        var forecastPosition = GlobalPosition +
                               worldDirection.Normalized() * lookaheadMeters;
        var head = GetNode<SpringArm3D>("Head");
        var clearances = ObstructionSearchYawDegrees
            .Select(degrees =>
            {
                var yaw = Mathf.DegToRad(degrees);
                return (Yaw: yaw, Length: Math.Min(
                    PredictCameraClearanceAt(GlobalPosition, yaw, head.SpringLength),
                    PredictCameraClearanceAt(forecastPosition, yaw, head.SpringLength)));
            })
            .Where(candidate => candidate.Length >= minimumAvatarCameraClearance)
            .OrderByDescending(candidate => ObstructionScore(candidate,
                minimumAvatarCameraClearance + .35f))
            .ThenBy(candidate => Math.Abs(candidate.Yaw))
            .ToArray();
        if (clearances.Length == 0) return false;
        selectedObstructionYaw = clearances[0].Yaw;
        head.Rotation = head.Rotation with { Y = selectedObstructionYaw };
        return true;
    }

    private static int ClosestWalkableCell(AuthoredNavigationGrid navigation,
        Vector3 point)
    {
        var closest = -1;
        var closestDistance = float.PositiveInfinity;
        for (var row = 0; row < navigation.Rows; row++)
            for (var column = 0; column < navigation.Columns; column++)
            {
                if (!navigation.IsWalkable(column, row)) continue;
                var candidate = NavigationCellPosition(navigation,
                    row * navigation.Columns + column);
                var distance = new Vector2(candidate.X - point.X,
                    candidate.Z - point.Z).LengthSquared();
                if (distance >= closestDistance) continue;
                closest = row * navigation.Columns + column;
                closestDistance = distance;
            }
        return closest;
    }

    private static Vector3 NavigationCellPosition(AuthoredNavigationGrid navigation,
        int cell)
    {
        var column = cell % navigation.Columns;
        var row = cell / navigation.Columns;
        return new Vector3(navigation.BaseX + column * navigation.CellSize, 0,
            -(navigation.BaseY + row * navigation.CellSize));
    }

    public void ConfigureThirdPersonView(DaoGameplayCameraConfiguration configuration,
        bool enhancedPresentation)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var head = GetNode<SpringArm3D>("Head");
        head.Position = head.Position with
        {
            Y = enhancedPresentation
                ? configuration.EnhancedPivotHeightMeters
                : configuration.PivotHeightMeters
        };
        head.Rotation = new Vector3(Mathf.DegToRad(configuration.PitchDegrees), 0, 0);
        head.SpringLength = configuration.SpringLengthMeters;
        authoredCameraArmLength = configuration.SpringLengthMeters;
        head.Margin = configuration.CollisionMarginMeters;
        head.CollisionMask = WorldCollisionMask;
        head.Shape = enhancedPresentation
            ? new SphereShape3D { Radius = configuration.EnhancedCollisionProbeRadiusMeters }
            : null;
        enhancedCameraClearance = enhancedPresentation;
        enhancedPivotHeight = configuration.EnhancedPivotHeightMeters;
        compressedPivotHeight = configuration.EnhancedCompressedPivotHeightMeters;
        compressedPivotLateral = configuration.EnhancedCompressedPivotLateralMeters;
        cameraProbeRadius = configuration.EnhancedCollisionProbeRadiusMeters;
        minimumAvatarCameraClearance = configuration.EnhancedMinimumAvatarClearanceMeters;
        avatarCameraClearanceHysteresis =
            configuration.EnhancedAvatarClearanceHysteresisMeters;
        avatarBodyTransparency = configuration.EnhancedAvatarBodyTransparency;
        avatarHeadTransparency = configuration.EnhancedAvatarHeadTransparency;
        avatarFadedForCameraClearance = false;
        cameraClearanceCaptureOverride = false;
        selectedObstructionYaw = 0;
        locomotionCamera.Rotation = Vector3.Zero;
        locomotionCamera.Fov = configuration.FieldOfViewDegrees;
        locomotionCamera.Near = configuration.NearPlaneMeters;
        locomotionCamera.Far = configuration.FarPlaneMeters;
        GD.Print($"OPENDAO_GAMEPLAY_CAMERA_PROFILE status=ready " +
                 $"calibration={configuration.CalibrationStatus} " +
                 $"evidence={(configuration.CalibrationStatus == DaoGameplayCameraConfiguration.RetailAccepted ? "matched-retail-player-camera-telemetry" : "blocked-matched-retail-player-camera-telemetry-required")} " +
                 $"fov={configuration.FieldOfViewDegrees:0.###} " +
                 $"pitch={configuration.PitchDegrees:0.###} " +
                 $"pivot_y={head.Position.Y:0.###} " +
                 $"spring={configuration.SpringLengthMeters:0.###} " +
                 $"margin={configuration.CollisionMarginMeters:0.###} " +
                 $"collision_probe={(enhancedPresentation ? "sphere" : "ray")} " +
                 $"probe_radius={(enhancedPresentation ? configuration.EnhancedCollisionProbeRadiusMeters : 0):0.###} " +
                 $"avatar_clearance={(enhancedPresentation ? configuration.EnhancedMinimumAvatarClearanceMeters : 0):0.###} " +
                 $"compressed_pivot=({(enhancedPresentation ? configuration.EnhancedCompressedPivotLateralMeters : 0):0.###}," +
                 $"{(enhancedPresentation ? configuration.EnhancedCompressedPivotHeightMeters : 0):0.###}) " +
                 $"adaptation={(enhancedPresentation ? "enhanced-over-shoulder-near-fade-non-parity" : "source-disabled")}");
    }

    public override void _Process(double delta)
    {
        if (!enhancedCameraClearance || cameraClearanceCaptureOverride || xrActive ||
            !IsPhysicsProcessing() || scriptedMotion ||
            avatar is null || !IsInstanceValid(avatar)) return;
        var head = GetNode<SpringArm3D>("Head");
        var actualArmLength = head.GlobalPosition.DistanceTo(locomotionCamera.GlobalPosition);
        var orbitClear = UpdateObstructionSearch(head, actualArmLength);
        var fadeThreshold = avatarFadedForCameraClearance
            ? minimumAvatarCameraClearance + avatarCameraClearanceHysteresis
            : minimumAvatarCameraClearance;
        var shouldFade = !orbitClear && actualArmLength < fadeThreshold;
        if (shouldFade == avatarFadedForCameraClearance) return;
        avatarFadedForCameraClearance = shouldFade;
        ApplyCameraClearancePresentation(shouldFade);
        GD.Print("OPENDAO_GAMEPLAY_CAMERA_CLEARANCE status=ready " +
                 $"avatar={(shouldFade ? "visible-near-faded" : "visible-opaque")} " +
                 $"actual_arm={actualArmLength:0.###} " +
                 $"threshold={minimumAvatarCameraClearance:0.###} " +
                 $"hysteresis={avatarCameraClearanceHysteresis:0.###} " +
                 $"pivot=({head.Position.X:0.###},{head.Position.Y:0.###}) " +
                 "collision_probe=sphere body_preserved=1 scope=application " +
                 "tier=enhanced parity_claim=none");
    }

    private bool UpdateObstructionSearch(SpringArm3D head, float actualArmLength)
    {
        if (selectedObstructionYaw == 0 &&
            actualArmLength >= minimumAvatarCameraClearance &&
            PredictCameraClearance(0, head.SpringLength) >= minimumAvatarCameraClearance)
            return false;

        var clearances = new List<(float Yaw, float Length)>(
            ObstructionSearchYawDegrees.Length);
        foreach (var degrees in ObstructionSearchYawDegrees)
        {
            var yaw = Mathf.DegToRad(degrees);
            clearances.Add((yaw, PredictCameraClearance(yaw, head.SpringLength)));
        }
        var baseline = clearances[0].Length;
        var current = clearances.MinBy(value =>
            Math.Abs(Mathf.AngleDifference(value.Yaw, selectedObstructionYaw)));
        // The configured safe-framing threshold is the minimum usable arm.
        // Once a candidate clears it, additional distance is capped in the
        // score so a small authored-yaw departure beats an unnecessary 90°
        // orbit while still rejecting near-first-person candidates.
        var minimumUsableArm = minimumAvatarCameraClearance;
        var lengthScoreCap = minimumUsableArm + .35f;
        var usable = clearances.Where(value => value.Length >= minimumUsableArm).ToArray();
        var best = usable
            .OrderByDescending(value =>
                ObstructionScore(value, lengthScoreCap))
            .ThenBy(value => Math.Abs(value.Yaw))
            .FirstOrDefault();

        if (selectedObstructionYaw != 0 &&
            baseline >= minimumAvatarCameraClearance + avatarCameraClearanceHysteresis &&
            baseline + ObstructionSearchSwitchHysteresis >= current.Length)
        {
            selectedObstructionYaw = 0;
            head.Rotation = head.Rotation with { Y = 0 };
            GD.Print("OPENDAO_GAMEPLAY_CAMERA_OBSTRUCTION_SEARCH status=ready " +
                     $"mode=authored-yaw-restored predicted_arm={baseline:0.###} " +
                     "sphere_casts=7 scope=application tier=enhanced parity_claim=none");
            return false;
        }

        if (usable.Length == 0)
        {
            if (selectedObstructionYaw != 0)
            {
                selectedObstructionYaw = 0;
                head.Rotation = head.Rotation with { Y = 0 };
            }
            GD.Print("OPENDAO_GAMEPLAY_CAMERA_OBSTRUCTION_SEARCH status=partial " +
                     $"mode=close-shoulder-fallback best_arm={clearances.Max(value => value.Length):0.###} " +
                     $"minimum_usable_arm={minimumUsableArm:0.###} sphere_casts=7 " +
                     "scope=application tier=enhanced parity_claim=none");
            return false;
        }

        if (selectedObstructionYaw == 0 ||
            current.Length < minimumUsableArm ||
            ObstructionScore(best, lengthScoreCap) >
            ObstructionScore(current, lengthScoreCap) + ObstructionSearchSwitchHysteresis)
        {
            selectedObstructionYaw = best.Yaw;
            head.Rotation = head.Rotation with { Y = selectedObstructionYaw };
            ApplyCameraClearancePresentation(false);
            GD.Print("OPENDAO_GAMEPLAY_CAMERA_OBSTRUCTION_SEARCH status=ready " +
                     $"mode=clear-orbit yaw_degrees={Mathf.RadToDeg(best.Yaw):0.###} " +
                     $"predicted_arm={best.Length:0.###} authored_arm={baseline:0.###} " +
                     $"minimum_usable_arm={minimumUsableArm:0.###} " +
                     $"score={ObstructionScore(best, lengthScoreCap):0.###} " +
                     "yaw_penalty_per_degree=0.03 sphere_casts=7 hysteresis=0.25 scope=application " +
                     "tier=enhanced parity_claim=none");
        }
        return true;
    }

    private static float ObstructionScore((float Yaw, float Length) candidate,
        float lengthScoreCap) =>
        Math.Min(candidate.Length, lengthScoreCap) -
        Math.Abs(Mathf.RadToDeg(candidate.Yaw)) * ObstructionSearchYawPenaltyPerDegree;

    private float PredictCameraClearance(float yaw, float springLength)
        => PredictCameraClearanceAt(GlobalPosition, yaw, springLength);

    private float PredictCameraClearanceAt(Vector3 playerPosition, float yaw,
        float springLength)
    {
        var pivot = playerPosition + GlobalBasis * new Vector3(0, enhancedPivotHeight, 0);
        var playerBack = GlobalBasis.Z;
        playerBack.Y = 0;
        if (playerBack.LengthSquared() < .001f) playerBack = Vector3.Back;
        playerBack = playerBack.Normalized();
        var direction = new Basis(Vector3.Up, yaw) * playerBack;
        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = new SphereShape3D { Radius = cameraProbeRadius },
            Transform = new Transform3D(Basis.Identity, pivot),
            Motion = direction * springLength,
            CollisionMask = WorldCollisionMask,
            CollideWithAreas = false,
            CollideWithBodies = true,
            Exclude = [GetRid()]
        };
        var motion = GetWorld3D().DirectSpaceState.CastMotion(query);
        var safeFraction = motion.Length > 0 ? Math.Clamp(motion[0], 0, 1) : 0;
        return Math.Max(0, springLength * safeFraction - cameraProbeRadius);
    }

    public void SetCameraClearanceCaptureOverride(bool enabled)
    {
        cameraClearanceCaptureOverride = enabled;
        if (enabled)
        {
            ApplyCameraClearancePresentation(false);
            return;
        }
        avatarFadedForCameraClearance = false;
    }

    private void ApplyCameraClearancePresentation(bool compressed)
    {
        var head = GetNode<SpringArm3D>("Head");
        head.Position = new Vector3(
            compressed ? compressedPivotLateral : 0,
            compressed ? compressedPivotHeight : enhancedPivotHeight,
            head.Position.Z);
        foreach (var mesh in avatarRoot.FindChildren("*", "MeshInstance3D", true, false)
                     .OfType<MeshInstance3D>())
        {
            if (!avatarBaseTransparency.TryGetValue(mesh, out var baseline))
            {
                baseline = mesh.Transparency;
                avatarBaseTransparency[mesh] = baseline;
                avatarBaseVisibility[mesh] = mesh.Visible;
            }
            if (!compressed)
            {
                mesh.Transparency = baseline;
                mesh.Visible = avatarBaseVisibility[mesh];
                continue;
            }
            var headSurface = false;
            if (mesh.Mesh is not null)
                for (var surface = 0; surface < mesh.Mesh.GetSurfaceCount(); surface++)
                {
                    var name = mesh.GetActiveMaterial(surface)?.ResourceName ?? string.Empty;
                    if (name.EndsWith("_Nikami.Aurora.GodotRuntimeHair", StringComparison.Ordinal) ||
                        name.EndsWith("_Nikami.Aurora.GodotRuntimeFace0", StringComparison.Ordinal) ||
                        name.EndsWith("_Nikami.Aurora.GodotRuntimeEyelash0", StringComparison.Ordinal))
                    {
                        headSurface = true;
                        break;
                    }
                }
            // Custom hair/face shaders do not consistently honor
            // GeometryInstance transparency. Suppress only those separate
            // head meshes when the camera is inside their bounds; retain the
            // partially faded clothing/body mesh as the third-person anchor.
            mesh.Visible = headSurface ? false : avatarBaseVisibility[mesh];
            mesh.Transparency = Math.Max(baseline,
                headSurface ? avatarHeadTransparency : avatarBodyTransparency);
        }
        avatarRoot.Visible = true;
    }

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
        var direction = movementWorldDirectionOverride is { } worldDirection
            ? new Vector3(worldDirection.X, 0, worldDirection.Z).Normalized()
            : (right * input.X + forward * -input.Y).Normalized();
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
