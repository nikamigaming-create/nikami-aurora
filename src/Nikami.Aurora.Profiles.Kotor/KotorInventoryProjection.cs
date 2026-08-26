namespace Nikami.Aurora.Profiles.Kotor;

public sealed record KotorInventoryProjectionResult<T>(
    IReadOnlyList<T> Items,
    int DefinitionVisits,
    int InventoryLookups,
    int FilterEvaluations,
    int RowsMaterialized)
{
    public long WorkUnits =>
        (long)DefinitionVisits + InventoryLookups + FilterEvaluations + RowsMaterialized;
}

public static class KotorInventoryProjection
{
    public static KotorInventoryProjectionResult<T> Project<T>(
        IReadOnlyList<T> definitions,
        IReadOnlyDictionary<string, int> inventory,
        Func<T, string> resref,
        Func<T, bool> isQuestItem,
        bool questItemsOnly,
        int repeat = 1)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(resref);
        ArgumentNullException.ThrowIfNull(isQuestItem);
        if (repeat <= 0)
            throw new ArgumentOutOfRangeException(nameof(repeat));

        var selected = new List<T>();
        var visits = 0;
        var lookups = 0;
        var filters = 0;
        foreach (var definition in definitions)
        {
            visits++;
            var key = resref(definition);
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("KOTOR inventory definition has no resref");
            lookups++;
            if (!inventory.ContainsKey(key))
                continue;
            filters++;
            if (!questItemsOnly || isQuestItem(definition))
                selected.Add(definition);
        }

        var rows = new List<T>(checked(selected.Count * repeat));
        for (var copy = 0; copy < repeat; copy++)
            rows.AddRange(selected);
        return new KotorInventoryProjectionResult<T>(
            rows,
            visits,
            lookups,
            filters,
            rows.Count);
    }
}
