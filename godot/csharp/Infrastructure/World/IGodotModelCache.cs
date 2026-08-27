using Godot;

namespace OpenDAO.Infrastructure.World;

public interface IGodotModelCache
{
    int Hits { get; }
    int Misses { get; }
    PackedScene? Load(string path);
    Node3D? Instantiate(string path);
    Task WarmAsync(IEnumerable<string> paths, Node owner, CancellationToken cancellationToken);
}
