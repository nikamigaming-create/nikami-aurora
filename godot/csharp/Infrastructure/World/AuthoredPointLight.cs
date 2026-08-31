using Godot;

namespace Nikami.Aurora.GodotRuntime.Infrastructure.World;

internal sealed partial class AuthoredPointLight : OmniLight3D
{
    internal float BaseEnergy { get; init; } = 1;
    internal float Variation { get; init; }
    internal float Period { get; init; }
    internal float PeriodDelta { get; init; }
    internal float Phase { get; init; }

    public override void _Process(double delta)
    {
        if (Variation <= 0 || Period <= 0) return;
        var time = Godot.Time.GetTicksMsec() * 0.001f;
        var primary = Mathf.Sin((time + Phase) * Mathf.Tau / Period);
        var secondaryPeriod = Math.Max(0.01f, Period + PeriodDelta);
        var secondary = Mathf.Sin((time * 1.371f + Phase * 0.43f) * Mathf.Tau / secondaryPeriod);
        LightEnergy = BaseEnergy * (1 + Variation * (primary * 0.65f + secondary * 0.35f));
    }
}
