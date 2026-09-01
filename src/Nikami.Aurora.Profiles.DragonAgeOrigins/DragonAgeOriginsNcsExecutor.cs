namespace Nikami.Aurora.Profiles.DragonAgeOrigins;

public sealed record DragonAgeNcsValue(byte Type, object Value);

public sealed record DragonAgeNcsActionResult(
    bool Succeeded,
    string Error,
    DragonAgeNcsValue? ReturnValue = null)
{
    public static DragonAgeNcsActionResult Complete(DragonAgeNcsValue? value = null) =>
        new(true, string.Empty, value);
    public static DragonAgeNcsActionResult Unsupported(string error) =>
        new(false, error);
}

public sealed record DragonAgeNcsExecutionResult(
    bool Succeeded,
    string Error,
    int Steps,
    IReadOnlyList<int> InvokedActions,
    DragonAgeNcsValue? ReturnValue = null)
{
    public static DragonAgeNcsExecutionResult Fail(string error, int steps,
        IReadOnlyList<int> actions) => new(false, error, steps, actions);
}

/// <summary>
/// Executes the game-neutral NCS stack/control-flow instruction set. Engine
/// actions cross a narrow callback boundary and fail closed when unsupported.
/// </summary>
public static class DragonAgeOriginsNcsExecutor
{
    public static DragonAgeNcsExecutionResult Execute(ReadOnlySpan<byte> source,
        Func<int, IReadOnlyList<DragonAgeNcsValue>, DragonAgeNcsActionResult> action,
        int maximumSteps = 100_000, int selfHandle = 0)
    {
        if (action is null) throw new ArgumentNullException(nameof(action));
        if (maximumSteps <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSteps));
        var decoded = DragonAgeOriginsNcsDecoder.Decode(source);
        if (!decoded.Succeeded)
            return DragonAgeNcsExecutionResult.Fail(decoded.Error, 0, []);
        var instructions = decoded.Instructions;
        var addresses = instructions.Select((instruction, index) => (instruction.Address, index))
            .ToDictionary(value => value.Address, value => value.index);
        var stack = new List<DragonAgeNcsValue>();
        var calls = new Stack<int>();
        var savedBasePointers = new Stack<int>();
        var basePointer = 0;
        var invokedActions = new List<int>();
        var pc = 0;
        var steps = 0;

