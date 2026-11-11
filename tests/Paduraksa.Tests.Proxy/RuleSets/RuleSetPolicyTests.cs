using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;

namespace Paduraksa.Tests.Proxy.RuleSets;

/// <summary>A rule set through the real gate: what a deny, a drop and an allow do, the path a rule reads, the counters, and what
/// a simulation leaves alone.</summary>
public sealed class RuleSetPolicyTests
{
    private const string Browser = "Mozilla/5.0 (X11; Linux x86_64) Firefox/130.0";

    [Fact]
    public void ADenyAnswers403WithThePageAndADropAbortsAnAllowGoesOn()
    {
        var gate = Gate(
            Rule(RuleAction.Allow, kind: RulePathKind.Prefix, pattern: "/admin/public"),
            Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin"),
            Rule(RuleAction.Drop, agents: ["sqlmap"]));

        Assert.Equal(GateAction.Forward, Decide(gate, "/admin/public/a").Action);

        var denied = Decide(gate, "/admin/users");
        Assert.Equal(GateAction.Respond, denied.Action);
        Assert.Equal(StatusCodes.Status403Forbidden, denied.Response!.StatusCode);
        Assert.NotNull(denied.Response.HtmlPage);

        Assert.Equal(GateAction.Abort, Decide(gate, "/", userAgent: "sqlmap/1.7").Action);
        Assert.Equal(GateAction.Forward, Decide(gate, "/").Action);
    }

