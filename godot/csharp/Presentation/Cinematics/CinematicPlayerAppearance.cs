using Godot;
using Nikami.Aurora.GodotRuntime.Presentation.Rigging;

namespace Nikami.Aurora.GodotRuntime.Presentation.Cinematics;

/// <summary>
/// Keeps the selected player's mesh and bind skeleton together, then merges
/// the authored CUT/DLG animation bank into that actor. Bone-name equality is
/// not sufficient for skinned meshes because facial bind transforms can vary.
/// </summary>
internal static class CinematicPlayerAppearance
{
    private const string AppearanceStateMeta = "opendao_appearance_state";
    private const string BedAppearanceMeta = "opendao_bed_appearance";
    private const string StandingAppearanceMeta = "opendao_standing_appearance";

    internal static bool AdoptSelectedActor(Node3D selectedActor, Node3D authoredAnimationBank,
        bool bedActor, out string failure)
    {
        failure = string.Empty;
        if (!MergeAnimationBank(selectedActor, authoredAnimationBank, out failure)) return false;
        var selectedSkeleton = FindSkeleton(selectedActor);
        if (selectedSkeleton is null)
        {
            failure = "selected-skeleton-missing";
            return false;
        }

        var selectedMeshes = selectedActor.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>().ToArray();
        if (selectedMeshes.Length == 0)
        {
            failure = "appearance-meshes-missing";
            return false;
        }
        if (!HasBodyGeometry(selectedMeshes))
        {
            failure = "appearance-body-mesh-missing";
            return false;
        }

        foreach (var mesh in selectedMeshes)
        {
            mesh.SetMeta(BedAppearanceMeta, true);
            mesh.SetMeta(StandingAppearanceMeta, true);
        }
        if (!(bedActor ? UseBedAppearance(selectedActor, out failure) :
                UseStandingAppearance(selectedActor, out failure))) return false;
        GD.Print($"OPENDAO_CINEMATIC_PLAYER_APPEARANCE status=ready " +
                 $"selected_meshes={selectedMeshes.Length} " +
                 $"clothing=selected-character-city-elf-start " +
                 $"bones={selectedSkeleton.GetBoneCount()} rig=selected-character");
        return true;
    }

    internal static bool UseBedAppearance(Node3D actor, out string failure) =>
        UseAppearance(actor, "bed", BedAppearanceMeta, null, out failure);

    internal static bool UseStandingAppearance(Node3D actor, out string failure) =>
        UseAppearance(actor, "standing", StandingAppearanceMeta, null, out failure);

    private static bool UseAppearance(Node3D actor, string state, string primaryMeta,
        string? secondaryMeta, out string failure)
    {
        failure = string.Empty;
        var meshes = actor.FindChildren("*", "MeshInstance3D", true, false)
            .OfType<MeshInstance3D>().ToArray();
        var selected = meshes.Where(mesh => mesh.HasMeta(primaryMeta) ||
            secondaryMeta is not null && mesh.HasMeta(secondaryMeta)).ToArray();
        if (selected.Length == 0)
        {
            failure = state + "-meshes-missing";
            return false;
        }

        foreach (var mesh in meshes)
            mesh.Visible = mesh.HasMeta(primaryMeta) ||
                           secondaryMeta is not null && mesh.HasMeta(secondaryMeta);
        var visible = meshes.Count(mesh => mesh.Visible);
        if (visible != selected.Length)
        {
            failure = $"{state}-visibility:{visible}!={selected.Length}";
            return false;
        }

        var previous = actor.HasMeta(AppearanceStateMeta)
            ? actor.GetMeta(AppearanceStateMeta).AsString()
            : string.Empty;
        actor.SetMeta(AppearanceStateMeta, state);
        if (!previous.Equals(state, StringComparison.Ordinal))
            GD.Print($"OPENDAO_CINEMATIC_PLAYER_OUTFIT state={state} meshes={selected.Length}");
        return true;
    }

    internal static bool MergeAnimationBank(Node3D targetActor, Node3D bankActor,
        out string failure)
    {
        var merged = RigAnimationBank.Merge(targetActor, bankActor, out failure);
        if (merged)
            GD.Print("OPENDAO_CINEMATIC_ANIMATION_BANK status=ready");
        return merged;
    }

    internal static bool CopyPose(Node3D sourceActor, Node3D targetActor, out string failure)
    {
        failure = string.Empty;
        var source = FindSkeleton(sourceActor);
        var target = FindSkeleton(targetActor);
        if (source is null || target is null)
        {
            failure = "skeleton-missing";
            return false;
        }

        var copied = 0;
        for (var targetBone = 0; targetBone < target.GetBoneCount(); targetBone++)
        {
            var sourceBone = source.FindBone(target.GetBoneName(targetBone));
            if (sourceBone < 0) continue;
            target.SetBonePosePosition(targetBone, source.GetBonePosePosition(sourceBone));
            target.SetBonePoseRotation(targetBone, source.GetBonePoseRotation(sourceBone));
            target.SetBonePoseScale(targetBone, source.GetBonePoseScale(sourceBone));
            copied++;
        }
        if (copied != target.GetBoneCount())
        {
            failure = $"bone-coverage:{copied}/{target.GetBoneCount()}";
            return false;
        }
        GD.Print($"OPENDAO_CINEMATIC_POSE_COPY status=ready bones={copied}");
        return true;
    }

    private static Skeleton3D? FindSkeleton(Node root) =>
        root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();

    private static bool HasBodyGeometry(IEnumerable<MeshInstance3D> meshes) =>
        meshes.Any(mesh =>
        {
            var name = mesh.Name.ToString().ToLowerInvariant();
            return name.Contains("cth_") || name.Contains("arm_") ||
                   name.Contains("rob_") || name.Contains("torso") ||
                   name.Contains("body");
        });

}
