using System.Diagnostics;

using Jint;

namespace Paduraksa.Mcp;

/// <summary>A limit a script ran into; the message is what the model is told.</summary>
internal sealed class SandboxLimitException(string message) : Exception(message);

/// <summary>
/// Every per-call limit the interpreter checks between statements, as one constraint: statements, interpreter time, bytes allocated, and the call's
/// own deadline and cancellation. The engine's built-in constraints are not used because they restart whenever the host calls into the engine and
/// the time constraint counts a wait for a request as script time; this one is armed once and never reset, so a promise callback chain or a loop of
/// host calls spends one budget. It is a CLR exception, not a JavaScript one, so a script's <c>try</c> cannot swallow it.
/// </summary>
internal sealed class SandboxBudget(SandboxLimits limits, CancellationToken cancellation, CancellationToken caller) : Constraint
{
    private const int CheckMask = 0x7;

    private readonly Stopwatch _script = new();
    private readonly Stopwatch _total = new();
    private int _statements;
    private long _allocatedAtStart;
    private bool _armed;

    public override bool IsAmortizable => false;

    public int Statements => _statements;

    public TimeSpan ScriptTime => _script.Elapsed;

    public void Arm()
    {
        _allocatedAtStart = GC.GetAllocatedBytesForCurrentThread();
        _script.Restart();
        _total.Restart();
        _armed = true;
    }

    /// <summary>Stops the interpreter clock while the thread waits for the API.</summary>
    public void Pause() => _script.Stop();

    public void Resume() => _script.Start();

    /// <summary>
    /// Runs host work that is not the script's (turning an API answer into script values, bounded by the response limit) without charging its
    /// allocations to the script's memory budget.
    /// </summary>
    public T Untracked<T>(Func<T> work)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            return work();
        }
        finally
        {
            _allocatedAtStart += GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    /// <summary>A token that ends with the call's total time or the caller going away, for work the call waits on.</summary>
    public CancellationTokenSource Link() => CancellationTokenSource.CreateLinkedTokenSource(cancellation, caller);

    /// <summary>What ended a wait that was cancelled: the caller (propagated as a cancellation) or the call's own total time (a limit like the others).</summary>
    public Exception CancelledWait() => caller.IsCancellationRequested
        ? new OperationCanceledException(caller)
        : new SandboxLimitException($"The call took more than {limits.TotalTime.TotalSeconds:0.#} s in all, requests included.");

    public override void Reset()
    {
        // Armed once by the sandbox; the engine's resets (every call into it) must not hand out a new budget.
    }

    public override void Check()
    {
        if (!_armed)
        {
            return;
        }

        if (++_statements > limits.MaxStatements)
        {
            throw new SandboxLimitException($"The script ran more than {limits.MaxStatements:N0} statements. Do less per call: filter before you loop.");
        }

        // Memory is read every statement: a doubling string or array grows past the limit in a few statements, and a check every few dozen would
        // let it reach gigabytes first. The clocks cost more and are read every few statements.
        EnforceMemory();
        if ((_statements & CheckMask) != 0)
        {
            return;
        }

        Enforce();
    }

    /// <summary>The limits that cost a clock read; called every few statements and by the guards that replace a native.</summary>
    public void Enforce()
    {
        caller.ThrowIfCancellationRequested();

        if (_script.Elapsed > limits.ScriptTime)
        {
            throw new SandboxLimitException($"The script ran for more than {limits.ScriptTime.TotalSeconds:0.#} s of its own time. Do less per call.");
        }

        if (cancellation.IsCancellationRequested || _total.Elapsed > limits.TotalTime)
        {
            throw new SandboxLimitException($"The call took more than {limits.TotalTime.TotalSeconds:0.#} s in all, requests included.");
        }

        EnforceMemory();
    }

    private void EnforceMemory()
    {
        if (GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart > limits.MaxAllocatedBytes)
        {
            throw new SandboxLimitException($"The script allocated more than {limits.MaxAllocatedBytes / (1024 * 1024)} MiB. Build less data per call.");
        }
    }
}
