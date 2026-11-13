using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Commands;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Integration.Config.ConfigTestSupport;

namespace Paduraksa.Tests.Integration.Hosting;

/// <summary>The edge-range refresh job inside a real host, against a fake Cloudflare on loopback: it fetches, stores, republishes
/// the live config without a revision, and a restart keeps what it stored.</summary>
public sealed class CloudflareRangeRefreshHostTests
{
    private static readonly string[] Common = ["173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "104.16.0.0/13"];
    private static readonly string[] CommonV6 = ["2400:cb00::/32", "2606:4700::/32", "2803:f800::/32"];

    [Fact]
    public async Task AFetchedListIsStoredPublishedWithoutARevisionAndKeptAcrossARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cloudflare = await FakeCloudflare.StartAsync(["192.0.2.0/24"], ct);
        var dataDirectory = TestDataDirectories.NewPath();
        var settings = new Dictionary<string, string?>
        {
            ["Paduraksa:Cloudflare:RangeRefresh:ApiUrl"] = cloudflare.Url,
            ["Paduraksa:Cloudflare:RangeRefresh:StartDelay"] = "00:00:00",
            ["Paduraksa:Cloudflare:RangeRefresh:Interval"] = "01:00:00",
        };

        await using (var host = TestHost.Create(settings: settings, dataDirectory: dataDirectory))
        {
            await host.StartAsync(ct);

            // The first fetch ran at startup, before any config trusted the edge: nothing to publish, but the list is in force.
            await WaitAsync(() => host.Shared.EdgeRanges.Current.Origin == EdgeRangeOrigin.Fetched, ct);
            Assert.Null(host.Shared.SnapshotStore.Current.EdgeRanges);
            Assert.True(File.Exists(Path.Combine(dataDirectory, CloudflareRangeFile.FileName)));

            // A config that trusts the edge compiles with it.
            var revision = await CommitAsync(host, new UpdateSettingsCommand((await host.Shared.Config.GetCurrentAsync(ct)).Document.Settings with
            {
                TrustedProxies = new TrustedProxySettings(TrustedProxyMode.CloudflareProxy, []),
            }), ct);
            var live = host.Shared.SnapshotStore.Current;
            Assert.Equal(EdgeRangeOrigin.Fetched, live.EdgeRanges!.Origin);
            Assert.True(live.TrustedProxies.IsTrustedPeer(IpKey.Parse("192.0.2.9")));
            Assert.Equal(CloudflareRangeSource.Fetched, host.Shared.RangeReport.Current.Source);
            Assert.Equal(Common.Length + CommonV6.Length + 1, host.Shared.RangeReport.Current.Count);

            // A changed list reaches the live config with no new revision.
            cloudflare.Set(["192.0.2.0/24", "198.51.100.0/24"]);
            var generation = live.Generation;
            Assert.Equal(RangeRefreshResult.Replaced, await host.Shared.RangeRefresher!.RefreshOnceAsync(ct));
            await WaitAsync(() => host.Shared.SnapshotStore.Current.Generation > generation, ct);
            Assert.True(host.Shared.SnapshotStore.Current.TrustedProxies.IsTrustedPeer(IpKey.Parse("198.51.100.9")));
            Assert.Equal(revision.Seq, (await host.Shared.Config.GetCurrentAsync(ct)).Revision!.Seq);

            // The same list again changes nothing live.
            generation = host.Shared.SnapshotStore.Current.Generation;
            Assert.Equal(RangeRefreshResult.Unchanged, await host.Shared.RangeRefresher.RefreshOnceAsync(ct));
            Assert.Equal(generation, host.Shared.SnapshotStore.Current.Generation);

            await host.StopAsync(ct);
        }

        // A restart with the refresh off still trusts the stored list from its first compile.
        cloudflare.Fail();
        await using var again = TestHost.Create(
            settings: new Dictionary<string, string?> { ["Paduraksa:Cloudflare:RangeRefresh:Enabled"] = "false" },
            dataDirectory: dataDirectory);
        await again.StartAsync(ct);
        Assert.Null(again.Shared.RangeRefresher);
        Assert.Equal(EdgeRangeOrigin.Fetched, again.Shared.EdgeRanges.Current.Origin);
        Assert.Equal(EdgeRangeOrigin.Fetched, again.Shared.SnapshotStore.Current.EdgeRanges!.Origin);
        Assert.True(again.Shared.SnapshotStore.Current.TrustedProxies.IsTrustedPeer(IpKey.Parse("198.51.100.9")));
    }

