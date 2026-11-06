using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Gate;

/// <summary>Published by the gate so later stages (YARP transforms, telemetry, error pages) see its decisions.</summary>
public interface IGateFeature
{
    GateRequestState State { get; }
}

/// <summary>
/// Everything the gate resolved for one request (spike S3 §3). One instance lives in Kestrel's
/// <c>IPersistentStateFeature</c> (per connection on H1, per pooled stream on H2/H3) and is <see cref="Reset"/> at
/// the start of every request, so it's never shared by two concurrent requests and costs nothing in steady state.
/// <para><b>Lifetime rule:</b> nothing may keep a reference to it after the request ends; the next request on the
/// connection or stream reuses it.</para>
/// <para><b>Accepted cost:</b> the references are only cleared when the next request starts, so an idle keep-alive
/// connection (or pooled H2/H3 stream) keeps the snapshot of its last request alive. That's bounded: at most one
/// snapshot per idle connection or pooled stream, for at most Kestrel's keep-alive timeout (130 s by default; pooled
/// H2 streams are evicted after about 5 s). Clearing it after the response would need a continuation on every
/// proxied request, which allocates.</para>
/// </summary>
public sealed class GateRequestState : IGateFeature
{
    /// <summary>The snapshot read once at gate entry; every decision for this request comes from it.</summary>
    public RuntimeSnapshot? Snapshot { get; internal set; }

    public CompiledHost? Host { get; internal set; }

    public CompiledRoute? Route { get; internal set; }

    // Last client address formatted for the forwarding headers, cached per connection (H1) or pooled stream (H2/H3)
    // so repeated requests from one client format it once. Deliberately not cleared by Reset.
    private IpKey _formattedAddress;
    private string? _formatted;

    /// <summary>
    /// The client as resolved at gate entry (PLAN §7.6): the address, where it came from, whether the peer is a trusted
    /// proxy and whether Cloudflare headers may be trusted. Policies and the forwarding headers use it.
    /// </summary>
    public ClientIpResolution ClientIp { get; internal set; }

    /// <summary>The resolved client address (IPv4 as IPv4-mapped, so TCP on <c>[::]</c> and QUIC agree);
    /// <see cref="IpKey.Unspecified"/> when unknown.</summary>
    public IpKey ClientAddress => ClientIp.Address;

    GateRequestState IGateFeature.State => this;

    /// <summary>Clears every field at the start of a request.</summary>
    internal void Reset()
    {
        Snapshot = null;
        Host = null;
        Route = null;
        ClientIp = default;
    }

    /// <summary>
    /// <see cref="ClientAddress"/> as text (<c>192.0.2.1</c>, <c>2001:db8::1</c>), for <c>X-Forwarded-For</c> and
    /// <c>X-Real-IP</c>. Allocates the string only when the address differs from the previous request's on this
    /// connection or stream.
    /// </summary>
    public string FormattedClientAddress()
    {
        IpKey address = ClientAddress;
        if (_formatted is null || address != _formattedAddress)
        {
            _formatted = address.ToString();
            _formattedAddress = address;
        }

        return _formatted;
    }
}
