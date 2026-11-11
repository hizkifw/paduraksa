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

        // A call that begins after that fails closed instead of using a disposed client.
        Assert.Equal("502", await RunAsync(gate, Context(cookie: "session=other")));
        Assert.Equal(1, runtime.CountersFor(Provider).Errors);
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