        while (pc >= 0 && pc < instructions.Count && steps < maximumSteps)
        {
            steps++;
            var instruction = instructions[pc];
            var args = instruction.Arguments;
            string? error = null;
            switch (instruction.Opcode)
            {
                case 0x01:
                    error = CopyStack(stack, Integer(args[0]), Integer(args[1]), push: false);
                    break;
                case 0x02:
                    stack.Add(DefaultValue(instruction.ValueType));
                    break;
                case 0x03:
                    error = CopyStack(stack, Integer(args[0]), Integer(args[1]), push: true);
                    break;
                case 0x04:
                    var constant = args[0];
                    if (instruction.ValueType == 6)
                    {
                        var rawObject = Integer(constant);
                        if (selfHandle > 0 && rawObject == 0) constant = selfHandle;
                        else if (rawObject is 1 or -1 or 0x7f000000) constant = 0;
                    }
                    stack.Add(new DragonAgeNcsValue(instruction.ValueType, constant));
                    break;
                case 0x05:
                {
                    var actionId = Integer(args[0]);
                    var count = Integer(args[1]);
                    if (count < 0 || stack.Count < count)
                    {
                        error = "action-stack-underflow";
                        break;
                    }
                    var parameters = new List<DragonAgeNcsValue>(count);
                    for (var index = 0; index < count; index++)
                    {
                        parameters.Add(stack[^1]);
                        stack.RemoveAt(stack.Count - 1);
                    }
                    invokedActions.Add(actionId);
                    var result = action(actionId, parameters);
                    if (!result.Succeeded)
                    {
                        error = $"action-{actionId}:{result.Error}";
                        break;
                    }
                    if (result.ReturnValue is not null) stack.Add(result.ReturnValue);
                    break;
                }
                case >= 0x06 and <= 0x18:
                    error = Binary(stack, instruction.Opcode);
                    break;
                case 0x19:
                case 0x1a:
                case 0x22:
                    error = Unary(stack, instruction.Opcode);
                    break;
                case 0x1b:
                    error = MoveStack(stack, Integer(args[0]));
                    break;
                case 0x1d:
                    if (!TryTarget(addresses, instruction.Address, Integer(args[0]), out pc))
                        error = "invalid-jump";
                    else continue;
                    break;
                case 0x1e:
                    if (!TryTarget(addresses, instruction.Address, Integer(args[0]), out var callTarget))
                    {
                        error = "invalid-call";
                        break;
                    }
                    calls.Push(pc + 1);
                    pc = callTarget;
                    continue;
                case 0x1f:
                case 0x25:
                    if (stack.Count == 0)
                    {
                        error = "branch-stack-underflow";
                        break;
                    }
                    var condition = Truthy(stack[^1].Value);
                    stack.RemoveAt(stack.Count - 1);
                    var take = instruction.Opcode == 0x1f ? !condition : condition;
                    if (take)
                    {
                        if (!TryTarget(addresses, instruction.Address, Integer(args[0]), out pc))
                            error = "invalid-branch";
                        else continue;
                    }
                    break;
                case 0x20:
                    if (calls.Count > 0)
                    {
                        pc = calls.Pop();
                        continue;
                    }
                    return new DragonAgeNcsExecutionResult(true, string.Empty, steps,
                        invokedActions, stack.Count > 0 ? stack[^1] : null);
                case 0x21:
                    error = Destruct(stack, Integer(args[0]), Integer(args[1]), Integer(args[2]));
                    break;
                case 0x23:
                case 0x24:
                    error = MutateInteger(stack, StackIndex(stack, Integer(args[0])),
                        instruction.Opcode == 0x23 ? -1 : 1);
                    break;
                case 0x26:
                    error = CopyBaseStack(stack, basePointer, Integer(args[0]), Integer(args[1]), false);
                    break;
                case 0x27:
                    error = CopyBaseStack(stack, basePointer, Integer(args[0]), Integer(args[1]), true);
                    break;
                case 0x28:
                case 0x29:
                    error = MutateInteger(stack, BaseStackIndex(stack, basePointer, Integer(args[0])),
                        instruction.Opcode == 0x28 ? -1 : 1);
                    break;
                case 0x2a:
                    savedBasePointers.Push(basePointer);
                    basePointer = stack.Count * 4;
                    stack.Add(new DragonAgeNcsValue(3, savedBasePointers.Peek()));
                    break;
                case 0x2b:
                    if (savedBasePointers.Count == 0 || stack.Count == 0)
                        error = "base-pointer-underflow";
                    else
                    {
                        stack.RemoveAt(stack.Count - 1);
                        basePointer = savedBasePointers.Pop();
                    }
                    break;
                case 0x2d:
                    break;
                default:
                    error = $"unsupported-opcode:0x{instruction.Opcode:x2}";
                    break;
            }
            if (error is not null)
                return DragonAgeNcsExecutionResult.Fail(error, steps, invokedActions);
            pc++;
        }
        return DragonAgeNcsExecutionResult.Fail(
            steps >= maximumSteps ? "step-limit" : "pc-out-of-range", steps, invokedActions);
    }

    private static bool TryTarget(IReadOnlyDictionary<int, int> addresses, int address,
        int delta, out int target)
    {
        var destination = (long)address + delta;
        if (destination is < int.MinValue or > int.MaxValue)
        {
            target = -1;
            return false;
        }
        return addresses.TryGetValue((int)destination, out target);
    }

    private static string? CopyStack(List<DragonAgeNcsValue> stack, int offset, int size, bool push)
    {
        if (size <= 0 || size % 4 != 0) return "unsupported-stack-copy-size";
        var start = StackIndex(stack, offset);
        var count = size / 4;
        if (start < 0 || start + count > stack.Count) return "stack-copy-out-of-range";
        var copied = stack.GetRange(start, count);
        if (push) stack.AddRange(copied);
        else
        {
            if (count > stack.Count) return "stack-copy-source-out-of-range";
            var source = stack.GetRange(stack.Count - count, count);
            for (var index = 0; index < count; index++) stack[start + index] = source[index];
        }
        return null;
    }

    private static string? CopyBaseStack(List<DragonAgeNcsValue> stack, int basePointer,
        int offset, int size, bool push)
    {
        if (size <= 0 || size % 4 != 0) return "unsupported-base-stack-copy-size";
        var start = BaseStackIndex(stack, basePointer, offset);
        var count = size / 4;
        if (start < 0 || start + count > stack.Count) return "base-stack-copy-out-of-range";
        var copied = stack.GetRange(start, count);
        if (push) stack.AddRange(copied);
        else
        {
            var source = stack.GetRange(stack.Count - count, count);
            for (var index = 0; index < count; index++) stack[start + index] = source[index];
        }
        return null;
    }

    private static string? MoveStack(List<DragonAgeNcsValue> stack, int delta)
    {
        if (delta > 0 || delta % 4 != 0) return "unsupported-positive-or-unaligned-movsp";
        var count = -delta / 4;
        if (count > stack.Count) return "movsp-underflow";
        stack.RemoveRange(stack.Count - count, count);
        return null;
    }

    private static string? Destruct(List<DragonAgeNcsValue> stack, int stackSize,
        int keepOffset, int keepSize)
    {
        if (stackSize < 0 || keepOffset < 0 || keepSize < 0 || stackSize % 4 != 0 ||
            keepOffset % 4 != 0 || keepSize % 4 != 0 || keepOffset + keepSize > stackSize)
            return "invalid-destruct-layout";
        var count = stackSize / 4;
        if (count > stack.Count) return "destruct-stack-underflow";
        var blockStart = stack.Count - count;
        var kept = stack.GetRange(blockStart + keepOffset / 4, keepSize / 4);
        stack.RemoveRange(blockStart, count);
        stack.AddRange(kept);
        return null;
    }

    private static string? MutateInteger(List<DragonAgeNcsValue> stack, int index, int delta)
    {
        if (index < 0) return "integer-stack-mutation-out-of-range";
        if (stack[index].Type != 3) return "integer-stack-mutation-type-mismatch";
        stack[index] = stack[index] with { Value = checked(Convert.ToInt32(stack[index].Value) + delta) };
        return null;
    }

    private static string? Unary(List<DragonAgeNcsValue> stack, byte opcode)
    {
        if (stack.Count == 0) return "stack-underflow";
        var cell = stack[^1];
        object value;
        try
        {
            var integer = Convert.ToInt32(cell.Value);
            value = opcode switch
            {
                0x19 => checked(-integer),
                0x1a => ~integer,
                _ => integer == 0 ? 1 : 0
            };
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
        {
            return "unary-type-mismatch";
        }
        stack[^1] = new DragonAgeNcsValue(3, value);
        return null;
    }

    private static string? Binary(List<DragonAgeNcsValue> stack, byte opcode)
    {
        if (stack.Count < 2) return "binary-stack-underflow";
        var right = stack[^1].Value;
        var left = stack[^2].Value;
        stack.RemoveRange(stack.Count - 2, 2);
        object value;
        try
        {
            value = opcode switch
            {
                0x06 => Truthy(left) && Truthy(right) ? 1 : 0,
                0x07 => Truthy(left) || Truthy(right) ? 1 : 0,
                0x08 => Convert.ToInt32(left) | Convert.ToInt32(right),
                0x09 => Convert.ToInt32(left) ^ Convert.ToInt32(right),
                0x0a => Convert.ToInt32(left) & Convert.ToInt32(right),
                0x0b => Equals(left, right) ? 1 : 0,
                0x0c => !Equals(left, right) ? 1 : 0,
                0x0d => Compare(left, right) >= 0 ? 1 : 0,
                0x0e => Compare(left, right) > 0 ? 1 : 0,
                0x0f => Compare(left, right) < 0 ? 1 : 0,
                0x10 => Compare(left, right) <= 0 ? 1 : 0,
                0x11 => Convert.ToInt32(left) << Convert.ToInt32(right),
                0x12 or 0x13 => Convert.ToInt32(left) >> Convert.ToInt32(right),
                0x14 when left is string || right is string => $"{left}{right}",
                0x14 when left is float || right is float => Convert.ToSingle(left) + Convert.ToSingle(right),
                0x14 => checked(Convert.ToInt32(left) + Convert.ToInt32(right)),
                0x15 when left is float || right is float => Convert.ToSingle(left) - Convert.ToSingle(right),
                0x15 => checked(Convert.ToInt32(left) - Convert.ToInt32(right)),
                0x16 when left is float || right is float => Convert.ToSingle(left) * Convert.ToSingle(right),
                0x16 => checked(Convert.ToInt32(left) * Convert.ToInt32(right)),
                0x17 when Convert.ToSingle(right) == 0 => throw new DivideByZeroException(),
                0x17 when left is float || right is float => Convert.ToSingle(left) / Convert.ToSingle(right),
                0x17 => Convert.ToInt32(left) / Convert.ToInt32(right),
                0x18 when Convert.ToInt32(right) == 0 => throw new DivideByZeroException(),
                0x18 => Convert.ToInt32(left) % Convert.ToInt32(right),
                _ => throw new InvalidOperationException()
            };
        }
        catch (DivideByZeroException) { return "division-by-zero"; }
        catch (Exception error) when (error is FormatException or InvalidCastException or
                                      OverflowException or InvalidOperationException)
        {
            return "binary-type-mismatch";
        }
        var type = value switch { string => (byte)5, float => (byte)4, _ => (byte)3 };
        stack.Add(new DragonAgeNcsValue(type, value));
        return null;
    }

    private static int Compare(object left, object right) =>
        left is float || right is float
            ? Convert.ToSingle(left).CompareTo(Convert.ToSingle(right))
            : Convert.ToInt32(left).CompareTo(Convert.ToInt32(right));
    private static bool Truthy(object value) => value switch
    {
        bool boolean => boolean,
        string text => text.Length > 0,
        float number => number != 0,
        _ => Convert.ToInt64(value) != 0
    };
    private static int Integer(object value) => Convert.ToInt32(value);
    private static int StackIndex(IReadOnlyCollection<DragonAgeNcsValue> stack, int offset) =>
        offset > 0 || offset % 4 != 0 ? -1 : stack.Count + offset / 4;
    private static int BaseStackIndex(IReadOnlyCollection<DragonAgeNcsValue> stack,
        int basePointer, int offset)
    {
        if (basePointer < 0 || basePointer % 4 != 0 || offset > -4 || offset % 4 != 0)
            return -1;
        var index = basePointer / 4 + offset / 4;
        return index >= 0 && index < stack.Count ? index : -1;
    }
    private static DragonAgeNcsValue DefaultValue(byte type) =>
        new(type, type is 5 or 96 ? string.Empty : 0);
}