    [Theory]
    [InlineData("//admin")]
    [InlineData("/ADMIN")]
    [InlineData("/admin;x=1")]
    [InlineData("/admin/")]
    [InlineData("/admin/.")]
    [InlineData("/./admin")]
    [InlineData("/x/../admin")]
    [InlineData("/admin.")]
    [InlineData("/admin%2Fusers")]
    [InlineData("/admin%5Cusers")]
    [InlineData("/\\admin")]
    [InlineData("/public/..%5Cadmin")]
    public void ARuleReadsTheCanonicalPathSoAnEncodingTrickCantDodgeIt(string path)
    {
        var gate = Gate(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin"));

        Assert.Equal(GateAction.Respond, Decide(gate, path).Action);
        Assert.Equal(GateAction.Forward, Decide(gate, "/administrator").Action);
    }

    [Fact]
    public void ADecodedPathIsMatchedOnItsCharactersNotItsEscapes()
    {
        // Kestrel hands the gate the decoded path: %61dmin is /admin by the time a rule sees it.
        var gate = Gate(Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/admin"));

        var context = Context("/admin");
        Assert.Equal(GateAction.Respond, gate.Decide(context).Action);
    }

    [Fact]
    public void ALongNonCanonicalPathIsCanonicalisedOnTheHeap()
    {
        var gate = Gate(Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/admin/deep"));
        var path = "//admin/" + new string('/', 400) + "deep";

        Assert.Equal(GateAction.Respond, Decide(gate, path).Action);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public void RobotsTxtIsNeverRuledOn(string method)
    {
        var gate = Gate(Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/"));

        var context = Context("/robots.txt", method);
        Assert.Equal(GateAction.Forward, gate.Decide(context).Action);

        // Only GET and HEAD: another method on the same path is an ordinary request.
        Assert.Equal(GateAction.Respond, gate.Decide(Context("/robots.txt", "POST")).Action);
        Assert.Equal(GateAction.Respond, gate.Decide(Context("/other", method)).Action);
    }

    [Fact]
    public void TheCountersFollowWhatTheRulesDid()
    {
        RuleSetRuntime runtime = new();
        var id = TestIds.RuleSet("counted");
        var gate = Gate(runtime, id, Rule(RuleAction.Allow, kind: RulePathKind.Exact, pattern: "/ok"), Rule(RuleAction.Deny, emptyAgent: true), Rule(RuleAction.Drop, kind: RulePathKind.Exact, pattern: "/drop"));

        Decide(gate, "/ok", userAgent: null);
        Decide(gate, "/x", userAgent: null);
        Decide(gate, "/x", userAgent: null);
        Decide(gate, "/drop");
        Decide(gate, "/fine");

        var counters = runtime.CountersFor(id);
        Assert.Equal(1, counters.Allowed);
        Assert.Equal(2, counters.Denied);
        Assert.Equal(1, counters.Dropped);
    }

    [Fact]
    public void ASimulationReportsTheRuleAndCountsNothing()
    {
        RuleSetRuntime runtime = new();
        var id = TestIds.RuleSet("sim");
        var snapshot = Compile(runtime, id, Rule(RuleAction.Allow, kind: RulePathKind.Exact, pattern: "/ok"), Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin", methods: ["POST"]));
        var policy = Assert.IsType<RuleSetPolicy>(snapshot.HostsByIndex[0].Routes[0].Policies[0]);
        var context = Context("/admin/x", "POST");
        GateRequestState state = new();
        RequestView simulated = new(context, state, isSimulation: true);

        var verdict = policy.Evaluate(in simulated);
        Assert.Equal(PolicyAction.Respond, verdict.Action);
        var sentence = ((IRequestPolicy)policy).Explain(in simulated, verdict);
        Assert.Contains("rule 2 of 'sim'", sentence, StringComparison.Ordinal);
        Assert.Contains("path starts with /admin, method POST", sentence, StringComparison.Ordinal);
        Assert.Contains("refuses it with 403", sentence, StringComparison.Ordinal);

        Assert.Equal(0, runtime.CountersFor(id).Denied);
    }

    [Fact]
    public void EveryVerdictOfASetWithoutARegexAllocatesNothing()
    {
        var snapshot = Bind(Compile(
            new RuleSetRuntime(),
            TestIds.RuleSet("alloc"),
            Rule(RuleAction.Allow, kind: RulePathKind.Prefix, pattern: "/admin/public"),
            Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin"),
            Rule(RuleAction.Deny, kind: RulePathKind.Exact, pattern: "/secret"),
            Rule(RuleAction.Drop, kind: RulePathKind.Glob, pattern: "/**/.env"),
            Rule(RuleAction.Deny, methods: ["TRACE"]),
            Rule(RuleAction.Deny, agents: ["sqlmap", "nikto", "masscan"], emptyAgent: true)));
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        var cases = new (string Name, DefaultHttpContext Context, GateAction Expected)[]
        {
            ("a path no rule matches", Context("/index.html"), GateAction.Forward),
            ("an allow rule", Context("/admin/public/x"), GateAction.Forward),
            ("a prefix deny", Context("/admin/users"), GateAction.Respond),
            ("an exact deny", Context("/secret"), GateAction.Respond),
            ("a glob drop", Context("/a/b/.env"), GateAction.Abort),
            ("a method deny", Context("/", "TRACE"), GateAction.Respond),
            ("a user-agent deny", Context("/", userAgent: "Mozilla sqlmap/1.7"), GateAction.Respond),
            ("an empty user agent deny", Context("/", userAgent: null), GateAction.Respond),
            ("a path that needs canonicalising on the stack", Context("//ADMIN;x/./users"), GateAction.Respond),
            ("a path that needs canonicalising in a pooled buffer", Context("//admin/" + new string('/', 400) + "x"), GateAction.Respond),
            ("a long canonical path", Context("/" + new string('a', 600)), GateAction.Forward),
            ("robots.txt", Context("/robots.txt", userAgent: null), GateAction.Forward),
        };
        foreach (var (name, context, expected) in cases)
        {
            Assert.Equal(expected, GateMiddleware.Decide(context, snapshot, addresses, null, default(NoGateTracer)).Action);
            AllocAssert.Zero((snapshot, addresses, context), static s => _ = GateMiddleware.Decide(s.context, s.snapshot, s.addresses, null, default(NoGateTracer)), name);
        }
    }

    [Fact]
    public void ARegexRuleMatchesWithoutAllocatingOncePathShapesAreKnown()
    {
        var snapshot = Bind(Compile(new RuleSetRuntime(), TestIds.RuleSet("regex"), Rule(RuleAction.Deny, kind: RulePathKind.Regex, pattern: @"\.(php|asp)$")));
        IClientAddressSource addresses = new TrustedProxyClientAddressSource();
        foreach (var (path, expected) in new[] { ("/index.php", GateAction.Respond), ("/index.html", GateAction.Forward) })
        {
            var context = Context(path);
            Assert.Equal(expected, GateMiddleware.Decide(context, snapshot, addresses, null, default(NoGateTracer)).Action);
            var result = AllocAssert.MeasureSettled((snapshot, addresses, context), static s => _ = GateMiddleware.Decide(s.context, s.snapshot, s.addresses, null, default(NoGateTracer)));
            Assert.True(result.TotalBytes == 0, $"{path}: {result}");
        }
    }

    private static GateMiddleware Gate(params RuleSpec[] rules) => Gate(new RuleSetRuntime(), TestIds.RuleSet("set"), rules);

    private static GateMiddleware Gate(RuleSetRuntime runtime, RuleSetId id, params RuleSpec[] rules)
    {
        SnapshotStore store = new();
        store.Publish(Bind(Compile(runtime, id, rules)));
        return new GateMiddleware(store, new TrustedProxyClientAddressSource());
    }

    private static RuntimeSnapshot Compile(RuleSetRuntime runtime, RuleSetId id, params RuleSpec[] rules)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            RuleSets = [new RuleSetSpec { Id = id, Name = "sim", Rules = rules }],
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("rules"),
                    Names = ["rules.test"],
                    Routes = [TestSnapshots.Route("root", "web") with { Policies = [new RuleSetPolicySpec(id)] }],
                },
            ],
        };
        return TestSnapshots.Compile(model, new SnapshotCompilerOptions { RuleSets = runtime });
    }

    private static RuntimeSnapshot Bind(RuntimeSnapshot candidate)
    {
        var endpoints = new Endpoint[candidate.RouteIdBySlot.Length];
        for (var i = 0; i < endpoints.Length; i++)
        {
            endpoints[i] = new Endpoint(static _ => Task.CompletedTask, EndpointMetadataCollection.Empty, candidate.RouteIdBySlot[i].ToString());
        }

        return candidate.Bind(1, endpoints);
    }

    private static GateDecision Decide(GateMiddleware gate, string path, string method = "GET", string? userAgent = Browser) =>
        gate.Decide(Context(path, method, userAgent));

    private static DefaultHttpContext Context(string path, string method = "GET", string? userAgent = Browser)
    {
        var context = TestContexts.Create("rules.test", path, method, remote: IPAddress.Parse("192.0.2.10"));
        if (userAgent is not null)
        {
            context.Request.Headers.UserAgent = userAgent;
        }

        return context;
    }
}
