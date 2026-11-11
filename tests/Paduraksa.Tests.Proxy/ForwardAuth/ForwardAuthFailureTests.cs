using System.Net;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.ForwardAuth.ForwardAuthFixtures;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>What the provider's answer may carry back: a header Kestrel or the upstream would refuse, an exception nobody listed, one sign-in
/// address, and a sign-in page that is behind the provider itself. Every one of the failures is a 502 and an error, never a 500 and never an allow.</summary>
public sealed class ForwardAuthFailureTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    private static ForwardAuthRuntime Answering(Func<HttpResponseMessage> answer) =>
        new(TimeProvider.System, null, _ => new StubHandler(_ => answer()));

    private static HttpResponseMessage Status(HttpStatusCode status, params (string Name, string Value)[] headers)
    {
        HttpResponseMessage response = new(status) { Content = new StringContent("body") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    [Theory]
    [InlineData("Location", "/x\r\nSet-Cookie: injected=1")]
    [InlineData("Location", "/café")]
    [InlineData("Set-Cookie", "a=b\u0001")]
    [InlineData("Set-Cookie", "a=\u007f")]
    [InlineData("WWW-Authenticate", "Basic realm=\"x\u0080\"")]
    public async Task ARelayedHeaderKestrelCouldNotWriteRefusesWith502AndCountsAnError(string name, string value)
    {
        var runtime = Answering(() => Status(HttpStatusCode.Found, ("Location", "/ok"), (name, value)));
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth")), out _);

        Assert.Equal("502", await RunAsync(gate, Context()));
        Assert.Equal((1, 0, 0), (runtime.CountersFor(Provider).Errors, runtime.CountersFor(Provider).Denied, runtime.CountersFor(Provider).Redirected));
    }

    [Fact]
    public async Task ARelayed401WithAnUnwritableSetCookieIsA502NotA500()
    {
        var runtime = Answering(() => Status(HttpStatusCode.Unauthorized, ("Set-Cookie", "s=1\r\nX: y")));
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth")), out _);

        Assert.Equal("502", await RunAsync(gate, Context()));
        Assert.Equal(1, runtime.CountersFor(Provider).Errors);
    }

    [Fact]
    public async Task AnAllowWhoseIdentityHeaderHasAByteTheUpstreamRequestCannotCarryRefuses()
    {
        var runtime = Answering(() => Status(HttpStatusCode.OK, ("Remote-User", "rémy")));
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth")), out _);

        Assert.Equal("502", await RunAsync(gate, Context(cookie: "session=ok")));
        Assert.Equal((0, 1), (runtime.CountersFor(Provider).Allowed, runtime.CountersFor(Provider).Errors));
    }

    [Fact]
    public async Task AnExceptionNobodyListedIsCountedAsAnErrorAnsweredWith502AndNeverAllows()
    {
        ForwardAuthRuntime runtime = new(TimeProvider.System, null, _ => new StubHandler(_ => throw new NotSupportedException("a handler's own failure")));
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth")), out _);

        var context = Context(cookie: "session=ok");
        Assert.Equal("502", await RunAsync(gate, context));
        Assert.Equal((0, 1, 1), (runtime.CountersFor(Provider).Allowed, runtime.CountersFor(Provider).Errors, runtime.CountersFor(Provider).Calls));
        Assert.Equal(0, runtime.StateOf(Provider)?.CachedAllows ?? 0);

        // The place the call held is given back.
        Assert.Equal("502", await RunAsync(gate, Context(cookie: "session=ok")));
        Assert.Equal(2, runtime.CountersFor(Provider).Errors);
        Assert.Equal(0, runtime.CountersFor(Provider).Busy);
    }

    [Fact]
    public async Task ASignInRedirectReplacesTheProvidersOwnLocationInsteadOfAddingASecondOne()
    {
        var runtime = Answering(() => Status(HttpStatusCode.Unauthorized, ("Location", "https://idp.example.com/login"), ("Set-Cookie", "flow=1; Path=/")));
        var gate = GateFor(runtime, Model(Spec("http://127.0.0.1:1/auth", b =>
        {
            b.SignInUrl = "/oauth2/start";
            b.SignInReturnParameter = "rd";
        })), out var snapshot);
        var policy = (IDeferredPolicy)snapshot.HostsByIndex[0].Routes[0].Policies[0];
        var context = Context(accept: "text/html", path: "/docs", rawTarget: "/docs");
        GateRequestState state = new() { ClientIp = new ClientIpResolution(IpKey.Parse("192.0.2.1"), ClientIpSource.Socket, ClientIpOutcome.PeerNotTrusted) };

        // The response as compiled, before a writer that keeps the last of several could hide a second Location.
        var decision = await policy.CompleteAsync(context, state);

        var response = decision.Response!;
        Assert.Equal(302, response.StatusCode);
        var location = Assert.Single(response.Headers, static h => h.Name == "Location");
        Assert.Equal("/oauth2/start?rd=http%3A%2F%2Fapp.test%2Fdocs", location.Value.ToString());
        Assert.Equal("flow=1; Path=/", Assert.Single(response.Headers, static h => h.Name == "Set-Cookie").Value.ToString());

        var page = Context(accept: "text/html", path: "/docs", rawTarget: "/docs");
        Assert.Equal("302", await RunAsync(gate, page));
        Assert.Equal(["/oauth2/start?rd=http%3A%2F%2Fapp.test%2Fdocs"], page.Response.Headers.Location.ToArray());
    }

    [Theory]
    [InlineData("/oauth2/start")]
    [InlineData("/oauth2/start/")]
    [InlineData("/OAuth2/Start")]
    [InlineData("//oauth2//start")]
    public async Task ASignInPageThatIsItselfBehindTheProviderIsNotRedirectedToItself(string path)
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        var runtime = new ForwardAuthRuntime();
        var gate = GateFor(runtime, Model(Spec(auth.Address, b =>
        {
            b.SignInUrl = "/oauth2/start";
            b.SignInReturnParameter = "rd";
        })), out _);

        var context = Context(accept: "text/html", path: path);

        // The provider's own 401 goes to the browser as it is: no Location, no rd= nested in rd=.
        Assert.Equal("401", await RunAsync(gate, context));
        Assert.False(context.Response.Headers.ContainsKey("Location"));
        Assert.Equal((0, 1, 0), (runtime.CountersFor(Provider).Redirected, runtime.CountersFor(Provider).Denied, runtime.CountersFor(Provider).Errors));

        // Any other page still goes to it.
        var other = Context(accept: "text/html", path: "/oauth2/start-here");
        Assert.Equal("302", await RunAsync(gate, other));
    }

    [Fact]
    public async Task AnAbsoluteSignInPageOnAnotherHostNeverMatchesARequestForTheSamePath()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b =>
        {
            b.SignInUrl = "https://login.example.com/oauth2/start";
            b.SignInReturnParameter = "rd";
        })), out _);

        Assert.Equal("302", await RunAsync(gate, Context(accept: "text/html", path: "/oauth2/start")));
    }

    [Fact]
    public async Task AnAbsoluteSignInPageOnTheSameHostIsNotRedirectedToItselfEither()
    {
        await using var auth = await FakeAuthServer.StartAsync(Cancel);
        var gate = GateFor(new ForwardAuthRuntime(), Model(Spec(auth.Address, b =>
        {
            b.SignInUrl = "http://app.test/oauth2/start";
            b.SignInReturnParameter = "rd";
        })), out _);

        Assert.Equal("401", await RunAsync(gate, Context(accept: "text/html", path: "/oauth2/start")));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(answer(request));
    }
}
