using System.Net.Http.Headers;

using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>The cookie transforms' steady-state paths allocate nothing: the admin route returns early, and a non-admin
/// response without the session cookie only looks for the header.</summary>
public sealed class SessionCookieAllocationTests
{
    [Fact]
    public void TheAdminRouteEarlyReturnAllocatesNothing()
    {
        DefaultHttpContext context = AdminContext();
        RequestTransformContext transform = new()
        {
            HttpContext = context,
            ProxyRequest = new HttpRequestMessage(),
        };
        transform.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", "__Host-paduraksa-session=live; theme=dark");

        AllocAssert.Zero(transform, static t => Apply(t), "admin-route request transform");
        Assert.Contains("__Host-paduraksa-session=live", transform.ProxyRequest.Headers.GetValues("Cookie").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminRouteResponseEarlyReturnAllocatesNothing()
    {
        DefaultHttpContext context = AdminContext();
        ResponseTransformContext transform = new()
        {
            HttpContext = context,
            ProxyResponse = new HttpResponseMessage(),
        };
        transform.ProxyResponse.Headers.TryAddWithoutValidation("Set-Cookie", "__Host-paduraksa-session=live; Path=/");

        AllocAssert.Zero(transform, static t => ApplyResponse(t), "admin-route response transform");
        Assert.True(transform.ProxyResponse.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public void ANonAdminResponseWithoutTheSessionCookieAllocatesNothing()
    {
        DefaultHttpContext context = new();
        context.Response.Headers["Set-Cookie"] = "theme=dark; Path=/";
        ResponseTransformContext transform = new()
        {
            HttpContext = context,
            ProxyResponse = new HttpResponseMessage(),
            HeadersCopied = true,
        };

        AllocAssert.Zero(transform, static t => ApplyResponse(t), "response transform without the session cookie");
        Assert.True(context.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Fact]
    public void TheInternalSecretTransformAllocatesNothingOnANonAdminRequest()
    {
        RequestTransformContext transform = new()
        {
            HttpContext = new DefaultHttpContext(),
            ProxyRequest = new HttpRequestMessage(),
        };
        AdminInternalSecretTransform secret = new(listener: null);

        AllocAssert.Zero(transform, t => ApplySecret(secret, t), "secret transform, no header");
        transform.ProxyRequest.Headers.TryAddWithoutValidation("X-Paduraksa-Internal", "client");
        AllocAssert.Zero(transform, t => ApplySecret(secret, t), "secret transform, client header");
        Assert.False(transform.ProxyRequest.Headers.Contains("X-Paduraksa-Internal"));
    }

    private static void ApplySecret(AdminInternalSecretTransform transform, RequestTransformContext context) =>
        transform.ApplyAsync(context).AsTask().GetAwaiter().GetResult();

    private static void Apply(RequestTransformContext context) =>
        SessionCookieTransform.ApplyAsync(context).AsTask().GetAwaiter().GetResult();

    private static void ApplyResponse(ResponseTransformContext context) =>
        SessionCookieTransform.ApplyResponseAsync(context).AsTask().GetAwaiter().GetResult();

    private static DefaultHttpContext AdminContext()
    {
        DefaultHttpContext context = new();
        context.Features.Set<IGateFeature>(new GateRequestState
        {
            Host = new CompiledHost
            {
                Index = 0,
                Id = TestIds.Host("admin"),
                Kind = HostKind.Admin,
                Routes = [],
                Policies = [],
            },
            Route = new CompiledRoute
            {
                Id = RouteId.Empty,
                MatchKind = RouteMatchKind.Prefix,
                Path = "/",
                Slot = -1,
                Policies = [],
                Admin = true,
            },
        });
        return context;
    }
}
