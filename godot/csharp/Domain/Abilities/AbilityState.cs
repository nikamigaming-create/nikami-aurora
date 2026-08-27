using OpenDAO.Application.Abstractions;
using OpenDAO.Domain.Common;

namespace OpenDAO.Domain.Abilities;

public sealed class AbilityState(IClock clock)
{
    public const float MaximumResource = 100.0f;
    public const float RegenerationPerSecond = 5.0f;
    public const int QuickSlotCount = 14;

    private readonly Dictionary<int, AbilityDefinition> granted = [];
    private readonly Dictionary<int, int> slots = [];
    private readonly Dictionary<int, string> slotItems = [];
    private readonly Dictionary<int, double> cooldownEnds = [];
    private readonly HashSet<int> activeModals = [];

    public event Action<AbilitySnapshot>? Changed;
    public event Action<AbilityDefinition, IReadOnlyDictionary<string, object?>>? Activated;

    public float Resource { get; private set; } = MaximumResource;
    public IReadOnlyDictionary<int, AbilityDefinition> Granted => granted;

    public OperationResult Grant(AbilityDefinition definition, int slot = 0)
    {
        if (definition.Id <= 0 || definition.Cost < 0 || definition.Cooldown < 0 ||
            slot is < 0 or > QuickSlotCount)
            return OperationResult.Unsupported("ability-record-invalid");

        granted[definition.Id] = definition;
        if (slot > 0)
        {
            slots[slot] = definition.Id;
            slotItems.Remove(slot);
        }

        Notify();
        return OperationResult.Complete(("id", definition.Id), ("slot", slot),
            ("provenance", definition.Provenance));
    }

    public OperationResult Revoke(int abilityId)
    {
        if (!granted.Remove(abilityId))
            return OperationResult.Complete(("id", abilityId), ("removed", false));

        cooldownEnds.Remove(abilityId);
        activeModals.Remove(abilityId);
        var cleared = slots.Where(x => x.Value == abilityId).Select(x => x.Key).ToArray();
        foreach (var slot in cleared)
        {
            slots.Remove(slot);
            slotItems.Remove(slot);
        }

        Notify();
        return OperationResult.Complete(("id", abilityId), ("removed", true), ("clearedSlots", cleared));
    }

    public OperationResult ActivateSlot(int slot) =>
        slots.TryGetValue(slot, out var id)
            ? Activate(id)
            : OperationResult.Unsupported("ability-slot-empty");

    public OperationResult Activate(int abilityId)
    {
        if (!granted.TryGetValue(abilityId, out var definition))
            return OperationResult.Unsupported("ability-not-granted");

        if (definition.UseType == 2 && activeModals.Remove(abilityId))
        {
            var deactivation = Provenance(definition, false);
            Activated?.Invoke(definition, deactivation);
            Notify();
            return OperationResult.Complete(("id", abilityId), ("record", definition),
                ("resource", Resource), ("modalActive", false), ("provenance", deactivation));
        }

        var remaining = CooldownRemaining(abilityId);
        if (remaining > 0)
            return OperationResult.Unsupported("ability-on-cooldown", ("remaining", remaining));
        if (Resource < definition.Cost)
            return OperationResult.Unsupported("ability-resource-insufficient",
                ("required", definition.Cost), ("available", Resource));

        Resource -= definition.Cost;
        cooldownEnds[abilityId] = clock.ElapsedSeconds + definition.Cooldown;
        if (definition.UseType == 2)
            activeModals.Add(abilityId);
        var provenance = Provenance(definition, activeModals.Contains(abilityId));
        Activated?.Invoke(definition, provenance);
        Notify();
        return OperationResult.Complete(("id", abilityId), ("record", definition),
            ("resource", Resource), ("modalActive", activeModals.Contains(abilityId)),
            ("provenance", provenance));
    }

    public OperationResult SetQuickSlot(int slot, int abilityId, string itemTag = "")
    {
        if (slot is < 1 or > QuickSlotCount || abilityId < 0)
            return OperationResult.Unsupported("quickslot-argument-invalid");
        if (abilityId != 0 && !granted.ContainsKey(abilityId))
            return OperationResult.Unsupported("quickslot-ability-not-granted");

        if (abilityId == 0)
        {
            slots.Remove(slot);
            slotItems.Remove(slot);
        }
        else
        {
            slots[slot] = abilityId;
            var normalized = itemTag.Trim().ToLowerInvariant();
            if (normalized.Length == 0) slotItems.Remove(slot);
            else slotItems[slot] = normalized;
        }

        Notify();
        return OperationResult.Complete(("slot", slot), ("abilityId", abilityId),
            ("itemTag", slotItems.GetValueOrDefault(slot, string.Empty)));
    }

    public void Advance(double deltaSeconds)
    {
        if (deltaSeconds <= 0 || Resource >= MaximumResource) return;
        Resource = Math.Min(MaximumResource, Resource + RegenerationPerSecond * (float)deltaSeconds);
    }

    public double CooldownRemaining(int abilityId) =>
        Math.Max(0, cooldownEnds.GetValueOrDefault(abilityId) - clock.ElapsedSeconds);

    public AbilityDefinition? ForSlot(int slot) =>
        slots.TryGetValue(slot, out var id) ? granted.GetValueOrDefault(id) : null;

    public AbilitySnapshot Snapshot() => new(Resource,
        new Dictionary<int, AbilityDefinition>(granted), new Dictionary<int, int>(slots),
        new Dictionary<int, string>(slotItems), new HashSet<int>(activeModals),
        cooldownEnds.ToDictionary(x => x.Key, x => Math.Max(0, x.Value - clock.ElapsedSeconds)));

    public void Restore(AbilitySnapshot snapshot)
    {
        Resource = Math.Clamp(snapshot.Resource, 0, MaximumResource);
        Replace(granted, snapshot.Granted);
        Replace(slots, snapshot.Slots);
        Replace(slotItems, snapshot.SlotItems);
        activeModals.Clear();
        activeModals.UnionWith(snapshot.ActiveModals);
        cooldownEnds.Clear();
        foreach (var item in snapshot.CooldownRemaining)
            cooldownEnds[item.Key] = clock.ElapsedSeconds + Math.Max(0, item.Value);
        Notify();
    }

    private static Dictionary<string, object?> Provenance(AbilityDefinition definition, bool active) => new()
    {
        ["basis"] = "installed-ability-cost+cooldown",
        ["ability"] = definition.Provenance,
        ["cost"] = definition.Cost,
        ["cooldown"] = definition.Cooldown,
        ["modalActive"] = active,
    };

    private void Notify() => Changed?.Invoke(Snapshot());

    private static void Replace<TKey, TValue>(Dictionary<TKey, TValue> target,
        IReadOnlyDictionary<TKey, TValue> source) where TKey : notnull
    {
        target.Clear();
        foreach (var item in source) target[item.Key] = item.Value;
    }
}

public sealed record AbilitySnapshot(
    float Resource,
    IReadOnlyDictionary<int, AbilityDefinition> Granted,
    IReadOnlyDictionary<int, int> Slots,
    IReadOnlyDictionary<int, string> SlotItems,
    IReadOnlySet<int> ActiveModals,
    IReadOnlyDictionary<int, double> CooldownRemaining);
