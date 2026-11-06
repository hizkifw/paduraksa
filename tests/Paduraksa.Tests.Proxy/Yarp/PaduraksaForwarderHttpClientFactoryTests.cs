using Paduraksa.Proxy.Yarp;

using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Forwarder;

namespace Paduraksa.Tests.Proxy.Yarp;

public sealed class PaduraksaForwarderHttpClientFactoryTests
{
    [Fact]
    public void HandlerDefaultsRecyclePooledConnectionsAndBoundConnects()
    {
        using SocketsHttpHandler handler = new();

        PaduraksaForwarderHttpClientFactory.ApplyDefaults(handler);

        Assert.Equal(TimeSpan.FromSeconds(60), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
    }

    [Fact]
    public void TheOldClientIsReusedOnlyWhileTheClientConfigIsUnchanged()
    {
        PaduraksaForwarderHttpClientFactory factory = new();
        HttpClientConfig config = new() { MaxConnectionsPerServer = 8 };
        using HttpMessageInvoker first = factory.CreateClient(new ForwarderHttpClientContext { ClusterId = "c", NewConfig = config });

        HttpMessageInvoker same = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            NewConfig = config with { },
        });
        using HttpMessageInvoker changed = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            NewConfig = config with { MaxConnectionsPerServer = 9 },
        });

        Assert.Same(first, same);
        Assert.NotSame(first, changed);
    }
}
