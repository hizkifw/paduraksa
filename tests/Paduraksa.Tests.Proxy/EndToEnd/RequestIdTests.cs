using System.Net;
using Microsoft.Extensions.Hosting;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// The request ID end to end: which value is adopted, and where
/// <see cref="RequestIdPropagation"/> sends it - upstream, back to the client, both or neither. An upstream's own
/// <c>X-Request-Id</c> is replaced or stripped, never passed through.
/// </summary>
public sealed class RequestIdTests
{
    private const string UpstreamId = "upstream-set";

    [Theory]
    [InlineData(RequestIdPropagation.UpstreamAndResponse, true, true)]
    [InlineData(RequestIdPropagation.UpstreamOnly, true, false)]
    [InlineData(RequestIdPropagation.ResponseOnly, false, true)]
    [InlineData(RequestIdPropagation.Off, false, false)]
    public async Task EachModeSendsTheIdWhereItSaysAndNowhereElse(RequestIdPropagation propagation, bool upstream, bool response)
    {
        await using RequestScope scope = await RequestScope.StartAsync(propagation, trustedPeer: false);

        (IReadOnlyList<string> ids, Dictionary<string, string> seen) = await scope.SendAsync("client-chosen");

        // The untrusted client's value is never the one that travels.
        Assert.NotEqual("client-chosen", seen.GetValueOrDefault(RequestIds.HeaderName));
        string ours = response ? Assert.Single(ids) : string.Empty;
        Assert.Equal(response, ids.Count > 0);

        if (upstream)
        {
            Assert.True(seen.ContainsKey(RequestIds.HeaderName), "the upstream should have received an X-Request-Id");
            Assert.NotEqual("client-chosen", seen[RequestIds.HeaderName]);
            if (response)
            {
                // One ID for the request: the upstream sees what the client sees.
                Assert.Equal(ours, seen[RequestIds.HeaderName]);
            }
        }
        else
        {
            Assert.False(seen.ContainsKey(RequestIds.HeaderName), "the upstream should not have received an X-Request-Id");
        }

        // The log always has it, whatever was sent where.
        Assert.True(scope.Server.AccessLog.TryRead(out AccessLogEntry entry));
        Assert.False(string.IsNullOrEmpty(entry.RequestId));
        if (response)
        {
            Assert.Equal(ours, entry.RequestId);
        }
    }

    [Fact]
    public async Task AnUpstreamsOwnResponseHeaderIsReplacedOrStripped()
    {
        // Upstream and client: ours wins over the upstream's.
        await using (RequestScope both = await RequestScope.StartAsync(RequestIdPropagation.UpstreamAndResponse, trustedPeer: false))
        {
            (IReadOnlyList<string> ids, _) = await both.SendAsync("client-chosen");
            string id = Assert.Single(ids);
            Assert.NotEqual(UpstreamId, id);
            Assert.NotEqual("client-chosen", id);
        }

        // Neither: the upstream's header doesn't reach the client either.
        await using (RequestScope none = await RequestScope.StartAsync(RequestIdPropagation.Off, trustedPeer: false))
        {
            (IReadOnlyList<string> ids, _) = await none.SendAsync("client-chosen");
            Assert.Empty(ids);
        }
    }

    [Fact]
    public async Task ATrustedProxysIdIsTheOneThatTravels()
    {
        await using RequestScope scope = await RequestScope.StartAsync(RequestIdPropagation.UpstreamAndResponse, trustedPeer: true);

        (IReadOnlyList<string> ids, Dictionary<string, string> seen) = await scope.SendAsync("edge-1234");

        Assert.Equal("edge-1234", Assert.Single(ids));
        Assert.Equal("edge-1234", seen[RequestIds.HeaderName]);
        Assert.True(scope.Server.AccessLog.TryRead(out AccessLogEntry entry));
        Assert.Equal("edge-1234", entry.RequestId);
    }

    [Fact]
    public async Task ANonTrustedClientsIdIsReplacedEverywhere()
    {
        await using RequestScope scope = await RequestScope.StartAsync(RequestIdPropagation.UpstreamAndResponse, trustedPeer: false);

        (IReadOnlyList<string> ids, Dictionary<string, string> seen) = await scope.SendAsync("client-chosen");

        string id = Assert.Single(ids);
        Assert.NotEqual("client-chosen", id);
        Assert.Equal(RequestIds.GeneratedLength, id.Length);
        Assert.Equal(id, seen[RequestIds.HeaderName]);
    }

    /// <summary>A proxy server and an upstream that records the request headers and sets its own <c>X-Request-Id</c>.</summary>
    private sealed class RequestScope : IAsyncDisposable
    {
        private readonly IHost _upstream;
        private readonly HttpClient _client;

        private RequestScope(ProxyTestServer server, IHost upstream, HttpClient client)
        {
            Server = server;
            _upstream = upstream;
            _client = client;
        }

        public ProxyTestServer Server { get; }

        public static async Task<RequestScope> StartAsync(RequestIdPropagation propagation, bool trustedPeer)
        {
            (IHost upstream, string address) = await HeaderEchoUpstreamHost.StartAsync(
                static response => response.Headers["X-Request-Id"] = UpstreamId,
                TestContext.Current.CancellationToken);
            ProxyConfigModel model = new()
            {
                Upstreams = [Upstream("echo", address)],
                Hosts =
                [
                    ProxyHost("app", "app.test", Route("app-root", "echo")) with { RequestIdPropagation = propagation },
                ],
                Defaults = new DefaultsSpec
                {
                    TrustedProxies = trustedPeer
                        ? new TrustedProxySpec { Mode = TrustedProxyMode.Generic, Proxies = ["127.0.0.0/8"] }
                        : TrustedProxySpec.Off,
                },
            };
            ProxyTestServer server = await ProxyTestServer.StartAsync(model, new() { ClientInfo = true });
            return new RequestScope(server, upstream, ProxyTestServer.CreateClient());
        }

        public async Task<(IReadOnlyList<string> Ids, Dictionary<string, string> Seen)> SendAsync(string requestId)
        {
            using HttpRequestMessage request = Server.Request("app.test", "/x");
            request.Headers.TryAddWithoutValidation(RequestIds.HeaderName, requestId);
            using HttpResponseMessage response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Dictionary<string, string> seen = HeaderEchoUpstreamHost.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            return (response.Headers.TryGetValues(RequestIds.HeaderName, out IEnumerable<string>? ids) ? [.. ids] : [], seen);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await Server.DisposeAsync();
            await _upstream.StopAsync(CancellationToken.None);
            _upstream.Dispose();
        }
    }
}
