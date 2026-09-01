using Nikami.Aurora.Core;

namespace Nikami.Aurora.Profiles.Kotor2;

public sealed class Kotor2GameProfile : IGameProfile
{
    public const string ProfileId = "kotor2";

    public GameProfileDescriptor Descriptor { get; } = new(
        ProfileId,
        "Star Wars: Knights of the Old Republic II: The Sith Lords",
        "Odyssey",
        "swkotor2.exe",
        new InstallationMarker[]
        {
            InstallationMarker.File("swkotor2.exe"),
            InstallationMarker.File("chitin.key"),
            InstallationMarker.File("dialog.tlk"),
            InstallationMarker.Directory("data"),
            InstallationMarker.Directory("Modules"),
            InstallationMarker.File("Modules/001EBO.rim"),
            InstallationMarker.File("Modules/001EBO_s.rim"),
            InstallationMarker.File("Modules/001EBO_dlg.erf"),
            InstallationMarker.Directory("lips"),
            InstallationMarker.Directory("StreamVoice"),
            InstallationMarker.Directory("TexturePacks")
        });
}
