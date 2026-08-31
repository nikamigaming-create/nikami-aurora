using System.Collections.ObjectModel;

namespace Nikami.Aurora.GodotRuntime.Domain.Common;

public sealed record OperationResult(
    bool Succeeded,
    string Status,
    string Reason,
    IReadOnlyDictionary<string, object?> Data)
{
    public static OperationResult Complete(params (string Key, object? Value)[] values) =>
        new(true, "complete", string.Empty, ToDictionary(values));

    public static OperationResult Unsupported(string reason, params (string Key, object? Value)[] values) =>
        new(false, "unsupported", reason, ToDictionary(values));

    private static IReadOnlyDictionary<string, object?> ToDictionary(
        IEnumerable<(string Key, object? Value)> values) =>
        new ReadOnlyDictionary<string, object?>(values.ToDictionary(x => x.Key, x => x.Value));
}
