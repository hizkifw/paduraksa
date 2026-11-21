using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Honeypot;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.ProxyProtocol;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// Where the gate sends a request into a host's trap, and what comes first: the prefix compare, the host's chain, the HTTPS redirect,
/// robots.txt and maintenance. What the trap then answers is in <c>HoneypotEndToEndTests</c>.
/// </summary>
public sealed class HoneypotGateTests : IDisposable
{
    private const string Prefix = "/archive-k7f3/";

    private static readonly AccessListSpec Staff = TestAccessLists.Allow("staff", "10.0.0.5");
    private static readonly HostHoneypotSpec On = new() { Enabled = true, Path = Prefix };

    private readonly GateTrapRig _rig = new();
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public HoneypotGateTests()
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            AccessLists = [Staff],
            Hosts =
            [
                ProxyHost("trap", "trap.test", Route("trap-root", "web")) with { Honeypot = On, Robots = new HostRobotsSpec { Mode = RobotsMode.DisallowAll } },
                ProxyHost("plain", "plain.test", Route("plain-root", "web")),
                ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { Honeypot = On, ForceHttps = true },
                ProxyHost("staff", "staff.test", Route("staff-root", "web")) with { Honeypot = On, Policies = [Staff.Policy()] },
                ProxyHost("down", "down.test", Route("down-root", "web")) with { Honeypot = On, Maintenance = new HostMaintenanceSpec { Enabled = true } },
                ProxyHost("pass", "pass.test", Route("pass-root", "web")) with { Honeypot = On },
                new HostSpec
                {
                    Id = TestIds.Host("old"),
                    Names = ["old.test"],
                    Kind = HostKind.Redirect,
                    Honeypot = On,
                    Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 },
                },
                new HostSpec
                {
                    Id = TestIds.Host("soon"),
                    Names = ["soon.test"],
                    Kind = HostKind.Respond,
                    Honeypot = On,
                    Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                },
            ],
        };
        _store.Publish(Bound(model, options: _rig.Options));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    [Theory]
    [InlineData("/archive-k7f3/")]
    [InlineData("/archive-k7f3/post-1/")]
    [InlineData("/archive-k7f3/sitemap.xml")]
    [InlineData("/archive-k7f3//post-1/")]
    [InlineData("/archive-k7f3/a;b=c/post-1/")]
    [InlineData("//archive-k7f3/")]
    [InlineData("/archive-k7f3/x/../post-1/")]
    public void EverythingUnderThePrefixGoesToTheTrap(string path)
    {
        var decision = _gate.Decide(TestContexts.Create("trap.test", path));

        Assert.Equal(GateAction.Honeypot, decision.Action);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/archive-k7f3")]
    [InlineData("/archive-k7f3x/")]
    [InlineData("/archive-k7f/")]
    [InlineData("/x/archive-k7f3/")]
    [InlineData("/ARCHIVE-K7F3/")]
    [InlineData("/archive-k7f3x/post-1/")]
    public void ANearMissGoesOnToTheRoutes(string path)
    {
        var decision = _gate.Decide(TestContexts.Create("trap.test", path));

        Assert.Equal(GateAction.Forward, decision.Action);
    }

    [Fact]
    public void AHostWithoutATrapHasNoPrefix()
    {
        Assert.Equal(GateAction.Forward, _gate.Decide(TestContexts.Create("plain.test", Prefix)).Action);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public void EveryMethodIsTheHandlersToAnswer(string method)
    {
        Assert.Equal(GateAction.Honeypot, _gate.Decide(TestContexts.Create("trap.test", Prefix, method)).Action);
    }

    [Theory]
    [InlineData("old.test")]
    [InlineData("soon.test")]
    public void ARedirectOrRespondHostCanCarryATrapToo(string host)
    {
        Assert.Equal(GateAction.Honeypot, _gate.Decide(TestContexts.Create(host, Prefix + "post-1/")).Action);
    }

    [Fact]
    public void ThePlainHttpRequestIsRedirectedBeforeItEntersTheTrap()
    {
        var plain = _gate.Decide(TestContexts.Create("secure.test", Prefix));
        var tls = _gate.Decide(TestContexts.Create("secure.test", Prefix, scheme: "https"));

        Assert.Equal(GateAction.Respond, plain.Action);
        Assert.Equal(301, plain.Response!.StatusCode);
        Assert.Equal(GateAction.Honeypot, tls.Action);
    }

    [Fact]
    public void TheHostsChainStillAppliesInsideTheTrap()
    {
        var refused = _gate.Decide(TestContexts.Create("staff.test", Prefix, remote: IPAddress.Parse("203.0.113.9")));
        var allowed = _gate.Decide(TestContexts.Create("staff.test", Prefix, remote: IPAddress.Parse("10.0.0.5")));

        Assert.Equal(GateAction.Respond, refused.Action);
        Assert.Equal(403, refused.Response!.StatusCode);
        Assert.Equal(GateAction.Honeypot, allowed.Action);
    }

    [Fact]
    public void ARobotsFileIsReadBeforeTheTrapAndMaintenanceDoesNotCoverIt()
    {
        Assert.Equal(GateAction.Respond, _gate.Decide(TestContexts.Create("trap.test", "/robots.txt")).Action);

        // A host in maintenance is down for its upstream, and still a trap for what ignores robots.txt.
        Assert.Equal(GateAction.Honeypot, _gate.Decide(TestContexts.Create("down.test", Prefix)).Action);
        Assert.Equal(503, _gate.Decide(TestContexts.Create("down.test", "/")).Response!.StatusCode);
    }

    [Fact]
    public void TheTrapIsAnAnswerOfTheHostNotAMissingRouteOrAnUnreadySnapshot()
    {
        var context = TestContexts.Create("trap.test", Prefix + "nothing/");

        _ = _gate.Decide(context);

        var state = context.Features.Get<IGateFeature>()!.State;
        Assert.Equal(Paduraksa.Proxy.Metrics.GateOutcome.Answered, state.Outcome);
        Assert.True(state.ProxyAnswered);
        Assert.False(state.HoneypotViaRule);
    }

    [Fact]
    public void TheSinkHearsOfEveryRobotsFetchOnAHostWithATrapAndOnlyThere()
    {
        var trap = TestContexts.Create("trap.test", "/robots.txt", remote: IPAddress.Parse("198.51.100.7"));
        trap.Request.Headers.UserAgent = "Crawler/1.0";
        _ = _gate.Decide(trap);
        _ = _gate.Decide(TestContexts.Create("trap.test", "/robots.txt", "HEAD"));
        _ = _gate.Decide(TestContexts.Create("plain.test", "/robots.txt"));
        _ = _gate.Decide(TestContexts.Create("trap.test", "/robots.txt", "POST"));

        // A host whose robots.txt passes through is asked too: the visitor read a file, even though the upstream's.
        _ = _gate.Decide(TestContexts.Create("pass.test", "/robots.txt"));

        Assert.Equal(3, _rig.Sink.RobotsFetches.Count);
        var (host, client, userAgent) = _rig.Sink.RobotsFetches[0];
        Assert.Equal(TestIds.Host("trap"), host);
        Assert.Equal(IpKey.Parse("198.51.100.7"), client);
        Assert.Equal("Crawler/1.0", userAgent);
        Assert.Empty(_rig.Sink.Hits);
    }
}
