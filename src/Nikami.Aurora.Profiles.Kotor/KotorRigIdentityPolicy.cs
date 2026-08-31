namespace Nikami.Aurora.Profiles.Kotor;

/// <summary>
/// Classifies source-authored Odyssey humanoid mesh identities without tying
/// the runtime to one player body variant's spelling convention.
/// </summary>
public static class KotorRigIdentityPolicy
{
    public static bool IsArmMeshName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.Contains("_LArm_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_RArm_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_armL_", StringComparison.OrdinalIgnoreCase) ||
               name.Contains("_armR_", StringComparison.OrdinalIgnoreCase);
    }
}
