using System.Net;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// The rate-limit policy through the real gate: a bucket per (policy, host), the route's own override, the 429 and its
/// precomputed <c>Retry-After</c>, a header key with its fallback, and the simulator's non-mutating peek.
/// </summary>
public sealed class RateLimitPolicyTests
{
    private static readonly IpKey Client = IpKey.Parse("192.0.2.10");

    [Fact]
    public void AClientGets429WithRetryAfterOnceItsBurstIsSpent()
    {
        var policy = TestIds.RateLimitPolicy("tight");
        RateLimitRuntime runtime = Runtime();
        var gate = Gate(runtime, Model(runtime, policy, new RateLimitParameters(1, 2)));

        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/").Action);
        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/").Action);

        var limited = Decide(gate, "rl.test", "/");
        Assert.Equal(GateAction.Respond, limited.Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, limited.Response!.StatusCode);
        Assert.Equal("1", limited.RetryAfter);
        Assert.NotNull(limited.Response.HtmlPage);
        Assert.Equal(2, runtime.Stats.Allowed);
        Assert.Equal(1, runtime.Stats.Limited);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void RobotsTxtIsNeverRateLimited(string method)
    {
        var policy = TestIds.RateLimitPolicy("robots");
        RateLimitRuntime runtime = Runtime();
        var gate = Gate(runtime, Model(runtime, policy, new RateLimitParameters(1, 1)));

        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/other").Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "rl.test", "/other").Response!.StatusCode);

        // The bucket is empty, but a crawler still gets the path, and the exemption spends nothing.
        DefaultHttpContext context = Context("rl.test", "/robots.txt");
        context.Request.Method = method;
        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);
        Assert.Equal(1, runtime.Stats.Allowed);
        Assert.Equal(1, runtime.Stats.Limited);

        // Only GET and HEAD are exempt: another method on the same path pays like any other request.
        DefaultHttpContext post = Context("rl.test", "/robots.txt");
        post.Request.Method = "POST";
        Assert.Equal(StatusCodes.Status429TooManyRequests, gate.Decide(post).Response!.StatusCode);
    }

