using System.Security.Cryptography;
using System.Text;
using Godot;
using Nikami.Aurora.GodotRuntime.Infrastructure.Configuration;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

public sealed class GodotModelCache(IGodotModelPostprocessor modelPostprocessor) : IGodotModelCache, IDisposable
{
    private const string CacheDirectory = "user://model-cache/v3";
    private const string DisableDiskCacheVariable = "OPENDAO_DISABLE_MODEL_DISK_CACHE";
    private readonly Dictionary<string, PackedScene> memory =
        new(StringComparer.OrdinalIgnoreCase);

    public int Hits { get; private set; }
    public int Misses { get; private set; }

    private static bool DiskCacheEnabled =>
        OS.GetEnvironment(DisableDiskCacheVariable) != "1";

    public PackedScene? Load(string path)
    {
        if (memory.TryGetValue(path, out var scene))
        {
            modelPostprocessor.Prepare(scene);
            Hits++;
            return scene;
        }

        if (path.StartsWith("res://", StringComparison.Ordinal) && ResourceLoader.Exists(path))
        {
            scene = ResourceLoader.Load<PackedScene>(path);
            if (scene is null) return null;
            modelPostprocessor.Prepare(scene);
            memory[path] = scene;
            Hits++;
            return scene;
        }

        var sourcePath = DaoRuntimePaths.ResolveSourcePath(path);
        if (!File.Exists(sourcePath)) return null;
        var cachePath = CachePath(sourcePath);
        if (DiskCacheEnabled && ResourceLoader.Exists(cachePath))
        {
            scene = ResourceLoader.Load<PackedScene>(cachePath, cacheMode: ResourceLoader.CacheMode.Reuse);
            if (scene is not null)
            {
                modelPostprocessor.Prepare(scene);
                memory[path] = scene;
                Hits++;
                return scene;
            }
        }

        scene = Import(sourcePath);
        if (scene is null) return null;
        memory[path] = scene;
        Misses++;
        if (DiskCacheEnabled) Persist(scene, cachePath);
        modelPostprocessor.Prepare(scene);
        return scene;
    }

    public Node3D? Instantiate(string path)
    {
        var scene = Load(path);
        if (scene?.Instantiate<Node3D>() is not { } instance) return null;
        modelPostprocessor.Prepare(instance);
        return instance;
    }

    public async Task WarmAsync(IEnumerable<string> paths, Node owner,
        CancellationToken cancellationToken)
    {
        var pending = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directImports = new List<string>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (memory.ContainsKey(path)) continue;
            if (path.StartsWith("res://", StringComparison.Ordinal))
            {
                if (ResourceLoader.Exists(path) && ResourceLoader.LoadThreadedRequest(path, "PackedScene", false,
                        ResourceLoader.CacheMode.Reuse) == Error.Ok)
                    pending[path] = path;
                else if (File.Exists(DaoRuntimePaths.ResolveSourcePath(path)))
                {
                    var directCachePath = CachePath(DaoRuntimePaths.ResolveSourcePath(path));
                    if (DiskCacheEnabled && ResourceLoader.Exists(directCachePath) && ResourceLoader.LoadThreadedRequest(directCachePath,
                            "PackedScene", false, ResourceLoader.CacheMode.Reuse) == Error.Ok)
                        pending[path] = directCachePath;
                    else
                        directImports.Add(path);
                }
                continue;
            }
            if (!File.Exists(path)) continue;
            if (!DiskCacheEnabled)
            {
                directImports.Add(path);
                continue;
            }
            var cachePath = CachePath(path);
            if (!ResourceLoader.Exists(cachePath)) continue;
            if (ResourceLoader.LoadThreadedRequest(cachePath, "PackedScene", false,
                    ResourceLoader.CacheMode.Reuse) == Error.Ok)
                pending[path] = cachePath;
        }

        while (pending.Values.Any(cachePath => ResourceLoader.LoadThreadedGetStatus(cachePath) ==
                                               ResourceLoader.ThreadLoadStatus.InProgress))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        cancellationToken.ThrowIfCancellationRequested();
        foreach (var (path, cachePath) in pending)
            if (ResourceLoader.LoadThreadedGetStatus(cachePath) == ResourceLoader.ThreadLoadStatus.Loaded &&
                ResourceLoader.LoadThreadedGet(cachePath) is PackedScene scene)
            {
                modelPostprocessor.Prepare(scene);
                memory[path] = scene;
            }
        foreach (var path in directImports)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Load(path);
            await owner.ToSignal(owner.GetTree(), SceneTree.SignalName.ProcessFrame);
        }
    }

    public void Dispose() => memory.Clear();

    private PackedScene? Import(string path)
    {
        var document = new GltfDocument();
        var state = new GltfState();
        if (document.AppendFromFile(path, state) != Error.Ok ||
            document.GenerateScene(state) is not Node3D imported) return null;
        modelPostprocessor.Process(imported, state, path);
        var packed = new PackedScene();
        if (packed.Pack(imported) != Error.Ok)
        {
            imported.Free();
            return null;
        }

        imported.Free();
        return packed;
    }

    private string CachePath(string path)
    {
        var info = new FileInfo(path);
        var fingerprint = $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|" +
                          modelPostprocessor.CacheFingerprint;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)))
            .ToLowerInvariant();
        return $"{CacheDirectory}/{hash}.scn";
    }

    private static void Persist(PackedScene scene, string cachePath)
    {
        var directory = ProjectSettings.GlobalizePath(CacheDirectory);
        Directory.CreateDirectory(directory);
        var result = ResourceSaver.Save(scene, cachePath,
            ResourceSaver.SaverFlags.Compress | ResourceSaver.SaverFlags.OmitEditorProperties);
        if (result != Error.Ok)
            GD.PushWarning($"Nikami.Aurora.GodotRuntime model cache write failed: {cachePath} ({result})");
    }
}
