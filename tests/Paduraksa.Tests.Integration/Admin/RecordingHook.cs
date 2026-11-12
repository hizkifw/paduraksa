using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>A loopback server that records what is POSTed to it (and ignores the Node prober's <c>GET /</c> that this machine sends to new ports).</summary>
internal sealed class RecordingHook : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IHost _host;
    private readonly ConcurrentQueue<(string Path, string Body, string? Signature)> _received = new();

    private RecordingHook(IHost host, string url)
    {
        _host = host;
        Url = url;
    }

    public string Url { get; }

    public IReadOnlyList<(string Path, string Body, string? Signature)> Received => [.. _received];

    public static async Task<RecordingHook> StartAsync()
    {
        RecordingHook? hook = null;
        var host = new HostBuilder()
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(async context =>
                {
                    if (context.Request.Method != "POST")
                    {
                        return;
                    }

                    using StreamReader reader = new(context.Request.Body, Encoding.UTF8);
                    hook!._received.Enqueue((context.Request.Path.Value!, await reader.ReadToEndAsync(), context.Request.Headers["X-Paduraksa-Signature"].ToString() is { Length: > 0 } sig ? sig : null));
                })))
            .Build();
        await host.StartAsync(Ct);
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
        hook = new RecordingHook(host, address);
        return hook;
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}
