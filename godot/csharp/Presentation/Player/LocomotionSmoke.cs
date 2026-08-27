using Godot;

namespace OpenDAO.Presentation.Player;

internal static class LocomotionSmoke
{
    private const int FramesPerDirection = 90;
    private const float MinimumProgress = 2.0f;

    internal static async Task<bool> RunAsync(PlayerController player, CancellationToken cancellationToken)
    {
        var tree = player.GetTree();
        var origin = player.GlobalTransform;
        var recoveryCount = player.GroundRecoveryCount;
        var bestDistance = 0.0f;
        var stepCount = player.StepUpCount;
        var authoredWalkObserved = false;
        var locomotionCapturePassed = true;
        var locomotionCaptured = false;
        (string Name, Vector2 Input)[] directions =
        [
            ("forward", Vector2.Up),
            ("right", Vector2.Right),
            ("back", Vector2.Down),
            ("left", Vector2.Left)
        ];

        try
        {
            foreach (var direction in directions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                player.GlobalTransform = origin;
                player.Velocity = Vector3.Zero;
                player.SnapToWalkableGround(origin.Origin, "locomotion-smoke", false);
                var start = player.GlobalPosition;
                var directionRecoveryCount = player.GroundRecoveryCount;
                var directionStepCount = player.StepUpCount;
                player.SetMovementInputOverride(direction.Input);
                for (var frame = 0; frame < FramesPerDirection; frame++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await player.ToSignal(tree, SceneTree.SignalName.PhysicsFrame);
                    authoredWalkObserved |= player.CurrentLocomotionState ==
                                            PlayerController.LocomotionState.Walk &&
                                            player.IsAvatarAnimationPlaying;
                    if (!locomotionCaptured && direction.Name == "forward" && frame == 45)
                    {
                        var capturePath = OS.GetEnvironment("OPENDAO_LOCOMOTION_CAPTURE");
                        if (capturePath.Length > 0)
                        {
                            locomotionCaptured = true;
                            var error = player.GetViewport().GetTexture().GetImage().SavePng(capturePath);
                            locomotionCapturePassed = error == Error.Ok;
                            GD.Print($"OPENDAO_LOCOMOTION_CAPTURE status=" +
                                     $"{(locomotionCapturePassed ? "pass" : "fail")} path={capturePath}");
                        }
                    }
                }

                player.SetMovementInputOverride(Vector2.Zero);
                var offset = player.GlobalPosition - start;
                offset.Y = 0;
                var distance = offset.Length();
                bestDistance = Math.Max(bestDistance, distance);
                GD.Print($"OPENDAO_LOCOMOTION_DIRECTION direction={direction.Name} " +
                         $"distance={distance:F3} recoveries={player.GroundRecoveryCount - directionRecoveryCount} " +
                         $"steps={player.StepUpCount - directionStepCount} start={start} end={player.GlobalPosition}");
            }
        }
        finally
        {
            player.SetMovementInputOverride(null);
            player.GlobalTransform = origin;
            player.ResetLocomotionState();
            player.SnapToWalkableGround(origin.Origin, "locomotion-smoke-restore", false);
        }

        var recoveries = player.GroundRecoveryCount - recoveryCount;
        var steps = player.StepUpCount - stepCount;
        var authoredBlocker = ValidateAuthoredBlocker(player);
        var animationPassed = player.HasPlayableWalkAnimation && authoredWalkObserved;
        var passed = bestDistance >= MinimumProgress && recoveries == 0 && authoredBlocker &&
                     animationPassed && locomotionCapturePassed;
        GD.Print($"OPENDAO_LOCOMOTION_TEST status={(passed ? "pass" : "fail")} " +
                 $"best_distance={bestDistance:F3} recoveries={recoveries} steps={steps} " +
                 $"authored_blocker={(authoredBlocker ? "pass" : "fail")} " +
                 $"authored_walk={(animationPassed ? "pass" : "fail")} " +
                 $"capture={(locomotionCapturePassed ? "pass" : "fail")}");
        return passed;
    }

    private static bool ValidateAuthoredBlocker(PlayerController player)
    {
        var blocker = player.GetTree().CurrentScene
            .FindChildren("AuthoredBlocker_*", "StaticBody3D", true, false)
            .OfType<StaticBody3D>()
            .FirstOrDefault(body => body.GetMeta("dao_template", string.Empty).AsString()
                .Contains("invisible_wide", StringComparison.OrdinalIgnoreCase));
        if (blocker is null)
            return player.GetTree().CurrentScene
                .FindChildren("*", "CollisionShape3D", true, false)
                .OfType<CollisionShape3D>()
                .Any(shape => shape.Shape is not null && shape.GetParent() is StaticBody3D);
        var start = blocker.GlobalTransform * new Vector3(0, 1.5f, -1.5f);
        var end = blocker.GlobalTransform * new Vector3(0, 1.5f, 1.5f);
        var query = PhysicsRayQueryParameters3D.Create(start, end, 2, [player.GetRid()]);
        var hit = player.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return hit.Count > 0 && hit["collider"].AsGodotObject() == blocker;
    }
}
