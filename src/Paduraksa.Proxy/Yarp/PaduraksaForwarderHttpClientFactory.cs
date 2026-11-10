using Microsoft.Extensions.Logging;

using Paduraksa.Core.Hosting;

using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Creates the upstream <see cref="HttpMessageInvoker"/> per cluster.
/// <para><b>Reuse:</b> when a cluster's <see cref="global::Yarp.ReverseProxy.Configuration.HttpClientConfig"/> is unchanged,
/// the existing client is returned. A new client counts as a cluster change and rebuilds every route on the cluster,
/// and it drops the warm connection pool; so an edit that doesn't touch the client config must not create one.
/// Base YARP does the same comparison; it's restated here because the reuse is a requirement, not a default.</para>
/// <para><b>Handler defaults:</b> pooled connections are recycled after <see cref="PooledConnectionLifetime"/> so a
/// restarted upstream container with a new IP is picked up quickly, and connects time out.</para>
/// <para><b>The admin cluster</b> (<see cref="YarpConfig.AdminClusterId"/>) connects through
/// <see cref="AdminInternalListener"/> instead of resolving its symbolic destination: a Unix socket, or the
/// loopback fallback's port.</para>
/// </summary>
internal sealed class PaduraksaForwarderHttpClientFactory : ForwarderHttpClientFactory
{
    private readonly AdminInternalListener? _admin;

    public PaduraksaForwarderHttpClientFactory(ILogger<ForwarderHttpClientFactory> logger, AdminInternalListener? admin)
        : base(logger)
    {
        _admin = admin;
    }

    /// <summary>The default; per-upstream values come from the upstream settings.</summary>
    public static TimeSpan PooledConnectionLifetime { get; } = TimeSpan.FromSeconds(60);

    public static TimeSpan ConnectTimeout { get; } = TimeSpan.FromSeconds(10);

    // TODO: per-upstream PooledConnectionLifetime, connect timeout and the mTLS client certificate become upstream
    // settings. They must then be part of this comparison (e.g. carried in HttpClientConfig or cluster metadata), or a
    // change to them would silently keep the old client.
    protected override bool CanReuseOldClient(ForwarderHttpClientContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.OldClient is not null
            && context.OldConfig == context.NewConfig
            && UpstreamCertificatePins.SamePins(context.OldMetadata, context.NewMetadata);
    }

    protected override void ConfigureHandler(ForwarderHttpClientContext context, SocketsHttpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(handler);
        base.ConfigureHandler(context, handler);
        ApplyDefaults(handler);

        if (UpstreamCertificatePins.FromMetadata(context.NewMetadata) is { } pins)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = pins.Validate;
        }

        if (string.Equals(context.ClusterId, YarpConfig.AdminClusterId, StringComparison.OrdinalIgnoreCase))
        {
            // The destination (http://admin.internal/) is never resolved: the connect callback knows the process's own
            // internal admin listener, which is all this cluster may ever reach.
            handler.ConnectCallback = (_admin ?? throw new InvalidOperationException(
                $"Cluster {YarpConfig.AdminClusterId} needs the admin internal listener, which isn't registered in this app."))
                .ConnectAsync;
        }
    }

    internal static void ApplyDefaults(SocketsHttpHandler handler)
    {
        handler.PooledConnectionLifetime = PooledConnectionLifetime;
        handler.ConnectTimeout = ConnectTimeout;
    }
}
