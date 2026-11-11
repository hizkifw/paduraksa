using System.Net;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Pages;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Gate;

/// <summary>
/// Maintenance mode through the real gate: after the host's chain, the request limits, the HTTPS redirect and the managed
/// <c>robots.txt</c>, before the host's own answers and the route; a request the bypass list's allow rules admit goes on as usual.
/// </summary>
public sealed class MaintenanceGateTests : IDisposable
{
    private static readonly IPAddress Operator = IPAddress.Parse("10.0.0.5");
    private static readonly IPAddress Visitor = IPAddress.Parse("192.0.2.9");

    private static readonly AccessListSpec Staff = TestAccessLists.Allow("staff", "10.0.0.0/24");
    private static readonly AccessListSpec DenyOnly = new() { Id = TestIds.AccessList("deny-only"), Name = "deny-only", Realm = "x", Deny = ["192.0.2.0/24"] };
    private static readonly AccessListSpec Locked = TestAccessLists.Allow("locked", "10.0.0.5");

    private static readonly ProxyConfigModel Config = new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        AccessLists = [Staff, DenyOnly, Locked],
        Hosts =
        [
            ProxyHost("down", "down.test", Route("down-root", "web")) with { Maintenance = Maintenance(Staff) },
            ProxyHost("down-open", "open.test", Route("open-root", "web")) with { Maintenance = Maintenance(null) },
            ProxyHost("down-deny", "deny.test", Route("deny-root", "web")) with { Maintenance = Maintenance(DenyOnly) },
            ProxyHost("up", "up.test", Route("up-root", "web")) with { Maintenance = new HostMaintenanceSpec { Enabled = false, Status = 500 } },
            ProxyHost("custom", "custom.test", Route("custom-root", "web")) with
            {
                Maintenance = Maintenance(null, status: 200, retryAfter: 0),
                ErrorPages = new HostErrorPagesSpec
                {
                    Pages = new Dictionary<int, ErrorPageSpec>
                    {
                        [ErrorPageStatuses.MaintenanceKey] = new() { Heading = "Back at 14:00", Text = "{status} {statusText}", Source = ErrorPageSource.Host },
                    },
                },
            },
            ProxyHost("secure", "secure.test", Route("secure-root", "web")) with { ForceHttps = true, Maintenance = Maintenance(null) },
            ProxyHost("guarded", "guarded.test", Route("guarded-root", "web")) with { Policies = [Locked.Policy()], Maintenance = Maintenance(Staff) },
            ProxyHost("small", "small.test", Route("small-root", "web")) with
            {
                RequestLimits = new HostRequestLimitsSpec { MaxUrlLength = 256 },
                Maintenance = Maintenance(null),
            },
            ProxyHost("crawled", "crawled.test", Route("crawled-root", "web")) with
            {
                Robots = new HostRobotsSpec { Mode = RobotsMode.DisallowAll },
                Maintenance = Maintenance(null),
            },
            new HostSpec
            {
                Id = TestIds.Host("old"),
                Names = ["old.test"],
                Kind = HostKind.Redirect,
                Redirect = new RedirectSpec { Target = "https://new.test", StatusCode = 308 },
                Maintenance = Maintenance(null),
            },
        ],
    };

    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public MaintenanceGateTests()
    {
        _store.Publish(Bound(Config, options: Port8443));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    private static HostMaintenanceSpec Maintenance(AccessListSpec? bypass, int status = 503, int retryAfter = 120) => new()
    {
        Enabled = true,
        Status = status,
        RetryAfterSeconds = retryAfter,
        BypassAccessListId = bypass?.Id ?? default,
    };

    [Fact]
    public void ARequestIsAnsweredWithTheStatusAndRetryAfterOfTheHost()
    {
        var decision = Decide("down.test", "/anything", Visitor);
        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(503, decision.Response!.StatusCode);
        Assert.Equal("120", decision.RetryAfter);
        Assert.Equal("no-store", decision.Response.CacheControl.ToString());
        Assert.Equal("down for maintenance\n", Encoding.UTF8.GetString(decision.Response.Body));

        var noDelay = Decide("custom.test", "/", Visitor);
        Assert.Equal(200, noDelay.Response!.StatusCode);
        Assert.Null(noDelay.RetryAfter);
    }

    [Fact]
    public void ARequestFromTheBypassListsAllowedAddressesGoesOn()
    {
        Assert.Equal(GateAction.Forward, Decide("down.test", "/", Operator).Action);
        Assert.Equal(GateAction.Forward, Decide("down.test", "/x", IPAddress.Parse("10.0.0.200")).Action);
        Assert.Equal(GateAction.Respond, Decide("down.test", "/", IPAddress.Parse("10.0.1.1")).Action);
        Assert.Equal(GateAction.Respond, Decide("down.test", "/", Visitor).Action);

        // No bypass list: nobody is let through.
        Assert.Equal(GateAction.Respond, Decide("open.test", "/", Operator).Action);
    }

    [Fact]
    public void OnlyAnAllowRuleBypassesSoAListOfDenialsLetsNoOneThrough()
    {
        // The list admits everyone it doesn't deny, but "admitted" is not "allowed": maintenance would otherwise be off for the world.
        Assert.Equal(GateAction.Respond, Decide("deny.test", "/", Operator).Action);
        Assert.Equal(GateAction.Respond, Decide("deny.test", "/", Visitor).Action);
    }

    [Fact]
    public void AHostNotInMaintenanceIsUntouched()
    {
        Assert.Equal(GateAction.Forward, Decide("up.test", "/", Visitor).Action);
        Assert.Null(_store.Current.Hosts.Find("up.test", out _)!.Maintenance);
    }

    [Fact]
    public void TheHostsPolicyChainStillRefusesFirst()
    {
        // The host's own allow list answers 403 before maintenance is looked at: a refused client learns nothing about the outage.
        var refused = Decide("guarded.test", "/", Visitor);
        Assert.Equal(403, refused.Response!.StatusCode);

        // An address the chain allows and the bypass list admits goes on; one the chain allows and the list doesn't gets the page.
        Assert.Equal(GateAction.Forward, Decide("guarded.test", "/", Operator).Action);
    }

    [Fact]
    public void TheRequestLimitsAndTheHttpsRedirectComeBeforeIt()
    {
        Assert.Equal(414, Decide("small.test", "/" + new string('a', 400), Visitor).Response!.StatusCode);
        Assert.Equal(503, Decide("small.test", "/ok", Visitor).Response!.StatusCode);

        var plain = Decide("secure.test", "/x", Visitor);
        Assert.Equal(301, plain.Response!.StatusCode);
        Assert.Equal(503, _gate.Decide(TestContexts.Create("secure.test", "/x", scheme: "https", remote: Visitor)).Response!.StatusCode);
    }

    [Fact]
    public void AManagedRobotsFileIsStillServedAndAPassedThroughOneIsNot()
    {
        var served = Decide("crawled.test", "/robots.txt", Visitor);
        Assert.Equal(200, served.Response!.StatusCode);
        Assert.Equal(RobotsText.DisallowAll, Encoding.UTF8.GetString(served.Response.Body));

        // Passthrough means the upstream answers, which it can't: the crawler is told to come back, as it is for every other path.
        Assert.Equal(503, Decide("down.test", "/robots.txt", Visitor).Response!.StatusCode);
        Assert.Equal(503, Decide("crawled.test", "/other", Visitor).Response!.StatusCode);
    }

    [Fact]
    public void ARedirectHostIsInMaintenanceToo()
    {
        Assert.Equal(503, Decide("old.test", "/", Visitor).Response!.StatusCode);
    }

    [Fact]
    public void AnAdminHostNeverCarriesIt()
    {
        var model = Config with
        {
            Hosts =
            [
                new HostSpec
                {
                    Id = TestIds.Host("admin"),
                    Names = ["admin.test"],
                    Kind = HostKind.Admin,
                    ForceHttps = true,
                    Maintenance = Maintenance(null),
                },
            ],
        };

        // The mapping drops it with a warning; the compiler refuses to carry it as well, so a hand-built model can't lock the dashboard out.
        var snapshot = Compile(model, new SnapshotCompilerOptions { PublicHttpsPort = 8443, AdminUpstream = true });
        Assert.Null(snapshot.Hosts.Find("admin.test", out _)!.Maintenance);
    }

    [Fact]
    public async Task ABrowserGetsTheMaintenancePageAndAClientGetsPlainText()
    {
        var builtIn = await ServeAsync("down.test", "text/html");
        Assert.Equal(503, builtIn.Status);
        Assert.Equal("120", builtIn.RetryAfter);
        Assert.Contains("<h1>Down for maintenance</h1>", builtIn.Body, StringComparison.Ordinal);
        Assert.Contains("This site is being worked on.", builtIn.Body, StringComparison.Ordinal);
        Assert.StartsWith("default-src 'none'; style-src 'sha256-", builtIn.Csp, StringComparison.Ordinal);

        var custom = await ServeAsync("custom.test", "text/html");
        Assert.Equal(200, custom.Status);
        Assert.Contains("<h1>Back at 14:00</h1>", custom.Body, StringComparison.Ordinal);
        Assert.Contains("<p>200 OK</p>", custom.Body, StringComparison.Ordinal);

        var plain = await ServeAsync("down.test", "application/json");
        Assert.Equal("down for maintenance\n", plain.Body);
        Assert.Equal("120", plain.RetryAfter);

        var head = await ServeAsync("down.test", "text/html", method: "HEAD");
        Assert.Equal(503, head.Status);
        Assert.Empty(head.Body);
    }

    [Fact]
    public void TheMaintenanceAnswerAndTheBypassAllocateNothing()
    {
        var answered = TestContexts.Create("down.test", "/x", remote: Visitor);
        AllocAssert.Zero((_gate, answered), static s => _ = s._gate.Decide(s.answered), "maintenance answer");

        var bypassed = TestContexts.Create("down.test", "/x", remote: Operator);
        AllocAssert.Zero((_gate, bypassed), static s => _ = s._gate.Decide(s.bypassed), "maintenance bypass");
    }

    private GateDecision Decide(string host, string path, IPAddress remote) => _gate.Decide(TestContexts.Create(host, path, remote: remote));

    private async Task<(int Status, string Body, string? RetryAfter, string? Csp)> ServeAsync(string host, string accept, string method = "GET")
    {
        var context = TestContexts.Create(host, "/", method, remote: Visitor);
        context.Request.Headers.Accept = accept;
        var body = new MemoryStream();
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(body));
        var decision = _gate.Decide(context);
        Assert.Equal(GateAction.Respond, decision.Action);
        await ResponseWriter.WriteAsync(context, decision.Response!, decision.RetryAfter);
        await context.Response.BodyWriter.FlushAsync();
        var headers = context.Response.Headers;
        return (
            context.Response.StatusCode,
            Encoding.UTF8.GetString(body.ToArray()),
            headers.RetryAfter.Count > 0 ? headers.RetryAfter.ToString() : null,
            headers.TryGetValue("Content-Security-Policy", out var csp) ? csp.ToString() : null);
    }
}
