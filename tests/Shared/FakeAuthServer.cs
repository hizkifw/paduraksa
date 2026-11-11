// Linked into Paduraksa.Tests.Proxy, Paduraksa.Tests.Integration and Paduraksa.Tests.Dashboard (<Compile Include Link>), so it
// compiles into each test assembly as an internal type of its own.
using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    private readonly IHost _host;
    private readonly ConcurrentQueue<RecordedAuthCall> _calls = new();

    private FakeAuthServer(IHost host, string address)
    {
        _host = host;
        Address = address;
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
        var host = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, 0);
                    kestrel.AddServerHeader = false;

                    // A provider writes a display name as UTF-8, as authentik does.
                    kestrel.ResponseHeaderEncodingSelector = static _ => System.Text.Encoding.UTF8;
                })
                .Configure(app => app.Run(context =>
                {
                    Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                    foreach (var header in context.Request.Headers)
                    {
                        headers[header.Key] = string.Join(", ", header.Value.ToArray());
                    }

                    server!._calls.Enqueue(new RecordedAuthCall(context.Request.Method, context.Features.Get<IHttpRequestFeature>()!.RawTarget, headers));
                    return server.Handler(context);
                })))
            .Build();
        await host.StartAsync(cancellationToken);
        var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        server = new FakeAuthServer(host, address);
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}
