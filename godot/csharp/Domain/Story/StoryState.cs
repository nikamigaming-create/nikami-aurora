using Nikami.Aurora.GodotRuntime.Domain.Common;

namespace Nikami.Aurora.GodotRuntime.Domain.Story;

public sealed class StoryState
{
    private readonly object gate = new();
    private Dictionary<int, StoryObject> objects = [];
    private readonly Dictionary<(string Plot, int Flag), bool> plotFlags = [];
    private readonly Dictionary<(int Handle, string Type, string Name), object?> locals = [];
    private readonly Dictionary<(int A, int B), bool> hostility = [];
    private int nextRuntimeHandle = 0x50000000;

    public event Action<StoryCommit>? Committed;
    public long Revision { get; private set; }
    public int PartyMoney { get; private set; }
    public IReadOnlyCollection<StoryObject> Objects { get { lock (gate) return objects.Values.ToArray(); } }

    public OperationResult Register(StoryObject value)
    {
        if (value.Handle <= 0 || value.ResourceReference.Trim().Length == 0)
            return OperationResult.Unsupported("story-object-invalid");
        lock (gate)
        {
            if (objects.ContainsKey(value.Handle)) return OperationResult.Unsupported("story-handle-duplicate");
            objects[value.Handle] = Normalize(value);
            Revision++;
        }
        return OperationResult.Complete(("handle", value.Handle));
    }

    public StoryObject Create(string resourceReference, string tag, StoryObjectKind kind,
        StoryPosition position, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        lock (gate)
        {
            var value = new StoryObject(nextRuntimeHandle++, Normalize(resourceReference), Normalize(tag), tag,
                kind, position, 0, true, true, false, false, 0, 0,
                metadata ?? new Dictionary<string, object?>());
            objects[value.Handle] = value;
            Revision++;
            return value;
        }
    }

    public StoryObject? ByHandle(int handle) { lock (gate) return objects.GetValueOrDefault(handle); }
    public StoryObject? ByResourceReference(string value)
    {
        lock (gate) return objects.Values
        .FirstOrDefault(x => x.ResourceReference.Equals(Normalize(value), StringComparison.Ordinal));
    }
    public StoryObject? ByTag(string value)
    {
        lock (gate) return objects.Values
        .FirstOrDefault(x => x.Tag.Equals(Normalize(value), StringComparison.Ordinal));
    }
    public IReadOnlyList<StoryObject> InRange(StoryPosition center, float radius,
        StoryObjectKind? kind = null)
    {
        lock (gate) return objects.Values.Where(x => x.Active &&
        (kind is null || x.Kind == kind) && x.Position.DistanceTo(center) <= radius).ToArray();
    }

    public OperationResult Commit(IEnumerable<StoryOperation> requested)
    {
        var operations = requested.ToArray();
        if (operations.Length == 0) return OperationResult.Complete(("revision", Revision));
        StoryCommit commit;
        lock (gate)
        {
            var candidate = new Dictionary<int, StoryObject>(objects);
            var changed = new Dictionary<int, StoryObject>();
            foreach (var operation in operations)
            {
                if (!candidate.TryGetValue(operation.Handle, out var current))
                    return OperationResult.Unsupported("story-handle-absent", ("handle", operation.Handle));
                var updated = Apply(current, operation);
                if (!Validate(updated)) return OperationResult.Unsupported("story-operation-invalid",
                    ("handle", operation.Handle), ("operation", operation.GetType().Name));
                if (operation is DestroyObject) candidate.Remove(operation.Handle);
                else candidate[operation.Handle] = updated;
                changed[operation.Handle] = updated;
            }
            objects = candidate;
            Revision++;
            commit = new StoryCommit(Revision, operations, changed);
        }
        Committed?.Invoke(commit);
        return OperationResult.Complete(("revision", commit.Revision), ("changed", commit.ChangedObjects));
    }

    public void SetPlotFlag(string plot, int flag, bool enabled = true)
    {
        lock (gate) { plotFlags[(Normalize(plot), flag)] = enabled; Revision++; }
    }
    public bool GetPlotFlag(string plot, int flag)
    {
        lock (gate)
            return plotFlags.GetValueOrDefault((Normalize(plot), flag));
    }

    public OperationResult SetLocal(int handle, string name, string type, object? value)
    {
        lock (gate)
        {
            if (!objects.ContainsKey(handle)) return OperationResult.Unsupported("local-owner-absent");
            locals[(handle, Normalize(type), Normalize(name))] = value;
            Revision++;
        }
        return OperationResult.Complete(("handle", handle), ("name", name), ("type", type));
    }
    public object? GetLocal(int handle, string name, string type)
    {
        lock (gate)
            return locals.GetValueOrDefault((handle, Normalize(type), Normalize(name)));
    }

    public int ChangePartyMoney(int delta)
    {
        lock (gate) { PartyMoney = checked(Math.Max(0, PartyMoney + delta)); Revision++; return PartyMoney; }
    }

    public void SetHostile(int firstGroup, int secondGroup, bool value)
    {
        lock (gate)
        {
            hostility[CanonicalPair(firstGroup, secondGroup)] = value;
            Revision++;
        }
    }
    public bool IsHostile(int firstGroup, int secondGroup)
    {
        lock (gate)
            return hostility.GetValueOrDefault(CanonicalPair(firstGroup, secondGroup));
    }

    private static StoryObject Apply(StoryObject value, StoryOperation operation) => operation switch
    {
        SetActive x => value with { Active = x.Value },
        SetInteractive x => value with { Interactive = x.Value },
        SetIdentity x => value with
        {
            Tag = x.Tag is null ? value.Tag : Normalize(x.Tag),
            DisplayName = x.DisplayName?.Trim() ?? value.DisplayName
        },
        SetPosition x => value with { Position = x.Value },
        SetFacing x => value with { Facing = NormalizeRadians(x.Value) },
        SetProtection x => value with { Plot = x.Plot ?? value.Plot, Immortal = x.Immortal ?? value.Immortal },
        SetGroupTeam x => value with { GroupId = x.GroupId ?? value.GroupId, TeamId = x.TeamId ?? value.TeamId },
        DestroyObject => value with { Active = false, Interactive = false },
        _ => value,
    };

    private static StoryObject Normalize(StoryObject value) => value with
    {
        ResourceReference = Normalize(value.ResourceReference),
        Tag = Normalize(value.Tag),
        DisplayName = value.DisplayName.Trim(),
        Facing = NormalizeRadians(value.Facing)
    };
    private static bool Validate(StoryObject value) => value.Handle > 0 &&
        value.ResourceReference.Length > 0 && float.IsFinite(value.Position.X) &&
        float.IsFinite(value.Position.Y) && float.IsFinite(value.Position.Z) && float.IsFinite(value.Facing);
    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private static float NormalizeRadians(float value) => MathF.IEEERemainder(value, MathF.Tau);
    private static (int, int) CanonicalPair(int a, int b) => a <= b ? (a, b) : (b, a);
}
