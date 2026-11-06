using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>
/// A real Kestrel server on the empty builder (as the public app uses), configured by the test. Every response body
/// is the negotiated protocol, so the test can see what ALPN chose.
/// </summary>
internal sealed class TlsTestServer : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TlsTestServer(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    /// <summary>The first bound port (TCP), resolved after start so port 0 works.</summary>
    public int Port { get; }

    public static async Task<TlsTestServer> StartAsync(
        Action<IWebHostBuilder> configureHost,
        Action<KestrelServerOptions> configureKestrel,
        RequestDelegate? handler = null)
    {
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore();
        configureHost(builder.WebHost);
        builder.WebHost.ConfigureKestrel(configureKestrel);
        var app = builder.Build();
        app.Run(handler ?? WriteProtocol);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var addresses = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
        var port = 0;
        foreach (var address in addresses)
        {
            port = new Uri(address).Port;
            break;
        }

        return new TlsTestServer(app, port);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
    }

    private static Task WriteProtocol(HttpContext context) => context.Response.WriteAsync(context.Request.Protocol);
}
