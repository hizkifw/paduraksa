// Linked into the test projects that start their own upstream (<Compile Include Link>), so it compiles into each assembly as an
// internal type of its own.
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Tests.Shared;

/// <summary>A Kestrel upstream on an ephemeral loopback port, answering every request with one handler.</summary>
internal sealed class LoopbackUpstream : IAsyncDisposable
{
    private LoopbackUpstream(IHost host, string address)
    {
        Host = host;
        Address = address;
    }

    public IHost Host { get; }

    /// <summary>The base address, with a trailing <c>/</c>.</summary>
    public string Address { get; }

    public int Port => new Uri(Address).Port;

    /// <summary><paramref name="listen"/> configures the one listener (protocols, TLS), <paramref name="kestrel"/> the server (limits, <c>AddServerHeader</c>).</summary>
    public static async Task<LoopbackUpstream> StartAsync(
        RequestDelegate handler,
        Action<ListenOptions>? listen = null,
        Action<KestrelServerOptions>? kestrel = null,
        bool webSockets = false,
        CancellationToken cancellationToken = default)
    {
        var host = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(options =>
                {
                    options.Listen(IPAddress.Loopback, 0, listen ?? (static _ => { }));
                    kestrel?.Invoke(options);
                })
                .Configure(app =>
                {
                    if (webSockets)
                    {
                        app.UseWebSockets();
                    }

                    app.Run(handler);
                }))
            .Build();
        await host.StartAsync(cancellationToken);
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new LoopbackUpstream(host, address.TrimEnd('/') + "/");
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync(CancellationToken.None);
        Host.Dispose();
    }
}
