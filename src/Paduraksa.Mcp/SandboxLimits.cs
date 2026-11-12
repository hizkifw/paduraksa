namespace Paduraksa.Mcp;

/// <summary>The hard limits of one sandboxed call (PLAN §11.2). Every one is checked by the engine or by the guard that replaces a native it would otherwise not stop.</summary>
public sealed record SandboxLimits
{
    public static SandboxLimits Default { get; } = new();

    /// <summary>Time the interpreter itself may run. A pending request does not count: the wait is the API's, bounded by <see cref="TotalTime"/>.</summary>
    public TimeSpan ScriptTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wall-clock time of the whole call, requests included.</summary>
    public TimeSpan TotalTime { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxStatements { get; init; } = 300_000;

    /// <summary>Bytes the script may allocate in total (not live), measured from when its own code starts.</summary>
    public long MaxAllocatedBytes { get; init; } = 64L * 1024 * 1024;

    public int MaxCallDepth { get; init; } = 200;

    public uint MaxArraySize { get; init; } = 100_000;

    public int MaxRequests { get; init; } = 50;

    /// <summary>The returned value as JSON, in UTF-8 bytes; what follows is cut and the result says so.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    public int MaxCodeChars { get; init; } = 32 * 1024;

    /// <summary>The longest string a native may build in one step (<c>repeat</c>, <c>padStart</c>, <c>join</c>, <c>JSON.stringify</c>): the engine's own bound is a gigabyte.</summary>
    public int MaxStringChars { get; init; } = 4 * 1024 * 1024;

    /// <summary>The largest request body a script may send, as JSON.</summary>
    public int MaxRequestBodyBytes { get; init; } = 256 * 1024;

    /// <summary>The API's answer is read up to this size; a larger one comes back as a note, not a body.</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Stack of the thread the interpreter runs on. Native recursion over a script's data (a chain of bound functions, a long prototype chain) is as deep as
    /// the script could build it, and a stack overflow ends the process, so the stack is sized for the statement and array limits, not for the default 8 MiB.
    /// </summary>
    public int StackBytes { get; init; } = 256 * 1024 * 1024;

    /// <summary>Interpreters that run at once; one more is told to retry.</summary>
    public int MaxConcurrentRuns { get; init; } = 4;
}
