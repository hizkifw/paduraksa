namespace Paduraksa.Core.Model;

/// <summary>The surface a change or audited action came in through.</summary>
public enum Channel
{
    System = 1,

    Cli = 2,

    Dashboard = 3,

    /// <summary>The REST API (<c>/api/v1</c>).</summary>
    Api = 4,

    /// <summary>The MCP endpoint (<c>/mcp</c>).</summary>
    Mcp = 5,
}