    [Fact]
    public void TwoHostsNamingOnePolicyKeepTheirOwnBuckets()
    {
        var policy = TestIds.RateLimitPolicy("shared");
        RateLimitRuntime runtime = Runtime();
        RateLimitPolicySpec spec = new() { Id = policy, Name = "shared", Parameters = new(1, 1) };
        ProxyConfigModel model = new()
        {
            Upstreams = [Web()],
            RateLimitPolicies = [spec],
            Hosts =
            [
                Host("one.test", Route("one", policy)),
                Host("two.test", Route("two", policy)),
            ],
        };
        var gate = Gate(runtime, model);

        // Each host has its own one-token bucket: the second host is not paying for the first's request.
        Assert.Equal(GateAction.Forward, Decide(gate, "one.test", "/").Action);
        Assert.Equal(GateAction.Forward, Decide(gate, "two.test", "/").Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "one.test", "/").Response!.StatusCode);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "two.test", "/").Response!.StatusCode);

        // Each host's pair is its own slot, so neither bucket is the other's.
        Assert.True(runtime.Slots.IsLive(runtime.SlotFor(policy + "/" + TestIds.Host("one.test"))));
        Assert.True(runtime.Slots.IsLive(runtime.SlotFor(policy + "/" + TestIds.Host("two.test"))));
    }

    [Fact]
    public void ARouteCanNameAnotherPolicyAndShareItWithTheHostsBucket()
    {
        var shared = TestIds.RateLimitPolicy("shared");
        var own = TestIds.RateLimitPolicy("own");
        RateLimitRuntime runtime = Runtime();
        ProxyConfigModel model = new()
        {
            Upstreams = [Web()],
            RateLimitPolicies =
            [
                new RateLimitPolicySpec { Id = shared, Name = "shared", Parameters = new(1, 1) },
                new RateLimitPolicySpec { Id = own, Name = "own", Parameters = new(1, 1) },
            ],
            Hosts =
            [
                Host("one.test", Route("root", shared), Route("other", own, "/other")),
            ],
        };
        var gate = Gate(runtime, model);

        Assert.Equal(GateAction.Forward, Decide(gate, "one.test", "/").Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "one.test", "/").Response!.StatusCode);
        // The override has its own pair, so its bucket is untouched by the host policy's spending.
        Assert.Equal(GateAction.Forward, Decide(gate, "one.test", "/other").Action);
    }

    [Fact]
    public void AHeaderKeyFallsBackToTheClientAddressWhenTheHeaderIsAbsent()
    {
        var policy = TestIds.RateLimitPolicy("header");
        RateLimitRuntime runtime = Runtime();
        ProxyConfigModel model = new()
        {
            Upstreams = [Web()],
            RateLimitPolicies = [new RateLimitPolicySpec { Id = policy, Name = "header", Parameters = new(1, 1), Key = RateLimitKey.Header, Header = "x-api-key" }],
            Hosts = [Host("rl.test", Route("root", policy))],
        };
        var gate = Gate(runtime, model);

        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/", "key-a").Action);
        // Another key has its own bucket.
        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/", "key-b").Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "rl.test", "/", "key-a").Response!.StatusCode);
        // No header is the client address's bucket, which is separate from any header value.
        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/").Action);
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "rl.test", "/").Response!.StatusCode);
    }

    [Fact]
    public void AClientIpAndPathPolicyKeysEachPathSeparately()
    {
        var policy = TestIds.RateLimitPolicy("path");
        RateLimitRuntime runtime = Runtime();
        ProxyConfigModel model = new()
        {
            Upstreams = [Web()],
            RateLimitPolicies = [new RateLimitPolicySpec { Id = policy, Name = "path", Parameters = new(1, 1), Key = RateLimitKey.ClientIpAndPath }],
            Hosts = [Host("rl.test", Route("root", policy))],
        };
        var gate = Gate(runtime, model);

        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/a").Action);
        Assert.Equal(GateAction.Forward, Decide(gate, "rl.test", "/b").Action);
        // A non-canonical spelling of a path is the same partition (the canonical matching view).
        Assert.Equal(StatusCodes.Status429TooManyRequests, Decide(gate, "rl.test", "//A").Response!.StatusCode);
    }

    [Fact]
    public void ASimulationPeeksWithoutSpendingAToken()
    {
        var policy = TestIds.RateLimitPolicy("sim");
        RateLimitRuntime runtime = Runtime();
        var model = Model(runtime, policy, new RateLimitParameters(1, 2));
        var compiled = TestSnapshots.Compile(model, new SnapshotCompilerOptions { RateLimits = runtime });
        var instance = Assert.Single(compiled.HostsByIndex[0].Routes[0].Policies.OfType<RateLimitPolicy>());

        var state = State();
        var context = Context("rl.test", "/");
        RequestView real = new(context, state);
        RequestView simulated = new(context, state, isSimulation: true);

        // Spend one of the two tokens for real; one is left.
        Assert.Equal(PolicyAction.Continue, instance.Evaluate(in real).Action);

        // The simulation says the next request would spend the last token, and leaves it there.
        PolicyDecision first = instance.Evaluate(in simulated);
        Assert.Equal(PolicyAction.Continue, first.Action);
        Assert.Contains("would allow, 0 tokens left", Explain(instance, in simulated, first), StringComparison.Ordinal);

        // A real request still finds the token.
        Assert.Equal(PolicyAction.Continue, instance.Evaluate(in real).Action);

        // Now the bucket is empty: the simulation reports the refusal, and the real request is refused too.
        PolicyDecision refused = instance.Evaluate(in simulated);
        Assert.Equal(PolicyAction.Respond, refused.Action);
        Assert.Contains("would refuse, retry after", Explain(instance, in simulated, refused), StringComparison.Ordinal);
        Assert.Equal(PolicyAction.Respond, instance.Evaluate(in real).Action);

        // A simulation never counts.
        Assert.Equal(2, runtime.Stats.Allowed);
        Assert.Equal(1, runtime.Stats.Limited);
    }

    private static RateLimitRuntime Runtime() => new(new RateLimitTable(64));

    private static GateMiddleware Gate(RateLimitRuntime runtime, ProxyConfigModel model) =>
        new(Store(runtime, model), new TrustedProxyClientAddressSource());

    private static SnapshotStore Store(RateLimitRuntime runtime, ProxyConfigModel model)
    {
        var snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { RateLimits = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);
        return store;
    }

    private static GateDecision Decide(GateMiddleware gate, string host, string path, string? headerValue = null)
    {
        DefaultHttpContext context = Context(host, path, headerValue);
        return gate.Decide(context);
    }

    private static DefaultHttpContext Context(string host, string path, string? headerValue = null)
    {
        DefaultHttpContext context = new();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.10");
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Request.Host = new HostString(host);
        if (headerValue is not null)
        {
            context.Request.Headers["x-api-key"] = headerValue;
        }

        return context;
    }

    private static GateRequestState State()
    {
        GateRequestState state = new();
        state.ClientIp = new ClientIpResolution(Client, ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted);
        return state;
    }

    private static string Explain(RateLimitPolicy policy, in RequestView request, PolicyDecision decision) =>
        ((IRequestPolicy)policy).Explain(request, decision);

    private static UpstreamSpec Web() => new() { Id = TestIds.Upstream("web"), Destinations = [new DestinationSpec(TestIds.Destination("web-d0"), "http://127.0.0.1:1")] };

    private static IReadOnlyList<RateLimitPolicySpec> Policies(RateLimitPolicyId policy, RateLimitParameters parameters) =>
        [new RateLimitPolicySpec { Id = policy, Name = policy.ToString()!, Parameters = parameters }];

    private static ProxyConfigModel Model(RateLimitRuntime runtime, RateLimitPolicyId policy, RateLimitParameters parameters) => new()
    {
        Upstreams = [Web()],
        RateLimitPolicies = Policies(policy, parameters),
        Hosts = [Host("rl.test", Route("root", policy))],
    };

    private static HostSpec Host(string name, params RouteSpec[] routes) => new()
    {
        Id = TestIds.Host(name),
        Names = [name],
        Https = false,
        Routes = routes,
    };

    private static RouteSpec Route(string label, RateLimitPolicyId policy, string path = "/") => new()
    {
        Id = TestIds.Route(label),
        Upstream = TestIds.Upstream("web"),
        Path = path,
        RateLimit = policy,
        RateLimitRouteOverride = true,
    };
}
