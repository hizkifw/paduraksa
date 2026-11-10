using System.Net;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>The challenge page through the real host: the template's largest segments, its CSP and the HEAD answer.</summary>
public sealed class ProofOfWorkPageEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    [Fact]
    public async Task ABrowserGetsTheChallengePageAndHeadGetsOnlyItsHeaders()
    {
        var runtime = new ProofOfWorkRuntime();
        runtime.UseKey(new TestMacKey());
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost("pow", "pow.test", Route("pow-root", "web")) with
                {
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 8 },
                },
            ],
        };
        ProxyTestServerOptions options = new()
        {
            ProofOfWork = runtime,
            CompilerOptions = new SnapshotCompilerOptions { ProofOfWork = runtime },
        };
        await using var server = await ProxyTestServer.StartAsync(model, options);
        using var client = ProxyTestServer.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        using var get = await client.SendAsync(Request(server, "pow.test", "/a?b=1", "text/html"), ct);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, get.Content.Headers.ContentType!.ToString());
        var page = await get.Content.ReadAsStringAsync(ct);
        Assert.Contains("<h1>Checking your browser</h1>", page, StringComparison.Ordinal);
        Assert.Contains("id=\"challenge\" name=\"challenge\" type=\"hidden\" value=\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"return\" name=\"return\" type=\"hidden\" value=\"/a?b=1\"", page, StringComparison.Ordinal);
        Assert.Contains("id=\"difficulty\" type=\"hidden\" value=\"8\"", page, StringComparison.Ordinal);
        Assert.Contains("worker-src blob:", get.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Contains("form-action 'self'", get.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        using var head = await client.SendAsync(Request(server, "pow.test", "/a", "text/html", HttpMethod.Head), ct);
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(HtmlPageTemplate.ContentType, head.Content.Headers.ContentType!.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync(ct));

        using var api = await client.SendAsync(Request(server, "pow.test", "/a", "application/json"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, api.StatusCode);
        Assert.Equal("text/plain; charset=utf-8", api.Content.Headers.ContentType!.ToString());
        Assert.Equal("This page needs a browser that runs JavaScript.\n", await api.Content.ReadAsStringAsync(ct));
    }

    private static HttpRequestMessage Request(ProxyTestServer server, string host, string path, string accept, HttpMethod? method = null)
    {
        var request = server.Request(host, path, method);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        return request;
    }
}
