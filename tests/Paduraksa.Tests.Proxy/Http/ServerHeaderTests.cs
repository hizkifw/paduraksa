using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Tls;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Transforms;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.Http;

/// <summary>
/// The per-host <c>Server</c> header (PLAN §14 M6): built once by the compiler, sent by the gate on gate answers and
/// forwards alike, removed or left to the upstream as the mode says, and never overwritten by Kestrel.
/// </summary>
public sealed class ServerHeaderTests : IDisposable
{
    private readonly SnapshotStore _store = new();
    private readonly GateMiddleware _gate;

    public ServerHeaderTests()
    {
        _store.Publish(Bound(Model()));
        _gate = new GateMiddleware(_store, new TrustedProxyClientAddressSource());
    }

    public void Dispose() => _store.Dispose();

    private static ProxyConfigModel Model() => new()
    {
        Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
        Hosts =
        [
            ProxyHost("named", "named.test", Route("named-root", "web")),
            ProxyHost("custom", "custom.test", Route("custom-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/1.0" },
            },
            ProxyHost("upstream", "upstream.test", Route("upstream-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Upstream },
            },
            ProxyHost("remove", "remove.test", Route("remove-root", "web")) with
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            ProxyHost("strict", "strict.test", Route("strict-root", "web")) with
            {
                Tls = new HostTlsSpec { MinimumVersion = TlsMinimumVersion.Tls13 },
            },
            new HostSpec
            {
                Id = TestIds.Host("soon"),
                Names = ["soon.test"],
                Kind = HostKind.Respond,
                Respond = new RespondSpec { StatusCode = 503, Body = "soon" },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge/2.0" },
            },
            new HostSpec
            {
                Id = TestIds.Host("old"),
                Names = ["old.test"],
                Kind = HostKind.Redirect,
                Redirect = new RedirectSpec { Target = "https://new.test" },
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Remove },
            },
            ProxyHost("api", "api.test", Route("api-only", "web", "/api")),
        ],
    };

    private static string? Sent(DefaultHttpContext context) =>
        context.Response.Headers.TryGetValue(ServerHeader.HeaderName, out Microsoft.Extensions.Primitives.StringValues value) && value.Count > 0
            ? value.ToString()
            : null;

    [Fact]
    public void TheCompilerPrecomputesOneValueAndModePerHost()
    {
        RuntimeSnapshot snapshot = _store.Current;

        Assert.Equal("Paduraksa", snapshot.Hosts.Find("named.test")!.Server.ToString());
        Assert.Equal(ServerHeaderMode.Paduraksa, snapshot.Hosts.Find("named.test")!.ServerMode);
        Assert.Equal("edge/1.0", snapshot.Hosts.Find("custom.test")!.Server.ToString());
        Assert.Equal(ServerHeaderMode.Custom, snapshot.Hosts.Find("custom.test")!.ServerMode);
        Assert.True(snapshot.Hosts.Find("upstream.test")!.Server.Count == 0);
        Assert.Equal(ServerHeaderMode.Upstream, snapshot.Hosts.Find("upstream.test")!.ServerMode);
        Assert.True(snapshot.Hosts.Find("remove.test")!.Server.Count == 0);
        Assert.Equal(ServerHeaderMode.Remove, snapshot.Hosts.Find("remove.test")!.ServerMode);

        // Built-in defaults, including the one an unknown host follows.
        Assert.Equal("Paduraksa", snapshot.DefaultServer.ToString());
        Assert.Equal(ServerHeaderMode.Paduraksa, snapshot.DefaultServerMode);
    }

    [Theory]
    [InlineData("named.test", "/", "Paduraksa")] // forwarded
    [InlineData("custom.test", "/", "edge/1.0")]
    [InlineData("upstream.test", "/", null)] // no header of ours; the upstream's is the transform's business
    [InlineData("remove.test", "/", null)]
    [InlineData("api.test", "/nothing", "Paduraksa")] // the gate's own 404
    [InlineData("soon.test", "/", "edge/2.0")] // a Respond host's fixed answer
    [InlineData("old.test", "/deep", null)] // a Redirect host's fixed answer, removed
    [InlineData("nobody.test", "/", "Paduraksa")] // unknown hosts follow the global defaults
    public void TheGateAppliesTheModeToEveryAnswerItGives(string host, string path, string? expected)
    {
        DefaultHttpContext context = TestContexts.Create(host, path, scheme: "http");

        _ = _gate.Decide(context);

        Assert.Equal(expected, Sent(context));
    }

