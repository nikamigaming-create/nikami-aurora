using Nikami.Aurora.GodotRuntime.Application.Characters;
using Nikami.Aurora.GodotRuntime.Domain.Story;
using Nikami.Aurora.GodotRuntime.Infrastructure.Catalogs;
using Nikami.Aurora.Profiles.DragonAgeOrigins;

namespace Nikami.Aurora.GodotRuntime.Application.Story;

public sealed record DaoPlaceableScriptResult(
    bool Succeeded,
    string Error,
    int Steps,
    IReadOnlyList<int> Actions,
    int PlotWrites,
    int LocalWrites,
    int Experience);

public sealed class DaoPlaceableScriptExecutor(
    DaoScriptBytecodeProvider scripts,
    StoryState story,
    CharacterProgression progression)
{
    private sealed record ScriptEvent(int Type);
    private sealed record LocalWrite(int Handle, string Name, string Type, object Value);
    private sealed record PlotWrite(string Plot, int Flag, bool Value);

    private readonly List<LocalWrite> localWrites = [];
    private readonly List<PlotWrite> plotWrites = [];
    private int stagedExperience = progression.Experience;
    private ScriptEvent currentEvent = new(0);
    private int currentSelf;
    private int dispatchDepth;

    public DaoPlaceableScriptResult Execute(string scriptResRef, int selfHandle, int eventType)
    {
        localWrites.Clear();
        plotWrites.Clear();
        stagedExperience = progression.Experience;
        currentEvent = new ScriptEvent(eventType);
        currentSelf = selfHandle;
        dispatchDepth = 0;
        var result = ExecuteInternal(scriptResRef);
        if (!result.Succeeded)
            return new(false, result.Error, result.Steps, result.InvokedActions,
                0, 0, progression.Experience);
        if (localWrites.Any(write => story.ByHandle(write.Handle) is null))
            return new(false, "local-owner-absent", result.Steps, result.InvokedActions,
                0, 0, progression.Experience);
        var validatedExperience = stagedExperience;
        if (stagedExperience != progression.Experience &&
            !DragonAgeOriginsCreatureProperty.TryApplyExperience(
                DragonAgeOriginsCreatureProperty.SetAction,
                DragonAgeOriginsCreatureProperty.Experience, stagedExperience,
                DragonAgeOriginsCreatureProperty.BaseValue, progression.Experience,
                out validatedExperience, out var validationError))
            return new(false, validationError, result.Steps, result.InvokedActions,
                0, 0, progression.Experience);
        foreach (var write in plotWrites) story.SetPlotFlag(write.Plot, write.Flag, write.Value);
        foreach (var write in localWrites)
        {
            var committed = story.SetLocal(write.Handle, write.Name, write.Type, write.Value);
            if (!committed.Succeeded)
                return new(false, committed.Reason, result.Steps, result.InvokedActions,
                    plotWrites.Count, 0, progression.Experience);
        }
        if (stagedExperience != progression.Experience &&
            !progression.ApplyCreatureProperty(DragonAgeOriginsCreatureProperty.SetAction,
                DragonAgeOriginsCreatureProperty.Experience, validatedExperience,
                DragonAgeOriginsCreatureProperty.BaseValue, out var experienceError))
            return new(false, experienceError, result.Steps, result.InvokedActions,
                plotWrites.Count, localWrites.Count, progression.Experience);
        return new(true, string.Empty, result.Steps, result.InvokedActions,
            plotWrites.Count, localWrites.Count, progression.Experience);
    }

    private DragonAgeNcsExecutionResult ExecuteInternal(string scriptResRef)
    {
        if (dispatchDepth >= 8)
            return DragonAgeNcsExecutionResult.Fail("nested-dispatch-depth-exhausted", 0, []);
        var bytecode = scripts.Load(scriptResRef, out var error);
        if (bytecode is null) return DragonAgeNcsExecutionResult.Fail(error, 0, []);
        dispatchDepth++;
        var result = DragonAgeOriginsNcsExecutor.Execute(bytecode, InvokeAction,
            maximumSteps: 100_000, selfHandle: currentSelf);
        dispatchDepth--;
        return result;
    }

    private DragonAgeNcsActionResult InvokeAction(int action,
        IReadOnlyList<DragonAgeNcsValue> values)
    {
        try
        {
            switch (action)
            {
                case 108 when values.Count == 0:
                    return Return(16, currentEvent);
                case 109 when values.Count == 1 && values[0].Value is ScriptEvent:
                case 126 when values.Count == 1 && values[0].Value is ScriptEvent:
                    return Return(6, currentSelf);
                case 111 when values.Count == 1 && values[0].Value is ScriptEvent scriptEvent:
                    return Return(3, scriptEvent.Type);
                case 113 when values.Count == 2 && values[0].Value is ScriptEvent:
                    return Return(3, 0);
                case 115 when values.Count == 2 && values[0].Value is ScriptEvent:
                    return Return(4, 0f);
                case 117 when values.Count == 2 && values[0].Value is ScriptEvent:
                    return Return(6, 0);
                case 119 when values.Count == 2 && values[0].Value is ScriptEvent:
                case 735 when values.Count == 2 && values[0].Value is ScriptEvent:
                    return Return(5, string.Empty);
                case 63 when values.Count == 2:
                {
                    var handle = Int(values[0]);
                    var name = Text(values[1]);
                    var pending = localWrites.LastOrDefault(write => write.Handle == handle &&
                        write.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && write.Type == "int");
                    var value = pending?.Value ?? story.GetLocal(handle, name, "int") ?? 0;
                    return Return(3, Convert.ToInt32(value));
                }
                case 64 when values.Count == 3:
                    localWrites.Add(new LocalWrite(Int(values[0]), Text(values[1]), "int",
                        Int(values[2])));
                    return Complete();
                case 121 when values.Count == 2 && values[0].Value is ScriptEvent nestedEvent:
                {
                    var previousEvent = currentEvent;
                    currentEvent = nestedEvent;
                    var nested = ExecuteInternal(Text(values[1]));
                    currentEvent = previousEvent;
                    return nested.Succeeded ? Complete() : Unsupported("nested-script:" + nested.Error);
                }
                case 502 when values.Count == 0:
                    return Return(6, 1);
                case 83 when values.Count == 1:
                    return Return(6, 2);
                case 660 when values.Count == 4:
                {
                    var plot = Text(values[1]);
                    var flag = Int(values[2]);
                    var pending = plotWrites.LastOrDefault(write =>
                        write.Plot.Equals(plot, StringComparison.OrdinalIgnoreCase) && write.Flag == flag);
                    return Return(3, (pending?.Value ?? story.GetPlotFlag(plot, flag)) ? 1 : 0);
                }
                case 661 when values.Count == 5:
                    plotWrites.Add(new PlotWrite(Text(values[1]), Int(values[2]), Int(values[3]) != 0));
                    return Complete();
                case 665 when values.Count == 1:
                    return Return(3, 0);
                case 742 when values.Count == 1:
                    return Return(5, Text(values[0]));
                case 743 when values.Count == 2:
                    return Return(5, string.Empty);
                case 143 when values.Count == 3:
                case 175 when values.Count == 8:
                case 842 when values.Count == 1:
                    return Complete();
                case 149 when values.Count == 1:
                    return Return(5, Convert.ToString(values[0].Value,
                        System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
                case 48 when values.Count == 1:
                    return Return(4, Convert.ToSingle(values[0].Value));
                case 49 when values.Count == 1:
                    return Return(3, Convert.ToInt32(Convert.ToSingle(values[0].Value)));
                case 50 when values.Count == 1:
                    return Return(5, Int(values[0]).ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
                case 738 when values.Count == 3 && Int(values[1]) ==
                                                   DragonAgeOriginsCreatureProperty.Experience:
                    return Return(4, (float)stagedExperience);
                case 740 when values.Count == 4:
                case 741 when values.Count == 4:
                    if (!DragonAgeOriginsCreatureProperty.TryApplyExperience(action, Int(values[1]),
                            Convert.ToSingle(values[2].Value), Int(values[3]), stagedExperience,
                            out stagedExperience, out var reason)) return Unsupported(reason);
                    return Complete();
                default:
                    return Unsupported("unsupported-engine-action");
            }
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or
                                      OverflowException)
        {
            return Unsupported("action-value-invalid");
        }
    }

    private static int Int(DragonAgeNcsValue value) => Convert.ToInt32(value.Value);
    private static string Text(DragonAgeNcsValue value) =>
        Convert.ToString(value.Value, System.Globalization.CultureInfo.InvariantCulture)?
            .Trim().ToLowerInvariant() ?? string.Empty;
    private static DragonAgeNcsActionResult Complete() => DragonAgeNcsActionResult.Complete();
    private static DragonAgeNcsActionResult Return(byte type, object value) =>
        DragonAgeNcsActionResult.Complete(new DragonAgeNcsValue(type, value));
    private static DragonAgeNcsActionResult Unsupported(string reason) =>
        DragonAgeNcsActionResult.Unsupported(reason);
}
