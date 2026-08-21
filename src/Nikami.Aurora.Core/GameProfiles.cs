using System.Security.Cryptography;

namespace Nikami.Aurora.Core;

public enum InstallationMarkerKind
{
    File,
    Directory
}

public sealed record InstallationMarker
{
    public InstallationMarker(string relativePath, InstallationMarkerKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Installation markers must be relative paths.", nameof(relativePath));

        var canonical = relativePath.Replace('\\', '/').Trim('/');
        var segments = canonical.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new ArgumentException("Installation markers cannot traverse outside the game root.",
                nameof(relativePath));

        RelativePath = string.Join('/', segments);
        Kind = kind;
    }

    public string RelativePath { get; }
    public InstallationMarkerKind Kind { get; }

    public static InstallationMarker File(string path) => new(path, InstallationMarkerKind.File);
    public static InstallationMarker Directory(string path) => new(path, InstallationMarkerKind.Directory);
}

public sealed record GameProfileDescriptor
{
    public GameProfileDescriptor(string id, string displayName, string engineFamily,
        string executableRelativePath, IReadOnlyList<InstallationMarker> markers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineFamily);
        ArgumentNullException.ThrowIfNull(markers);
        if (id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '-')))
            throw new ArgumentException("Profile IDs may contain only ASCII letters, digits, and hyphens.",
                nameof(id));
        if (markers.Count == 0)
            throw new ArgumentException("A profile requires at least one installation marker.", nameof(markers));

        var executableMarker = InstallationMarker.File(executableRelativePath);
        if (!markers.Any(marker => marker.Kind == InstallationMarkerKind.File &&
                                  string.Equals(marker.RelativePath, executableMarker.RelativePath,
                                      StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("The executable must also be a required file marker.", nameof(markers));

        Id = id.ToLowerInvariant();
        DisplayName = displayName;
        EngineFamily = engineFamily;
        ExecutableRelativePath = executableMarker.RelativePath;
        Markers = markers.ToArray();
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string EngineFamily { get; }
    public string ExecutableRelativePath { get; }
    public IReadOnlyList<InstallationMarker> Markers { get; }
}

public interface IGameProfile
{
    GameProfileDescriptor Descriptor { get; }
}

public sealed record InstallationMarkerProbe(
    string RelativePath,
    InstallationMarkerKind Kind,
    string ResolvedPath,
    bool Present);

public sealed record GameInstallationProbe(
    int SchemaVersion,
    string ProfileId,
    string DisplayName,
    string EngineFamily,
    string RootPath,
    bool IsValid,
    string ExecutablePath,
    string? ExecutableSha256,
    IReadOnlyList<InstallationMarkerProbe> Markers);

public static class GameInstallProber
{
    public const int CurrentSchemaVersion = 1;

    public static GameInstallationProbe Probe(IGameProfile profile, string rootPath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var root = Path.GetFullPath(rootPath);
        var markerResults = profile.Descriptor.Markers
            .Select(marker => ProbeMarker(root, marker))
            .ToArray();
        var executablePath = Resolve(root, profile.Descriptor.ExecutableRelativePath);
        string? executableHash = null;
        if (File.Exists(executablePath))
        {
            using var executable = File.OpenRead(executablePath);
            executableHash = Convert.ToHexString(SHA256.HashData(executable));
        }

        return new GameInstallationProbe(
            CurrentSchemaVersion,
            profile.Descriptor.Id,
            profile.Descriptor.DisplayName,
            profile.Descriptor.EngineFamily,
            root,
            markerResults.All(marker => marker.Present),
            executablePath,
            executableHash,
            markerResults);
    }

    private static InstallationMarkerProbe ProbeMarker(string root, InstallationMarker marker)
    {
        var resolved = Resolve(root, marker.RelativePath);
        var present = marker.Kind switch
        {
            InstallationMarkerKind.File => File.Exists(resolved),
            InstallationMarkerKind.Directory => Directory.Exists(resolved),
            _ => false
        };
        return new InstallationMarkerProbe(marker.RelativePath, marker.Kind, resolved, present);
    }

    private static string Resolve(string root, string canonicalRelativePath) =>
        Path.GetFullPath(Path.Combine(root,
            canonicalRelativePath.Replace('/', Path.DirectorySeparatorChar)));
}

public sealed class GameProfileRegistry
{
    private readonly IReadOnlyDictionary<string, IGameProfile> profiles;

    public GameProfileRegistry(IEnumerable<IGameProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var map = new Dictionary<string, IGameProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            if (!map.TryAdd(profile.Descriptor.Id, profile))
                throw new InvalidOperationException($"Duplicate game profile ID: {profile.Descriptor.Id}");
        }

        this.profiles = map;
    }

    public IReadOnlyList<IGameProfile> All => profiles.Values
        .OrderBy(profile => profile.Descriptor.Id, StringComparer.Ordinal)
        .ToArray();

    public IGameProfile Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return profiles.TryGetValue(id, out var profile)
            ? profile
            : throw new KeyNotFoundException($"Unknown game profile: {id}");
    }
}
