using System.Net;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.AccessLists;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.AccessLists.AccessListFixtures;

namespace Paduraksa.Tests.Proxy.AccessLists;

/// <summary>A request that needs a PBKDF2 waits in a bounded line for a slot instead of being refused, then goes on through the
/// rest of the gate as if the answer had been immediate. The line's own mechanics are in <see cref="AccessListVerificationQueueTests"/>.</summary>
public sealed class AccessListQueueGateTests
{
    private static readonly IpKey Holder = IpKey.Parse("203.0.113.250");

    private static AccessListRuntime Runtime(int slots, int maxQueued = 64, int places = AccessListRuntime.DefaultMaxPlacesPerClient, TimeSpan? maxWait = null) =>
        AccessListFixtures.NewRuntime(TimeProvider.System, maxConcurrentVerifications: slots, maxPlacesPerClient: places, maxQueued: maxQueued, maxWait: maxWait);

    private static async Task HoldAsync(AccessListRuntime runtime, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            Assert.Equal(VerificationSlot.Granted, await runtime.Queue.EnterAsync(Holder, TestContext.Current.CancellationToken));
        }
    }

    private static void Release(AccessListRuntime runtime, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            runtime.Queue.Exit(Holder);
        }
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
    public async Task AColdBurstLargerThanTheSlotsIsQueuedNotRefused()
    {
        var runtime = Runtime(slots: 2);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 2);

        var logins = Enumerable.Range(1, 12).Select(n => RunAsync(gate, $"192.0.2.{n}", Basic("alice", Password))).ToArray();
        await WaitUntilAsync(() => runtime.Queue.Queued == 12);
        Release(runtime, slots: 2);

        Assert.All(await Task.WhenAll(logins), outcome => Assert.Equal("pass", outcome));
        Assert.Equal((0, 12, 12), (runtime.Stats.Busy, runtime.Stats.Queued, runtime.Stats.Allowed));
        Assert.Equal((0, 0), (runtime.Queue.Running, runtime.Queue.Queued));
    }

    [Fact]
    public async Task ARequestThatWaitsFindsTheCredentialsAnotherOneJustVerifiedAndSkipsItsOwnPbkdf2()
    {
        var runtime = Runtime(slots: 1, places: 8);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);

        // A page's burst: the same credentials from the same client, all cold.
        var requests = Enumerable.Range(0, 6).Select(_ => RunAsync(gate, "192.0.2.1", Basic("alice", Password))).ToArray();
        await WaitUntilAsync(() => runtime.Queue.Queued == 6);
        Release(runtime, slots: 1);

        Assert.All(await Task.WhenAll(requests), outcome => Assert.Equal("pass", outcome));
        Assert.Equal((1, 5), (runtime.Stats.Verifications, runtime.Stats.CacheHits));
    }

    [Fact]
    public async Task OneClientFloodingWrongPasswordsFillsOnlyItsOwnPlacesAndAnotherClientStillLogsIn()
    {
        var runtime = Runtime(slots: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);

        var spray = Enumerable.Range(0, 40).Select(_ => RunAsync(gate, "198.51.100.66", Basic("alice", "wrong"))).ToArray();
        var places = AccessListRuntime.DefaultMaxPlacesPerClient;
        await WaitUntilAsync(() => runtime.Stats.QueueFull == 40 - places && runtime.Queue.Queued == places);
        var member = RunAsync(gate, "203.0.113.9", Basic("alice", Password));
        await WaitUntilAsync(() => runtime.Queue.Queued == places + 1);

        // The rest were refused at once and none of the member's place was taken.
        Assert.Equal(40 - places, spray.Count(task => task.IsCompleted));
        Assert.All(spray.Where(task => task.IsCompleted), task => Assert.Equal("429", task.Result));

        Release(runtime, slots: 1);

        Assert.Equal("pass", await member);
        Assert.Equal(places, (await Task.WhenAll(spray)).Count(outcome => outcome == "401"));
        Assert.Equal((40 - places, 0), (runtime.Stats.Busy, runtime.Stats.QueueTimeouts));
    }

    [Fact]
    public async Task ABurstOfTwelveColdLoginsFromOneClientAllSucceedAndPayOnePbkdf2()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        // An HTTP/2 page after the cache expired: a dozen credentialed requests at once.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => RunAsync(gate, "192.0.2.1", Basic("alice", Password)), TestContext.Current.CancellationToken)));

        Assert.All(outcomes, outcome => Assert.Equal("pass", outcome));
        Assert.Equal((0, 1), (runtime.Stats.Busy, runtime.Stats.Verifications));
        Assert.Equal((0, 0), (runtime.Queue.Running, runtime.Queue.Queued));
    }

    [Fact]
    public async Task ABurstOfTwelveChecksThatCanNotShareOneFromOneClientIsQueuedNotRefused()
    {
        var runtime = AccessListFixtures.NewRuntime();
        var gate = GateFor(runtime, Model(List("office")), out _);

        // Twelve names nobody has: each is its own PBKDF2, and one client runs only two at once, so the rest wait for their turn.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 12).Select(n => Task.Run(() => RunAsync(gate, "192.0.2.1", Basic($"nobody{n}", "x")), TestContext.Current.CancellationToken)));

        Assert.All(outcomes, outcome => Assert.Equal("401", outcome));
        Assert.Equal((0, 12), (runtime.Stats.Busy, runtime.Stats.Verifications));
    }

    [Fact]
    public async Task IdenticalCredentialsThatAreBeingCheckedNowWaitForThatCheckInsteadOfStartingAnother()
    {
        var runtime = Runtime(slots: 4);
        var gate = GateFor(runtime, Model(List("office")), out _);

        // Slots to spare and room under the client's running cap, and still only one of the six runs a PBKDF2.
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => RunAsync(gate, "192.0.2.1", Basic("alice", Password)), TestContext.Current.CancellationToken)));

        Assert.All(outcomes, outcome => Assert.Equal("pass", outcome));
        Assert.Equal(1, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task AFullLineAnswers429WithRetryAfterAndTheRestOfTheLineStillGoesThrough()
    {
        var runtime = Runtime(slots: 1, maxQueued: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);
        var waiting = RunAsync(gate, "192.0.2.1", Basic("alice", Password));
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);

        var refused = Context("192.0.2.2", Basic("alice", Password));
        Assert.Equal("429", await RunAsync(gate, refused));

        Assert.Equal("1", refused.Response.Headers.RetryAfter.ToString());
        Assert.Equal((1, 1), (runtime.Stats.QueueFull, runtime.Stats.Busy));
        Release(runtime, slots: 1);
        Assert.Equal("pass", await waiting);
    }

    [Fact]
    public async Task ARequestThatWaitsTooLongIsAnswered429WithRetryAfter()
    {
        var runtime = Runtime(slots: 1, maxWait: TimeSpan.FromMilliseconds(150));
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);
        var context = Context("192.0.2.1", Basic("alice", Password));

        Assert.Equal("429", await RunAsync(gate, context).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Equal("1", context.Response.Headers.RetryAfter.ToString());
        Assert.Equal((1, 1, 0), (runtime.Stats.QueueTimeouts, runtime.Stats.Busy, runtime.Stats.Verifications));
        Assert.Equal(0, runtime.Queue.Queued);
        Release(runtime, slots: 1);
    }

    [Fact]
    public async Task ARequestWhoseClientWentAwayLeavesTheLineAtOnceAndNothingIsAnswered()
    {
        var runtime = Runtime(slots: 1, places: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);
        using CancellationTokenSource gone = new();
        var context = Context("192.0.2.1", Basic("alice", Password));
        context.RequestAborted = gone.Token;
        var abandoned = RunAsync(gate, context);
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);

        await gone.CancelAsync();

        Assert.Equal("drop", await abandoned.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal((0, 0), (runtime.Queue.Queued, runtime.Stats.Verifications));

        // Its client's one place is free again.
        var retry = RunAsync(gate, "192.0.2.1", Basic("alice", Password));
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);
        Release(runtime, slots: 1);
        Assert.Equal("pass", await retry);
    }

    [Fact]
    public async Task AWrongPasswordThatWaitedIsStillA401AndNothingIsRemembered()
    {
        var runtime = Runtime(slots: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        await HoldAsync(runtime, slots: 1);
        var login = RunAsync(gate, "192.0.2.1", Basic("alice", "wrong"));
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);

        Release(runtime, slots: 1);

        Assert.Equal("401", await login);
        Assert.Equal((1, 0), (runtime.Stats.Unauthorized, AccessListFixtures.Cached(runtime)));
    }

    [Fact]
    public async Task AnAbsentCredentialAndAPasswordInTheCacheNeverWait()
    {
        var runtime = Runtime(slots: 1);
        var gate = GateFor(runtime, Model(List("office")), out _);
        Assert.Equal("pass", await RunAsync(gate, "192.0.2.1", Basic("alice", Password)));
        await HoldAsync(runtime, slots: 1);

        Assert.Equal("401", await RunAsync(gate, "192.0.2.2"));
        Assert.Equal("401", await RunAsync(gate, "192.0.2.2", "Basic ???"));
        Assert.Equal("pass", await RunAsync(gate, "192.0.2.1", Basic("alice", Password)));

        Assert.Equal((0, 0), (runtime.Queue.Queued, runtime.Stats.Busy));
        Release(runtime, slots: 1);
    }

    [Fact]
    public void AMissThatFindsASlotFreeAllocatesLittleMoreThanARefusal()
    {
        // The check and the answer are all that is left of the miss: with a slot free nothing waits, so nothing is queued or boxed.
        // Compared with the 401 a missing credential gets, which is the answer's own cost. Bounded generously because a Debug
        // build's async state machines are classes; in Release the difference measures about zero.
        var runtime = Runtime(slots: 8, places: 1000);
        var gate = GateFor(runtime, Model(List("office")), out _);
        const int Warmup = 20;
        const int Measured = 30;

        long Average(Func<int, string?> authorization)
        {
            var contexts = new DefaultHttpContext[Warmup + Measured];
            for (var i = 0; i < contexts.Length; i++)
            {
                contexts[i] = Context($"192.0.2.{1 + i}", authorization(i));
                _ = contexts[i].RequestAborted;
            }

            long total = 0;
            for (var i = 0; i < contexts.Length; i++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var task = gate.InvokeAsync(contexts[i], static _ => Task.CompletedTask);
                var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(task.IsCompletedSuccessfully, "the request should have run to the end without waiting");
                total += i >= Warmup ? allocated : 0;
            }

            return total / Measured;
        }

        var refusal = Average(static _ => null);
        var miss = Average(static i => Basic("alice", "wrong" + i));

        Assert.True(miss - refusal < 1024, $"a miss allocated {miss} B, a refusal {refusal} B");
        Assert.Equal(Warmup + Measured, runtime.Stats.Verifications);
    }

    [Fact]
    public async Task ARateLimitAfterTheListStillAppliesToARequestThatWaited()
    {
        var policy = TestIds.RateLimitPolicy("api");
        RateLimitRuntime limits = new(new RateLimitTable(64));
        var runtime = Runtime(slots: 1);
        var list = List("office");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            RateLimitPolicies = [new RateLimitPolicySpec { Id = policy, Name = "api", Parameters = new(1, 1), Key = RateLimitKey.Header, Header = "x-api-key" }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("rl"),
                    Names = ["acl.test"],
                    Https = false,
                    Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)], RateLimit = policy, RateLimitRouteOverride = true }],
                },
            ],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime, RateLimits = limits });
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        await HoldAsync(runtime, slots: 1);

        // Two clients, so two cold logins, sharing one rate-limit bucket by their API key.
        var first = Context("192.0.2.1", Basic("alice", Password));
        first.Request.Headers["x-api-key"] = "k";
        var second = Context("192.0.2.2", Basic("alice", Password));
        second.Request.Headers["x-api-key"] = "k";
        var firstRun = RunAsync(gate, first);
        await WaitUntilAsync(() => runtime.Queue.Queued == 1);
        var secondRun = RunAsync(gate, second);
        await WaitUntilAsync(() => runtime.Queue.Queued == 2);
        Release(runtime, slots: 1);

        Assert.Equal("pass", await firstRun);
        Assert.Equal("429", await secondRun);
        Assert.Equal(2, runtime.Stats.Verifications);
        Assert.Equal((1, 1), (limits.Stats.Allowed, limits.Stats.Limited));
    }

    [Fact]
    public async Task AProofOfWorkGateAfterTheListStillChallengesARequestThatWaited()
    {
        var pow = new ProofOfWorkRuntime(new FixedTime(Now));
        pow.UseKey(new TestMacKey());
        var runtime = Runtime(slots: 1);
        var list = List("office");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("pow"),
                    Names = ["acl.test"],
                    Https = false,
                    ProofOfWork = new HostProofOfWorkSpec { Enabled = true, Difficulty = 10, TokenLifetimeHours = 168 },
                    Routes = [TestSnapshots.Route("root", "web") with { Policies = [new AccessListPolicySpec(list.Id)] }],
                },
            ],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime, ProofOfWork = pow });
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource(), challenges: null, pow);
        await HoldAsync(runtime, slots: 1);

        // Right password, no token: the list lets it in after the wait and the challenge still stands in the way.
        var browser = Context("192.0.2.1", Basic("alice", Password));
        browser.Request.Headers.Accept = "text/html";
        var script = Context("192.0.2.2", Basic("alice", Password));
        var browserRun = RunAsync(gate, browser);
        var scriptRun = RunAsync(gate, script);
        await WaitUntilAsync(() => runtime.Queue.Queued == 2);
        Release(runtime, slots: 1);

        Assert.Equal("200", await browserRun);
        Assert.Equal("403", await scriptRun);
        Assert.Equal((1, 1), (pow.Stats.ChallengesServed, pow.Stats.BlockedNonBrowser));
        Assert.Equal(2, runtime.Stats.Allowed);

        // With a valid token for the client the same cold request goes through to the upstream.
        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        Assert.True(ProofOfWorkToken.TryEncode(pow.Key!, snapshot.HostsByIndex[0].ProofOfWorkHostScope, IpKey.Parse("192.0.2.3"), (uint)(Now.ToUnixTimeSeconds() + 3600), 10, token, out var written));
        var solved = Context("192.0.2.3", Basic("alice", Password));
        solved.Request.Headers.Accept = "text/html";
        solved.Request.Headers.Cookie = $"{ProofOfWorkToken.CookieName}={token[..written]}";

        Assert.Equal("pass", await RunAsync(gate, solved));
    }

    [Fact]
    public async Task AListOnAHostChainResumesTheHostsFixedAnswerAfterTheWait()
    {
        var runtime = Runtime(slots: 1);
        var list = List("office");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AccessLists = [list],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("moved"),
                    Names = ["acl.test"],
                    Https = false,
                    Kind = HostKind.Respond,
                    Respond = new RespondSpec { StatusCode = 200, Body = "hello" },
                    Policies = [new AccessListPolicySpec(list.Id)],
                },
            ],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { AccessLists = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        await HoldAsync(runtime, slots: 1);

        var wrong = RunAsync(gate, "192.0.2.1", Basic("alice", "wrong"));
        var right = RunAsync(gate, "192.0.2.2", Basic("alice", Password));
        await WaitUntilAsync(() => runtime.Queue.Queued == 2);
        Release(runtime, slots: 1);

        // The host's fixed answer is what the host chain resumes into; the wrong password never reaches it.
        Assert.Equal("401", await wrong);
        Assert.Equal("200", await right);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
}
