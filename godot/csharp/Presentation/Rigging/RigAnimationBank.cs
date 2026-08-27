using Godot;

namespace OpenDAO.Presentation.Rigging;

internal static class RigAnimationBank
{
    internal static bool Merge(Node3D targetActor, Node3D bankActor, out string failure)
    {
        failure = string.Empty;
        var targetPlayer = FindAnimationPlayer(targetActor);
        var bankPlayer = FindAnimationPlayer(bankActor);
        if (targetPlayer is null || bankPlayer is null)
        {
            failure = "animation-player-missing";
            return false;
        }
        var targetSkeleton = FindSkeleton(targetActor);
        var bankSkeleton = FindSkeleton(bankActor);
        if (targetSkeleton is null || bankSkeleton is null)
        {
            failure = "skeleton-missing";
            return false;
        }
        if (!IsCompatible(targetSkeleton, bankSkeleton, bankPlayer, out failure))
            return false;

        var libraryName = new StringName(string.Empty);
        var library = targetPlayer.HasAnimationLibrary(libraryName)
            ? targetPlayer.GetAnimationLibrary(libraryName)
            : new AnimationLibrary();
        if (!targetPlayer.HasAnimationLibrary(libraryName) &&
            targetPlayer.AddAnimationLibrary(libraryName, library) != Error.Ok)
        {
            failure = "animation-library-add-failed";
            return false;
        }

        var added = 0;
        foreach (var qualifiedName in bankPlayer.GetAnimationList())
        {
            var text = qualifiedName.ToString();
            var separator = text.LastIndexOf('/');
            var localName = new StringName(separator >= 0 ? text[(separator + 1)..] : text);
            if (library.HasAnimation(localName)) continue;
            if (library.AddAnimation(localName, bankPlayer.GetAnimation(qualifiedName)) != Error.Ok)
            {
                failure = "animation-add-failed:" + localName;
                return false;
            }
            added++;
        }
        GD.Print($"OPENDAO_ANIMATION_BANK status=ready added={added} " +
                 $"total={targetPlayer.GetAnimationList().Length}");
        return true;
    }

    internal static Skeleton3D? FindSkeleton(Node root) =>
        root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();

    internal static AnimationPlayer? FindAnimationPlayer(Node root) =>
        root.FindChildren("*", "AnimationPlayer", true, false)
            .OfType<AnimationPlayer>().FirstOrDefault();

    private static bool IsCompatible(Skeleton3D target, Skeleton3D bank,
        AnimationPlayer bankPlayer, out string failure)
    {
        failure = string.Empty;
        var targetBones = Enumerable.Range(0, target.GetBoneCount())
            .Select(target.GetBoneName).ToHashSet();
        var requiredBones = new HashSet<StringName>();
        foreach (var animationName in bankPlayer.GetAnimationList())
        {
            var animation = bankPlayer.GetAnimation(animationName);
            for (var track = 0; track < animation.GetTrackCount(); track++)
            {
                var path = animation.TrackGetPath(track);
                if (path.GetSubNameCount() == 0) continue;
                var candidate = path.GetSubName(path.GetSubNameCount() - 1);
                if (bank.FindBone(candidate) >= 0) requiredBones.Add(candidate);
            }
        }
        var missing = requiredBones.Where(bone => !targetBones.Contains(bone))
            .Select(value => value.ToString()).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            failure = $"animation-bones-missing:{missing.Length}:{string.Join(',', missing.Take(8))}";
            return false;
        }
        GD.Print($"OPENDAO_CINEMATIC_RIG_COMPATIBILITY status=ready " +
                 $"required_animation_bones={requiredBones.Count} target_bones={target.GetBoneCount()} " +
                 $"bank_bones={bank.GetBoneCount()} order_independent=true");
        return true;
    }
}
