using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>
/// Spike S6, rate-limit part: under steady load on one limited route, with a publish every 100 ms, the buckets are
/// carried over untouched. The 2xx count over the window stays within burst + rate x elapsed (a reset would hand out a
/// fresh burst on every publish), and 429s keep coming throughout. A lowered burst applies on the bucket's next touch.
/// </summary>
public sealed class RateLimitContinuityTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    private static readonly TimeSpan LoadDuration = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PublishEvery = TimeSpan.FromMilliseconds(100);

    private static RateLimitPolicyId Policy => TestIds.RateLimitPolicy("s6");
    private static HostId LimitedHost => TestIds.Host("s6-limited");

    [Fact]
    public async Task BucketsSurvivePublishesUnderLoad()
    {
        var ct = TestContext.Current.CancellationToken;
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        await using var server = await ProxyTestServer.StartAsync(
            Model(burst: 5, policyName: "budget", extraHost: false),
            new() { RateLimits = runtime, CompilerOptions = new SnapshotCompilerOptions { RateLimits = runtime } });
        using var client = ProxyTestServer.CreateClient(maxConnections: 4);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ConcurrentQueue<int> statuses = new();
        var allowed = 0;
        var limited = 0;
        var elapsed = Stopwatch.StartNew();

        var load = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    using var response = await client.SendAsync(server.Request("s6.test", "/"), ct);
                    var status = (int)response.StatusCode;
                    statuses.Enqueue(status);
                    if (status == StatusCodes.Status200OK)
                    {
                        Interlocked.Increment(ref allowed);
                    }
                    else if (status == StatusCodes.Status429TooManyRequests)
                    {
                        Interlocked.Increment(ref limited);
                    }

                    await response.Content.LoadIntoBufferAsync(ct);
                }
            },
            ct);

        var publishes = 0;
        var publisher = Task.Run(
            async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var started = Stopwatch.StartNew();
                    // Every other publish changes something unrelated to the pair: a host comes and goes, and the
                    // policy's label changes. The numbers stay the same, which is the edit §6.3 calls "carried over".
                    var result = await server.PublishAsync(Model(burst: 5, policyName: (++publishes) % 2 == 0 ? "budget" : "budget (renamed)", extraHost: publishes % 3 == 1));
                    Assert.True(result.Succeeded, string.Join("; ", result.Errors));
                    var rest = PublishEvery - started.Elapsed;
                    if (rest > TimeSpan.Zero && !stop.IsCancellationRequested)
                    {
                        await Task.Delay(rest, CancellationToken.None);
                    }
                }
            },
            ct);

        await Task.Delay(LoadDuration, ct);
        await stop.CancelAsync();
        await Task.WhenAll(load, publisher);
        elapsed.Stop();

        Assert.True(publishes >= 10, $"only {publishes} publishes in {LoadDuration}");
        Assert.True(limited >= 50, $"only {limited} refusals: {string.Join(", ", statuses.Take(20))}");

        // burst + rate x elapsed, plus a small tolerance for the load loop's own scheduling: a publish that reset the
        // bucket would have let another five through every 100 ms, far past this.
        var bound = 5 + elapsed.Elapsed.TotalSeconds + 3;
        Assert.True(allowed <= bound, $"{allowed} allowed in {elapsed.Elapsed.TotalSeconds:F2} s, bound {bound:F2}");

        // Refusals keep coming to the end, not just before the first publish.
        Assert.Contains(StatusCodes.Status429TooManyRequests, statuses.TakeLast(50));
    }

    [Fact]
    public async Task ALoweredBurstAppliesOnTheNextTouch()
    {
        var ct = TestContext.Current.CancellationToken;
        RateLimitRuntime runtime = new(new RateLimitTable(64));
        var options = new ProxyTestServerOptions { RateLimits = runtime, CompilerOptions = new SnapshotCompilerOptions { RateLimits = runtime } };
        await using var server = await ProxyTestServer.StartAsync(Model(burst: 20, policyName: "budget", extraHost: false), options);
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        // Spend one token of the twenty.
        using (var first = await client.SendAsync(server.Request("s6.test", "/"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        // Lower the burst to two and keep the same policy: the bucket's nineteen tokens are clamped to two on the next
        // touch, so only two more requests get through before the rate has to earn another.
        Assert.True((await server.PublishAsync(Model(burst: 2, policyName: "budget", extraHost: false))).Succeeded);

        var allowed = 0;
        for (var i = 0; i < 10; i++)
        {
            using var response = await client.SendAsync(server.Request("s6.test", "/"), ct);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                allowed++;
            }
        }

        Assert.InRange(allowed, 1, 2);
    }

    private ProxyConfigModel Model(int burst, string policyName, bool extraHost) => new()
    {
        Upstreams = [Upstream("web", upstream.Address("s6"))],
        RateLimitPolicies =
        [
            // One token a second: the bucket is not refilled within either test's window.
            new RateLimitPolicySpec { Id = Policy, Name = policyName, Parameters = new RateLimitParameters(1, burst) },
        ],
        Hosts =
        [
            new HostSpec
            {
                Id = LimitedHost,
                Names = ["s6.test"],
                Https = false,
                Routes = [Route("s6-root", "web") with { RateLimit = Policy, RateLimitRouteOverride = true }],
            },
            .. extraHost
                ? [new HostSpec { Id = TestIds.Host("s6-extra"), Names = ["extra.test"], Https = false, Routes = [Route("s6-extra-root", "web")] }]
                : (HostSpec[])[],
        ],
    };
}
