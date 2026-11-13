namespace Paduraksa.Mcp;

/// <summary>
/// What bounds one tool call (docs/design/admin-surfaces.md "MCP in code mode (M10)"). The boundary is the child process: the parent enforces these from outside and kills the child on any breach.
/// The <c>Engine*</c> values only turn the ordinary runaway into a clean error inside the child; they are not what the security rests on.
/// </summary>
public sealed record SandboxLimits
{
    public static SandboxLimits Default { get; } = new();

    /// <summary>Wall time of one run, not counting the time the parent spends answering the script's requests.</summary>
    public TimeSpan ScriptTime { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>CPU time the child may use for one run.</summary>
    public TimeSpan CpuTime { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Wall time of the whole call, requests included.</summary>
    public TimeSpan TotalTime { get; init; } = TimeSpan.FromSeconds(30);

    public int MaxRequests { get; init; } = 50;

    /// <summary>The returned value as JSON, in UTF-8 bytes; what follows is cut and the result says so.</summary>
    public int MaxOutputBytes { get; init; } = 64 * 1024;

    public int MaxCodeChars { get; init; } = 32 * 1024;

    public int MaxRequestBodyBytes { get; init; } = 256 * 1024;

    /// <summary>The API's answer is read up to this size; a larger one comes back as a note, not a body.</summary>
    public int MaxResponseBytes { get; init; } = 1024 * 1024;

    /// <summary>Resident memory of the child past which it is killed (sampled every <see cref="PollInterval"/>).</summary>
    public long ChildRssBytes { get; init; } = 384L * 1024 * 1024;

    /// <summary>The child's GC heap limit (<c>DOTNET_GCHeapHardLimit</c>): an allocation past it is an out-of-memory in the child, not growth in the parent's neighbourhood.</summary>
    public long ChildHeapBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>Total CPU seconds of a child's life (<c>RLIMIT_CPU</c>); the pool recycles it well before.</summary>
    public int ChildCpuLifetimeSeconds { get; init; } = 300;

    /// <summary>A child is replaced after this many runs.</summary>
    public int MaxRunsPerChild { get; init; } = 200;

    /// <summary>Children that exist at once, and so calls that run at once.</summary>
    public int MaxChildren { get; init; } = 4;

    /// <summary>How long a call waits for a child when all are busy.</summary>
    public TimeSpan AcquireTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>The child's startup: how long it may take to say it is ready.</summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Jint's own constraints (statements, memory, call depth, native stack). Off only in tests, to show that the boundary alone contains a script.</summary>
    public bool EngineConstraints { get; init; } = true;

    public int EngineMaxStatements { get; init; } = 300_000;

    public long EngineMemoryBytes { get; init; } = 64L * 1024 * 1024;

    public int EngineCallDepth { get; init; } = 200;
}
