using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nikami.Aurora.Profiles.Kotor;

public sealed record KotorRuntimeConfiguration(
    string Schema,
    KotorGameplayConfiguration Gameplay,
    KotorPresentationConfiguration Presentation,
    KotorAutomationConfiguration Automation,
    KotorComplexityConfiguration Complexity,
    string? SourceSha256 = null)
{
    public const string CurrentSchema = "nikami-aurora-kotor-runtime-config-v1";

    public static KotorRuntimeConfiguration Load(string path)
    {
        var payload = File.ReadAllBytes(path);
        var configuration = JsonSerializer.Deserialize<KotorRuntimeConfiguration>(
                payload,
                SerializerOptions())
            ?? throw new InvalidDataException("KOTOR runtime configuration is empty");
        return (configuration with
        {
            SourceSha256 = Convert.ToHexString(SHA256.HashData(payload))
        }).Validate(requireSourceHash: true);
    }

    public KotorRuntimeConfiguration Validate(bool requireSourceHash = false)
    {
        if (!string.Equals(Schema, CurrentSchema, StringComparison.Ordinal))
            throw new InvalidDataException($"Unsupported KOTOR runtime configuration: {Schema}");
        ArgumentNullException.ThrowIfNull(Gameplay);
        ArgumentNullException.ThrowIfNull(Presentation);
        ArgumentNullException.ThrowIfNull(Automation);
        ArgumentNullException.ThrowIfNull(Complexity);
        Gameplay.Validate();
        Presentation.Validate();
        Automation.Validate();
        Complexity.Validate();
        if (requireSourceHash &&
            (SourceSha256?.Length != 64 || !SourceSha256.All(Uri.IsHexDigit)))
            throw new InvalidDataException(
                "KOTOR runtime configuration has no valid source SHA-256");
        return this;
    }

    private static JsonSerializerOptions SerializerOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
}

public sealed record KotorGameplayConfiguration(
    int PlayerExperience,
    int PlayerCredits,
    KotorPartyMemberConfiguration PlayerPartyMember)
{
    internal void Validate()
    {
        if (PlayerExperience < 0)
            throw new InvalidDataException("Configured player experience cannot be negative");
        if (PlayerCredits < 0)
            throw new InvalidDataException("Configured player credits cannot be negative");
        ArgumentNullException.ThrowIfNull(PlayerPartyMember);
        PlayerPartyMember.Validate();
    }
}

public sealed record KotorPartyMemberConfiguration(
    string Id,
    string DisplayName,
    int CurrentVitality,
    int MaximumVitality,
    int Defense)
{
    internal void Validate()
    {
        _ = new KotorPartyMemberDefinition(
            Id,
            DisplayName,
            CurrentVitality,
            MaximumVitality,
            Defense,
            IsPlayer: true).Validate();
    }
}

public sealed record KotorPresentationConfiguration(
    int FallbackFontSize,
    int DescriptionFontSize,
    float ModalDimOpacity,
    KotorColorConfiguration FallbackTextColor,
    KotorColorConfiguration EmphasisColor,
    KotorColorConfiguration SelectedTint,
    KotorLoadingPresentationConfiguration Loading,
    KotorHudPresentationConfiguration Hud,
    KotorInventoryPresentationConfiguration Inventory,
    KotorEquipmentPresentationConfiguration Equipment)
{
    internal void Validate()
    {
        if (FallbackFontSize <= 0 || DescriptionFontSize <= 0)
            throw new InvalidDataException("Configured KOTOR UI font sizes must be positive");
        if (!float.IsFinite(ModalDimOpacity) || ModalDimOpacity is < 0 or > 1)
            throw new InvalidDataException("Configured KOTOR modal opacity must be in [0, 1]");
        ArgumentNullException.ThrowIfNull(FallbackTextColor);
        ArgumentNullException.ThrowIfNull(EmphasisColor);
        ArgumentNullException.ThrowIfNull(SelectedTint);
        ArgumentNullException.ThrowIfNull(Loading);
        ArgumentNullException.ThrowIfNull(Hud);
        ArgumentNullException.ThrowIfNull(Inventory);
        ArgumentNullException.ThrowIfNull(Equipment);
        FallbackTextColor.Validate(nameof(FallbackTextColor));
        EmphasisColor.Validate(nameof(EmphasisColor));
        SelectedTint.Validate(nameof(SelectedTint));
        Loading.Validate();
        Hud.Validate();
        Inventory.Validate();
        Equipment.Validate();
    }
}

public sealed record KotorLoadingPresentationConfiguration(
    float InitialProgress,
    float RoomLoadingStart,
    float RoomLoadingSpan,
    float CompleteProgress,
    float MusicVolumeDb)
{
    internal void Validate()
    {
        var normalized = new[]
        {
            InitialProgress,
            RoomLoadingStart,
            RoomLoadingSpan,
            CompleteProgress
        };
        if (normalized.Any(value => !float.IsFinite(value) || value is < 0 or > 1) ||
            InitialProgress > RoomLoadingStart ||
            RoomLoadingStart + RoomLoadingSpan > CompleteProgress ||
            CompleteProgress <= 0 || !float.IsFinite(MusicVolumeDb))
            throw new InvalidDataException("Configured KOTOR loading presentation is invalid");
    }
}

public sealed record KotorUiInsetsConfiguration(
    int Left,
    int Top,
    int Right,
    int Bottom)
{
    internal void Validate(string name)
    {
        if (Left < 0 || Top < 0 || Right < 0 || Bottom < 0)
            throw new InvalidDataException($"Configured KOTOR UI insets {name} are invalid");
    }
}

public sealed record KotorHudPresentationConfiguration(
    KotorUiInsetsConfiguration MinimapInset)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(MinimapInset);
        MinimapInset.Validate("hud.minimapInset");
    }
}

