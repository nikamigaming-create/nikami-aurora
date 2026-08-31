using Godot;

namespace Nikami.Aurora.GodotRuntime.Presentation.Cinematics;

/// <summary>
/// Plays the authored pose/transition channel and evaluates every active DAO
/// AANI node as one weighted absolute-pose blend. DLB nodes are siblings under
/// a single root; their CUT curves are weights, not additive transform deltas.
/// </summary>
internal sealed class LayeredAnimationPlayback
{
    private readonly AnimationPlayer bodyPlayer;
    private readonly Skeleton3D skeleton;
    private readonly Dictionary<string, ActiveBlend> blends =
        new(StringComparer.OrdinalIgnoreCase);
    private string bodyResource = string.Empty;
    private double bodySpeed = 1;

    private LayeredAnimationPlayback(AnimationPlayer bodyPlayer, Skeleton3D skeleton)
    {
        this.bodyPlayer = bodyPlayer;
        this.skeleton = skeleton;
    }

    internal static LayeredAnimationPlayback? Create(Node actor)
    {
        var player = actor.FindChildren("*", "AnimationPlayer", true, false)
            .OfType<AnimationPlayer>().FirstOrDefault();
        var skeleton = actor.FindChildren("*", "Skeleton3D", true, false)
            .OfType<Skeleton3D>().FirstOrDefault();
        return player is null || skeleton is null ? null : new(player, skeleton);
    }

    internal static LayeredAnimationPlayback? Create(AnimationPlayer? player)
    {
        if (player?.GetParent() is not { } root) return null;
        var skeleton = root.FindChildren("*", "Skeleton3D", true, false)
            .OfType<Skeleton3D>().FirstOrDefault();
        return skeleton is null ? null : new(player, skeleton);
    }

    internal bool PlayBody(string resource, double offset = 0, double speed = 1,
        double startOffset = 0)
    {
        var clip = FindClip(bodyPlayer, resource);
        if (clip.Length == 0) return false;
        bodyResource = resource;
        bodySpeed = speed;
        bodyPlayer.SpeedScale = (float)Math.Max(0.0001, speed);
        bodyPlayer.Play(clip);
        // AnimationPlayer.Play() does not evaluate the new clip until its next
        // process tick. CUT actions can stop one body clip and start another at
        // the same authored timestamp, so leaving position zero unevaluated
        // exposes the skeleton's reset pose for one rendered frame.
        var position = Math.Max(0, startOffset + offset * speed);
        bodyPlayer.Seek(position, true);
        return true;
    }

    internal bool PlayOverlay(string resource, double offset = 0, double speed = 1,
        double startOffset = 0)
        => StartBlend(resource, resource, offset, speed, startOffset, false);

    internal bool PlayAction(string key, string resource, double offset = 0, double speed = 1,
        double startOffset = 0)
        => StartBlend(key, resource, offset, speed, startOffset, true);

    private bool StartBlend(string key, string resource, double offset, double speed,
        double startOffset, bool timelineDriven)
    {
        var clip = FindClip(bodyPlayer, resource);
        if (clip.Length == 0) return false;
        var animation = bodyPlayer.GetAnimation(clip);
        var ocular = IsOcularAnimation(resource);
        var maskedTracks = ocular ? CountNonOcularTracks(animation) : CountFacialTracks(animation);
        if (maskedTracks > 0)
            GD.Print($"OPENDAO_ANIMATION_LAYER_MASK resource={resource} " +
                     (ocular
                         ? $"channel=ocular excluded_nonocular_tracks={maskedTracks}"
                         : $"channel=body-gesture excluded_facial_tracks={maskedTracks}"));
        blends[key] = new ActiveBlend(resource, animation,
            startOffset + offset * speed, Math.Max(0.0001, speed), 1, timelineDriven);
        return true;
    }

    internal void SetOverlayWeight(string resource, double weight)
        => SetActionWeight(resource, weight);

    internal void SetActionWeight(string key, double weight)
    {
        if (blends.TryGetValue(key, out var blend))
            blend.Weight = Mathf.Clamp((float)weight, 0, 1);
    }

