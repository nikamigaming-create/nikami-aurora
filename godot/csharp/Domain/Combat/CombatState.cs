using OpenDAO.Domain.Common;

namespace OpenDAO.Domain.Combat;

public sealed record Combatant(int Handle, int CurrentHealth, int MaximumHealth, bool InCombat,
    int TargetHandle, bool Plot, bool Immortal, bool Defeated);

public sealed class CombatState
{
    private readonly Dictionary<int, Combatant> combatants = [];
    public event Action<Combatant>? Changed;
    public event Action<Combatant>? Defeated;
    public IReadOnlyCollection<Combatant> Combatants => combatants.Values.ToArray();

    public OperationResult Register(int handle, int maximumHealth, bool plot = false, bool immortal = false)
    {
        if (handle <= 0 || maximumHealth <= 0) return OperationResult.Unsupported("combatant-invalid");
        combatants[handle] = new(handle, maximumHealth, maximumHealth, false, 0, plot, immortal, false);
        Changed?.Invoke(combatants[handle]);
        return OperationResult.Complete(("handle", handle), ("health", maximumHealth));
    }

    public OperationResult Attack(int sourceHandle, int targetHandle, int damage)
    {
        if (!combatants.ContainsKey(sourceHandle) || !combatants.TryGetValue(targetHandle, out var target))
            return OperationResult.Unsupported("combatant-absent");
        if (damage < 0 || target.Defeated) return OperationResult.Unsupported("damage-invalid");
        combatants[sourceHandle] = combatants[sourceHandle] with { InCombat = true, TargetHandle = targetHandle };
        var floor = target.Plot || target.Immortal ? 1 : 0;
        var current = Math.Max(floor, target.CurrentHealth - damage);
        var defeated = current == 0;
        target = target with { CurrentHealth = current, InCombat = !defeated, Defeated = defeated };
        combatants[targetHandle] = target;
        Changed?.Invoke(target);
        if (defeated) Defeated?.Invoke(target);
        return OperationResult.Complete(("source", sourceHandle), ("target", targetHandle),
            ("damage", damage), ("health", current), ("defeated", defeated));
    }

    public OperationResult Heal(int handle, int amount)
    {
        if (!combatants.TryGetValue(handle, out var target) || amount < 0)
            return OperationResult.Unsupported("heal-invalid");
        target = target with
        {
            CurrentHealth = Math.Min(target.MaximumHealth, target.CurrentHealth + amount),
            Defeated = false
        };
        combatants[handle] = target;
        Changed?.Invoke(target);
        return OperationResult.Complete(("handle", handle), ("health", target.CurrentHealth));
    }

    public void LeaveCombat(int handle)
    {
        if (!combatants.TryGetValue(handle, out var target)) return;
        combatants[handle] = target with { InCombat = false, TargetHandle = 0 };
        Changed?.Invoke(combatants[handle]);
    }
}
