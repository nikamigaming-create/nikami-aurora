using Nikami.Aurora.GodotRuntime.Domain.Common;

namespace Nikami.Aurora.GodotRuntime.Domain.Inventory;

public sealed class InventoryState
{
    private readonly Dictionary<string, InventoryEntry> items = new(StringComparer.OrdinalIgnoreCase);

    public event Action<IReadOnlyCollection<InventoryEntry>>? Changed;
    public event Action<InventoryEntry>? ItemUsed;

    public IReadOnlyCollection<InventoryEntry> Items => items.Values.ToArray();

    public OperationResult Add(string tag, string name, int quantity = 1,
        IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var key = Normalize(tag);
        if (key.Length == 0 || quantity <= 0)
            return OperationResult.Unsupported("inventory-item-invalid");

        var existing = items.GetValueOrDefault(key);
        items[key] = existing is null
            ? new(key, name.Trim().Length == 0 ? key : name.Trim(), quantity,
                metadata ?? new Dictionary<string, object?>())
            : existing with { Quantity = checked(existing.Quantity + quantity) };
        Notify();
        return OperationResult.Complete(("tag", key), ("quantity", items[key].Quantity));
    }

    public OperationResult Remove(string tag, int quantity = 1)
    {
        var key = Normalize(tag);
        if (quantity <= 0 || !items.TryGetValue(key, out var item) || item.Quantity < quantity)
            return OperationResult.Unsupported("inventory-quantity-insufficient");

        var remaining = item.Quantity - quantity;
        if (remaining == 0) items.Remove(key);
        else items[key] = item with { Quantity = remaining };
        Notify();
        return OperationResult.Complete(("tag", key), ("quantity", remaining));
    }

    public OperationResult Use(string tag)
    {
        var key = Normalize(tag);
        if (!items.TryGetValue(key, out var item))
            return OperationResult.Unsupported("inventory-item-absent");
        ItemUsed?.Invoke(item);
        return OperationResult.Complete(("tag", key), ("item", item));
    }

    public int Quantity(string tag) => items.GetValueOrDefault(Normalize(tag))?.Quantity ?? 0;

    public void Restore(IEnumerable<InventoryEntry> entries)
    {
        items.Clear();
        foreach (var entry in entries.Where(x => x.Quantity > 0))
            items[Normalize(entry.Tag)] = entry with { Tag = Normalize(entry.Tag) };
        Notify();
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
    private void Notify() => Changed?.Invoke(Items);
}

public sealed record InventoryEntry(string Tag, string Name, int Quantity,
    IReadOnlyDictionary<string, object?> Metadata);
