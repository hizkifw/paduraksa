namespace Paduraksa.Core.Hosting;

/// <summary>
/// The connection item that marks a connection accepted by the admin app's internal listener (PLAN §3.1). It is set by a
/// connection middleware on that <c>ListenOptions</c> only, so nothing depends on a peer address or a port (which can collide
/// when the admin port is OS-assigned).
/// </summary>
public static class AdminInternalConnectionMarker
{
    /// <summary>The <c>ConnectionContext.Items</c> key. ASP.NET Core exposes the same dictionary as <c>IConnectionItemsFeature</c>.</summary>
    public static object Key { get; } = new();
}
