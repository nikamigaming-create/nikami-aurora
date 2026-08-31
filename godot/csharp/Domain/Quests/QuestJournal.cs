using Nikami.Aurora.GodotRuntime.Domain.Common;

namespace Nikami.Aurora.GodotRuntime.Domain.Quests;

public sealed class QuestJournal
{
    private readonly Dictionary<string, QuestEntry> entries = new(StringComparer.OrdinalIgnoreCase);
    public event Action<IReadOnlyCollection<QuestEntry>>? Changed;
    public IReadOnlyCollection<QuestEntry> Entries => entries.Values
        .OrderBy(x => x.Group).ThenBy(x => x.Title).ToArray();

    public OperationResult Upsert(string id, string title, string group, string description,
        QuestStatus status, IReadOnlyDictionary<string, object?>? provenance = null)
    {
        var key = id.Trim().ToLowerInvariant();
        if (key.Length == 0) return OperationResult.Unsupported("quest-id-empty");
        entries[key] = new(key, title.Trim(), group.Trim(), description.Trim(), status,
            provenance ?? new Dictionary<string, object?>());
        Changed?.Invoke(Entries);
        return OperationResult.Complete(("id", key), ("status", status));
    }

    public OperationResult CloseGroup(string group)
    {
        var affected = 0;
        foreach (var key in entries.Where(x => string.Equals(x.Value.Group, group,
                     StringComparison.OrdinalIgnoreCase)).Select(x => x.Key).ToArray())
        {
            entries[key] = entries[key] with { Status = QuestStatus.Closed };
            affected++;
        }
        Changed?.Invoke(Entries);
        return OperationResult.Complete(("group", group), ("affected", affected));
    }

    public void Restore(IEnumerable<QuestEntry> values)
    {
        entries.Clear();
        foreach (var value in values) entries[value.Id] = value;
        Changed?.Invoke(Entries);
    }
}

public enum QuestStatus { Active, Completed, Failed, Closed }

public sealed record QuestEntry(string Id, string Title, string Group, string Description,
    QuestStatus Status, IReadOnlyDictionary<string, object?> Provenance);
