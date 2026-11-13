using Microsoft.Extensions.Logging.Abstractions;

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
        Assert.Null(handler.ActivityHeadersPropagator);
    }

    [Fact]
    public void TheOldClientIsReusedOnlyWhileTheClientConfigIsUnchanged()
    {
        PaduraksaForwarderHttpClientFactory factory = new(NullLogger<ForwarderHttpClientFactory>.Instance, admin: null);
        HttpClientConfig config = new() { MaxConnectionsPerServer = 8 };
        using var first = factory.CreateClient(new ForwarderHttpClientContext { ClusterId = "c", NewConfig = config });

        var same = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            NewConfig = config with { },
        });
        using var changed = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            NewConfig = config with { MaxConnectionsPerServer = 9 },
        });

        Assert.Same(first, same);
        Assert.NotSame(first, changed);
    }

    [Fact]
    public void ChangedPinsBuildANewClient()
    {
        PaduraksaForwarderHttpClientFactory factory = new(NullLogger<ForwarderHttpClientFactory>.Instance, admin: null);
        HttpClientConfig config = new();
        Dictionary<string, string> pinned = new() { [UpstreamCertificatePins.MetadataKey] = new string('a', 64) };
        using var first = factory.CreateClient(new ForwarderHttpClientContext { ClusterId = "c", NewConfig = config, NewMetadata = pinned });

        var same = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            OldMetadata = pinned,
            NewConfig = config,
            NewMetadata = new Dictionary<string, string>(pinned),
        });
        using var repinned = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            OldMetadata = pinned,
            NewConfig = config,
            NewMetadata = new Dictionary<string, string> { [UpstreamCertificatePins.MetadataKey] = new string('b', 64) },
        });
        using var unpinned = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            OldMetadata = pinned,
            NewConfig = config,
        });

        Assert.Same(first, same);
        Assert.NotSame(first, repinned);
        Assert.NotSame(first, unpinned);
    }

    [Fact]
    public void AChangedConnectTimeoutBuildsANewClientAndAnUnchangedOneKeepsTheOld()
    {
        PaduraksaForwarderHttpClientFactory factory = new(NullLogger<ForwarderHttpClientFactory>.Instance, admin: null);
        HttpClientConfig config = new();
        Dictionary<string, string> threeSeconds = new() { [UpstreamTimeouts.ConnectMetadataKey] = "3000" };
        using var first = factory.CreateClient(new ForwarderHttpClientContext { ClusterId = "c", NewConfig = config, NewMetadata = threeSeconds });

        ForwarderHttpClientContext Next(IReadOnlyDictionary<string, string>? now) => new()
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            OldMetadata = threeSeconds,
            NewConfig = config,
            NewMetadata = now,
        };

        var same = factory.CreateClient(Next(new Dictionary<string, string>(threeSeconds)));
        using var longer = factory.CreateClient(Next(new Dictionary<string, string> { [UpstreamTimeouts.ConnectMetadataKey] = "4000" }));
        using var backToDefault = factory.CreateClient(Next(null));

        Assert.Same(first, same);
        Assert.NotSame(first, longer);
        Assert.NotSame(first, backToDefault);
    }

    [Fact]
    public void AnUnrelatedMetadataEntryDoesNotRebuildTheClient()
    {
        // The custom Host name rides in the same metadata; changing it changes the health probe, not the connection.
        PaduraksaForwarderHttpClientFactory factory = new(NullLogger<ForwarderHttpClientFactory>.Instance, admin: null);
        HttpClientConfig config = new();
        using var first = factory.CreateClient(new ForwarderHttpClientContext { ClusterId = "c", NewConfig = config });

        var same = factory.CreateClient(new ForwarderHttpClientContext
        {
            ClusterId = "c",
            OldClient = first,
            OldConfig = config,
            NewConfig = config,
            NewMetadata = new Dictionary<string, string> { [UpstreamHost.MetadataKey] = "app.internal" },
        });

        Assert.Same(first, same);
    }

    [Fact]
    public void TheConnectTimeoutIsReadFromTheMetadataAndFallsBackToTheDefault()
    {
        Assert.Equal(TimeSpan.FromSeconds(10), UpstreamTimeouts.ConnectOf(null));
        Assert.Equal(TimeSpan.FromMilliseconds(2500), UpstreamTimeouts.ConnectOf(new Dictionary<string, string> { [UpstreamTimeouts.ConnectMetadataKey] = "2500" }));
        Assert.Equal(TimeSpan.FromSeconds(10), UpstreamTimeouts.ConnectOf(new Dictionary<string, string> { [UpstreamTimeouts.ConnectMetadataKey] = "-5" }));
        Assert.Equal(TimeSpan.FromSeconds(10), UpstreamTimeouts.ConnectOf(new Dictionary<string, string> { [UpstreamTimeouts.ConnectMetadataKey] = "soon" }));
    }

    [Theory]
    [InlineData("AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89:AB:CD:EF:01:23:45:67:89", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", true)]
    [InlineData(" abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789 ", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef012345678", false)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567890", false)]
    [InlineData("gbcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", false)]
    [InlineData("", false)]
    public void AFingerprintIs64HexDigitsWithOptionalColons(string text, bool valid)
    {
        Assert.Equal(valid, UpstreamCertificatePins.TryNormalize(text, out var normal));
        if (valid)
        {
            Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", normal);
        }
    }
}
