namespace Nikami.Aurora.GodotRuntime.Domain.World;

public sealed record AreaPresentation(string DisplayName, int NorthQuarterTurns)
{
    public int NormalizedNorthQuarterTurns => ((NorthQuarterTurns % 4) + 4) % 4;
}

public sealed record AreaPresentationResult(
    bool Succeeded,
    AreaPresentation Presentation,
    string Error)
{
    public static AreaPresentationResult Complete(string displayName, int northQuarterTurns) =>
        new(true, new AreaPresentation(displayName, northQuarterTurns), string.Empty);

    public static AreaPresentationResult Failed(string error) =>
        new(false, new AreaPresentation(string.Empty, 0), error);
}