    internal void SetActionPosition(string key, double position)
    {
        if (blends.TryGetValue(key, out var blend)) blend.Position = position;
    }

    internal void AdvanceOverlays(double delta)
        => AdvanceActions(delta);

    internal void AdvanceActions(double delta)
    {
        foreach (var blend in blends.Values.Where(value => !value.TimelineDriven))
        {
            blend.Position += delta * blend.Speed;
        }
        ApplyAbsoluteBlend();
    }

    internal bool Stop(string resource)
    {
        var stoppedBody = false;
        if (resource.Equals(bodyResource, StringComparison.OrdinalIgnoreCase))
        {
            // CUT body actions are adjacent at identical timestamps. Preserve
            // the evaluated terminal pose until the replacement clip is sampled;
            // resetting here exposes the rig/rest pose for one rendered frame.
            bodyPlayer.Stop(true);
            bodyResource = string.Empty;
            stoppedBody = true;
        }
        blends.Remove(resource);
        return stoppedBody;
    }

    internal void StopAction(string key) => blends.Remove(key);

    internal LayeredAnimationState Snapshot() => new(bodyResource,
        bodyResource.Length > 0 ? bodyPlayer.CurrentAnimationPosition : 0, bodySpeed);

    internal IReadOnlyList<LayeredOverlayState> SnapshotOverlays() => blends.Select(value =>
        new LayeredOverlayState(value.Key, value.Value.Position, value.Value.Speed,
            value.Value.Weight)).ToArray();

    internal void RestoreOverlays(IEnumerable<LayeredOverlayState> states)
    {
        foreach (var state in states)
            if (PlayOverlay(state.Resource, speed: state.Speed, startOffset: state.Position))
                SetOverlayWeight(state.Resource, state.Weight);
    }

    internal bool Restore(LayeredAnimationState state) => state.Resource.Length > 0 &&
        PlayBody(state.Resource, speed: state.Speed, startOffset: state.Position);

    internal bool TransitionBody(string transitionResource, string targetResource, double speed,
        out double duration)
    {
        duration = 0;
        var transition = FindClip(bodyPlayer, transitionResource);
        var target = FindClip(bodyPlayer, targetResource);
        if (transition.Length == 0 || target.Length == 0) return false;
        duration = bodyPlayer.GetAnimation(transition).Length / Math.Max(0.0001, speed);
        bodyResource = targetResource;
        bodySpeed = speed;
        bodyPlayer.SpeedScale = (float)Math.Max(0.0001, speed);
        bodyPlayer.Play(transition);
        bodyPlayer.Seek(0, true);
        bodyPlayer.Queue(target);
        return true;
    }

    // Opening CUT playback still separates its authored body channel from the
    // partial PO/ocular nodes. Dialogue DLB playback does not use this heuristic.
    internal static bool IsOverlay(string resource) =>
        resource.Contains(".po_2arm_", StringComparison.OrdinalIgnoreCase) ||
        resource.Contains(".po_l_arm_", StringComparison.OrdinalIgnoreCase) ||
        resource.Contains(".po_r_arm_", StringComparison.OrdinalIgnoreCase) ||
        resource.Contains(".po_tw_", StringComparison.OrdinalIgnoreCase) ||
        resource.Contains(".po_body_", StringComparison.OrdinalIgnoreCase) ||
        resource.Contains("_o.", StringComparison.OrdinalIgnoreCase);

