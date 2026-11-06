using System.Net.Http;

using Microsoft.Extensions.Logging;

using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Creates the upstream <see cref="HttpMessageInvoker"/> per cluster (PLAN §5.1, §6.2).
/// <para><b>Reuse:</b> when a cluster's <see cref="global::Yarp.ReverseProxy.Configuration.HttpClientConfig"/> is unchanged,
/// the existing client is returned. A new client counts as a cluster change and rebuilds every route on the cluster,
/// and it drops the warm connection pool; so an edit that doesn't touch the client config must not create one.
/// Base YARP does the same comparison; it's restated here because the reuse is a requirement, not a default.</para>
/// <para><b>Handler defaults:</b> pooled connections are recycled after <see cref="PooledConnectionLifetime"/> so a
/// restarted upstream container with a new IP is picked up quickly (PLAN §5.1), and connects time out.</para>
/// </summary>
internal sealed class PaduraksaForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    public PaduraksaForwarderHttpClientFactory()
    {
    }

    public PaduraksaForwarderHttpClientFactory(ILogger<ForwarderHttpClientFactory> logger)
        : base(logger)
    {
    }

    /// <summary>PLAN §5.1 default; per-upstream values come with the upstream settings (M2).</summary>
    public static TimeSpan PooledConnectionLifetime { get; } = TimeSpan.FromSeconds(60);

    public static TimeSpan ConnectTimeout { get; } = TimeSpan.FromSeconds(10);

    // TODO(M2): per-upstream PooledConnectionLifetime, connect timeout and the mTLS client certificate become upstream
    // settings. They must then be part of this comparison (e.g. carried in HttpClientConfig or cluster metadata), or a
    // change to them would silently keep the old client.
    protected override bool CanReuseOldClient(ForwarderHttpClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.OldClient is not null && context.OldConfig == context.NewConfig;
    }

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        base.ConfigureHandler(context, handler);
        ApplyDefaults(handler);
    }

    internal static void ApplyDefaults(SocketsHttpHandler handler)
    {
        handler.PooledConnectionLifetime = PooledConnectionLifetime;
        handler.ConnectTimeout = ConnectTimeout;
    }
}
