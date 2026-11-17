// Linked into Paduraksa.Tests.Proxy, Paduraksa.Tests.Integration and Paduraksa.Tests.Dashboard (<Compile Include Link>), so it
// compiles into each test assembly as an internal type of its own.
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Tests.Shared;

/// <summary>What the fake auth endpoint was asked: the method, the raw target and every header, in the case they arrived in.</summary>
internal sealed record RecordedAuthCall(string Method, string Target, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A forward-auth endpoint on a loopback port: it records every call it gets and answers with whatever <see cref="Handler"/> says, which
/// a test swaps while it runs. The default answer is the one an unauthenticated user gets from authentik's outpost: 401.
/// </summary>
internal sealed class FakeAuthServer : IAsyncDisposable
{
    private readonly LoopbackUpstream _upstream;
    private readonly ConcurrentQueue<RecordedAuthCall> _calls = new();

    private FakeAuthServer(LoopbackUpstream upstream)
    {
        _upstream = upstream;
        Address = upstream.Address.TrimEnd('/');
    }

    /// <summary>The server's base address with no trailing slash.</summary>
    public string Address { get; }

    /// <summary>Answers a call. Swappable while the server runs.</summary>
    public Func<HttpContext, Task> Handler { get; set; } = static context =>
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    };

    public IReadOnlyList<RecordedAuthCall> Calls => [.. _calls];

    public int CallCount => _calls.Count;

    public static async Task<FakeAuthServer> StartAsync(CancellationToken cancellationToken = default)
    {
        FakeAuthServer? server = null;
        var upstream = await LoopbackUpstream.StartAsync(
            context =>
            {
                // The proxy always sends X-Forwarded-Proto, to an auth endpoint and to an upstream alike; a request without it is
                // a stranger (a dev tool probing new localhost ports was seen doing GET /) and must not count as a call.
                if (!context.Request.Headers.ContainsKey("X-Forwarded-Proto"))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (var header in context.Request.Headers)
                {
                    headers[header.Key] = string.Join(", ", header.Value.ToArray());
                }

                server!._calls.Enqueue(new RecordedAuthCall(context.Request.Method, context.Features.Get<IHttpRequestFeature>()!.RawTarget, headers));
                return server.Handler(context);
            },
            kestrel: static kestrel =>
            {
                kestrel.AddServerHeader = false;

                // A provider writes a display name as UTF-8, as authentik does.
                kestrel.ResponseHeaderEncodingSelector = static _ => System.Text.Encoding.UTF8;
            },
            cancellationToken: cancellationToken);
        server = new FakeAuthServer(upstream);
        return server;
    }

    public ValueTask DisposeAsync() => _upstream.DisposeAsync();
}
