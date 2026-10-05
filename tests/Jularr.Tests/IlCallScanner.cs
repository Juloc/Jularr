using System.Reflection;
using System.Reflection.Emit;

namespace Jularr.Tests;

/// <summary>
/// Finds, by reading IL, which methods a handler reaches: the handler, its async state machine and the methods of the same endpoint class it
/// calls (to a small depth). Used to prove an endpoint that returns file bytes is classified as streaming, instead of trusting a hand list.
/// </summary>
internal static class IlCallScanner
{
    private static readonly Dictionary<short, OpCode> OpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(code => code.Value);

    /// <summary>Every method called by <paramref name="handler"/> or, within the same top-level type, by the methods it reaches (at most <paramref name="depth"/> hops).</summary>
    public static IReadOnlyCollection<MethodBase> Reach(MethodInfo handler, int depth = 3)
    {
        var scope = TopLevel(handler.DeclaringType!);
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<(MethodBase Method, int Depth)>();
        queue.Enqueue((handler, 0));
        while (queue.Count > 0)
        {
            var (method, hops) = queue.Dequeue();
            if (!seen.Add(method))
            {
                continue;
            }

            var bodies = new List<MethodBase> { method };
            if (method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() is { } machine
                && machine.StateMachineType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public) is { } moveNext)
            {
                bodies.Add(moveNext);
                seen.Add(moveNext);
            }

            foreach (var body in bodies)
            {
                foreach (var called in Calls(body))
                {
                    seen.Add(called);
                    if (hops < depth && called.DeclaringType is { } type && TopLevel(type) == scope)
                    {
                        queue.Enqueue((called, hops + 1));
                    }
                }
            }
        }

        return seen;
    }

    private static Type TopLevel(Type type)
    {
        while (type.DeclaringType is { } outer)
        {
            type = outer;
        }

        return type;
    }

    private static IEnumerable<MethodBase> Calls(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            yield break;
        }

        var module = method.Module;
        var typeArguments = method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null;
        var position = 0;
        while (position < il.Length)
        {
            var value = (short)il[position++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            var code = OpCodes[value];
            var operand = code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, position),
                _ => 4
            };
            if (code.OperandType is OperandType.InlineMethod)
            {
                MethodBase? called = null;
                try
                {
                    called = module.ResolveMethod(BitConverter.ToInt32(il, position), typeArguments, method.IsGenericMethod ? method.GetGenericArguments() : null);
                }
                catch (ArgumentException)
                {
                }

                if (called is not null)
                {
                    yield return called;
                }
            }

            position += operand;
        }
    }
}
