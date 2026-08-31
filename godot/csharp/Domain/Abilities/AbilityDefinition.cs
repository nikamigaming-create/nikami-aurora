namespace Nikami.Aurora.GodotRuntime.Domain.Abilities;

public sealed record AbilityDefinition(
    int Id,
    string Label,
    int NameStringReference,
    int DescriptionStringReference,
    string Icon,
    int AbilityType,
    int GuiType,
    float Cost,
    int TargetType,
    float Range,
    int UseType,
    string Script,
    float Cooldown,
    IReadOnlyDictionary<string, object?> Provenance);
