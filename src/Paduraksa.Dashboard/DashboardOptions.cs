namespace Paduraksa.Dashboard;

/// <summary>The dashboard's knobs. The composition root builds one; tests shorten the intervals.</summary>
public sealed class DashboardOptions
{
    /// <summary>The shortest password the identity services accept, for the setup form's hint and its browser-side check.</summary>
    public int MinPasswordLength { get; init; } = 12;

    /// <summary>Whether the admin app serves <c>/mcp</c> (<c>Paduraksa:Mcp:Enabled</c>): the tokens page shows how to connect, or how to turn it on.</summary>
    public bool McpEnabled { get; init; }

    /// <summary>
    /// How often an open circuit re-checks its session in the background: a revoked or expired session, a disabled user or a
    /// password change elsewhere ends the page within this interval. The background check does not count as use, so an unattended page
    /// still idles out.
    /// </summary>
    public TimeSpan SessionRecheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The least interval between re-checks made because the person did something on the page (a click, a navigation); those count as
    /// use. Every service call re-checks as well, whatever this says.
    /// </summary>
    public TimeSpan ActivityRecheckInterval { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a circuit whose session ended may still talk to the server (time for the browser to follow the redirect to the sign-in page) before it is torn down.</summary>
    public TimeSpan EndedCircuitGrace { get; init; } = TimeSpan.FromSeconds(10);
}
