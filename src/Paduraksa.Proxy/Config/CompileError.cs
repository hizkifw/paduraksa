using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Config;

/// <summary>
/// One error of <see cref="SnapshotCompiler.Compile"/>: the object it is about, by typed ID, and why. The source lets
/// a caller that built the <see cref="ProxyConfigModel"/> from a document point back into it;
/// <see cref="ToString"/> reads <c>host 'host_...', route 'rte_...': the path ...</c> for logs and tests.
/// </summary>
/// <param name="Source">What the error is about.</param>
/// <param name="Message">Human-readable explanation, without the source; wording may change.</param>
public sealed record CompileError(CompileErrorSource Source, string Message)
{
    public override string ToString() => $"{Source}: {Message}";
}

/// <summary>
/// What a <see cref="CompileError"/> is about. An object's ID may be empty or duplicated, which is often the error
/// itself; it is reported as given.
/// </summary>
public abstract record CompileErrorSource
{
    /// <summary>The global settings (<see cref="DefaultsSpec"/>), apart from the trusted proxies.</summary>
    public static CompileErrorSource Defaults { get; } = new DefaultsErrorSource();

    /// <summary><see cref="DefaultsSpec.TrustedProxies"/>.</summary>
    public static CompileErrorSource TrustedProxies { get; } = new TrustedProxiesErrorSource();

    public static CompileErrorSource Host(HostId host) => new HostErrorSource(host);

    public static CompileErrorSource Route(HostId host, RouteId route) => new RouteErrorSource(host, route);

    public static CompileErrorSource Upstream(UpstreamId upstream) => new UpstreamErrorSource(upstream);

    public static CompileErrorSource Destination(UpstreamId upstream, DestinationId destination) =>
        new DestinationErrorSource(upstream, destination);
}

/// <summary>The global settings, apart from the trusted proxies.</summary>
public sealed record DefaultsErrorSource : CompileErrorSource
{
    public override string ToString() => "defaults";
}

/// <summary>The trusted proxies of the global settings.</summary>
public sealed record TrustedProxiesErrorSource : CompileErrorSource
{
    public override string ToString() => "defaults: trusted proxies";
}

/// <summary>A host, its names, settings, certificate or host-level policies.</summary>
public sealed record HostErrorSource(HostId HostId) : CompileErrorSource
{
    public override string ToString() => $"host '{HostId}'";
}

/// <summary>A route of <paramref name="HostId"/>, with its own policies.</summary>
public sealed record RouteErrorSource(HostId HostId, RouteId RouteId) : CompileErrorSource
{
    public override string ToString() => $"host '{HostId}', route '{RouteId}'";
}

/// <summary>An upstream (YARP cluster) as a whole.</summary>
public sealed record UpstreamErrorSource(UpstreamId UpstreamId) : CompileErrorSource
{
    public override string ToString() => $"upstream '{UpstreamId}'";
}

/// <summary>A destination of <paramref name="UpstreamId"/>.</summary>
public sealed record DestinationErrorSource(UpstreamId UpstreamId, DestinationId DestinationId) : CompileErrorSource
{
    public override string ToString() => $"upstream '{UpstreamId}', destination '{DestinationId}'";
}