    [Fact]
    public void AMisdirectedRequestStillCarriesTheHostsHeader()
    {
        DefaultHttpContext context = TestContexts.Create("strict.test", "/", scheme: "https");

        GateDecision decision = _gate.Decide(context);

        Assert.Equal(GateAction.Respond, decision.Action);
        Assert.Equal(421, decision.Response!.StatusCode);
        Assert.Equal("Paduraksa", Sent(context));
    }

    [Fact]
    public void UpstreamAndRemoveModesLeaveAGateAnswerBare()
    {
        foreach (string host in (string[])["upstream.test", "remove.test"])
        {
            DefaultHttpContext context = TestContexts.Create(host, "/", scheme: "http");
            _ = _gate.Decide(context);
            Assert.Null(Sent(context));
        }
    }

    [Fact]
    public void AnUnknownHostFollowsTheGlobalDefaultsMode()
    {
        ProxyConfigModel model = Model() with
        {
            Defaults = new DefaultsSpec
            {
                ServerHeader = new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "global/1.0" },
            },
        };
        using SnapshotStore store = new();
        store.Publish(Bound(model));
        GateMiddleware gate = new(store, new TrustedProxyClientAddressSource());
        DefaultHttpContext context = TestContexts.Create("nobody.test", "/", scheme: "http");

        _ = gate.Decide(context);

