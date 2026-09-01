namespace Nikami.Aurora.Profiles.Kotor;

public readonly record struct KotorCreatureEffectSchedule(
    double Length,
    IReadOnlyList<double> EventTimes);

public readonly record struct KotorCreatureAtlasPlayback(
    float Offset,
    float Cycles,
    bool Loop);

public static class KotorCreatureEffectPolicy
{
    public static int RequiredBurstPoolSize(
        IEnumerable<KotorCreatureEffectSchedule> schedules,
        double lifetime)
    {
        if (!double.IsFinite(lifetime) || lifetime <= 0)
            throw new InvalidDataException("Creature burst lifetime is invalid");
        var required = 1;
        foreach (var schedule in schedules)
        {
            if (!double.IsFinite(schedule.Length) || schedule.Length <= 0 ||
                schedule.EventTimes.Any(time => !double.IsFinite(time) ||
                    time < 0 || time > schedule.Length))
                throw new InvalidDataException("Creature effect schedule is invalid");
            if (schedule.EventTimes.Count == 0) continue;
            var cycles = Math.Max(2, (int)Math.Ceiling(
                lifetime / schedule.Length) + 2);
            var times = Enumerable.Range(0, cycles)
                .SelectMany(cycle => schedule.EventTimes.Select(time =>
                    time + cycle * schedule.Length))
                .OrderBy(time => time)
                .ToArray();
            var start = 0;
            for (var end = 0; end < times.Length; end++)
            {
                while (times[end] - times[start] >= lifetime)
                    start++;
                required = Math.Max(required, end - start + 1);
            }
        }
        return required;
    }

    public static KotorCreatureAtlasPlayback RequireAtlasPlayback(
        int columns,
        int rows,
        float frameStart,
        float frameEnd,
        float framesPerSecond,
        float lifetime,
        int loop)
    {
        var frameCount = checked(columns * rows);
        var fixedFrame = frameStart == frameEnd;
        var fullAtlas = frameStart == 0 && frameEnd == frameCount - 1;
        if (columns <= 0 || rows <= 0 || frameCount <= 0 ||
            loop is not (0 or 1) ||
            !float.IsFinite(frameStart) || !float.IsFinite(frameEnd) ||
            !float.IsFinite(framesPerSecond) || framesPerSecond < 0 ||
            !float.IsFinite(lifetime) || lifetime <= 0 ||
            frameStart != MathF.Truncate(frameStart) ||
            frameEnd != MathF.Truncate(frameEnd) ||
            frameStart < 0 || frameEnd >= frameCount ||
            !fixedFrame && !fullAtlas)
            throw new InvalidDataException(
                "Creature emitter atlas range is unsupported");
        return new KotorCreatureAtlasPlayback(
            frameStart / frameCount,
            fixedFrame ? 0.0f : framesPerSecond * lifetime / frameCount,
            loop != 0);
    }
}
