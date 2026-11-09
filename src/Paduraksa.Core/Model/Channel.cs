namespace Paduraksa.Core.Model;

/// <summary>
/// The surface a change or audited action came in through. Persisted by name
/// (<see cref="ModelNames"/>).
/// </summary>
public enum Channel
{
    /// <summary>Paduraksa itself (startup, schedulers).</summary>
    System = 1,

    /// <summary>The local CLI.</summary>
    Cli = 2,

    /// <summary>The Blazor dashboard.</summary>
    Dashboard = 3,

    /// <summary>The REST API (<c>/api/v1</c>).</summary>
    Api = 4,

    /// <summary>The MCP endpoint (<c>/mcp</c>).</summary>
    Mcp = 5,
}
