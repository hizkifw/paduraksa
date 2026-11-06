namespace Paduraksa.Server.Hosting;

/// <summary>The three hosts living in the one <c>paduraksa</c> process.</summary>
internal enum HostRole
{
    /// <summary>Non-web host owning process-wide background services. Starts first, stops last.</summary>
    Core,

    /// <summary>Public app (generic web host): :80 / :443, proxy pipeline only.</summary>
    Public,

    /// <summary>Admin <c>WebApplication</c>: :9443, dashboard, API, MCP, health.</summary>
    Admin,
}

internal static class HostRoleExtensions
{
    /// <summary>Lower-case name used in logs (<c>app=public</c>).</summary>
    public static string ToLogName(this HostRole role) => role switch
    {
        HostRole.Core => "core",
        HostRole.Public => "public",
        HostRole.Admin => "admin",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null),
    };
}
