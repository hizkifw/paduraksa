using System.Net;
using System.Net.Http.Headers;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.AccessLists;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>The line for password checks on a real listener: a burst is queued and served, the rest of the pipeline runs after the
/// wait (the upstream never sees the credentials), a full line is a 429 with <c>Retry-After</c>, and a client that hangs up leaves.</summary>
public sealed class AccessListQueueEndToEndTests(HeaderEchoUpstream upstream) : IClassFixture<HeaderEchoUpstream>
{
    private static readonly Paduraksa.Proxy.Net.IpKey Holder = Paduraksa.Proxy.Net.IpKey.Parse("203.0.113.250");

    private ProxyConfigModel Model() => AccessListFixtures.Model(List("office")) with { Upstreams = [TestSnapshots.Upstream("web", upstream.Address)] };

    private static async Task<ProxyTestServer> StartAsync(ProxyConfigModel model, AccessListRuntime runtime) =>
        await ProxyTestServer.StartAsync(model, new() { CompilerOptions = new SnapshotCompilerOptions { AccessLists = runtime } });

    private static HttpRequestMessage Login(ProxyTestServer server, string password)
    {
        var request = server.Request("acl.test");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"alice:{password}")));
        return request;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition never held");
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ABurstOfColdLoginsIsQueuedServedAndNeverShownToTheUpstream()
    {
        var runtime = AccessListFixtures.NewRuntime(TimeProvider.System, maxConcurrentVerifications: 2, maxPlacesPerClient: 64);
        await using var server = await StartAsync(Model(), runtime);
        using var client = ProxyTestServer.CreateClient(maxConnections: 16);
        Assert.Equal(VerificationSlot.Granted, await runtime.Queue.EnterAsync(Holder, TestContext.Current.CancellationToken));
        Assert.Equal(VerificationSlot.Granted, await runtime.Queue.EnterAsync(Holder, TestContext.Current.CancellationToken));

        var requests = Enumerable.Range(0, 10).Select(async _ =>
        {
            using var request = Login(server, Password);
            using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
            return (response.StatusCode, Body: await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }).ToArray();
        await WaitUntilAsync(() => runtime.Queue.Queued == 10);
        runtime.Queue.Exit(Holder);
        runtime.Queue.Exit(Holder);

        foreach (var (status, body) in await Task.WhenAll(requests))
        {
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(HeaderEchoUpstream.Parse(body).ContainsKey("Authorization"));
        }

        Assert.Equal((0, 10), (runtime.Stats.Busy, runtime.Stats.Queued));
    }

    [Fact]
    public async Task AFullLineIsA429WithRetryAfterAndAWrongPasswordThatWaitedIsA401()
    {
        var runtime = AccessListFixtures.NewRuntime(TimeProvider.System, maxConcurrentVerifications: 1, maxPlacesPerClient: 64, maxQueued: 1);
        await using var server = await StartAsync(Model(), runtime);
        using var client = ProxyTestServer.CreateClient();
        Assert.Equal(VerificationSlot.Granted, await runtime.Queue.EnterAsync(Holder, TestContext.Current.CancellationToken));
        using var waitingRequest = Login(server, "wrong");
        var waiting = client.SendAsync(waitingRequest, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);

        using var refusedRequest = Login(server, Password);
        using var refused = await client.SendAsync(refusedRequest, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(1), refused.Headers.RetryAfter!.Delta);
        runtime.Queue.Exit(Holder);
        using var answered = await waiting;
        Assert.Equal(HttpStatusCode.Unauthorized, answered.StatusCode);
    }

    [Fact]
    public async Task AClientThatHangsUpWhileWaitingLeavesTheLine()
    {
        var runtime = AccessListFixtures.NewRuntime(TimeProvider.System, maxConcurrentVerifications: 1, maxPlacesPerClient: 64);
        await using var server = await StartAsync(Model(), runtime);
        using var client = ProxyTestServer.CreateClient();
        Assert.Equal(VerificationSlot.Granted, await runtime.Queue.EnterAsync(Holder, TestContext.Current.CancellationToken));
        using CancellationTokenSource hangUp = new();
        using var request = Login(server, Password);
        var abandoned = client.SendAsync(request, hangUp.Token);
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);

        await hangUp.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await WaitUntilAsync(() => runtime.Queue.Queued == 0);
        runtime.Queue.Exit(Holder);
        Assert.Equal((0, 0, 0), (runtime.Queue.Running, runtime.Stats.Verifications, runtime.Stats.Busy));
    }
}