    private static string FindClip(AnimationPlayer player, string resource)
    {
        var normalized = Normalize(resource);
        return player.GetAnimationList().Select(value => value.ToString()).FirstOrDefault(value =>
            Normalize(value).Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
            Normalize(value).Equals(Normalize(resource + ".ani"),
                StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
    }

    private static string Normalize(string value) =>
        string.Concat(value.Select(character => char.IsLetterOrDigit(character)
            ? char.ToLowerInvariant(character) : '_'));

    private void ApplyAbsoluteBlend()
    {
        var bodyPose = SampleBodyPose();
        var boneBlends = new Dictionary<int, BoneBlend>();
        foreach (var blend in blends.Values.Where(value => value.Weight > 0.00001f))
        {
            var animation = blend.Animation;
            var time = animation.Length <= 0 ? 0 : blend.Position;
            if (animation.LoopMode != Animation.LoopModeEnum.None)
                time %= animation.Length;
            else
                time = Math.Min(time, animation.Length);

            for (var track = 0; track < animation.GetTrackCount(); track++)
            {
                var path = animation.TrackGetPath(track);
                if (path.GetSubNameCount() == 0) continue;
                var boneName = path.GetSubName(path.GetSubNameCount() - 1).ToString();
                if (IsOcularAnimation(blend.Resource)
                        ? !IsOcularDeformationBone(boneName)
                        : IsFacialDeformationBone(boneName))
                    continue;
                if (IsRootMotionBone(boneName)) continue;
                var bone = skeleton.FindBone(boneName);
                if (bone < 0) continue;
                if (!boneBlends.TryGetValue(bone, out var accumulator))
                {
                    var stableBase = bodyPose.GetValueOrDefault(bone,
                        new BonePose(Vector3.Zero, Quaternion.Identity, Vector3.One));
                    accumulator = new BoneBlend(stableBase.Position,
                        stableBase.Rotation.Normalized(), stableBase.Scale);
                    boneBlends[bone] = accumulator;
                }

                switch (animation.TrackGetType(track))
                {
                    case Animation.TrackType.Position3D:
                        accumulator.AddPosition(animation.PositionTrackInterpolate(track, time),
                            blend.Weight);
                        break;
                    case Animation.TrackType.Rotation3D:
                        accumulator.AddRotation(animation.RotationTrackInterpolate(track, time).Normalized(),
                            blend.Weight);
                        break;
                    case Animation.TrackType.Scale3D:
                        accumulator.AddScale(animation.ScaleTrackInterpolate(track, time), blend.Weight);
                        break;
                }
            }
        }

        foreach (var (bone, blend) in boneBlends)
        {
            if (blend.PositionWeight > 0)
                skeleton.SetBonePosePosition(bone, blend.ResolvePosition());
            if (blend.RotationWeight > 0)
                skeleton.SetBonePoseRotation(bone, blend.ResolveRotation());
            if (blend.ScaleWeight > 0)
                skeleton.SetBonePoseScale(bone, blend.ResolveScale());
        }
    }

    private Dictionary<int, BonePose> SampleBodyPose()
    {
        var clip = bodyPlayer.CurrentAnimation.ToString();
        var isPlaying = clip.Length > 0 && bodyPlayer.HasAnimation(clip);
        if (!isPlaying) clip = FindClip(bodyPlayer, bodyResource);
        if (clip.Length == 0) return [];
        var animation = bodyPlayer.GetAnimation(clip);
        var time = isPlaying ? bodyPlayer.CurrentAnimationPosition : animation.Length;
        if (animation.LoopMode != Animation.LoopModeEnum.None && animation.Length > 0)
            time %= animation.Length;
        else
            time = Math.Min(time, animation.Length);

        var poses = new Dictionary<int, BonePose>();
        for (var track = 0; track < animation.GetTrackCount(); track++)
        {
            var path = animation.TrackGetPath(track);
            if (path.GetSubNameCount() == 0) continue;
            var boneName = path.GetSubName(path.GetSubNameCount() - 1).ToString();
            var bone = skeleton.FindBone(boneName);
            if (bone < 0) continue;
            var pose = poses.GetValueOrDefault(bone,
                new BonePose(Vector3.Zero, Quaternion.Identity, Vector3.One));
            poses[bone] = animation.TrackGetType(track) switch
            {
                Animation.TrackType.Position3D => pose with
                {
                    Position = animation.PositionTrackInterpolate(track, time)
                },
                Animation.TrackType.Rotation3D => pose with
                {
                    Rotation = animation.RotationTrackInterpolate(track, time).Normalized()
                },
                Animation.TrackType.Scale3D => pose with
                {
                    Scale = animation.ScaleTrackInterpolate(track, time)
                },
                _ => pose
            };
        }
        return poses;
    }

    private static int CountFacialTracks(Animation animation)
    {
        var count = 0;
        for (var track = 0; track < animation.GetTrackCount(); track++)
        {
            var path = animation.TrackGetPath(track);
            if (path.GetSubNameCount() == 0) continue;
            var boneName = path.GetSubName(path.GetSubNameCount() - 1).ToString();
            if (IsFacialDeformationBone(boneName)) count++;
        }
        return count;
    }

    private static int CountNonOcularTracks(Animation animation)
    {
        var count = 0;
        for (var track = 0; track < animation.GetTrackCount(); track++)
        {
            var path = animation.TrackGetPath(track);
            if (path.GetSubNameCount() == 0) continue;
            var boneName = path.GetSubName(path.GetSubNameCount() - 1).ToString();
            if (!IsOcularDeformationBone(boneName)) count++;
        }
        return count;
    }

    private static bool IsOcularAnimation(string resource) =>
        resource.Contains("_o.", StringComparison.OrdinalIgnoreCase);

    private static bool IsOcularDeformationBone(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("eye") || value.Contains("brow");
    }

    private static bool IsFacialDeformationBone(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("eye") || value.Contains("brow") || value.Contains("cheek") ||
               value.Contains("lip") || value.Contains("jaw") || value.Contains("mouth") ||
               value.Contains("sneer") || value.Contains("tongue") ||
               value.Equals("headbase", StringComparison.Ordinal);
    }

    private static bool IsRootMotionBone(string name) =>
        name.Equals("Root", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("God", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Gob", StringComparison.OrdinalIgnoreCase);

    private sealed class ActiveBlend(string resource, Animation animation, double position,
        double speed, float weight, bool timelineDriven)
    {
        internal string Resource { get; } = resource;
        internal Animation Animation { get; } = animation;
        internal double Position { get; set; } = position;
        internal double Speed { get; } = speed;
        internal float Weight { get; set; } = weight;
        internal bool TimelineDriven { get; } = timelineDriven;
    }

    private readonly record struct BonePose(Vector3 Position, Quaternion Rotation, Vector3 Scale);

    private sealed class BoneBlend(Vector3 basePosition, Quaternion baseRotation, Vector3 baseScale)
    {
        private Vector3 weightedPosition;
        private Vector3 weightedScale;
        private float rotationX;
        private float rotationY;
        private float rotationZ;
        private float rotationW;

        internal float PositionWeight { get; private set; }
        internal float RotationWeight { get; private set; }
        internal float ScaleWeight { get; private set; }

        internal void AddPosition(Vector3 value, float weight)
        {
            weightedPosition += value * weight;
            PositionWeight += weight;
        }

        internal void AddScale(Vector3 value, float weight)
        {
            weightedScale += value * weight;
            ScaleWeight += weight;
        }

        internal void AddRotation(Quaternion value, float weight)
        {
            if (baseRotation.Dot(value) < 0) value = new Quaternion(-value.X, -value.Y, -value.Z, -value.W);
            rotationX += value.X * weight;
            rotationY += value.Y * weight;
            rotationZ += value.Z * weight;
            rotationW += value.W * weight;
            RotationWeight += weight;
        }

        internal Vector3 ResolvePosition()
        {
            var baseWeight = Math.Max(0, 1 - PositionWeight);
            return (basePosition * baseWeight + weightedPosition) /
                   Math.Max(0.00001f, baseWeight + PositionWeight);
        }

        internal Vector3 ResolveScale()
        {
            var baseWeight = Math.Max(0, 1 - ScaleWeight);
            return (baseScale * baseWeight + weightedScale) /
                   Math.Max(0.00001f, baseWeight + ScaleWeight);
        }

        internal Quaternion ResolveRotation()
        {
            var baseWeight = Math.Max(0, 1 - RotationWeight);
            var result = new Quaternion(baseRotation.X * baseWeight + rotationX,
                baseRotation.Y * baseWeight + rotationY,
                baseRotation.Z * baseWeight + rotationZ,
                baseRotation.W * baseWeight + rotationW);
            return result.LengthSquared() > 0.000001f ? result.Normalized() : baseRotation;
        }
    }
}

internal readonly record struct LayeredAnimationState(string Resource, double Position, double Speed);
internal readonly record struct LayeredOverlayState(string Resource, double Position, double Speed,
    float Weight);
