using System.Collections.ObjectModel;
using System.Numerics;

namespace Nikami.Aurora.Profiles.Kotor;

public enum KotorScriptContractKind
{
    PlotExperienceIfPlayerExperience,
    DialogueOpenDoor,
    TriggerDialogue,
    GlobalNumberAdd,
    GlobalNumberSet,
    RevealMap
}

public sealed record KotorTriggerDialogueBehavior(
    string TriggerTemplate,
    string GlobalName,
    int GlobalValue,
    string ActorTag,
    int UserEvent,
    float InputLockSeconds,
    float DelaySeconds,
    string Conversation,
    int DialogueStarter,
    string ActorScriptSourceSha256,
    int ActorScriptInstructionCount,
    string ConditionScriptSourceSha256,
    int ConditionScriptInstructionCount)
{
    public KotorTriggerDialogueBehavior Validate()
    {
        if (string.IsNullOrWhiteSpace(TriggerTemplate))
            throw new ArgumentException("Trigger template cannot be empty", nameof(TriggerTemplate));
        if (string.IsNullOrWhiteSpace(GlobalName))
            throw new ArgumentException("Global name cannot be empty", nameof(GlobalName));
        if (string.IsNullOrWhiteSpace(ActorTag))
            throw new ArgumentException("Dialogue actor tag cannot be empty", nameof(ActorTag));
        if (UserEvent < 0)
            throw new ArgumentOutOfRangeException(nameof(UserEvent));
        if (!float.IsFinite(InputLockSeconds) || InputLockSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(InputLockSeconds));
        if (!float.IsFinite(DelaySeconds) || DelaySeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(DelaySeconds));
        if (string.IsNullOrWhiteSpace(Conversation))
            throw new ArgumentException("Conversation cannot be empty", nameof(Conversation));
        if (DialogueStarter < 0)
            throw new ArgumentOutOfRangeException(nameof(DialogueStarter));
        ValidateSource(ActorScriptSourceSha256, ActorScriptInstructionCount, "actor script");
        ValidateSource(ConditionScriptSourceSha256, ConditionScriptInstructionCount,
            "dialogue condition");
        return this;
    }

    private static void ValidateSource(string sha256, int instructionCount, string kind)
    {
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException(
                $"{kind} SHA-256 must contain 64 hexadecimal characters");
        if (instructionCount <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(instructionCount), $"{kind} instruction count must be positive");
    }
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
    bool? ResumeConversation = null,
    KotorTriggerDialogueBehavior? TriggerDialogue = null,
    string? GlobalName = null,
    int? GlobalValue = null)
{
    public string KindName => Kind switch
    {
        KotorScriptContractKind.PlotExperienceIfPlayerExperience => "plot-xp-if-player-xp",
        KotorScriptContractKind.DialogueOpenDoor => "dialogue-open-door",
        KotorScriptContractKind.TriggerDialogue => "trigger-dialogue",
        KotorScriptContractKind.GlobalNumberAdd => "global-number-add",
        KotorScriptContractKind.GlobalNumberSet => "global-number-set",
        KotorScriptContractKind.RevealMap => "reveal-map",
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
            case KotorScriptContractKind.TriggerDialogue:
                if (TriggerDialogue is null)
                    throw new ArgumentException(
                        "Trigger-dialogue behavior cannot be empty", nameof(TriggerDialogue));
                TriggerDialogue.Validate();
                break;
            case KotorScriptContractKind.GlobalNumberAdd:
            case KotorScriptContractKind.GlobalNumberSet:
                if (string.IsNullOrWhiteSpace(GlobalName))
                    throw new ArgumentException(
                        "Global-number script name cannot be empty", nameof(GlobalName));
                if (GlobalValue is null)
                    throw new ArgumentException(
                        "Global-number script value cannot be empty", nameof(GlobalValue));
                break;
            case KotorScriptContractKind.RevealMap:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Kind));
        }
        return this;
    }
}

public sealed record KotorDoorDefinition(string InstanceId, string Tag, string? OnOpenScript);

