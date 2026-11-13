using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Http;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>What custom header rules cost per request: nothing without rules, nothing for response rules that set or remove, and no more than the
/// header writes themselves for request rules.</summary>
public sealed class CustomHeadersAllocationTests
{
    [Fact]
    public void AnAddAppliedAgainLandsOnceAndAfterAClearLandsAgain()
    {
        var rules = Compile(response: [new HeaderRuleSpec("Vary", HeaderOperation.Add, "Origin")]).Response;
        DefaultHttpContext context = new();
        var headers = context.Response.Headers;
        headers.Vary = "Accept-Encoding";

        CompiledHeaders.ApplyResponse(headers, rules);
        CompiledHeaders.ApplyResponse(headers, rules, again: true);
        Assert.Equal(["Accept-Encoding", "Origin"], headers.Vary.ToArray());

        // YARP cleared the response after the transform ran (an upstream that failed after its headers): the rule comes back.
        headers.Clear();
        CompiledHeaders.ApplyResponse(headers, rules, again: true);
        Assert.Equal(["Origin"], headers.Vary.ToArray());
    }

    [Fact]
    public void ARouteWithoutRulesCostsNothing()
    {
        RequestTransformContext transform = new() { HttpContext = ContextFor(CompiledHeaders.None), ProxyRequest = new HttpRequestMessage() };

        AllocAssert.Zero(transform, static t => Synchronously(CustomHeadersTransform.ApplyAsync(t)), "the request transform without rules");
    }

    [Fact]
    public void ResponseRulesThatSetRemoveOrAddToNothingCostNothing()
    {
        var rules = Compile(response:
        [
            new HeaderRuleSpec("X-Frame-Options", HeaderOperation.Set, "DENY"),
            new HeaderRuleSpec("Server", HeaderOperation.Set, "edge"),
            new HeaderRuleSpec("X-Powered-By", HeaderOperation.Remove, null),
            new HeaderRuleSpec("X-Joined", HeaderOperation.Add, "one"),
        ]).Response;
        // ASP.NET's own HeaderDictionary, not Kestrel's store: this pins the rules' own code. RouteTransformsKestrelAllocationTests runs
        // the same rules through the response transform on a real Kestrel's store.
        DefaultHttpContext context = new();
        context.Response.Headers["X-Powered-By"] = "Express";

        // X-Joined is taken off before each call, so the add finds nothing to join: the add that builds no array.
        AllocAssert.Zero(
            (context.Response.Headers, rules),
            static s =>
            {
                s.Headers.Remove("X-Joined");
                CompiledHeaders.ApplyResponse(s.Headers, s.rules);
            },
            "response rules");
        Assert.Equal("one", context.Response.Headers["X-Joined"]);
        Assert.False(context.Response.Headers.ContainsKey("X-Powered-By"));
    }

    [Fact]
    public void RequestRulesCostNoMoreThanTheHeaderWritesThemselves()
    {
        // Measured on a fresh request each time, like YARP's, which already holds the client's copies of the names: the rules' removals
        // and lookups must add nothing to the same removals and writes made on the HTTP client's header store directly.
        var headers = Compile(request:
        [
            new HeaderRuleSpec("X-Tenant", HeaderOperation.Set, "homelab"),
            new HeaderRuleSpec("X-Trace", HeaderOperation.Add, "proxy"),
            new HeaderRuleSpec("Cookie", HeaderOperation.Remove, null),
        ]);
        var context = ContextFor(headers);
        var writes = AllocAssert.Measure(
            context,
            static c =>
            {
                RequestTransformContext transform = new() { HttpContext = c, ProxyRequest = Copied() };
                var upstream = transform.ProxyRequest.Headers;
                upstream.Remove("X-Tenant");
                upstream.TryAddWithoutValidation("X-Tenant", "homelab");
                upstream.TryAddWithoutValidation("X-Trace", "proxy");
                upstream.Remove("Cookie");
            });
        var rules = AllocAssert.Measure(
            context,
            static c =>
            {
                RequestTransformContext transform = new() { HttpContext = c, ProxyRequest = Copied() };
                Synchronously(CustomHeadersTransform.ApplyAsync(transform));
            });

        RequestTransformContext applied = new() { HttpContext = context, ProxyRequest = Copied() };
        Synchronously(CustomHeadersTransform.ApplyAsync(applied));
        Assert.False(applied.ProxyRequest.Headers.Contains("Cookie"));
        Assert.Equal(["homelab"], applied.ProxyRequest.Headers.GetValues("X-Tenant"));
        Assert.Equal(["client", "proxy"], applied.ProxyRequest.Headers.GetValues("X-Trace"));

        Assert.True(rules.BytesPerCall <= writes.BytesPerCall, $"{rules.BytesPerCall:F1} B a request with the rules, {writes.BytesPerCall:F1} B for the writes alone");
    }

    /// <summary>A request as YARP hands it to the transforms: the client's headers already copied, the names the rules touch among them.</summary>
    private static HttpRequestMessage Copied()
    {
        HttpRequestMessage request = new();
        request.Headers.TryAddWithoutValidation("X-Tenant", "client");
        request.Headers.TryAddWithoutValidation("X-Trace", "client");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        return request;
    }

    private static void Synchronously(ValueTask task)
    {
        if (!task.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("the request transform should complete synchronously");
        }
    }

    private static CompiledHeaders Compile(HeaderRuleSpec[]? request = null, HeaderRuleSpec[]? response = null) =>
        CompiledHeaders.Compile(new HostHeadersSpec { Request = request ?? [], Response = response ?? [] }, static message => throw new InvalidOperationException(message));

    private static DefaultHttpContext ContextFor(CompiledHeaders headers)
    {
        DefaultHttpContext context = new();
        context.Features.Set<IGateFeature>(new GateRequestState
        {
            Route = new CompiledRoute { Id = TestIds.Route("r"), MatchKind = RouteMatchKind.Prefix, Path = "/", Slot = 0, Policies = [], Headers = headers },
        });
        return context;
    }
}
