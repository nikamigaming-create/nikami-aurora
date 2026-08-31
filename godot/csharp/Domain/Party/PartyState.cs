using Nikami.Aurora.GodotRuntime.Domain.Common;

namespace Nikami.Aurora.GodotRuntime.Domain.Party;

public sealed record PartyMember(string CharacterId, int StoryHandle, string DisplayName,
    bool Available, bool Active, bool Leader);

public sealed class PartyState
{
    private readonly Dictionary<string, PartyMember> members = new(StringComparer.OrdinalIgnoreCase);
    public event Action<IReadOnlyList<PartyMember>>? Changed;
    public IReadOnlyList<PartyMember> Members => members.Values.OrderByDescending(x => x.Leader)
        .ThenBy(x => x.CharacterId).ToArray();

    public OperationResult Add(PartyMember member)
    {
        var id = member.CharacterId.Trim().ToLowerInvariant();
        if (id.Length == 0 || member.StoryHandle <= 0) return OperationResult.Unsupported("party-member-invalid");
        if (member.Active && members.Values.Count(x => x.Active) >= 4)
            return OperationResult.Unsupported("party-full");
        members[id] = member with { CharacterId = id };
        if (!members.Values.Any(x => x.Leader)) members[id] = members[id] with { Leader = true, Active = true };
        Notify();
        return OperationResult.Complete(("characterId", id));
    }

    public OperationResult SetActive(string characterId, bool active)
    {
        var id = characterId.Trim().ToLowerInvariant();
        if (!members.TryGetValue(id, out var member) || (active && !member.Available))
            return OperationResult.Unsupported("party-member-unavailable");
        if (active && !member.Active && members.Values.Count(x => x.Active) >= 4)
            return OperationResult.Unsupported("party-full");
        if (!active && member.Leader) return OperationResult.Unsupported("party-leader-cannot-be-inactive");
        members[id] = member with { Active = active };
        Notify();
        return OperationResult.Complete(("characterId", id), ("active", active));
    }

    public OperationResult SetLeader(string characterId)
    {
        var id = characterId.Trim().ToLowerInvariant();
        if (!members.TryGetValue(id, out var member) || !member.Active)
            return OperationResult.Unsupported("party-leader-inactive");
        foreach (var key in members.Keys.ToArray()) members[key] = members[key] with { Leader = key == id };
        Notify();
        return OperationResult.Complete(("characterId", id), ("handle", member.StoryHandle));
    }

    private void Notify() => Changed?.Invoke(Members);
}