public sealed record KotorColorConfiguration(float Red, float Green, float Blue)
{
    internal void Validate(string name)
    {
        if (!IsChannel(Red) || !IsChannel(Green) || !IsChannel(Blue))
            throw new InvalidDataException($"Configured KOTOR UI color {name} must be in [0, 1]");
    }

    private static bool IsChannel(float value) =>
        float.IsFinite(value) && value is >= 0 and <= 1;
}

public sealed record KotorUiBoxConfiguration(int Left, int Top, int Width, int Height)
{
    internal void Validate(string name)
    {
        if (Left < 0 || Top < 0 || Width <= 0 || Height <= 0)
            throw new InvalidDataException($"Configured KOTOR UI box {name} is invalid");
    }

    public void ValidateWithin(int outerWidth, int outerHeight, string name)
    {
        Validate(name);
        if ((long)Left + Width > outerWidth || (long)Top + Height > outerHeight)
            throw new InvalidDataException(
                $"Configured KOTOR UI box {name} escapes its source prototype");
    }
}

public sealed record KotorInventoryRowConfiguration(
    KotorUiBoxConfiguration Icon,
    KotorUiBoxConfiguration Name,
    KotorUiBoxConfiguration Quantity)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Icon);
        ArgumentNullException.ThrowIfNull(Name);
        ArgumentNullException.ThrowIfNull(Quantity);
        Icon.Validate("inventory.row.icon");
        Name.Validate("inventory.row.name");
        Quantity.Validate("inventory.row.quantity");
    }

    public void ValidateWithin(int width, int height)
    {
        Icon.ValidateWithin(width, height, "inventory.row.icon");
        Name.ValidateWithin(width, height, "inventory.row.name");
        Quantity.ValidateWithin(width, height, "inventory.row.quantity");
    }
}

public sealed record KotorInventoryPresentationConfiguration(
    int DescriptionBottomInset,
    int ScrollThumbHorizontalInset,
    int OverflowAcceptanceRepeat,
    KotorInventoryRowConfiguration Row)
{
    internal void Validate()
    {
        if (DescriptionBottomInset < 0 || ScrollThumbHorizontalInset < 0)
            throw new InvalidDataException("Configured KOTOR inventory insets cannot be negative");
        if (OverflowAcceptanceRepeat < 2)
            throw new InvalidDataException(
                "Configured KOTOR inventory overflow repeat must exercise overflow");
        ArgumentNullException.ThrowIfNull(Row);
        Row.Validate();
    }
}

public sealed record KotorEquipmentRowConfiguration(
    KotorUiBoxConfiguration Icon,
    KotorUiBoxConfiguration Name)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Icon);
        ArgumentNullException.ThrowIfNull(Name);
        Icon.Validate("equipment.row.icon");
        Name.Validate("equipment.row.name");
    }

    public void ValidateWithin(int width, int height)
    {
        Icon.ValidateWithin(width, height, "equipment.row.icon");
        Name.ValidateWithin(width, height, "equipment.row.name");
    }
}

public sealed record KotorEquipmentPresentationConfiguration(
    int DescriptionBottomInset,
    int SlotIconInset,
    KotorEquipmentRowConfiguration Row)
{
    internal void Validate()
    {
        if (DescriptionBottomInset < 0 || SlotIconInset < 0)
            throw new InvalidDataException("Configured KOTOR equipment insets cannot be negative");
        ArgumentNullException.ThrowIfNull(Row);
        Row.Validate();
    }
}

public sealed record KotorAutomationConfiguration(
    int DoorFrame,
    int ChoiceFrame,
    int StateFrame,
    int CapturePreparationFrame,
    int MenuOpenFrame,
    int PrimaryFrame,
    int SecondaryFrame,
    int SceneReadyFrame,
    IReadOnlyList<int> EquipmentTransactionFrames)
{
    internal void Validate()
    {
        var milestones = new[]
        {
            DoorFrame,
            ChoiceFrame,
            StateFrame,
            CapturePreparationFrame,
            MenuOpenFrame,
            PrimaryFrame,
            SecondaryFrame,
            SceneReadyFrame
        };
        if (milestones.Any(frame => frame <= 0) ||
            milestones.Zip(milestones.Skip(1), (left, right) => left < right).Any(valid => !valid))
            throw new InvalidDataException(
                "Configured KOTOR automation milestones must be positive and increasing");
        if (EquipmentTransactionFrames is null || EquipmentTransactionFrames.Count != 8 ||
            EquipmentTransactionFrames[0] < MenuOpenFrame ||
            EquipmentTransactionFrames.Any(frame => frame <= 0) ||
            EquipmentTransactionFrames.Zip(
                    EquipmentTransactionFrames.Skip(1),
                    (left, right) => left < right)
                .Any(valid => !valid))
            throw new InvalidDataException(
                "Configured KOTOR equipment transaction frames are invalid");
    }
}

public sealed record KotorComplexityConfiguration(
    IReadOnlyList<int> InventoryProjectionSampleSizes,
    double MaximumExponent)
{
    internal void Validate()
    {
        if (InventoryProjectionSampleSizes is null || InventoryProjectionSampleSizes.Count < 3 ||
            InventoryProjectionSampleSizes.Any(size => size <= 0) ||
            InventoryProjectionSampleSizes.Zip(
                    InventoryProjectionSampleSizes.Skip(1),
                    (left, right) => left < right)
                .Any(valid => !valid))
            throw new InvalidDataException(
                "Configured inventory projection samples must be positive and increasing");
        if (!double.IsFinite(MaximumExponent) || MaximumExponent < 1)
            throw new InvalidDataException(
                "Configured inventory projection exponent must be at least one");
    }
}
