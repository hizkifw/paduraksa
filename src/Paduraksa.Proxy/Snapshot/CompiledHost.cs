using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>Host kinds in v1 (PLAN §5.1): Proxy, Redirect, Respond and Admin (PLAN §3.1).</summary>
public enum HostKind : byte
{
    /// <summary>Routes to upstreams through YARP.</summary>
    Proxy,

    /// <summary>Answers every request with a precomputed redirect.</summary>
    Redirect,

    /// <summary>Answers every request with a fixed status and body.</summary>
    Respond,

    /// <summary>Forwards every request to the admin app's internal listener (the <c>__admin</c> cluster).</summary>
    Admin,
}

/// <summary>A configured host, compiled for the gate (PLAN §6.2). Immutable; belongs to one snapshot.</summary>
public sealed class CompiledHost
{
    /// <summary>Position in <see cref="RuntimeSnapshot.HostsByIndex"/>; valid for this snapshot only.</summary>
    public required int Index { get; init; }

    /// <summary>Stable identity across snapshots.</summary>
    public required HostId Id { get; init; }

    public required HostKind Kind { get; init; }

    /// <summary>Proxy hosts: ordered, first match wins. Empty for the other kinds.</summary>
    public required CompiledRoute[] Routes { get; init; }

    /// <summary>
    /// The host-level chain. It runs right after host lookup, before the HTTPS redirect, a Redirect/Respond answer
    /// and route matching, so <c>State.Route</c> is still <see langword="null"/> for these policies.
    /// </summary>
    public required IRequestPolicy[] Policies { get; init; }

    /// <summary>Redirect and Respond hosts: the whole answer. <see langword="null"/> for Proxy hosts.</summary>
    public PrecomputedResponse? FixedResponse { get; init; }

    /// <summary>
    /// The precomputed <c>Alt-Svc</c> value (<c>h3=":443"; ma=86400</c>, with the public HTTPS port), or empty when the
    /// host isn't served over HTTP/3. The gate sets it on the host's TCP HTTPS responses, forwarded or answered
    /// (<see cref="Tls.AltSvcHeader.Apply"/>).
    /// </summary>
    public StringValues AltSvc { get; init; }

    /// <summary>
    /// The host's TLS settings. The handshake applies the settings of the SNI's host; the gate re-checks this host's
    /// against the request's connection (<see cref="ConnectionTlsCheck"/>), since clients coalesce connections across
    /// names, and answers 421 when the connection is weaker.
    /// </summary>
    public TlsPolicy Tls { get; init; } = TlsPolicy.Default;

    /// <summary>
    /// False: the host isn't served over HTTPS, and the gate answers its HTTPS requests 421 (a connection negotiated
    /// for another name, or on the default certificate, must not reach it).
    /// </summary>
    public bool Https { get; init; } = true;

    /// <summary>Set when the host forces HTTPS: plain-HTTP requests get this redirect first.</summary>
    internal HttpsRedirect? HttpsRedirect { get; init; }

    /// <summary>
    /// Placeholder for the stable metric slot (PLAN §6.3, §7.4): the same host keeps the same slot across snapshots.
    /// -1 until the metrics registry exists (M7).
    /// </summary>
    public int MetricSlot { get; init; } = -1;

    /// <summary>The first route that takes <paramref name="path"/>, or <see langword="null"/>.</summary>
    public CompiledRoute? Match(ReadOnlySpan<char> path)
    {
        foreach (CompiledRoute route in Routes)
        {
            if (route.Matches(path))
            {
                return route;
            }
        }

        return null;
    }
}

/// <summary>
/// The precomputed HTTP→HTTPS redirect of one host: <c>301</c> for <c>GET</c>/<c>HEAD</c>, <c>308</c> otherwise so the
/// method and body survive. The <c>Location</c> keeps the request host and target and uses the <b>public</b> HTTPS
/// port from settings, never the bound port (PLAN §7.5).
/// </summary>
internal sealed class HttpsRedirect
{
    public HttpsRedirect(int publicHttpsPort)
    {
        RedirectTarget target = RedirectTarget.Https(publicHttpsPort);
        Safe = PrecomputedResponse.RedirectTo(StatusCodes.Status301MovedPermanently, target);
        Unsafe = PrecomputedResponse.RedirectTo(StatusCodes.Status308PermanentRedirect, target);
    }

    public PrecomputedResponse Safe { get; }

    public PrecomputedResponse Unsafe { get; }

    public PrecomputedResponse For(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ? Safe : Unsafe;
}
