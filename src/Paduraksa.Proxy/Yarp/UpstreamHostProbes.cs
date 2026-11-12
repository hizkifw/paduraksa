using Microsoft.Extensions.DependencyInjection;

using Yarp.ReverseProxy.Health;
using Yarp.ReverseProxy.Model;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// An active probe sends the same <c>Host</c> a request would, when the upstream's is a custom name: an upstream that answers
/// only to its own name would otherwise fail every probe and be taken out. Other modes leave YARP's request alone: the
/// destination's address is what a probe sends anyway, and a probe has no client whose Host could be copied.
/// </summary>
internal sealed class UpstreamHostProbes(IProbingRequestFactory inner) : IProbingRequestFactory
{
    public HttpRequestMessage CreateRequest(ClusterModel cluster, DestinationModel destination)
    {
        var request = inner.CreateRequest(cluster, destination);
        if (cluster.Config.Metadata is { } metadata && metadata.TryGetValue(UpstreamHost.MetadataKey, out var host))
        {
            request.Headers.Host = host;
        }

        return request;
    }

    /// <summary>Wraps YARP's registered factory; call after <c>AddReverseProxy()</c>.</summary>
    /// <exception cref="InvalidOperationException">YARP registered no factory this can wrap (a change in how it does): failing here is better than probes that silently drop a custom Host.</exception>
    internal static IServiceCollection SendCustomHostWithProbes(IServiceCollection services)
    {
        var wrapped = false;
        for (var i = 0; i < services.Count; i++)
        {
            var descriptor = services[i];
            if (descriptor.ServiceType != typeof(IProbingRequestFactory) || descriptor.ImplementationType is not { } implementation)
            {
                continue;
            }

            services[i] = ServiceDescriptor.Singleton<IProbingRequestFactory>(provider => new UpstreamHostProbes(
                (IProbingRequestFactory)ActivatorUtilities.CreateInstance(provider, implementation)));
            wrapped = true;
        }

        return wrapped
            ? services
            : throw new InvalidOperationException("YARP registered no IProbingRequestFactory by type, so active health probes could not be made to send a custom Host header.");
    }
}
