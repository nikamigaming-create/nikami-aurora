using Godot;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class KotorLipModifier : SkeletonModifier3D
{
    private Animation? animation;
    private IReadOnlyList<TrackBinding> tracks = [];
    private int leftShape;
    private int rightShape;
    private float factor;
    private bool announced;

    public void Configure(Animation source, IReadOnlyList<TrackBinding> bindings)
    {
        animation = source;
        tracks = bindings;
        foreach (var track in tracks)
        {
            if (track.PositionTrack >= 0)
                track.SourceNeutralPosition =
                    animation.PositionTrackInterpolate(track.PositionTrack, 0.0);
            if (track.RotationTrack >= 0)
                track.SourceNeutralRotation = NormalizeOrIdentity(
                    animation.RotationTrackInterpolate(track.RotationTrack, 0.0));
        }
        Influence = 1.0f;
        Active = false;
    }

    public void SetSample(int left, int right, float interpolation)
    {
        leftShape = Math.Clamp(left, 0, 15);
        rightShape = Math.Clamp(right, 0, 15);
        factor = Math.Clamp(interpolation, 0.0f, 1.0f);
        Active = true;
    }

    public void SetNeutral()
    {
        leftShape = 0;
        rightShape = 0;
        factor = 0.0f;
        Active = true;
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        var skeleton = GetSkeleton();
        if (!Active || skeleton is null || animation is null) return;
        if (!announced)
        {
            announced = true;
            GD.Print($"NIKAMI_AURORA_LIP_RIG status=applying bones={tracks.Count}");
        }
        const float shapeCount = 16.0f;
        var leftTime = leftShape / shapeCount * animation.GetLength();
        var rightTime = rightShape / shapeCount * animation.GetLength();
        foreach (var track in tracks)
        {
            var rest = skeleton.GetBoneRest(track.BoneIndex);
            var position = rest.Origin;
            if (track.PositionTrack >= 0)
            {
                var left = animation.PositionTrackInterpolate(track.PositionTrack, leftTime);
                var right = animation.PositionTrackInterpolate(track.PositionTrack, rightTime);
                position += left.Lerp(right, factor) - track.SourceNeutralPosition;
            }
            var rotation = rest.Basis.GetRotationQuaternion();
            if (track.RotationTrack >= 0)
            {
                var left = NormalizeOrIdentity(
                    animation.RotationTrackInterpolate(track.RotationTrack, leftTime));
                var right = NormalizeOrIdentity(
                    animation.RotationTrackInterpolate(track.RotationTrack, rightTime));
                var sample = left.Slerp(right, factor).Normalized();
                var deltaRotation = track.SourceNeutralRotation.Inverse() * sample;
                rotation = (rotation * deltaRotation).Normalized();
            }
            var localPose = new Transform3D(new Basis(rotation), position);
            var parentIndex = skeleton.GetBoneParent(track.BoneIndex);
            var globalPose = parentIndex >= 0
                ? skeleton.GetBoneGlobalPose(parentIndex) * localPose
                : localPose;
            skeleton.SetBoneGlobalPose(track.BoneIndex, globalPose);
        }
    }

    private static Quaternion NormalizeOrIdentity(Quaternion value)
    {
        var lengthSquared = value.X * value.X + value.Y * value.Y +
                            value.Z * value.Z + value.W * value.W;
        return lengthSquared > 0.00000001f ? value.Normalized() : Quaternion.Identity;
    }

    public sealed class TrackBinding(int boneIndex)
    {
        public int BoneIndex { get; } = boneIndex;
        public int PositionTrack { get; set; } = -1;
        public int RotationTrack { get; set; } = -1;
        public Vector3 SourceNeutralPosition { get; set; }
        public Quaternion SourceNeutralRotation { get; set; } = Quaternion.Identity;
    }
}
