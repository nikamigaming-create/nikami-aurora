using Nikami.Aurora.Core;

namespace Nikami.Aurora.Profiles.Kotor;

public sealed class KotorGameProfile : IGameProfile
{
    public const string ProfileId = "kotor";

    public GameProfileDescriptor Descriptor { get; } = new(
        ProfileId,
        "Star Wars: Knights of the Old Republic",
        "Odyssey",
        "swkotor.exe",
        new InstallationMarker[]
        {
            InstallationMarker.File("swkotor.exe"),
            InstallationMarker.File("chitin.key"),
            InstallationMarker.File("dialog.tlk"),
            InstallationMarker.Directory("data"),
            InstallationMarker.Directory("modules"),
            InstallationMarker.File("modules/end_m01aa.rim"),
            InstallationMarker.File("modules/end_m01aa_s.rim")
        });
}
