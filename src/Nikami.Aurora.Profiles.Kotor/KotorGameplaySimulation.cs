using System.Collections.ObjectModel;

namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorScriptContractKind
{
    PlotExperienceIfPlayerExperience,
    DialogueOpenDoor
}

public sealed record KotorScriptContract(
    string Resref,
    KotorScriptContractKind Kind,
    string SourceSha256,
    int InstructionCount,
    string? DoorTag = null,
    int? RequiredPlayerExperience = null,
    string? PlotLabel = null,
    int? PlotPercentage = null,
    int? PlotBaseExperience = null,
    int? AwardedExperience = null,
    bool? PauseConversation = null,
    string? MoveTargetTag = null,
    bool? MoveRun = null,
    float? MoveRange = null,
    bool? ResumeConversation = null)
{
    public string KindName => Kind switch
    {
        KotorScriptContractKind.PlotExperienceIfPlayerExperience => "plot-xp-if-player-xp",
        KotorScriptContractKind.DialogueOpenDoor => "dialogue-open-door",
        _ => throw new InvalidOperationException($"Unsupported KOTOR script-contract kind: {Kind}")
    };

    public KotorScriptContract Validate()
    {
        if (string.IsNullOrWhiteSpace(Resref))
            throw new ArgumentException("Script resref cannot be empty", nameof(Resref));
        if (SourceSha256.Length != 64 || !SourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Script source SHA-256 must contain 64 hexadecimal characters",
                nameof(SourceSha256));
        if (InstructionCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(InstructionCount));

        switch (Kind)
        {
            case KotorScriptContractKind.PlotExperienceIfPlayerExperience:
                if (RequiredPlayerExperience is null or < 0)
                    throw new ArgumentOutOfRangeException(nameof(RequiredPlayerExperience));
                if (string.IsNullOrWhiteSpace(PlotLabel))
                    throw new ArgumentException("Plot label cannot be empty", nameof(PlotLabel));
                if (PlotPercentage is null or < 0)
                    throw new ArgumentOutOfRangeException(nameof(PlotPercentage));
                if (PlotBaseExperience is null or < 0)
                    throw new ArgumentOutOfRangeException(nameof(PlotBaseExperience));
                if (AwardedExperience is null or < 0)
                    throw new ArgumentOutOfRangeException(nameof(AwardedExperience));
                if (AwardedExperience != checked(PlotBaseExperience * PlotPercentage / 100))
                    throw new ArgumentException("Awarded experience does not match the plot contract",
                        nameof(AwardedExperience));
                break;
            case KotorScriptContractKind.DialogueOpenDoor:
                if (string.IsNullOrWhiteSpace(DoorTag))
                    throw new ArgumentException("Dialogue door tag cannot be empty", nameof(DoorTag));
                if (MoveRange is { } range && (!float.IsFinite(range) || range < 0))
                    throw new ArgumentOutOfRangeException(nameof(MoveRange));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
        return this;
    }
}

public sealed record KotorDoorDefinition(string InstanceId, string Tag, string? OnOpenScript);

public sealed record KotorItemDefinition(
    string Resref,
    string DisplayName,
    string Tag,
    string SourceSha256,
    string BaseItemsSourceSha256,
    int BaseItem,
    int Charges,
    int StackSize,
    int ModelVariation,
    int BodyVariation,
    int TextureVariation,
    int EquipableSlots,
    string ItemClass,
    int ModelType,
    string DefaultModel,
    string DefaultIcon)
{
    public KotorItemDefinition Validate()
    {
        if (string.IsNullOrWhiteSpace(Resref))
            throw new ArgumentException("Item resref cannot be empty", nameof(Resref));
        if (string.IsNullOrWhiteSpace(DisplayName))
            throw new ArgumentException("Item display name cannot be empty", nameof(DisplayName));
        if (SourceSha256.Length != 64 || !SourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("Item source SHA-256 must contain 64 hexadecimal characters",
                nameof(SourceSha256));
        if (BaseItemsSourceSha256.Length != 64 || !BaseItemsSourceSha256.All(Uri.IsHexDigit))
            throw new ArgumentException(
                "Base-items source SHA-256 must contain 64 hexadecimal characters",
                nameof(BaseItemsSourceSha256));
        if (BaseItem < 0)
            throw new ArgumentOutOfRangeException(nameof(BaseItem));
        if (Charges < 0)
            throw new ArgumentOutOfRangeException(nameof(Charges));
        if (StackSize < 0)
            throw new ArgumentOutOfRangeException(nameof(StackSize));
        if (ModelVariation < 0)
            throw new ArgumentOutOfRangeException(nameof(ModelVariation));
        if (BodyVariation < 0)
            throw new ArgumentOutOfRangeException(nameof(BodyVariation));
        if (TextureVariation < 0)
            throw new ArgumentOutOfRangeException(nameof(TextureVariation));
        if (EquipableSlots < 0)
            throw new ArgumentOutOfRangeException(nameof(EquipableSlots));
        if (ModelType < 0)
            throw new ArgumentOutOfRangeException(nameof(ModelType));
        return this;
    }
}

public sealed record KotorItemStack(
    KotorItemDefinition Item,
    int Quantity,
    bool Droppable,
    bool Infinite)
{
    public KotorItemStack Validate()
    {
        Item.Validate();
        if (Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Quantity));
        if (Infinite)
            throw new NotSupportedException(
                $"Infinite container item stacks are not yet supported: {Item.Resref}");
        return this;
    }
}

public sealed record KotorPlaceableDefinition(
    string InstanceId,
    string Tag,
    string? OnInventoryScript,
    IReadOnlyList<KotorItemStack>? Inventory = null);

public sealed record KotorGameplaySnapshot(
    int PlayerExperience,
    IReadOnlyDictionary<string, bool> DoorStates,
    IReadOnlyDictionary<string, bool> PlaceableStates,
    IReadOnlyDictionary<string, int> PlayerInventory);

public abstract record KotorGameplayEvent;

public sealed record KotorDoorStateChanged(
    KotorDoorDefinition Door,
    bool Open) : KotorGameplayEvent;

public sealed record KotorPlaceableOpened(
    KotorPlaceableDefinition Placeable) : KotorGameplayEvent;

public sealed record KotorPlaceableAlreadyOpened(
    KotorPlaceableDefinition Placeable) : KotorGameplayEvent;

public sealed record KotorItemsTransferred(
    KotorPlaceableDefinition Placeable,
    IReadOnlyList<KotorItemStack> Items) : KotorGameplayEvent;

public sealed record KotorExperienceAwarded(
    KotorScriptContract Contract,
    int Before,
    int Awarded,
    int After) : KotorGameplayEvent;

public sealed record KotorScriptExecuted(KotorScriptContract Contract) : KotorGameplayEvent;

public sealed record KotorScriptSkipped(
    KotorScriptContract Contract,
    int ActualPlayerExperience) : KotorGameplayEvent;

public sealed record KotorScriptUnsupported(string Resref) : KotorGameplayEvent;

public sealed record KotorGameplayTransition(
    KotorGameplaySnapshot Before,
    KotorGameplaySnapshot After,
    IReadOnlyList<KotorGameplayEvent> Events);

public sealed class KotorGameplaySimulation
{
    private readonly Dictionary<string, KotorScriptContract> scripts;
    private readonly Dictionary<string, KotorDoorDefinition> doors;
    private readonly IReadOnlyList<KotorDoorDefinition> doorOrder;
    private readonly Dictionary<string, KotorPlaceableDefinition> placeables;
    private readonly Dictionary<string, bool> doorStates;
    private readonly Dictionary<string, bool> placeableStates;
    private readonly Dictionary<string, int> playerInventory =
        new(StringComparer.OrdinalIgnoreCase);
    private int playerExperience;

    public KotorGameplaySimulation(
        IEnumerable<KotorScriptContract> scripts,
        IEnumerable<KotorDoorDefinition> doors,
        IEnumerable<KotorPlaceableDefinition> placeables,
        int initialPlayerExperience = 0)
    {
        if (initialPlayerExperience < 0)
            throw new ArgumentOutOfRangeException(nameof(initialPlayerExperience));
        this.scripts = UniqueByKey(scripts.Select(contract => contract.Validate()),
            contract => contract.Resref, "script contract");
        doorOrder = doors.ToArray();
        this.doors = UniqueByKey(doorOrder,
            definition => RequireInstanceId(definition.InstanceId, "door"), "door instance");
        var validatedPlaceables = placeables.Select(definition => definition with
        {
            Inventory = (definition.Inventory ?? []).Select(stack => stack.Validate()).ToArray()
        });
        this.placeables = UniqueByKey(validatedPlaceables,
            definition => RequireInstanceId(definition.InstanceId, "placeable"),
            "placeable instance");
        doorStates = this.doors.Keys.ToDictionary(instanceId => instanceId, _ => false,
            StringComparer.OrdinalIgnoreCase);
        placeableStates = this.placeables.Keys.ToDictionary(instanceId => instanceId, _ => false,
            StringComparer.OrdinalIgnoreCase);
        playerExperience = initialPlayerExperience;
    }

    public KotorGameplaySnapshot CaptureSnapshot() => new(
        playerExperience,
        ReadOnlyCopy(doorStates),
        ReadOnlyCopy(placeableStates),
        ReadOnlyCopy(playerInventory));

    public bool IsDoorOpen(string instanceId) =>
        GetState(doorStates, instanceId, "door instance");

    public bool IsPlaceableOpened(string instanceId) =>
        GetState(placeableStates, instanceId, "placeable instance");

    public KotorGameplayTransition ToggleDoor(string instanceId)
    {
        var before = CaptureSnapshot();
        var events = new List<KotorGameplayEvent>();
        var definition = GetDefinition(doors, instanceId, "door instance");
        var open = !doorStates[definition.InstanceId];
        doorStates[definition.InstanceId] = open;
        events.Add(new KotorDoorStateChanged(definition, open));
        if (open)
            ExecuteScriptCore(definition.OnOpenScript, events,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return Complete(before, events);
    }

    public KotorGameplayTransition UsePlaceable(string instanceId)
    {
        var before = CaptureSnapshot();
        var events = new List<KotorGameplayEvent>();
        var definition = GetDefinition(placeables, instanceId, "placeable instance");
        if (placeableStates[definition.InstanceId])
        {
            events.Add(new KotorPlaceableAlreadyOpened(definition));
            return Complete(before, events);
        }

        placeableStates[definition.InstanceId] = true;
        events.Add(new KotorPlaceableOpened(definition));
        var contents = definition.Inventory ?? [];
        if (contents.Count > 0)
        {
            foreach (var stack in contents)
            {
                playerInventory.TryGetValue(stack.Item.Resref, out var current);
                playerInventory[stack.Item.Resref] = checked(current + stack.Quantity);
            }
            events.Add(new KotorItemsTransferred(definition, contents));
        }
        ExecuteScriptCore(definition.OnInventoryScript, events,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return Complete(before, events);
    }

    public KotorGameplayTransition ExecuteScript(string? resref)
    {
        var before = CaptureSnapshot();
        var events = new List<KotorGameplayEvent>();
        ExecuteScriptCore(resref, events, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return Complete(before, events);
    }

    private void ExecuteScriptCore(
        string? resref,
        List<KotorGameplayEvent> events,
        HashSet<string> executionStack)
    {
        if (string.IsNullOrWhiteSpace(resref)) return;
        if (!scripts.TryGetValue(resref, out var contract))
        {
            events.Add(new KotorScriptUnsupported(resref));
            return;
        }
        if (!executionStack.Add(contract.Resref))
            throw new InvalidOperationException($"Script-contract cycle detected at {contract.Resref}");

        try
        {
            switch (contract.Kind)
            {
                case KotorScriptContractKind.PlotExperienceIfPlayerExperience:
                    ExecutePlotExperience(contract, events);
                    break;
                case KotorScriptContractKind.DialogueOpenDoor:
                    ExecuteDialogueDoor(contract, events, executionStack);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported script-contract kind {contract.Kind} for {contract.Resref}");
            }
        }
        finally
        {
            executionStack.Remove(contract.Resref);
        }
    }

    private void ExecutePlotExperience(
        KotorScriptContract contract,
        List<KotorGameplayEvent> events)
    {
        var required = contract.RequiredPlayerExperience!.Value;
        if (playerExperience != required)
        {
            events.Add(new KotorScriptSkipped(contract, playerExperience));
            return;
        }

        var before = playerExperience;
        var awarded = contract.AwardedExperience!.Value;
        playerExperience = checked(playerExperience + awarded);
        events.Add(new KotorExperienceAwarded(
            contract, before, awarded, playerExperience));
    }

    private void ExecuteDialogueDoor(
        KotorScriptContract contract,
        List<KotorGameplayEvent> events,
        HashSet<string> executionStack)
    {
        var definition = doorOrder.FirstOrDefault(candidate =>
            candidate.Tag.Equals(contract.DoorTag, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException(
                $"Unknown KOTOR door tag: {contract.DoorTag}");
        if (!doorStates[definition.InstanceId])
        {
            doorStates[definition.InstanceId] = true;
            events.Add(new KotorDoorStateChanged(definition, true));
            ExecuteScriptCore(definition.OnOpenScript, events, executionStack);
        }
        events.Add(new KotorScriptExecuted(contract));
    }

    private KotorGameplayTransition Complete(
        KotorGameplaySnapshot before,
        List<KotorGameplayEvent> events) =>
        new(before, CaptureSnapshot(), events.ToArray());

    private static Dictionary<string, T> UniqueByKey<T>(
        IEnumerable<T> values,
        Func<T, string> keySelector,
        string kind)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var key = keySelector(value);
            if (!result.TryAdd(key, value))
                throw new ArgumentException($"Duplicate KOTOR {kind}: {key}");
        }
        return result;
    }

    private static string RequireInstanceId(string instanceId, string kind) =>
        !string.IsNullOrWhiteSpace(instanceId)
            ? instanceId
            : throw new ArgumentException($"KOTOR {kind} ID cannot be empty");

    private static T GetDefinition<T>(
        IReadOnlyDictionary<string, T> definitions,
        string tag,
        string kind) =>
        definitions.TryGetValue(tag, out var definition)
            ? definition
            : throw new KeyNotFoundException($"Unknown KOTOR {kind}: {tag}");

    private static bool GetState(
        IReadOnlyDictionary<string, bool> states,
        string tag,
        string kind) =>
        states.TryGetValue(tag, out var value)
            ? value
            : throw new KeyNotFoundException($"Unknown KOTOR {kind}: {tag}");

    private static IReadOnlyDictionary<string, T> ReadOnlyCopy<T>(
        IReadOnlyDictionary<string, T> source)
    {
        var copy = new SortedDictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
            copy.Add(pair.Key, pair.Value);
        return new ReadOnlyDictionary<string, T>(copy);
    }
}
