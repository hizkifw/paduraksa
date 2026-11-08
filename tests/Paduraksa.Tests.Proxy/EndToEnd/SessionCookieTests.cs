using System.Net;

using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>The admin session cookie never reaches a proxied upstream (PLAN §3.1); every other cookie does, as sent.</summary>
public sealed class SessionCookieTests(HeaderEchoUpstream upstream) : IClassFixture<HeaderEchoUpstream>
{
    private const string Session = "__Host-paduraksa-session";

    private ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("echo", upstream.Address)],
        Hosts = [ProxyHost("app", "app.test", Route("app-root", "echo"))],
    };

    private async Task<Dictionary<string, string>> SendAsync(params string[] cookieHeaders)
    {
        await using ProxyTestServer server = await ProxyTestServer.StartAsync(Model(), new() { ClientInfo = true });
        using HttpClient client = ProxyTestServer.CreateClient();
        using HttpRequestMessage request = server.Request("app.test", "/");
        if (cookieHeaders.Length > 0)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeaders);
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return HeaderEchoUpstream.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSessionCookieIsRemovedAndTheOthersAreIntact()
    {
        Dictionary<string, string> seen = await SendAsync($"theme=dark; {Session}=secretvalue; lang=nl");

        Assert.Equal("theme=dark; lang=nl", seen["Cookie"]);
        Assert.DoesNotContain("secretvalue", string.Join('|', seen.Values), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionCookieAloneLeavesNoCookieHeader()
    {
        Dictionary<string, string> seen = await SendAsync($"{Session}=secretvalue");

        Assert.False(seen.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task NoCookieMeansNoChange()
    {
        Dictionary<string, string> seen = await SendAsync();

        Assert.False(seen.ContainsKey("Cookie"));
    }

    [Fact]
    public async Task CookiesWithoutTheSessionCookieAreForwardedUntouched()
    {
        Dictionary<string, string> seen = await SendAsync("a=1; b=2", "x" + Session + "=1");

        Assert.Contains("a=1", seen["Cookie"], StringComparison.Ordinal);
        Assert.Contains("x" + Session + "=1", seen["Cookie"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("a=1; __Host-paduraksa-session=s; b=2", "a=1; b=2")]
    [InlineData("__Host-paduraksa-session=s; a=1", "a=1")]
    [InlineData("a=1;__Host-paduraksa-session=s", "a=1")]
    [InlineData("__Host-paduraksa-session=s", "")]
    [InlineData("__Host-paduraksa-session=s; __Host-paduraksa-session=t", "")]
    [InlineData("x__Host-paduraksa-session=s; a=1", "x__Host-paduraksa-session=s; a=1")]
    public void Without_RemovesOnlyThatCookie(string header, string expected) =>
        Assert.Equal(expected, SessionCookieTransform.Without(new StringValues(header)));

    [Fact]
    public void Without_HandlesSeveralHeaderValues() =>
        Assert.Equal("a=1; b=2", SessionCookieTransform.Without(new StringValues(["a=1", $"{Session}=s; b=2"])));
}
