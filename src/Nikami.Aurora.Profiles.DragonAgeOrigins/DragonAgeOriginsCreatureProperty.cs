namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

/// <summary>
/// DAO script-action identities and property semantics owned by the profile.
/// The runtime consumes these neutral mutations without knowing DAO action ids.
/// </summary>
public static class DragonAgeOriginsCreatureProperty
{
    public const int GetAction = 738;
    public const int SetAction = 740;
    public const int UpdateAction = 741;
    public const int Experience = 19;
    public const int TotalValue = 1;
    public const int BaseValue = 2;
    public const int CurrentValue = 3;
    public const int ModifierValue = 4;

    public static bool TryApplyExperience(int action, int property, float value, int valueType,
        int currentExperience, out int updatedExperience, out string reason)
    {
        updatedExperience = currentExperience;
        if (property != Experience)
        {
            reason = "creature-property-not-experience";
            return false;
        }
        if (currentExperience < 0)
        {
            reason = "current-experience-invalid";
            return false;
        }

        if (!float.IsFinite(value) || value != MathF.Truncate(value) ||
            value < int.MinValue || value > int.MaxValue)
        {
            reason = "experience-value-invalid";
            return false;
        }
        if ((action == SetAction && valueType != BaseValue) ||
            (action == UpdateAction && valueType is not (CurrentValue or ModifierValue)))
        {
            reason = "experience-value-type-unsupported";
            return false;
        }

        var integerValue = (int)value;
        try
        {
            updatedExperience = action switch
            {
                SetAction => integerValue,
                UpdateAction => checked(currentExperience + integerValue),
                _ => -1,
            };
        }
        catch (OverflowException)
        {
            reason = "experience-overflow";
            updatedExperience = currentExperience;
            return false;
        }

        if (updatedExperience < 0)
        {
            reason = action is SetAction or UpdateAction
                ? "experience-negative"
                : "creature-property-action-unsupported";
            updatedExperience = currentExperience;
            return false;
        }

        reason = "ready";
        return true;
    }
}
