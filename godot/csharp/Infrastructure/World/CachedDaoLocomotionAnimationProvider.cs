using Godot;
using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.Characters;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class CachedDaoLocomotionAnimationProvider : ILocomotionAnimationProvider
{
    public LocomotionAnimationSet? Resolve(CharacterProfile character)
    {
        var female = character.Gender.Equals("female", StringComparison.OrdinalIgnoreCase);
        var model = female
            ? "den300cr_crowd_elf_fem_3.glb"
            : "den300cr_crowd_elf_male_3.glb";
        var path = DaoRuntimePaths.Cache("playable-characters", "lak100d",
            "actors", model);
        if (!File.Exists(path))
        {
            GD.PushWarning("OPENDAO_LOCOMOTION_BANK status=missing path=" + path);
            return null;
        }

        var result = female
            ? new LocomotionAnimationSet(path, "cs_female.stand_idle1.ani",
                "fh_m.mov_wf.ani", "fh_m.mov_rf_fem.ani")
            : new LocomotionAnimationSet(path, "cs_male.stand_idle1.ani",
                "mh_m.mov_wf.ani", "mh_m.mov_rf.ani");
        GD.Print($"OPENDAO_LOCOMOTION_BANK status=ready gender={character.Gender} " +
                 $"idle={result.Idle} walk={result.Walk} run={result.Run} source=installed-anims-erf");
        return result;
    }
}