public sealed record KotorTriggerDefinition(
    string InstanceId,
    string Template,
    IReadOnlyList<Vector3> Polygon,
    string? OnEnterScript);

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

public enum KotorEquipmentSlot
{
    Head = 0x00001,
    Armor = 0x00002,
    Gauntlet = 0x00008,
    RightHand = 0x00010,
    LeftHand = 0x00020,
    RightArm = 0x00080,
    LeftArm = 0x00100,
    Implant = 0x00200,
    Belt = 0x00400
}

public readonly record struct KotorEquipRequest(string Resref, KotorEquipmentSlot Slot);

public sealed record KotorPlaceableDefinition(
    string InstanceId,
    string Tag,
    string? OnInventoryScript,
    IReadOnlyList<KotorItemStack>? Inventory = null);

public sealed record KotorGameplaySnapshot(
    int PlayerExperience,
    int PlayerCurrentVitality,
    int PlayerMaximumVitality,
    int PlayerDefense,
    int PlayerCredits,
    IReadOnlyDictionary<string, bool> DoorStates,
    IReadOnlyDictionary<string, bool> PlaceableStates,
    IReadOnlyDictionary<string, int> PlayerInventory,
    IReadOnlyDictionary<KotorEquipmentSlot, string> Equipment,
    IReadOnlyDictionary<string, bool> TriggerStates,
    IReadOnlyDictionary<string, int> GlobalNumbers,
    bool MapRevealed);

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

public sealed record KotorEquipmentChanged(
    KotorEquipmentSlot Slot,
    KotorItemDefinition Item,
    string? PreviousResref) : KotorGameplayEvent;

public sealed record KotorItemUsed(
    KotorItemDefinition Item,
    int QuantityBefore,
    int QuantityAfter,
    int VitalityBefore,
    int VitalityAfter) : KotorGameplayEvent;

public sealed record KotorTriggerEntered(KotorTriggerDefinition Trigger) : KotorGameplayEvent;

public sealed record KotorGlobalNumberChanged(
    string Name,
    int Before,
    int After) : KotorGameplayEvent;

public sealed record KotorMapRevealed(bool Before, bool After) : KotorGameplayEvent;

