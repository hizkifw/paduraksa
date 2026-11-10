using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>
/// The proof-of-work handshake through a real <c>PaduraksaHost</c> and the real vault key: a browser-shaped GET gets the
/// challenge page, a solution found in C# (the page's own rule) earns the cookie with a 303, and the cookie then lets the
/// request through to the upstream. The same cookie from another loopback address is challenged again, which is the
/// token's client binding on the wire.
/// </summary>
public sealed class PublicProofOfWorkTests
{
    private const int Difficulty = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SolvesTheChallengeAndThenForwardsTheCookieBoundPage()
    {
        await using var upstream = new Upstream(await HeaderEchoUpstreamHost.StartAsync(null, Ct));
        await using var host = TestHost.Create();
        await host.StartAsync(Ct);

        var published = await host.Shared.SnapshotPublisher.PublishAsync(Compile(host, upstream.Address), Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));

        using var client = ClientAt("127.0.0.1");
        var endpoint = TestHost.Endpoint(host, HostRole.Public, "http");

        // A browser gets the challenge page, with the page's strict headers.
        using var challengeResponse = await client.SendAsync(BrowserGet(endpoint, "/"), Ct);
        Assert.Equal(HttpStatusCode.OK, challengeResponse.StatusCode);
        Assert.Equal("text/html", challengeResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", challengeResponse.Headers.CacheControl?.ToString());
        var csp = Assert.Single(challengeResponse.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("worker-src blob:", csp, StringComparison.Ordinal);
        Assert.Equal("no-referrer", Assert.Single(challengeResponse.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("nosniff", Assert.Single(challengeResponse.Headers.GetValues("X-Content-Type-Options")));
        var page = await challengeResponse.Content.ReadAsStringAsync(Ct);
        var challenge = Regex.Match(page, "name=\"challenge\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.False(string.IsNullOrEmpty(challenge), page);

        // Anything that isn't a browser is refused before it can solve anything.
        using var api = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/"));
        api.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var apiResponse = await client.SendAsync(api, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, apiResponse.StatusCode);
        Assert.Equal("text/plain", apiResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("JavaScript", await apiResponse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Solve with the page's rule, post it, and take the cookie from the 303.
        var solution = Solve(challenge, Difficulty);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["challenge"] = challenge,
            ["solution"] = solution,
            ["return"] = "/page?x=1",
        });
        form.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var verify = await client.PostAsync(new Uri(endpoint, "/.well-known/paduraksa/pow"), form, Ct);
        var verifyBody = await verify.Content.ReadAsStringAsync(Ct);
        Assert.True(HttpStatusCode.SeeOther == verify.StatusCode, $"verify answered {verify.StatusCode}: {verifyBody}");
        Assert.Equal("/page?x=1", verify.Headers.Location?.OriginalString);
        var setCookie = Assert.Single(verify.Headers.GetValues("Set-Cookie"));
        Assert.Contains("paduraksa_pow=", setCookie, StringComparison.Ordinal);
        Assert.Contains("Path=/", setCookie, StringComparison.Ordinal);
        Assert.Contains("HttpOnly", setCookie, StringComparison.Ordinal);
        Assert.Contains("SameSite=Lax", setCookie, StringComparison.Ordinal);
        Assert.Contains("Max-Age=86400", setCookie, StringComparison.Ordinal);
        Assert.DoesNotContain("Secure", setCookie, StringComparison.Ordinal);
        var cookie = setCookie[..setCookie.IndexOf(';')];

        // With the cookie, the request reaches the upstream.
        using var forwarded = BrowserGet(endpoint, "/page?x=1");
        forwarded.Headers.Add("Cookie", cookie);
        using var forwardedResponse = await client.SendAsync(forwarded, Ct);
        Assert.Equal(HttpStatusCode.OK, forwardedResponse.StatusCode);
        Assert.Contains("target: /page?x=1", await forwardedResponse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // The same cookie from another address is another client: it is challenged.
        using var otherClient = ClientAt("127.0.0.2");
        using var other = BrowserGet(endpoint, "/page?x=1");
        other.Headers.Add("Cookie", cookie);
        using var otherResponse = await otherClient.SendAsync(other, Ct);
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
        Assert.Equal("text/html", otherResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("name=\"challenge\"", await otherResponse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // A GET on the verify path is not the endpoint: it answers 405 on a host where the gate is on.
        using var wrongMethod = BrowserGet(endpoint, "/.well-known/paduraksa/pow");
        using var wrongMethodResponse = await client.SendAsync(wrongMethod, Ct);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethodResponse.StatusCode);
        Assert.Equal("POST", Assert.Single(wrongMethodResponse.Content.Headers.Allow));

        // robots.txt is never challenged, so a crawler can read it even without a cookie.
        using var robots = BrowserGet(endpoint, "/robots.txt");
        using var robotsResponse = await client.SendAsync(robots, Ct);
        Assert.Equal(HttpStatusCode.OK, robotsResponse.StatusCode);
        Assert.Contains("target: /robots.txt", await robotsResponse.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    private static RuntimeSnapshot Compile(PaduraksaHost host, string upstreamAddress)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [new UpstreamSpec { Id = TestIds.Upstream("pow-upstream"), Destinations = [new(TestIds.Destination("pow-upstream-0"), upstreamAddress)] }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("pow"),
                    Names = ["127.0.0.1"],
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = false, Difficulty = Difficulty, TokenLifetimeHours = 24 },
                    Routes =
                    [
                        new RouteSpec
                        {
                            Id = TestIds.Route("pow-root"),
                            Upstream = TestIds.Upstream("pow-upstream"),
                            ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = Difficulty, TokenLifetimeHours = 24 },
                            ProofOfWorkRouteOverride = true,
                        },
                    ],
                },
            ],
        };
        return PublicProxyTests.Compile(host, model);
    }

    private static HttpRequestMessage BrowserGet(Uri endpoint, string pathAndQuery)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(endpoint, pathAndQuery));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return request;
    }

    private static HttpClient ClientAt(string localAddress) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        ConnectCallback = async (context, cancellationToken) =>
        {
            Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.Bind(new IPEndPoint(IPAddress.Parse(localAddress), 0));
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        },
    })
    {
        Timeout = TimeSpan.FromSeconds(10),
    };

    private static string Solve(string challenge, int difficulty)
    {
        var hash = new byte[32];
        for (var n = 0; ; n++)
        {
            var text = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var bytes = Encoding.ASCII.GetBytes(string.Concat(challenge, ":", text));
            _ = SHA256.HashData(bytes, hash);
            var bits = 0;
            foreach (var value in hash)
            {
                if (value == 0)
                {
                    bits += 8;
                    continue;
                }

                bits += System.Numerics.BitOperations.LeadingZeroCount((uint)value) - 24;
                break;
            }

            if (bits >= difficulty)
            {
                return text;
            }
        }
    }

    /// <summary>The upstream host, disposed with the test.</summary>
    private sealed class Upstream((Microsoft.Extensions.Hosting.IHost Host, string Address) started) : IAsyncDisposable
    {
        public string Address { get; } = started.Address;

        public async ValueTask DisposeAsync()
        {
            await started.Host.StopAsync(Ct);
            started.Host.Dispose();
        }
    }
}
