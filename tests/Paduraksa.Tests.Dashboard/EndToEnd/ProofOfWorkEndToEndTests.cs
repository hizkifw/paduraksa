using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Server.Hosting;

using static Microsoft.Playwright.Assertions;

namespace Paduraksa.Tests.Dashboard.EndToEnd;

/// <summary>
/// The page in a real Chromium: a PoW-protected host at 10 bits answers the challenge, the page's own JavaScript (pure-JS
/// SHA-256 in Blob workers) finds the solution, posts it, takes the cookie and lands on the upstream's page. It also pins
/// the CSP hashes against the browser: a wrong hash is a console violation and the solve never happens.
/// </summary>
[Collection(PlaywrightBrowser.CollectionName)]
public sealed class ProofOfWorkEndToEndTests
{
    private const int Difficulty = 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ChromiumSolvesTheChallengeAndReachesTheUpstream()
    {
        var (playwright, browser) = await PlaywrightBrowser.LaunchOrSkipAsync();
        using var ownedPlaywright = playwright;
        await using var ownedBrowser = browser;
        await using var host = await DashboardHost.StartAsync(Ct);
        await using var upstream = await Upstream.StartAsync(Ct);
        await PublishProtectedHostAsync(host.Host, upstream.Address);

        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1100, Height = 800 },
        });
        var page = await context.NewPageAsync();
        var problems = PlaywrightBrowser.CollectProblems(page);
        var address = PublicHttpAddress(host.Host);

        // The page's script solves the challenge and navigates on its own, so the body of the browser's own response may be gone by the time
        // a test reads it. The challenge as served is read by a plain request (no script, no cookie), with the browser's Accept.
        var challenge = await context.APIRequest.GetAsync(address, new() { Headers = new Dictionary<string, string> { ["Accept"] = "text/html" } });
        Assert.Equal(200, challenge.Status);
        var challengeHtml = await challenge.TextAsync();
        Assert.Contains("Checking your browser", challengeHtml, StringComparison.Ordinal);
        Assert.Contains("Enable JavaScript to continue", challengeHtml, StringComparison.Ordinal);
        Assert.Equal("no-store", challenge.Headers["cache-control"]);

        var first = await page.GotoAsync(address);
        Assert.NotNull(first);
        Assert.Equal(200, first.Status);
        await Expect(page.Locator("body")).ToContainTextAsync("the upstream page", new() { Timeout = 30_000 });
        var cookies = await context.CookiesAsync();
        var cookie = Assert.Single(cookies, cookie => cookie.Name == "paduraksa_pow");
        Assert.Equal("/", cookie.Path);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(SameSiteAttribute.Lax, cookie.SameSite);
        Assert.False(cookie.Secure);

        // The token lasts: a second visit goes straight through.
        await page.GotoAsync(address);
        await Expect(page.Locator("body")).ToContainTextAsync("the upstream page", new() { Timeout = 15_000 });

        Assert.Empty(problems);
    }

    private static async Task PublishProtectedHostAsync(PaduraksaHost host, string upstreamAddress)
    {
        var upstreamId = UpstreamId.New();
        ProxyConfigModel model = new()
        {
            Upstreams =
            [
                new UpstreamSpec { Id = upstreamId, Destinations = [new DestinationSpec(DestinationId.New(), upstreamAddress)] },
            ],
            Hosts =
            [
                new HostSpec
                {
                    Id = HostId.New(),
                    Names = ["127.0.0.1"],
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = Difficulty, TokenLifetimeHours = 24 },
                    Routes =
                    [
                        new RouteSpec { Id = RouteId.New(), Upstream = upstreamId },
                    ],
                },
            ],
        };

        var options = host.Shared.PublicListeners.CompilerOptions with { Certificates = host.Shared.Certificates };
        var compiled = SnapshotCompiler.Compile(model, options);
        Assert.True(compiled.Succeeded, string.Join("; ", compiled.Errors));
        var published = await host.Shared.SnapshotPublisher.PublishAsync(compiled.Snapshot!, Ct);
        Assert.True(published.Succeeded, string.Join("; ", published.Errors));
    }

    private static string PublicHttpAddress(PaduraksaHost host)
    {
        var address = host.GetListenAddresses(HostRole.Public).Single(a => a.StartsWith("http://", StringComparison.Ordinal));
        return new UriBuilder(address) { Host = IPAddress.Loopback.ToString() }.Uri.ToString();
    }

    /// <summary>A loopback upstream whose page says where the request arrived.</summary>
    private sealed class Upstream : IAsyncDisposable
    {
        private readonly IHost _host;

        private Upstream(IHost host, string address)
        {
            _host = host;
            Address = address;
        }

        public string Address { get; }

        public static async Task<Upstream> StartAsync(CancellationToken cancellationToken)
        {
            var host = new HostBuilder()
                .ConfigureLogging(static logging => logging.ClearProviders())
                .ConfigureWebHost(web => web
                    .UseKestrel(static kestrel =>
                    {
                        kestrel.Listen(IPAddress.Loopback, 0);
                        kestrel.AddServerHeader = false;
                    })
                    .Configure(app => app.Run(static context => context.Response.WriteAsync("the upstream page"))))
                .Build();
            await host.StartAsync(cancellationToken);
            var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
            return new Upstream(host, address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _host.StopAsync(Ct);
            _host.Dispose();
        }
    }
}
