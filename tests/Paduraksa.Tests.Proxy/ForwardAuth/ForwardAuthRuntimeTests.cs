using System.Net;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>When a provider's state becomes the live one, and when the client of a replaced one is let go.</summary>
public sealed class ForwardAuthRuntimeTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASnapshotThatNeverGoesLiveLeavesTheLiveProviderStatesAlone()
    {
        var runtime = new ForwardAuthRuntime();
        SnapshotStore store = new();
        await using var server = await ProxyTestServer.StartAsync(
            Model(Spec("http://127.0.0.1:1/auth")),
            new()
            {
                Publisher = new SnapshotPublisher(store, forwardAuth: runtime),
                CompilerOptions = new SnapshotCompilerOptions { ForwardAuth = runtime },
            });
        var live = runtime.StateOf(Provider);
        Assert.NotNull(live);
        Assert.Same(live, Assert.Single(store.Current.AuthProviderStates));

        // A candidate that changes the provider but fails to compile (its host has no route): the process keeps what it had.
        var broken = Model(Spec("http://127.0.0.1:2/auth")) with { Hosts = [TestSnapshots.ProxyHost("empty", "empty.test")] };
        var failed = SnapshotCompiler.Compile(broken, new SnapshotCompilerOptions { ForwardAuth = runtime });
        Assert.Null(failed.Snapshot);
        Assert.Same(live, runtime.StateOf(Provider));
        Assert.False(live.IsDisposed);

        // One that compiles but is never published: the same.
        var compiled = TestSnapshots.Compile(Model(Spec("http://127.0.0.1:2/auth")), new SnapshotCompilerOptions { ForwardAuth = runtime });
        Assert.NotSame(live, Assert.Single(compiled.AuthProviderStates));
        Assert.Same(live, runtime.StateOf(Provider));

        // Publishing it is what makes it live.
        Assert.True((await server.PublishAsync(Model(Spec("http://127.0.0.1:2/auth")))).Succeeded);
        var next = runtime.StateOf(Provider);
        Assert.NotSame(live, next);
        Assert.Same(next, Assert.Single(store.Current.AuthProviderStates));
    }

    [Fact]
    public void APublishThatDropsTheProviderRetiresItsStateAndSoDoesOneThatChangesIt()
    {
        var runtime = new ForwardAuthRuntime();
        var first = runtime.Bind(Spec("http://127.0.0.1:1/auth"));
        runtime.Publish([first]);
        var second = runtime.Bind(Spec("http://127.0.0.1:2/auth"));
        runtime.Publish([second]);
        Assert.Same(second, runtime.StateOf(Provider));

        Assert.False(first.IsDisposed);
        runtime.Publish([]);

        Assert.Null(runtime.StateOf(Provider));
    }

    [Fact]
    public async Task AReplacedStatesClientIsDisposedAfterTheGracePeriodAndNotWhileACallRuns()
    {
        ManualTime time = new();
        TrackingHandler handler = new();
        ForwardAuthRuntime runtime = new(time, null, _ => handler);
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth")), out var snapshot);
        var old = Assert.Single(snapshot.AuthProviderStates);
        runtime.Publish([old]);
        Assert.Equal("pass", await RunAsync(gate, Context(cookie: "session=ok")));
        Assert.Equal(1, handler.Calls);

        // A call is running when the replacement goes live.
        Assert.True(old.TryEnter());
        var replacement = runtime.Bind(Spec("http://127.0.0.1:2/auth"));
        runtime.Publish([replacement]);
        Assert.False(handler.Disposed);

        // Snapshots published before still use it: nothing is disposed within the timeout and the margin after it.
        time.Advance(TimeSpan.FromSeconds(14));
        Assert.False(handler.Disposed);

        // The timer fires but a call is still running: it waits.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(handler.Disposed);
        Assert.False(old.IsDisposed);

        old.Exit();
        time.Advance(TimeSpan.FromSeconds(11));
        Assert.True(handler.Disposed);
        Assert.True(old.IsDisposed);

        // A call that begins after that fails closed instead of using a disposed client, and makes no new one.
        Assert.Equal("502", await RunAsync(gate, Context(cookie: "session=other")));
        Assert.Equal(1, runtime.CountersFor(Provider).Errors);
        Assert.Equal(1, handler.Calls);
        Assert.False(old.TryEnter());
        Assert.Same(replacement, runtime.StateOf(Provider));
    }

    [Fact]
    public void AStateThatWasNeverCalledHasNothingToDisposeAndRetiringItTwiceIsHarmless()
    {
        ManualTime time = new();
        ForwardAuthRuntime runtime = new(time, null, _ => throw new InvalidOperationException("no client should be made"));
        var first = runtime.Bind(Spec("http://127.0.0.1:1/auth"));
        runtime.Publish([first]);

        runtime.Publish([]);
        first.Retire();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.True(first.IsDisposed);
    }

    [Fact]
    public void AStateTheCommitThatRevertedAChangeBoundIsLiveAgainWhenTheOtherCommitHasRetiredItAndIsNeverDisposedWhileLive()
    {
        ManualTime time = new();
        ForwardAuthRuntime runtime = new(time, null, _ => new TrackingHandler());
        var original = Spec("http://127.0.0.1:1/auth");
        var live = runtime.Bind(original);
        runtime.Publish([live]);

        // Two commits overlap: C1 changes the provider and is still publishing when C2, which reverts the change, compiles on the process's state.
        var changed = runtime.Bind(Spec("http://127.0.0.1:2/auth"));
        var reverted = runtime.Bind(original);
        Assert.Same(live, reverted);

        runtime.Publish([changed]);
        Assert.False(live.IsLive);
        Assert.Equal(1, runtime.RetiredStates);
        runtime.Publish([reverted]);

        Assert.Same(live, runtime.StateOf(Provider));
        Assert.True(live.IsLive);
        time.Advance(TimeSpan.FromSeconds(5 + 11));

        // The timer that was set when it was retired did nothing: it still takes calls, and only the one the revert replaced is gone.
        Assert.False(live.IsDisposed);
        Assert.True(live.TryEnter());
        Assert.NotNull(live.Client);
        live.Exit();
        Assert.True(changed.IsDisposed);
        Assert.Equal(0, runtime.RetiredStates);
    }

    [Fact]
    public void AStateThatWasDisposedBeforeTheCommitThatBoundItPublishesGetsAClientAgainAndKeepsItsCache()
    {
        ManualTime time = new();
        List<TrackingHandler> handlers = [];
        ForwardAuthRuntime runtime = new(time, null, _ =>
        {
            TrackingHandler handler = new();
            handlers.Add(handler);
            return handler;
        });
        var original = Spec("http://127.0.0.1:1/auth");
        var live = runtime.Bind(original);
        runtime.Publish([live]);
        Assert.True(live.TryEnter());
        _ = live.Client;
        live.Exit();
        var allowance = new ForwardAuthAllowance(7, time.GetTimestamp() + 1_000_000_000_000, []);
        live.Cache.Add(allowance, time.GetTimestamp());

        var changed = runtime.Bind(Spec("http://127.0.0.1:2/auth"));
        var reverted = runtime.Bind(original);
        runtime.Publish([changed]);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(live.IsDisposed);
        Assert.True(handlers[0].Disposed);

        // The publish of the reverting commit was slow: its snapshot still holds the state, so the state is what comes back.
        runtime.Publish([reverted]);

        Assert.Same(live, runtime.StateOf(Provider));
        Assert.False(live.IsDisposed);
        Assert.True(live.TryEnter());
        _ = live.Client;
        live.Exit();
        Assert.Equal(2, handlers.Count);
        Assert.False(handlers[1].Disposed);
        Assert.True(live.Cache.TryGet(7, time.GetTimestamp(), out _));

        // Only the state the revert replaced is waiting to be disposed.
        Assert.Equal(1, runtime.RetiredStates);
        Assert.False(changed.IsLive);
    }

    [Fact]
    public void BindNeverHandsOutARetiredOrADisposedState()
    {
        ManualTime time = new();
        ForwardAuthRuntime runtime = new(time, null, _ => new TrackingHandler());
        var spec = Spec("http://127.0.0.1:1/auth");
        var live = runtime.Bind(spec);
        runtime.Publish([live]);
        Assert.Same(live, runtime.Bind(spec));

        // Between a publish's removal of the state from the live set and its retirement, or after a retirement made on its own, it is still what the process holds.
        Assert.True(live.Retire());
        Assert.NotSame(live, runtime.Bind(spec));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(live.IsDisposed);
        Assert.NotSame(live, runtime.Bind(spec));
    }

    [Fact]
    public void ARetiredStateIsHeldUntilItsClientIsDisposedAndThenRefusesEveryCall()
    {
        ManualTime time = new();
        ForwardAuthRuntime runtime = new(time, null, _ => throw new InvalidOperationException("a disposed state must not make a client"));
        var state = runtime.Bind(Spec("http://127.0.0.1:1/auth"));
        runtime.Publish([state]);
        runtime.Publish([]);

        // The runtime, not only the state's own timer, roots it while it waits.
        Assert.Equal(1, runtime.RetiredStates);
        Assert.True(state.TryEnter());
        time.Advance(TimeSpan.FromSeconds(16));
        Assert.False(state.IsDisposed);
        state.Exit();
        time.Advance(TimeSpan.FromSeconds(11));

        Assert.True(state.IsDisposed);
        Assert.Equal(0, runtime.RetiredStates);
        Assert.False(state.TryEnter());
        Assert.Throws<ObjectDisposedException>(() => state.Client);
    }

    [Fact]
    public async Task NoCallThatHoldsAPlaceEverFindsItsClientDisposedWhileTheStateIsRetiredAndRevivedUnderLoad()
    {
        ManualTime time = new();
        ForwardAuthRuntime runtime = new(time, null, _ => new TrackingHandler());
        var state = runtime.Bind(Spec("http://127.0.0.1:1/auth"));
        runtime.Publish([state]);
        var stop = false;
        var failures = 0;
        var entered = 0;

        async Task WorkerAsync()
        {
            while (!Volatile.Read(ref stop))
            {
                if (!state.TryEnter())
                {
                    await Task.Yield();
                    continue;
                }

                try
                {
                    Interlocked.Increment(ref entered);
                    using var response = await state.Client.GetAsync("http://127.0.0.1:1/", Cancel);
                    await Task.Yield();
                    using var again = await state.Client.GetAsync("http://127.0.0.1:1/", Cancel);
                }
                catch (ObjectDisposedException)
                {
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    state.Exit();
                }
            }
        }

        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(WorkerAsync, Cancel)).ToArray();
        for (var i = 0; i < 400; i++)
        {
            state.Retire();
            time.Advance(TimeSpan.FromSeconds(16));
            state.Revive();
            await Task.Yield();
        }

        Volatile.Write(ref stop, true);
        await Task.WhenAll(workers);

        Assert.True(entered > 0);
        Assert.Equal(0, failures);
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private int _calls;

        public bool Disposed { get; private set; }

        public int Calls => Volatile.Read(ref _calls);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}
