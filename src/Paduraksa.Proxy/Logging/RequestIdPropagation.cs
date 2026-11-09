namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Where a request's ID goes (PLAN §7.4): the proxy's own compiled copy of the config document's
/// <c>RequestIdPropagation</c> (the two enums must keep the same members in the same order; a test pins it).
/// </summary>
/// <remarks>
/// The ID always exists: the access log records it whatever this says, and error pages will show it (M8). This decides
/// only what leaves the proxy. Upstream: when it is not sent, an incoming <c>X-Request-Id</c> is removed rather than
/// passed through, so the setting means what it says even to a trusted peer. Response: when it is not sent, an
/// <c>X-Request-Id</c> the upstream put on its response is stripped; when it is, ours replaces it.
/// </remarks>
public enum RequestIdPropagation : byte
{
    /// <summary>The upstream and the client's response (the built-in default).</summary>
    UpstreamAndResponse = 0,

    /// <summary>The upstream only.</summary>
    UpstreamOnly = 1,

    /// <summary>The client's response only.</summary>
    ResponseOnly = 2,

    /// <summary>Neither: only the proxy's own logs mention it.</summary>
    Off = 3,
}

/// <summary>What <see cref="RequestIdPropagation"/> sends where.</summary>
public static class RequestIdPropagationExtensions
{
    /// <summary>Whether the ID goes to the upstream.</summary>
    public static bool SendsUpstream(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.UpstreamOnly;

    /// <summary>Whether the ID goes back to the client.</summary>
    public static bool SendsResponse(this RequestIdPropagation propagation) =>
        propagation is RequestIdPropagation.UpstreamAndResponse or RequestIdPropagation.ResponseOnly;
}
