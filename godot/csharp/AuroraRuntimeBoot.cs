using Godot;
using Nikami.Aurora.Profiles.DragonAgeOrigins;
using Nikami.Aurora.Profiles.Kotor;

namespace Nikami.Aurora.GodotRuntime;

public sealed partial class AuroraRuntimeBoot : Node
{
    public const string ProfileEnvironmentVariable = "NIKAMI_AURORA_PROFILE";

    public override void _Ready()
    {
        var requested = System.Environment.GetEnvironmentVariable(
            ProfileEnvironmentVariable)?.Trim();
        var profile = string.IsNullOrWhiteSpace(requested)
            ? KotorGameProfile.ProfileId
            : requested;
        var scenePath = profile switch
        {
            KotorGameProfile.ProfileId => "res://kotor_main.tscn",
            DragonAgeOriginsGameProfile.ProfileId => "res://dao_boot.tscn",
            _ => throw new InvalidDataException(
                $"Unsupported Nikami Aurora profile: {profile}")
        };
        var scene = ResourceLoader.Load<PackedScene>(scenePath) ??
                    throw new InvalidDataException(
                        $"Nikami Aurora profile scene is missing: {scenePath}");
        AddChild(scene.Instantiate());
        GD.Print($"NIKAMI_AURORA_RUNTIME status=ready profile={profile} scene={scenePath}");
    }
}