public sealed record KotorDialogueRequested(
    string ActorTag,
    string Conversation,
    int StarterIndex,
    int UserEvent,
    float InputLockSeconds,
    float DelaySeconds) : KotorGameplayEvent;

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
    private readonly Dictionary<string, KotorTriggerDefinition> triggers;
    private readonly Dictionary<string, KotorItemDefinition> itemDefinitions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> doorStates;
    private readonly Dictionary<string, bool> placeableStates;
    private readonly Dictionary<string, bool> triggerStates;
    private readonly Dictionary<string, int> playerInventory =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<KotorEquipmentSlot, string> equipment = [];
    private readonly Dictionary<string, int> globalNumbers =
        new(StringComparer.OrdinalIgnoreCase);
    private int playerExperience;
    private int playerCurrentVitality;
    private readonly int playerMaximumVitality;
    private readonly int playerDefense;
    private readonly int playerCredits;
    private bool mapRevealed;

    public KotorGameplaySimulation(
        IEnumerable<KotorScriptContract> scripts,
        IEnumerable<KotorDoorDefinition> doors,
        IEnumerable<KotorPlaceableDefinition> placeables,
        int initialPlayerExperience = 0,
        IEnumerable<KotorTriggerDefinition>? triggers = null,
        int initialCurrentVitality = 20,
        int initialMaximumVitality = 20,
        int initialDefense = 10,
        int initialCredits = 0)
    {
        if (initialPlayerExperience < 0)
            throw new ArgumentOutOfRangeException(nameof(initialPlayerExperience));
        if (initialMaximumVitality <= 0)
            throw new ArgumentOutOfRangeException(nameof(initialMaximumVitality));
        if (initialCurrentVitality < 0 ||
            initialCurrentVitality > initialMaximumVitality)
            throw new ArgumentOutOfRangeException(nameof(initialCurrentVitality));
        if (initialDefense < 0)
            throw new ArgumentOutOfRangeException(nameof(initialDefense));
        if (initialCredits < 0)
            throw new ArgumentOutOfRangeException(nameof(initialCredits));
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
        var validatedTriggers = (triggers ?? []).Select(ValidateTrigger);
        this.triggers = UniqueByKey(validatedTriggers,
            definition => RequireInstanceId(definition.InstanceId, "trigger"),
            "trigger instance");
        foreach (var definition in this.placeables.Values)
        {
            foreach (var stack in definition.Inventory ?? [])
            {
                if (itemDefinitions.TryGetValue(stack.Item.Resref, out var existing) &&
                    existing != stack.Item)
                    throw new ArgumentException(
                        $"Conflicting KOTOR item definition: {stack.Item.Resref}");
                itemDefinitions[stack.Item.Resref] = stack.Item;
            }
        }
        doorStates = this.doors.Keys.ToDictionary(instanceId => instanceId, _ => false,
            StringComparer.OrdinalIgnoreCase);
        placeableStates = this.placeables.Keys.ToDictionary(instanceId => instanceId, _ => false,
            StringComparer.OrdinalIgnoreCase);
        triggerStates = this.triggers.Keys.ToDictionary(instanceId => instanceId, _ => false,
            StringComparer.OrdinalIgnoreCase);
        playerExperience = initialPlayerExperience;
        playerCurrentVitality = initialCurrentVitality;
        playerMaximumVitality = initialMaximumVitality;
        playerDefense = initialDefense;
        playerCredits = initialCredits;
    }

    public KotorGameplaySnapshot CaptureSnapshot() => new(
        playerExperience,
        playerCurrentVitality,
        playerMaximumVitality,
        playerDefense,
        playerCredits,
        ReadOnlyCopy(doorStates),
        ReadOnlyCopy(placeableStates),
        ReadOnlyCopy(playerInventory),
        new ReadOnlyDictionary<KotorEquipmentSlot, string>(
            new Dictionary<KotorEquipmentSlot, string>(equipment)),
        ReadOnlyCopy(triggerStates),
        ReadOnlyCopy(globalNumbers),
        mapRevealed);

    public bool IsDoorOpen(string instanceId) =>
        GetState(doorStates, instanceId, "door instance");

    public bool IsPlaceableOpened(string instanceId) =>
        GetState(placeableStates, instanceId, "placeable instance");

    public KotorGameplayTransition UpdateTriggers(Vector3 previous, Vector3 current)
    {
        if (!IsFinite(previous) || !IsFinite(current))
            throw new ArgumentOutOfRangeException(nameof(current));
        var before = CaptureSnapshot();
        var events = new List<KotorGameplayEvent>();
        foreach (var trigger in triggers.Values)
        {
            if (triggerStates[trigger.InstanceId] ||
                !SegmentTouchesPolygon(previous, current, trigger.Polygon))
                continue;
            if (trigger.OnEnterScript is { Length: > 0 } resref &&
                scripts.TryGetValue(resref, out var contract) &&
                contract.TriggerDialogue is { } behavior &&
                !behavior.TriggerTemplate.Equals(
                    trigger.Template, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Trigger {trigger.InstanceId} does not match script {resref}");
            triggerStates[trigger.InstanceId] = true;
            events.Add(new KotorTriggerEntered(trigger));
            ExecuteScriptCore(trigger.OnEnterScript, events,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }
        return Complete(before, events);
    }

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

    public KotorGameplayTransition EquipItems(IEnumerable<KotorEquipRequest> requests)
    {
        var requested = requests.ToArray();
        if (requested.GroupBy(request => request.Slot).Any(group => group.Count() > 1))
            throw new ArgumentException("An equipment transaction cannot target one slot twice",
                nameof(requests));
        var before = CaptureSnapshot();
        var nextInventory = new Dictionary<string, int>(
            playerInventory, StringComparer.OrdinalIgnoreCase);
        var nextEquipment = new Dictionary<KotorEquipmentSlot, string>(equipment);
        var events = new List<KotorGameplayEvent>();

        foreach (var request in requested)
        {
            if (!Enum.IsDefined(request.Slot))
                throw new ArgumentOutOfRangeException(nameof(requests), request.Slot,
                    "Unknown KOTOR equipment slot");
            if (!itemDefinitions.TryGetValue(request.Resref, out var item))
                throw new KeyNotFoundException($"Unknown KOTOR item: {request.Resref}");
            if ((item.EquipableSlots & (int)request.Slot) == 0)
                throw new InvalidOperationException(
                    $"KOTOR item {item.Resref} cannot equip to {request.Slot}");
            if (nextEquipment.TryGetValue(request.Slot, out var equippedResref) &&
                equippedResref.Equals(item.Resref, StringComparison.OrdinalIgnoreCase))
                continue;

            if (equippedResref is not null)
            {
                nextInventory.TryGetValue(equippedResref, out var previousCount);
                nextInventory[equippedResref] = checked(previousCount + 1);
            }
            if (!nextInventory.TryGetValue(item.Resref, out var available) || available <= 0)
                throw new InvalidOperationException(
                    $"KOTOR item {item.Resref} is not available to equip");
            if (available == 1)
                nextInventory.Remove(item.Resref);
            else
                nextInventory[item.Resref] = available - 1;
            nextEquipment[request.Slot] = item.Resref;
            events.Add(new KotorEquipmentChanged(request.Slot, item, equippedResref));
        }

        playerInventory.Clear();
        foreach (var pair in nextInventory)
            playerInventory.Add(pair.Key, pair.Value);
        equipment.Clear();
        foreach (var pair in nextEquipment)
            equipment.Add(pair.Key, pair.Value);
        return Complete(before, events);
    }

    public KotorGameplayTransition UseMedpac(
        string resref,
        int wisdomModifier = 0,
        int treatInjurySkill = 0)
    {
        if (string.IsNullOrWhiteSpace(resref))
            throw new ArgumentException("Medpac resref cannot be empty", nameof(resref));
        if (treatInjurySkill < 0)
            throw new ArgumentOutOfRangeException(nameof(treatInjurySkill));
        var before = CaptureSnapshot();
        var events = new List<KotorGameplayEvent>();
        if (!itemDefinitions.TryGetValue(resref, out var item))
            throw new KeyNotFoundException($"Unknown KOTOR item: {resref}");
        if (item.BaseItem != 55)
            throw new InvalidOperationException(
                $"KOTOR item {item.Resref} is not medical equipment");
        if (!playerInventory.TryGetValue(item.Resref, out var quantity) || quantity <= 0)
            throw new InvalidOperationException(
                $"KOTOR item {item.Resref} is not available to use");
        if (playerCurrentVitality >= playerMaximumVitality)
            return Complete(before, events);

        var healing = Math.Max(1, checked(10 + wisdomModifier + treatInjurySkill));
        var vitalityBefore = playerCurrentVitality;
        playerCurrentVitality = Math.Min(
            playerMaximumVitality,
            checked(playerCurrentVitality + healing));
        if (quantity == 1)
            playerInventory.Remove(item.Resref);
        else
            playerInventory[item.Resref] = quantity - 1;
        events.Add(new KotorItemUsed(
            item,
            quantity,
            quantity - 1,
            vitalityBefore,
            playerCurrentVitality));
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
                case KotorScriptContractKind.TriggerDialogue:
                    ExecuteTriggerDialogue(contract, events);
                    break;
                case KotorScriptContractKind.GlobalNumberAdd:
                case KotorScriptContractKind.GlobalNumberSet:
                    ExecuteGlobalNumber(contract, events);
                    break;
                case KotorScriptContractKind.RevealMap:
                    ExecuteRevealMap(contract, events);
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

    private void ExecuteTriggerDialogue(
        KotorScriptContract contract,
        List<KotorGameplayEvent> events)
    {
        var behavior = contract.TriggerDialogue
            ?? throw new InvalidOperationException(
                $"Trigger-dialogue contract is incomplete: {contract.Resref}");
        globalNumbers.TryGetValue(behavior.GlobalName, out var before);
        globalNumbers[behavior.GlobalName] = behavior.GlobalValue;
        events.Add(new KotorGlobalNumberChanged(
            behavior.GlobalName, before, behavior.GlobalValue));
        events.Add(new KotorDialogueRequested(
            behavior.ActorTag,
            behavior.Conversation,
            behavior.DialogueStarter,
            behavior.UserEvent,
            behavior.InputLockSeconds,
            behavior.DelaySeconds));
        events.Add(new KotorScriptExecuted(contract));
    }

    private void ExecuteGlobalNumber(
        KotorScriptContract contract,
        List<KotorGameplayEvent> events)
    {
        var name = contract.GlobalName
            ?? throw new InvalidOperationException(
                $"Global-number contract is incomplete: {contract.Resref}");
        var value = contract.GlobalValue
            ?? throw new InvalidOperationException(
                $"Global-number contract is incomplete: {contract.Resref}");
        globalNumbers.TryGetValue(name, out var before);
        var after = contract.Kind == KotorScriptContractKind.GlobalNumberAdd
            ? checked(before + value)
            : value;
        globalNumbers[name] = after;
        events.Add(new KotorGlobalNumberChanged(name, before, after));
        events.Add(new KotorScriptExecuted(contract));
    }

    private void ExecuteRevealMap(
        KotorScriptContract contract,
        List<KotorGameplayEvent> events)
    {
        var before = mapRevealed;
        mapRevealed = true;
        events.Add(new KotorMapRevealed(before, mapRevealed));
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

    private static KotorTriggerDefinition ValidateTrigger(KotorTriggerDefinition trigger)
    {
        RequireInstanceId(trigger.InstanceId, "trigger");
        if (string.IsNullOrWhiteSpace(trigger.Template))
            throw new ArgumentException("KOTOR trigger template cannot be empty");
        if (trigger.Polygon.Count < 3 || trigger.Polygon.Any(point => !IsFinite(point)))
            throw new ArgumentException(
                $"KOTOR trigger polygon is invalid: {trigger.InstanceId}");
        return trigger with { Polygon = trigger.Polygon.ToArray() };
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool SegmentTouchesPolygon(
        Vector3 previous,
        Vector3 current,
        IReadOnlyList<Vector3> polygon)
    {
        var start = new Vector2(previous.X, previous.Y);
        var end = new Vector2(current.X, current.Y);
        if (PointInPolygon(start, polygon) || PointInPolygon(end, polygon))
            return true;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a3 = polygon[index];
            var b3 = polygon[(index + 1) % polygon.Count];
            if (SegmentsIntersect(start, end, new Vector2(a3.X, a3.Y), new Vector2(b3.X, b3.Y)))
                return true;
        }
        return false;
    }

    private static bool PointInPolygon(Vector2 point, IReadOnlyList<Vector3> polygon)
    {
        var inside = false;
        for (int current = 0, previous = polygon.Count - 1;
             current < polygon.Count;
             previous = current++)
        {
            var a = polygon[current];
            var b = polygon[previous];
            if ((a.Y > point.Y) == (b.Y > point.Y)) continue;
            var crossing = (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X;
            if (point.X < crossing)
                inside = !inside;
        }
        return inside;
    }

    private static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        const float epsilon = 0.00001f;
        var ab = b - a;
        var cd = d - c;
        var denominator = Cross(ab, cd);
        if (MathF.Abs(denominator) <= epsilon)
            return false;
        var offset = c - a;
        var first = Cross(offset, cd) / denominator;
        var second = Cross(offset, ab) / denominator;
        return first >= -epsilon && first <= 1 + epsilon &&
               second >= -epsilon && second <= 1 + epsilon;
    }

    private static float Cross(Vector2 left, Vector2 right) =>
        left.X * right.Y - left.Y * right.X;

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
