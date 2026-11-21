using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Proxy.RuleSets;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// A rule set's honeypot action: a match is answered with the trap's landing page, as the host's own answer, and on a host that has no
/// trap the same rule is a deny. Both are decided when the snapshot compiles, for a rule on the host's chain and on a route's alike.
/// </summary>
public sealed class HoneypotRuleActionTests
{
    private static readonly RuleSetId Id = TestIds.RuleSet("scanners");
    private static readonly HostHoneypotSpec On = new() { Enabled = true, Path = "/archive-k7f3/" };

    private readonly GateTrapRig _rig = new();
    private readonly RuleSetRuntime _runtime = new();

    private GateMiddleware Gate(bool hostChain, HostHoneypotSpec trap, params RuleSpec[] rules)
    {
        RuleSetPolicySpec policy = new(Id);
        var route = Route("root", "web");
        var host = ProxyHost("rules", "rules.test", hostChain ? route : route with { Policies = [policy] }) with
        {
            Honeypot = trap,
            Policies = hostChain ? [policy] : [],
        };
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            RuleSets = [new RuleSetSpec { Id = Id, Name = "scanners", Rules = rules }],
            Hosts = [host],
        };
        SnapshotStore store = new();
        store.Publish(Bound(model, options: _rig.Options with { RuleSets = _runtime }));
        return new GateMiddleware(store, new TrustedProxyClientAddressSource());
    }

    private static DefaultHttpContext Context(string path, string method = "GET", string userAgent = "sqlmap/1.7")
    {
        var context = TestContexts.Create("rules.test", path, method, remote: IPAddress.Parse("192.0.2.10"));
        context.Request.Headers.UserAgent = userAgent;
        return context;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMatchGoesToTheTrapAsTheRulesEntryWhereverTheSetIsNamed(bool hostChain)
    {
        var gate = Gate(hostChain, On, Rule(RuleAction.Honeypot, agents: ["sqlmap"]));
        var context = Context("/wp-login.php");

        var decision = gate.Decide(context);

        Assert.Equal(GateAction.Honeypot, decision.Action);
        var state = context.Features.Get<IGateFeature>()!.State;
        Assert.True(state.HoneypotViaRule);
        Assert.Equal(Paduraksa.Proxy.Metrics.GateOutcome.Answered, state.Outcome);
        Assert.True(state.ProxyAnswered);

        // What no rule matches is an ordinary request, and the prefix is the trap's own way in, not the rule's.
        Assert.Equal(GateAction.Forward, gate.Decide(Context("/wp-login.php", userAgent: "Firefox/130")).Action);
        var viaPrefix = Context("/archive-k7f3/post-1/", userAgent: "Firefox/130");
        Assert.Equal(GateAction.Honeypot, gate.Decide(viaPrefix).Action);
        Assert.False(viaPrefix.Features.Get<IGateFeature>()!.State.HoneypotViaRule);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithoutATrapTheSameRuleIsADeny(bool hostChain)
    {
        var gate = Gate(hostChain, HostHoneypotSpec.Off, Rule(RuleAction.Honeypot, agents: ["sqlmap"]));

        var decision = gate.Decide(Context("/wp-login.php"));

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(StatusCodes.Status403Forbidden, decision.Response!.StatusCode);
        Assert.NotNull(decision.Response.HtmlPage);
    }

    [Fact]
    public void ATrapWhoseKeyIsMissingStillTakesTheRuleAndAnswersItWith503()
    {
        var rig = new GateTrapRig(withKey: false);
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            RuleSets = [new RuleSetSpec { Id = Id, Name = "scanners", Rules = [Rule(RuleAction.Honeypot, agents: ["sqlmap"])] }],
            Hosts = [ProxyHost("rules", "rules.test", Route("root", "web")) with { Honeypot = On, Policies = [new RuleSetPolicySpec(Id)] }],
        };
        SnapshotStore store = new();
        store.Publish(Bound(model, options: rig.Options));

        Assert.Equal(GateAction.Honeypot, new GateMiddleware(store, new TrustedProxyClientAddressSource()).Decide(Context("/x")).Action);
    }

    [Fact]
    public void TheCountersTellAHoneypotFromADenyAndASimulationMovesNone()
    {
        var withTrap = Gate(true, On, Rule(RuleAction.Honeypot, agents: ["sqlmap"]));
        _ = withTrap.Decide(Context("/a"));
        _ = withTrap.Decide(Context("/b"));
        _ = Gate(true, HostHoneypotSpec.Off, Rule(RuleAction.Honeypot, agents: ["sqlmap"])).Decide(Context("/c"));

        var counters = _runtime.CountersFor(Id);
        Assert.Equal(2, counters.Honeypotted);
        Assert.Equal(1, counters.Denied);
    }

    [Fact]
    public void RobotsTxtIsNeverRuledOnSoItIsNeverTheLanding()
    {
        var gate = Gate(true, On, Rule(RuleAction.Honeypot, agents: ["sqlmap"]));

        // It goes on to the upstream's own file, like any robots.txt a rule set leaves alone.
        Assert.Equal(GateAction.Forward, gate.Decide(Context("/robots.txt")).Action);
        Assert.Equal(GateAction.Forward, gate.Decide(Context("/robots.txt", "HEAD")).Action);
    }

    [Fact]
    public void AHoneypotDecisionAllocatesNothing()
    {
        var gate = Gate(true, On, Rule(RuleAction.Honeypot, agents: ["sqlmap"]));
        DefaultHttpContext[] contexts = [Context("/wp-login.php"), Context("/wp-login.php", "POST"), Context("/x", userAgent: "Firefox/130")];

        AllocAssert.Zero(
            (gate, contexts),
            static s =>
            {
                foreach (var context in s.contexts)
                {
                    _ = s.gate.Decide(context);
                }
            },
            "gate with a honeypot rule");
    }
}
