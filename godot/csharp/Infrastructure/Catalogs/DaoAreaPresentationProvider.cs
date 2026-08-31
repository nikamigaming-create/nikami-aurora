using Nikami.Aurora.GodotRuntime.Application.Abstractions;
using Nikami.Aurora.GodotRuntime.Domain.World;
using Nikami.Aurora.GodotRuntime.Infrastructure.Archives;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;

public sealed class DaoAreaPresentationProvider(IJsonStore store) : IAreaPresentationProvider
{
    private readonly Dictionary<string, AreaPresentationResult> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public AreaPresentationResult Resolve(WorldProfile profile)
    {
        var cacheKey = profile.SourceKey + "\n" + profile.TalktableFile;
        if (cache.TryGetValue(cacheKey, out var cached)) return cached;

        var split = profile.SourceKey.Split("::", 2, StringSplitOptions.None);
        if (split.Length != 2 || profile.GameRoot.Length == 0)
            return Cache(cacheKey, AreaPresentationResult.Failed("area-presentation-source-absent"));

        var archivePath = Path.Combine(profile.GameRoot,
            split[0].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(archivePath))
            return Cache(cacheKey, AreaPresentationResult.Failed("area-presentation-archive-absent"));

        try
        {
            var archive = ErfArchive.Open(archivePath);
            if (!archive.Contains(split[1]))
                return Cache(cacheKey,
                    AreaPresentationResult.Failed("area-presentation-entry-absent"));

            var root = new ClassicGff32RootReader(archive.Read(split[1]));
            if (!root.TryReadLocStringReference("Name", out var stringReference))
                return Cache(cacheKey,
                    AreaPresentationResult.Failed("area-presentation-strref-absent"));
            if (!root.TryReadInt32("North", out var north))
                return Cache(cacheKey,
                    AreaPresentationResult.Failed("area-presentation-north-absent"));

            var talktable = store.Read(profile.TalktableFile);
            var displayName = talktable?["strings"]?[stringReference.ToString()]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(displayName))
                return Cache(cacheKey,
                    AreaPresentationResult.Failed("area-presentation-talktable-entry-absent"));

            return Cache(cacheKey, AreaPresentationResult.Complete(displayName, north));
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or
                                           ArgumentException or OverflowException)
        {
            return Cache(cacheKey, AreaPresentationResult.Failed(
                "area-presentation-read-failed:" + exception.GetType().Name));
        }
    }

    private AreaPresentationResult Cache(string key, AreaPresentationResult result)
    {
        cache[key] = result;
        return result;
    }
}
