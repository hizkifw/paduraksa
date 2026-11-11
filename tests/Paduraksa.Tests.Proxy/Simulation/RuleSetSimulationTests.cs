using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// The simulator's rule-set step: which set, which rule (counted from 1, like the dashboard's list), what matched and the action,
/// in words and as facts. It runs the live policy, so the parity test puts a matrix of paths, methods and user agents through
/// both the simulator and the real gate and compares what each does; a simulation counts nothing.
/// </summary>
public sealed class RuleSetSimulationTests
{
    private const string Browser = "Mozilla/5.0 Firefox/130.0";

    private static readonly RuleSpec[] Rules =
    [
        Rule(RuleAction.Allow, kind: RulePathKind.Prefix, pattern: "/admin/public"),
        Rule(RuleAction.Deny, kind: RulePathKind.Prefix, pattern: "/admin", methods: ["POST", "PUT"]),
        Rule(RuleAction.Drop, kind: RulePathKind.Glob, pattern: "/**/.env"),
        Rule(RuleAction.Deny, kind: RulePathKind.Regex, pattern: @"\.(php|asp)$"),
        Rule(RuleAction.Deny, agents: ["sqlmap", "nikto"], emptyAgent: true),
    ];

    public static TheoryData<string, string, string?> Matrix
    {
        get
        {
            TheoryData<string, string, string?> rows = [];
            foreach (var path in new[] { "/", "/admin", "/admin/users", "/admin/public/a.png", "//ADMIN;x/users", "/x/.env", "/a/b.php", "/robots.txt", "/admin%2Fusers" })
            {
                foreach (var method in new[] { "GET", "POST", "PUT" })
                {
                    foreach (var agent in new[] { Browser, "sqlmap/1.7", null })
                    {
                        rows.Add(path, method, agent);
                    }
                }
            }

            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void TheSimulatedOutcomeIsWhatTheRealGateDoes(string path, string method, string? agent)
    {
        var snapshot = Build(new RuleSetRuntime());
        var context = TestContexts.Create("rules.test", path, method, remote: IPAddress.Parse("192.0.2.1"));
        if (agent is not null)
        {
            context.Request.Headers.UserAgent = agent;
        }

        var real = GateMiddleware.Decide(context, snapshot, new TrustedProxyClientAddressSource(), null, default(NoGateTracer));
        var simulated = GateSimulator.Run(snapshot, Probe(path, method, agent)).Outcome;

        Assert.Equal(
            real.Action switch { GateAction.Forward => "forward", GateAction.Abort => "abort", _ => real.Response!.StatusCode.ToString() },
            simulated.Action switch { GateOutcomeAction.Forward => "forward", GateOutcomeAction.Abort => "abort", _ => simulated.StatusCode.ToString() });
    }

    [Theory]
    [InlineData("/admin/users", "POST", Browser, "2", "deny", "path starts with /admin, method POST")]
    [InlineData("/admin/public/x", "POST", Browser, "1", "allow", "path starts with /admin/public")]
    [InlineData("/a/.env", "GET", Browser, "3", "drop", "path matches glob /**/.env")]
    [InlineData("/a/b.php", "GET", Browser, "4", "deny", @"path matches regex \.(php|asp)$")]
    [InlineData("/", "GET", "Mozilla sqlmap/1.7", "5", "deny", "user agent contains 'sqlmap'")]
    [InlineData("/", "GET", null, "5", "deny", "no user agent")]
    public void TheStepNamesTheSetTheRuleWhatMatchedAndTheAction(string path, string method, string? agent, string rule, string action, string matched)
    {
        var step = RuleSetStep(GateSimulator.Run(Build(new RuleSetRuntime()), Probe(path, method, agent)));

        Assert.Equal("ruleSet", Fact(step, "policy"));
        Assert.Equal("rules", Fact(step, "ruleSet"));
        Assert.Equal(rule, Fact(step, "rule"));
        Assert.Equal(matched, Fact(step, "matched"));
        Assert.Equal(action, Fact(step, "action"));
        Assert.Contains($"rule {rule} of 'rules' matches ({matched})", step.Summary, StringComparison.Ordinal);
        Assert.Equal(action == "allow" ? GateStepVerdict.Passed : GateStepVerdict.Stopped, step.Verdict);
    }

    [Fact]
    public void ARequestNoRuleMatchesGoesOnAndSaysSo()
    {
        var run = GateSimulator.Run(Build(new RuleSetRuntime()), Probe("/index.html", "GET", Browser));
        var step = RuleSetStep(run);

        Assert.Equal("none", Fact(step, "rule"));
        Assert.Contains("none of the 5 rules of 'rules' match, so the request goes on", step.Summary, StringComparison.Ordinal);
        Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
    }

    [Fact]
    public void RobotsTxtIsReportedAsExempt()
    {
        var step = RuleSetStep(GateSimulator.Run(Build(new RuleSetRuntime()), Probe("/robots.txt", "GET", null)));

        Assert.Equal("exempt", Fact(step, "rule"));
        Assert.Contains("never rules on robots.txt", step.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ASimulationCountsNothingWhileARealRequestDoes()
    {
        RuleSetRuntime runtime = new();
        var snapshot = Build(runtime);
        var counters = runtime.CountersFor(TestIds.RuleSet("rules"));

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(403, GateSimulator.Run(snapshot, Probe("/admin/users", "POST", Browser)).Outcome.StatusCode);
        }

        Assert.Equal(0, counters.Denied);

        var context = TestContexts.Create("rules.test", "/admin/users", "POST", remote: IPAddress.Parse("192.0.2.1"));
        context.Request.Headers.UserAgent = Browser;
        GateMiddleware.Decide(context, snapshot, new TrustedProxyClientAddressSource(), null, default(NoGateTracer));
        Assert.Equal(1, counters.Denied);
    }

    [Fact]
    public void TheHostChainStepNamesTheSetToo()
    {
        var id = TestIds.RuleSet("rules");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            RuleSets = [new RuleSetSpec { Id = id, Name = "rules", Rules = Rules }],
            Hosts = [new HostSpec { Id = TestIds.Host("rules"), Names = ["rules.test"], Policies = [new RuleSetPolicySpec(id)], Routes = [TestSnapshots.Route("root", "web")] }],
        };

        var run = GateSimulator.Run(TestSnapshots.Bound(model), Probe("/admin/users", "POST", Browser));

        var step = Assert.Single(run.Steps, s => s.Kind == GateStepKind.HostPolicy);
        Assert.Equal("rules", Fact(step, "ruleSet"));
        Assert.Equal(403, run.Outcome.StatusCode);
    }

    private static RuntimeSnapshot Build(RuleSetRuntime runtime)
    {
        var id = TestIds.RuleSet("rules");
        ProxyConfigModel model = new()
        {
            Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
            RuleSets = [new RuleSetSpec { Id = id, Name = "rules", Rules = Rules }],
            Hosts = [new HostSpec { Id = TestIds.Host("rules"), Names = ["rules.test"], Routes = [TestSnapshots.Route("root", "web") with { Policies = [new RuleSetPolicySpec(id)] }] }],
        };
        return TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { RuleSets = runtime });
    }

    private static GateProbe Probe(string path, string method, string? agent) => new()
    {
        Host = "rules.test",
        Path = path,
        Method = method,
        ClientAddress = IPAddress.Parse("192.0.2.1"),
        Headers = agent is null ? [] : [new ProbeHeader("User-Agent", agent)],
    };

    private static GateStep RuleSetStep(GateSimulation run) =>
        Assert.Single(run.Steps, s => s.Facts.Any(f => f is { Name: "policy", Value: "ruleSet" }));

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;
}
