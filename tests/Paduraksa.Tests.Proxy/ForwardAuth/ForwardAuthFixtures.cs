using System.Net;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Proxy.ForwardAuth;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Proxy.ForwardAuth;

/// <summary>A provider on a fake endpoint, a route that names it and the gate over them, for the policy tests.</summary>
internal static class ForwardAuthFixtures
{
    public static readonly AuthProviderId Provider = TestIds.AuthProvider("sso");

    public static AuthProviderSpec Spec(string url, Action<AuthProviderSpecBuilder>? tweak = null)
    {
        AuthProviderSpecBuilder builder = new() { Url = url };
        tweak?.Invoke(builder);
        return builder.Build();
    }

    public static ProxyConfigModel Model(AuthProviderSpec spec, params HostSpec[] extraHosts) => new()
    {
        Upstreams = [TestSnapshots.Upstream("web", "http://127.0.0.1:1")],
        AuthProviders = [spec],
        Hosts =
        [
            TestSnapshots.ProxyHost("app", "app.test", TestSnapshots.Route("app-root", "web") with { AuthProvider = spec.Id }),
            .. extraHosts,
        ],
    };

    public static GateMiddleware GateFor(ForwardAuthRuntime runtime, ProxyConfigModel model, out RuntimeSnapshot snapshot)
    {
        snapshot = TestSnapshots.Bound(model, options: new SnapshotCompilerOptions { ForwardAuth = runtime });
        SnapshotStore store = new();
        store.Publish(snapshot);
        return new GateMiddleware(store, new TrustedProxyClientAddressSource());
    }

    public static DefaultHttpContext Context(
        string client = "192.0.2.1", string? cookie = null, string host = "app.test", string path = "/", string method = "GET", string? accept = null, string? rawTarget = null)
    {
        var context = TestContexts.Create(host, path, method, remote: IPAddress.Parse(client), rawTarget: rawTarget);
        if (cookie is not null)
        {
            context.Request.Headers.Cookie = cookie;
        }

        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        return context;
    }

    public static async Task<string> BodyOfAsync(HttpContext context)
    {
        await context.Response.BodyWriter.FlushAsync();
        var stream = (MemoryStream)context.Features.Get<IHttpResponseBodyFeature>()!.Stream;
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Runs the whole gate: "pass" when the request reached the upstream, "drop", or the status it was answered with.</summary>
    public static async Task<string> RunAsync(GateMiddleware gate, DefaultHttpContext context)
    {
        var forwarded = false;
        await gate.InvokeAsync(context, _ =>
        {
            forwarded = true;
            return Task.CompletedTask;
        });
        return forwarded ? "pass"
            : context.Features.Get<IGateFeature>()!.State.Aborted ? "drop"
            : context.Response.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The endpoint that lets a request with the cookie <c>session=ok</c> through as alice and answers everyone else 401.</summary>
    public static Func<HttpContext, Task> AllowsSessionOk(string header = "Remote-User", string value = "alice") => context =>
    {
        if (context.Request.Headers.Cookie.ToString().Contains("session=ok", StringComparison.Ordinal))
        {
            context.Response.Headers[header] = value;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };
}

/// <summary>A mutable <see cref="AuthProviderSpec"/>, so a test names only what it changes.</summary>
internal sealed class AuthProviderSpecBuilder
{
    public required string Url { get; set; }

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(10);

    public ForwardAuthCacheScope Scope { get; set; } = ForwardAuthCacheScope.Request;

    public string[] RequestHeaders { get; set; } = [];

    public string[] ResponseHeaders { get; set; } = ["Remote-User", "Remote-Groups"];

    public string? SignInUrl { get; set; }

    public string? SignInReturnParameter { get; set; }

    public string Name { get; set; } = "sso";

    public AuthProviderSpec Build() => new()
    {
        Id = ForwardAuthFixtures.Provider,
        Name = Name,
        Url = Url,
        Timeout = Timeout,
        CacheTtl = CacheTtl,
        CacheScope = Scope,
        RequestHeaders = RequestHeaders,
        ResponseHeaders = ResponseHeaders,
        SignInUrl = SignInUrl,
        SignInReturnParameter = SignInReturnParameter,
    };
}
