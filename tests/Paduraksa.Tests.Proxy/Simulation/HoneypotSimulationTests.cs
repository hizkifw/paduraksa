using System.Net;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.RuleSets.RuleSpecs;
using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Simulation;

/// <summary>
/// The simulator's honeypot step: the entry, the page's kind and link, whether it is behind the challenge and the status the trap would
/// answer, for a request under the prefix and for one a rule set sends there. A simulation records no hit, counts nothing and tells the
/// sink nothing, and its status is the handler's (<c>HoneypotEndToEndTests</c> checks that against a real answer).
/// </summary>
public sealed class HoneypotSimulationTests
{
    private const string Prefix = "/archive-k7f3/";
    private static readonly IPAddress Client = IPAddress.Parse("192.0.2.1");

    private readonly GateTrapRig _rig = new();

    private RuntimeSnapshot Snapshot(HostHoneypotSpec? trap = null, HostRobotsSpec? robots = null)
    {
        var set = TestIds.RuleSet("scanners");
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            RuleSets = [new RuleSetSpec { Id = set, Name = "scanners", Rules = [Rule(RuleAction.Honeypot, agents: ["sqlmap"])] }],
            Hosts =
            [
                ProxyHost("trap", "trap.test", Route("trap-root", "web")) with
                {
                    Honeypot = trap ?? new HostHoneypotSpec { Enabled = true, Path = Prefix },
                    Robots = robots ?? new HostRobotsSpec { Mode = RobotsMode.DisallowAll },
                    Policies = [new RuleSetPolicySpec(set)],
                },
                ProxyHost("plain", "plain.test", Route("plain-root", "web")) with { Policies = [new RuleSetPolicySpec(set)] },
            ],
        };
        return Bound(model, options: _rig.Options);
    }

    private static GateProbe Probe(string path, string host = "trap.test", string method = "GET", params ProbeHeader[] headers) => new()
    {
        Host = host,
        Path = path,
        Method = method,
        ClientAddress = Client,
        Headers = headers,
    };

    private static GateStep Step(GateSimulation run) => Assert.Single(run.Steps, s => s.Kind == GateStepKind.Honeypot);

    private static string Fact(GateStep step, string name) => Assert.Single(step.Facts, f => f.Name == name).Value;

    [Fact]
    public void ARequestUnderThePrefixSaysWhichPageItIsAndHowItGotIn()
    {
        var run = GateSimulator.Run(Snapshot(), Probe(Prefix + "hidden-3/"));

        Assert.Equal((GateOutcomeAction.Respond, GateReason.Honeypot, 200), (run.Outcome.Action, run.Outcome.Reason, run.Outcome.StatusCode));
        var step = Step(run);
        Assert.Equal(GateStepVerdict.Stopped, step.Verdict);
        Assert.Equal(("robots", Prefix, "post", "hidden", "no", "200"), (Fact(step, "entry"), Fact(step, "prefix"), Fact(step, "pageKind"), Fact(step, "linkKind"), Fact(step, "challenged"), Fact(step, "status")));
        Assert.Contains("under the host's trap prefix /archive-k7f3/, which robots.txt disallows", step.Summary, StringComparison.Ordinal);
        Assert.Contains("post page is answered 200", step.Summary, StringComparison.Ordinal);
        Assert.Equal(step.Summary, Assert.Single(run.Steps, s => s.Verdict == GateStepVerdict.Stopped).Summary);
    }

    [Fact]
    public void WithoutADisallowLineTheEntryIsThePrefixAndAMissingPageIs404()
    {
        var snapshot = Snapshot(robots: new HostRobotsSpec { Mode = RobotsMode.Passthrough });

        var run = GateSimulator.Run(snapshot, Probe(Prefix + "nothing/"));

        Assert.Equal(404, run.Outcome.StatusCode);
        Assert.Equal("prefix", Fact(Step(run), "entry"));
        Assert.Contains("page that isn't there", Step(run).Summary, StringComparison.Ordinal);
        Assert.Contains("robots.txt doesn't disallow it", Step(run).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void APageBehindTheChallengeIsChallengedUntilTheRequestCarriesTheTrapsCookie()
    {
        var snapshot = Snapshot();
        var browser = new ProbeHeader("Accept", "text/html");

        var shown = GateSimulator.Run(snapshot, Probe(Prefix + "secure-9/", headers: browser));
        Assert.Equal(200, shown.Outcome.StatusCode);
        Assert.Equal(("yes", "challenge"), (Fact(Step(shown), "challenged"), Fact(Step(shown), "answer")));
        Assert.Contains("the challenge page is answered", Step(shown).Summary, StringComparison.Ordinal);

        var plain = GateSimulator.Run(snapshot, Probe(Prefix + "secure-9/"));
        Assert.Equal(403, plain.Outcome.StatusCode);
        Assert.Contains("isn't a browser's: 403", Step(plain).Summary, StringComparison.Ordinal);

        Span<char> token = stackalloc char[ProofOfWorkToken.EncodedLength];
        var host = snapshot.Hosts.Find("trap.test", out _)!;
        Assert.True(ProofOfWorkToken.TryEncode(
            ProofOfWorkDomain.Honeypot, new TestMacKey(), host.ProofOfWorkHostScope, IpKey.FromIPAddress(Client),
            (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600), 16, token, out var length));
        var passed = GateSimulator.Run(snapshot, Probe(Prefix + "secure-9/", headers: [browser, new ProbeHeader("Cookie", "reader_token=" + token[..length].ToString())]), proofOfWork: _rig.ProofOfWork);
        Assert.Equal(200, passed.Outcome.StatusCode);
        Assert.Equal(("no", "page"), (Fact(Step(passed), "challenged"), Fact(Step(passed), "answer")));
        Assert.Contains("which the request's cookie has passed", Step(passed).Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("POST", 405, "methodNotAllowed")]
    [InlineData("DELETE", 405, "methodNotAllowed")]
    [InlineData("HEAD", 200, "page")]
    public void OnlyReadsAreAnsweredWithAPage(string method, int status, string answer)
    {
        var run = GateSimulator.Run(Snapshot(), Probe(Prefix + "post-1/", method: method));

        Assert.Equal(status, run.Outcome.StatusCode);
        Assert.Equal(answer, Fact(Step(run), "answer"));
    }

    [Fact]
    public void ThePostToTheVerifyPathIsRefusedForWantOfAForm()
    {
        var run = GateSimulator.Run(Snapshot(), Probe(Prefix + "verify", method: "POST"));

        Assert.Equal(403, run.Outcome.StatusCode);
        Assert.Equal("verify", Fact(Step(run), "answer"));
        Assert.Contains("posts no form", Step(run).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARulesHoneypotActionNamesTheRuleThenTheTrapAndWithoutATrapIsA403()
    {
        var snapshot = Snapshot();
        var run = GateSimulator.Run(snapshot, Probe("/wp-login.php", headers: new ProbeHeader("User-Agent", "sqlmap/1.7")));

        Assert.Equal((GateOutcomeAction.Respond, GateReason.Honeypot, 200), (run.Outcome.Action, run.Outcome.Reason, run.Outcome.StatusCode));
        var rule = Assert.Single(run.Steps, s => s.Kind == GateStepKind.HostPolicy);
        Assert.Equal(GateStepVerdict.Passed, rule.Verdict);
        Assert.Contains("answers it with a page of the host's trap", rule.Summary, StringComparison.Ordinal);
        Assert.Equal(("honeypot", "yes"), (Fact(rule, "action"), Fact(rule, "trap")));
        var step = Step(run);
        Assert.Equal(("rule", "landing"), (Fact(step, "entry"), Fact(step, "pageKind")));
        Assert.Contains("a rule set's honeypot action sends it to the host's trap", step.Summary, StringComparison.Ordinal);

        // The host that has no trap says so, and the rule is the deny it was compiled as.
        var denied = GateSimulator.Run(snapshot, Probe("/wp-login.php", "plain.test", headers: new ProbeHeader("User-Agent", "sqlmap/1.7")));
        Assert.Equal((GateOutcomeAction.Respond, 403), (denied.Outcome.Action, denied.Outcome.StatusCode));
        var denial = Assert.Single(denied.Steps, s => s.Kind == GateStepKind.HostPolicy);
        Assert.Equal(GateStepVerdict.Stopped, denial.Verdict);
        Assert.Contains("refuses it with 403, because the host has no trap to answer with", denial.Summary, StringComparison.Ordinal);
        Assert.Equal("none", Fact(denial, "trap"));
        Assert.DoesNotContain(denied.Steps, s => s.Kind == GateStepKind.Honeypot);
    }

    [Fact]
    public void WithoutTheKeyTheTrapSays503()
    {
        var rig = new GateTrapRig(withKey: false);
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("trap", "trap.test", Route("trap-root", "web")) with { Honeypot = new HostHoneypotSpec { Enabled = true, Path = Prefix } }],
        };

        var run = GateSimulator.Run(Bound(model, options: rig.Options), Probe(Prefix + "post-1/"));

        Assert.Equal(503, run.Outcome.StatusCode);
        Assert.Contains("key it comes from isn't open", Step(run).Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void AnythingElseOnTheHostIsNotTheTrap()
    {
        var run = GateSimulator.Run(Snapshot(), Probe("/archive-k7f3x/"));

        Assert.Equal(GateOutcomeAction.Forward, run.Outcome.Action);
        Assert.DoesNotContain(run.Steps, s => s.Kind == GateStepKind.Honeypot);
    }

    [Fact]
    public void ASimulationRecordsNoHitCountsNothingAndTellsTheSinkNothing()
    {
        var snapshot = Snapshot();

        _ = GateSimulator.Run(snapshot, Probe(Prefix + "post-1/"));
        _ = GateSimulator.Run(snapshot, Probe(Prefix + "secure-9/", headers: new ProbeHeader("Accept", "text/html")));
        _ = GateSimulator.Run(snapshot, Probe("/wp-login.php", headers: new ProbeHeader("User-Agent", "sqlmap/1.7")));
        _ = GateSimulator.Run(snapshot, Probe("/robots.txt"));

        Assert.Empty(_rig.Sink.Hits);
        Assert.Empty(_rig.Sink.RobotsFetches);
        Assert.Equal(0, _rig.Honeypot.Stats.Served);
        Assert.Equal(0, _rig.ProofOfWork.Stats.ChallengesServed);
    }
}
