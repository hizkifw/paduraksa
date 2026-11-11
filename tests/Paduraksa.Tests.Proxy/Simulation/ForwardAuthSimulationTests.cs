using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// The simulator's forward-auth step says "would call <c>provider</c>" (or that a remembered allow would answer) and makes no call: the fake
/// endpoint sees nothing, no counter moves and no allow is written. The parity case puts the same requests through the simulator and
/// through the real gate against an endpoint that allows them, and compares what each does.
/// </summary>
public sealed class ForwardAuthSimulationTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASimulationNamesTheProviderSaysItWouldCallAndMakesNoCall()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var snapshot = Build(runtime, auth.Address);
        var counters = runtime.CountersFor(Provider);

        for (var i = 0; i < 3; i++)
        {
            var run = GateSimulator.Run(snapshot, Probe("session=ok"));
            var step = Step(run);

            Assert.Equal("sso", Fact(step, "provider"));
            Assert.Equal(auth.Address + "/", Fact(step, "endpoint"));
            Assert.Equal("wouldCall", Fact(step, "outcome"));
            Assert.Equal("request", Fact(step, "cacheScope"));
            Assert.Equal("10", Fact(step, "cacheSeconds"));
            Assert.Contains("would call provider 'sso'", step.Summary, StringComparison.Ordinal);
            Assert.Equal(GateStepVerdict.Passed, step.Verdict);
            // The request is shown going on: the answer is not known.
            Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
        }

        Assert.Equal(0, auth.CallCount);
        Assert.Equal((0, 0, 0, 0), (counters.Allowed, counters.Calls, counters.Denied, counters.CacheHits));
        Assert.Equal(0, snapshot.HostsByIndex[0].Routes[0].AuthProvider!.State.CachedAllows);
    }

    [Fact]
    public async Task ARememberedAllowIsReportedAsSuchAndIsNeitherRefreshedNorCounted()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var snapshot = Build(runtime, auth.Address);
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        Assert.Equal("pass", await RunAsync(gate, Context(cookie: "session=ok", path: "/docs")));
        var counters = runtime.CountersFor(Provider);
        var hits = counters.CacheHits;

        var run = GateSimulator.Run(snapshot, Probe("session=ok"));
        var step = Step(run);

        Assert.Equal("cached", Fact(step, "outcome"));
        Assert.Contains("allowed these credentials a moment ago", step.Summary, StringComparison.Ordinal);
        Assert.Equal(hits, counters.CacheHits);
        Assert.Equal(1, auth.CallCount);

        // Other credentials have no remembered allow.
        Assert.Equal("wouldCall", Fact(Step(GateSimulator.Run(snapshot, Probe("session=other"))), "outcome"));
    }

    [Theory]
    [InlineData("session=ok", "GET", "/docs")]
    [InlineData("session=ok", "POST", "/api/items")]
    [InlineData("session=ok; theme=dark", "GET", "/")]
    public async Task WhereTheEndpointAllowsARequestTheSimulationAndTheRealGateBothForward(string cookie, string method, string path)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        auth.Handler = AllowsSessionOk();
        var runtime = new ForwardAuthRuntime();
        var snapshot = Build(runtime, auth.Address);
        SnapshotStore store = new();
        store.Publish(snapshot);
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());

        var simulated = GateSimulator.Run(snapshot, Probe(cookie, method, path)).Outcome;
        var real = await RunAsync(gate, Context(cookie: cookie, path: path, method: method));

        Assert.Equal(GateOutcomeAction.Forward, simulated.Action);
        Assert.Equal("pass", real);
    }

    [Fact]
    public void ASimulationThroughTheRouteThatTurnsAuthenticationOffHasNoForwardAuthStep()
    {
        var spec = Spec("http://127.0.0.1:1/auth");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            AuthProviders = [spec],
            Hosts =
            [
                TestSnapshots.ProxyHost(
                    "app",
                    "app.test",
                    TestSnapshots.Route("api", "web", "/oauth2", RouteMatchKind.Prefix),
                    TestSnapshots.Route("root", "web") with { AuthProvider = Provider }),
            ],
        };
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { ForwardAuth = new ForwardAuthRuntime() });

        Assert.DoesNotContain(GateSimulator.Run(snapshot, Probe(null, "GET", "/oauth2/start")).Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "forwardAuth" }));
        Assert.Single(GateSimulator.Run(snapshot, Probe(null, "GET", "/page")).Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "forwardAuth" }));
    }

    private static RuntimeSnapshot Build(ForwardAuthRuntime runtime, string address) =>
        TestSnapshots.Bound(Model(Spec(address + "/")), options: new SnapshotCompilerOptions { ForwardAuth = runtime });

    private static GateProbe Probe(string? cookie, string method = "GET", string path = "/docs") => new()
    {
        Host = "app.test",
        Path = path,
        Method = method,
        ClientAddress = IPAddress.Parse("192.0.2.1"),
        Headers = cookie is null ? [] : [new ProbeHeader("Cookie", cookie)],
    };

    private static GateStep Step(GateSimulation run) =>
        Assert.Single(run.Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "forwardAuth" }));

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;
}
