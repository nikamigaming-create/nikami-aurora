using Nikami.Aurora.Core;

namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed class DragonAgeOriginsGameProfile : IGameProfile
{
    public const string ProfileId = "dragon-age-origins";

    public GameProfileDescriptor Descriptor { get; } = new(
        ProfileId,
        "Dragon Age: Origins",
        "Eclipse",
        "bin_ship/daorigins.exe",
        new InstallationMarker[]
        {
            InstallationMarker.File("bin_ship/daorigins.exe"),
            InstallationMarker.Directory("packages/core/data"),
            InstallationMarker.Directory("modules/Single Player")
        });
}