    [Fact]
    public async Task AFailingCloudflareKeepsTheBuiltInListAndCountsTheFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var cloudflare = await FakeCloudflare.StartAsync(["192.0.2.0/24"], ct);
        cloudflare.Fail();
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Paduraksa:Cloudflare:RangeRefresh:ApiUrl"] = cloudflare.Url,
            ["Paduraksa:Cloudflare:RangeRefresh:TextV4Url"] = cloudflare.Url + "v4",
            ["Paduraksa:Cloudflare:RangeRefresh:TextV6Url"] = cloudflare.Url + "v6",
            ["Paduraksa:Cloudflare:RangeRefresh:StartDelay"] = "01:00:00",
        });
        await host.StartAsync(ct);

        Assert.Equal(RangeRefreshResult.Failed, await host.Shared.RangeRefresher!.RefreshOnceAsync(ct));

        Assert.Same(EdgeRangeList.BuiltIn, host.Shared.EdgeRanges.Current);
        Assert.Equal(1, host.Shared.RangeReport.Current.ConsecutiveFailures);
        Assert.Equal(CloudflareRangeSource.BuiltIn, host.Shared.RangeReport.Current.Source);
    }

    [Theory]
    [InlineData("Paduraksa:Cloudflare:RangeRefresh:ApiUrl", "http://example.com/ips")]
    [InlineData("Paduraksa:Cloudflare:RangeRefresh:ApiUrl", "ftp://example.com/ips")]
    [InlineData("Paduraksa:Cloudflare:RangeRefresh:Interval", "00:00:10")]
    [InlineData("Paduraksa:Cloudflare:RangeRefresh:StartDelay", "03:00:00")]
    [InlineData("Paduraksa:Cloudflare:OriginPullCaFile", "/no/such/ca.pem")]
    public void InvalidCloudflareOptionsFailBeforeAnythingIsBuilt(string key, string value)
    {
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => TestHost.Create(settings: new Dictionary<string, string?> { [key] = value }));
        Assert.NotEmpty(ex.Failures);
    }

    [Fact]
    public async Task AReplacementOriginPullCaIsLoadedAndAFileThatHoldsNoCertificateStopsTheStart()
    {
        var ct = TestContext.Current.CancellationToken;
        using TempDirectory directory = new();
        var good = Path.Combine(directory.Path, "ca.pem");
        await File.WriteAllTextAsync(good, "# a replacement\n" + OriginPullCa.Default[0].ExportCertificatePem() + "\n", ct);

        await using (var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Cloudflare:OriginPullCaFile"] = good }))
        {
            Assert.NotNull(host.Shared.PublicListeners.CompilerOptions.OriginPullCa);
            Assert.Single(host.Shared.PublicListeners.CompilerOptions.OriginPullCa!);
        }

        var empty = Path.Combine(directory.Path, "empty.pem");
        await File.WriteAllTextAsync(empty, "no certificate here", ct);
        var ex = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(
            () => TestHost.Create(settings: new Dictionary<string, string?> { ["Paduraksa:Cloudflare:OriginPullCaFile"] = empty }));
        Assert.Contains("OriginPullCaFile", Assert.Single(ex.Failures), StringComparison.Ordinal);
    }

    private static async Task WaitAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            // Poll step of a wait bounded to 15 s.
            await Task.Delay(10, ct);
        }
    }

    /// <summary>A Cloudflare on loopback: <c>/</c> answers the JSON list with the common ranges plus the ones set.</summary>
    private sealed class FakeCloudflare : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private string[] _extra;
        private bool _failing;

        private FakeCloudflare(WebApplication app, string[] extra, string url)
        {
            _app = app;
            _extra = extra;
            Url = url;
        }

        public string Url { get; }

        public void Set(string[] extra) => _extra = extra;

        public void Fail() => _failing = true;

        public static async Task<FakeCloudflare> StartAsync(string[] extra, CancellationToken ct)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            FakeCloudflare? fake = null;
            app.MapGet("/", (HttpContext context) =>
            {
                if (fake!._failing)
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                var v4 = string.Join(',', Common.Concat(fake._extra).Select(b => $"\"{b}\""));
                var v6 = string.Join(',', CommonV6.Select(b => $"\"{b}\""));
                return Results.Text($$"""{"result":{"ipv4_cidrs":[{{v4}}],"ipv6_cidrs":[{{v6}}]},"success":true}""", "application/json");
            });
            app.MapFallback(() => Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
            await app.StartAsync(ct);
            var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single() + "/";
            fake = new FakeCloudflare(app, extra, url);
            return fake;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync(CancellationToken.None);
            await _app.DisposeAsync();
        }
    }
}