        Assert.Equal("global/1.0", Sent(context));
    }

    [Fact]
    public void TheCompilerRefusesACustomValueThatCantBeSent()
    {
        static ProxyConfigModel Invalid(HostServerHeaderSpec serverHeader) => new()
        {
            Upstreams = [Upstream("web", "http://127.0.0.1:1/")],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "web")) with { ServerHeader = serverHeader }],
        };

        foreach (HostServerHeaderSpec invalid in (HostServerHeaderSpec[])
        [
            new() { Mode = ServerHeaderMode.Custom, Value = null },
            new() { Mode = ServerHeaderMode.Custom, Value = string.Empty },
            new() { Mode = ServerHeaderMode.Custom, Value = " edge/1.0" },
            new() { Mode = ServerHeaderMode.Custom, Value = "edge/1.0 " },
            new() { Mode = ServerHeaderMode.Custom, Value = "edge\t1.0" },
            new() { Mode = ServerHeaderMode.Custom, Value = new string('a', 129) },
            new() { Mode = (ServerHeaderMode)9 },
        ])
        {
            CompileResult result = SnapshotCompiler.Compile(Invalid(invalid));
            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.Errors);
        }

        // 128 visible characters and spaces inside the value are fine; the value is ignored outside custom mode.
        Assert.True(SnapshotCompiler.Compile(Invalid(new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = new string('a', 128) })).Succeeded);
        Assert.True(SnapshotCompiler.Compile(Invalid(new HostServerHeaderSpec { Mode = ServerHeaderMode.Custom, Value = "edge proxy/1.0" })).Succeeded);
        Assert.True(SnapshotCompiler.Compile(Invalid(new HostServerHeaderSpec { Mode = ServerHeaderMode.Paduraksa, Value = "bad\tvalue" })).Succeeded);
    }

    [Theory]
    [InlineData("Paduraksa", true)]
    [InlineData("edge proxy/1.0", true)]
    [InlineData("~", true)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData(" edge/1.0", false)]
    [InlineData("edge/1.0 ", false)]
    [InlineData("edge\t1.0", false)]
    [InlineData("edge\u007f1.0", false)]
    [InlineData("edge\u00e91.0", false)]
    [InlineData("edge\r\nInjected: yes", false)]
    public void TheCustomValueGrammarIsTheSimpleVisibleSubset(string value, bool valid) =>
        Assert.Equal(valid, ServerHeader.IsValidValue(value));

    [Fact]
    public async Task TheResponseTransformMakesEachModeHoldAfterYarpCopiesTheUpstreamsHeader()
    {
        foreach ((string host, string? expected) in new (string Host, string? Expected)[]
        {
            ("named.test", "Paduraksa"),
            ("custom.test", "edge/1.0"),
            ("upstream.test", "upstream/1.0"),
            ("remove.test", null),
        })
        {
            DefaultHttpContext context = TestContexts.Create(host, "/", scheme: "http");
            context.Response.Headers[ServerHeader.HeaderName] = host switch
            {
                "named.test" => new StringValues(["Paduraksa", "upstream/1.0"]),
                "custom.test" => new StringValues(["edge/1.0", "upstream/1.0"]),
                _ => new StringValues("upstream/1.0"),
            };
            ResponseTransformContext transform = Transform(context, host, headersCopied: true);

            await ServerHeaderTransform.ApplyResponseAsync(transform);

            Assert.Equal(expected, Sent(context));
        }
    }

    [Fact]
    public async Task TheResponseTransformHandlesAFailedForwardAndHeadersNotCopied()
    {
        // A connection failure has no proxy response at all; the gate's header (or its absence) is what the client gets.
        DefaultHttpContext failedContext = TestContexts.Create("custom.test", "/", scheme: "http");
        failedContext.Response.Headers[ServerHeader.HeaderName] = "edge/1.0";
        await ServerHeaderTransform.ApplyResponseAsync(Transform(failedContext, "custom.test", headersCopied: true, proxy: null));
        Assert.Equal("edge/1.0", Sent(failedContext));

        DefaultHttpContext removed = TestContexts.Create("remove.test", "/", scheme: "http");
        removed.Response.Headers[ServerHeader.HeaderName] = "upstream/1.0";
        using HttpResponseMessage proxy = new();
        proxy.Headers.TryAddWithoutValidation(ServerHeader.HeaderName, "upstream/1.0");
        await ServerHeaderTransform.ApplyResponseAsync(Transform(removed, "remove.test", headersCopied: false, proxy));
        Assert.Null(Sent(removed));
        Assert.False(proxy.Headers.Contains(ServerHeader.HeaderName));
    }

    [Theory]
    [InlineData("named.test")]
    [InlineData("custom.test")]
    [InlineData("upstream.test")]
    [InlineData("remove.test")]
    public void ApplyingEachModeAllocatesNothing(string host)
    {
        DefaultHttpContext context = TestContexts.Create(host, "/", scheme: "http");
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), $"gate forward with Server mode {host}");
    }

    [Fact]
    public void ApplyingADefaultAnswerAllocatesNothing()
    {
        DefaultHttpContext context = TestContexts.Create("nobody.test", "/", scheme: "http", remote: IPAddress.Loopback);
        AllocAssert.Zero((_gate, context), static s => _ = s._gate.Decide(s.context), "gate default answer with Server");
    }

    [Fact]
    public void TheResponseTransformAllocatesNothingForEachMode()
    {
        foreach (string host in (string[])["named.test", "custom.test", "upstream.test", "remove.test"])
        {
            DefaultHttpContext context = TestContexts.Create(host, "/", scheme: "http");
            context.Response.Headers[ServerHeader.HeaderName] = new StringValues(["Paduraksa", "upstream/1.0"]);
            ResponseTransformContext transform = Transform(context, host, headersCopied: true);

            AllocAssert.Zero(
                transform,
                static t =>
                {
                    ValueTask task = ServerHeaderTransform.ApplyResponseAsync(t);
                    if (!task.IsCompletedSuccessfully)
                    {
                        throw new InvalidOperationException("the response transform should complete synchronously");
                    }

                    task.GetAwaiter().GetResult();
                },
                $"response transform with Server mode {host}");
        }
    }

    private ResponseTransformContext Transform(
        DefaultHttpContext context, string hostName, bool headersCopied, HttpResponseMessage? proxy = default)
    {
        GateRequestState state = new() { Host = _store.Current.Hosts.Find(hostName) };
        context.Features.Set<IGateFeature>(state);
        return new ResponseTransformContext
        {
            HttpContext = context,
            ProxyResponse = proxy,
            HeadersCopied = headersCopied,
        };
    }
}
