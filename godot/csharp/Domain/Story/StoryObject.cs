namespace Nikami.Aurora.GodotRuntime.Domain.Story;

public enum StoryObjectKind { Area, Creature, Placeable, Item, Waypoint, Trigger, Unknown }

public readonly record struct StoryPosition(float X, float Y, float Z)
{
    public float DistanceTo(StoryPosition other)
    {
        var x = X - other.X; var y = Y - other.Y; var z = Z - other.Z;
        return MathF.Sqrt(x * x + y * y + z * z);
    }
}

public sealed record StoryObject(
    int Handle,
    string ResourceReference,
    string Tag,
    string DisplayName,
    StoryObjectKind Kind,
    StoryPosition Position,
    float Facing,
    bool Active,
    bool Interactive,
    bool Plot,
    bool Immortal,
    int GroupId,
    int TeamId,
    IReadOnlyDictionary<string, object?> Metadata);
