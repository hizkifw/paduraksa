using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

public sealed class RawPathForwardingTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    [Theory]
    [InlineData("/group%2Fproject/-/tree/main", "/a/group%2Fproject/-/tree/main")]
    [InlineData("/x%2fy", "/a/x%2fy")]
    [InlineData("/100%25", "/a/100%25")]
    [InlineData("/x%252Fy", "/a/x%252Fy")]
    [InlineData("/a%20b+c", "/a/a%20b+c")]
    [InlineData("/caf%C3%A9/%E2%82%AC", "/a/caf%C3%A9/%E2%82%AC")]
    [InlineData("/%61dmin", "/a/%61dmin")]
    [InlineData("/", "/a/")]
    public async Task ThePathIsForwardedAsReceived(string target, string expected)
    {
        await using var server = await StartAsync();

        var (status, body, _) = await RawHttp.GetAsync(server.BaseAddress, "raw.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
        Assert.Equal(expected, body);
    }

    [Theory]
    [InlineData("/p?x=%2F&y=a+b", "?x=%2F&y=a+b")]
    [InlineData("/p%2Fq?z=%E2%82%AC", "?z=%E2%82%AC")]
    public async Task TheQueryIsForwardedUnchanged(string target, string expectedQuery)
    {
        await using var server = await StartAsync();

        var (status, _, head) = await RawHttp.GetAsync(server.BaseAddress, "raw.test", target, TestContext.Current.CancellationToken);

        Assert.Equal(200, status);
        Assert.Contains("x-query: " + expectedQuery, head, StringComparison.OrdinalIgnoreCase);
    }

    private Task<ProxyTestServer> StartAsync() => ProxyTestServer.StartAsync(new ProxyConfigModel
    {
        Upstreams = [Upstream("ua", upstream.Address("a"))],
        Hosts = [ProxyHost("raw", "raw.test", Route("raw-root", "ua"))],
    });
}
