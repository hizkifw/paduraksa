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
/// </summary>
public sealed class GateRequestState : IGateFeature
{
    /// <summary>The snapshot read once at gate entry; every decision for this request comes from it.</summary>
    public RuntimeSnapshot? Snapshot { get; internal set; }

    public CompiledHost? Host { get; internal set; }

    public CompiledRoute? Route { get; internal set; }

    /// <summary>The resolved client address, IPv4 as IPv4-mapped IPv6 (<c>::ffff:a.b.c.d</c>); 0 when unknown.</summary>
    public UInt128 ClientAddress { get; internal set; }

    GateRequestState IGateFeature.State => this;

    /// <summary>Clears every field, which also stops a finished request from pinning an old snapshot.</summary>
    public void Reset()
    {
        Snapshot = null;
        Host = null;
        Route = null;
        ClientAddress = UInt128.Zero;
    }
}
