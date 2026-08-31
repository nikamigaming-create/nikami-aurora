using Godot;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

public static class DaoRuntimePaths
{
    public const string CacheRootEnvironmentVariable =
        "NIKAMI_AURORA_DAO_CACHE_ROOT";
    public const string GeneratedRootEnvironmentVariable =
        "NIKAMI_AURORA_DAO_GENERATED_ROOT";

    private const string GeneratedResourcePrefix = "res://assets/generated/";
    private const string CacheResourcePrefix = "res://../cache/dao-world/";

    public static string Cache(params string[] segments) =>
        CombineUnderRoot(ResolveRoot(CacheRootEnvironmentVariable,
            Path.Combine("..", "cache", "dao-world")), segments);

    public static string Generated(params string[] segments) =>
        CombineUnderRoot(ResolveRoot(GeneratedRootEnvironmentVariable,
            Path.Combine("assets", "generated")), segments);

    public static string ResolveSourcePath(string path)
    {
        if (path.StartsWith(GeneratedResourcePrefix,
                StringComparison.OrdinalIgnoreCase))
            return Generated(path[GeneratedResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar));
        if (path.StartsWith(CacheResourcePrefix,
                StringComparison.OrdinalIgnoreCase))
            return Cache(path[CacheResourcePrefix.Length..]
                .Replace('/', Path.DirectorySeparatorChar));
        return path.StartsWith("res://", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("user://", StringComparison.OrdinalIgnoreCase)
            ? ProjectSettings.GlobalizePath(path)
            : path;
    }

    public static Texture2D? LoadTexture(string path)
    {
        if (ResourceLoader.Exists(path))
            return ResourceLoader.Load<Texture2D>(path);
        var sourcePath = ResolveSourcePath(path);
        if (!File.Exists(sourcePath)) return null;
        var image = Image.LoadFromFile(sourcePath);
        return image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
    }

    private static string ResolveRoot(string environmentVariable,
        string projectRelativeDefault)
    {
        var configured = System.Environment.GetEnvironmentVariable(
            environmentVariable)?.Trim();
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(Path.Combine(
                ProjectSettings.GlobalizePath("res://"), projectRelativeDefault))
            : Path.GetFullPath(configured);
    }

    private static string CombineUnderRoot(string root,
        IReadOnlyList<string> segments)
    {
        var candidate = segments.Aggregate(root, Path.Combine);
        var resolved = Path.GetFullPath(candidate);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        if (!resolved.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            !resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"DAO runtime path escapes its configured root: {resolved}");
        return resolved;
    }
}
