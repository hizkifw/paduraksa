using System.Runtime.CompilerServices;

namespace Paduraksa.Fuzz;

/// <summary>A property a target found broken. It is thrown, never caught: to libFuzzer it is a crash, to the replay test a failure.</summary>
public sealed class PropertyViolationException(string message) : Exception(message)
{
    public PropertyViolationException()
        : this(string.Empty)
    {
    }

    public PropertyViolationException(string message, Exception inner)
        : this(message + ": " + inner.GetType().Name + ": " + inner.Message)
    {
    }
}

/// <summary>The properties every target states the same way.</summary>
internal static class Checks
{
    /// <summary>Fails the input when <paramref name="condition"/> is false.</summary>
    public static void That(bool condition, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new PropertyViolationException(expression ?? "a property");
        }
    }

    public static void That(bool condition, string message, [CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition)
        {
            throw new PropertyViolationException($"{message} ({expression})");
        }
    }

    /// <summary>The same verdict twice for the same bytes: <paramref name="verdict"/> is run twice and what it returns must be equal.</summary>
    public static T Deterministic<T>(Func<T> verdict, string what)
        where T : IEquatable<T>
    {
        var first = verdict();
        var second = verdict();
        That(first.Equals(second), $"{what}: the same bytes gave {first} and then {second}");
        return first;
    }

    /// <summary>
    /// 0 B where a path promises it: <paramref name="action"/> is run once to warm it (a static, a first resize), then measured twice on this thread.
    /// An allocation counts only when both measured runs make it, so a one-off of the runtime's is not one of the code's.
    /// </summary>
    public static void AllocatesNothing(Action action, string what)
    {
        action();
        var first = Measure(action);
        if (first == 0)
        {
            return;
        }

        var second = Measure(action);
        That(second == 0, $"{what} allocated {first} B and then {second} B for the same input");
    }

    private static long Measure(Action action)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
