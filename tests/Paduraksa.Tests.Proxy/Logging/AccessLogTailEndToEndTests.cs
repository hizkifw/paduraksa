using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Logging;
using Paduraksa.Tests.Proxy.Testing;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Logging;

/// <summary>Requests through a real listener, the real ring and the drain: what the tail shows, and what it never does.</summary>
public sealed class AccessLogTailEndToEndTests(TestUpstream upstream) : IClassFixture<TestUpstream>
{
    [Fact]
    public async Task RequestsShowUpWithTheirFieldsAndAHostWithTheLogOffNeverDoes()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", upstream.Address("a"))],
            Hosts =
            [
                ProxyHost("app", "app.test", Route("app-root", "web")),
                ProxyHost("off", "quiet.test", Route("off-root", "web")) with { AccessLog = false },
            ],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        var tail = server.Services.GetRequiredService<AccessLogTail>();
        using var subscription = tail.Subscribe(AccessLogTailFilter.All, 0)!;
        AccessLogDrainService drain = new(server.AccessLog, NullAccessLogSink.Instance, AccessLogFormatOptions.Default, NullLogger<AccessLogDrainService>.Instance, tail);
        // One connection: a request's line is queued from its completion callback, before the next request on it is served.
        using var client = ProxyTestServer.CreateClient(maxConnections: 1);

        var ct = TestContext.Current.CancellationToken;
        using (var known = await client.SendAsync(server.Request("app.test", "/api/items?token=hunter2&page=2", HttpMethod.Post), ct))
        {
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        }

        using (var quiet = await client.SendAsync(server.Request("quiet.test", "/hidden"), ct))
        {
            Assert.Equal(HttpStatusCode.OK, quiet.StatusCode);
        }

        using (var nobody = await client.SendAsync(server.Request("nobody.test", "/scan"), ct))
        {
            Assert.Equal(HttpStatusCode.NotFound, nobody.StatusCode);
        }

        // The last response's callback may trail the client's read by a moment.
        for (var attempt = 0; attempt < 2_000 && server.AccessLog.EntriesWritten < 2; attempt++)
        {
            await Task.Delay(1, ct);
        }

        drain.Drain();

        // A stray request to the listener (a port prober on a developer machine) may be in the stream too: the test reads its own.
        List<AccessLogTailEntry> seen = [];
        while (subscription.Reader.TryRead(out var entry))
        {
            seen.Add(entry);
        }

        var first = Assert.Single(seen, e => e.Path == "/api/items");
        var second = Assert.Single(seen, e => e.Path == "/scan");
        Assert.DoesNotContain(seen, e => e.Path == "/hidden");
        Assert.Equal("POST", first.Method);
        Assert.Equal("/api/items", first.Path);
        Assert.Equal("?token=REDACTED&page=2", first.Query);
        Assert.Equal(200, first.StatusCode);
        Assert.StartsWith("app.test", first.Host, StringComparison.Ordinal);
        Assert.Equal(["app.test"], first.MatchedHostNames);
        Assert.Equal("127.0.0.1", first.ClientAddress);
        Assert.Equal("HTTP/1.1", first.Protocol);
        Assert.False(string.IsNullOrEmpty(first.RequestId));
        Assert.StartsWith("http://127.0.0.1:", first.UpstreamAddress, StringComparison.Ordinal);
        Assert.True(first.UpstreamTicks >= 0 && first.TotalTicks >= first.UpstreamTicks);
        Assert.InRange(new DateTime(first.TimestampUtcTicks, DateTimeKind.Utc), DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddSeconds(1));

        Assert.Equal("/scan", second.Path);
        Assert.Equal(404, second.StatusCode);
        Assert.Null(second.MatchedHostNames);
        Assert.True(second.Sequence > first.Sequence);

        Assert.DoesNotContain(tail.Recent(AccessLogTailFilter.All, 100), entry => entry.Path == "/hidden");
        drain.Dispose();
    }
}
