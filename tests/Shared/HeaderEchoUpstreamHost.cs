// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), so it compiles into each
// test assembly as an internal type of its own.
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// A loopback upstream that answers with the forwarding-related request headers it received, one <c>name: value</c>
/// per line (repeated lines joined with <c>|</c>), plus <c>target</c>: the raw request target, and on HTTPS <c>sni</c>: the
/// server name the client's handshake asked for (absent when it sent none).
/// </summary>
internal static class HeaderEchoUpstreamHost
{
    /// <summary>The request headers echoed back (when present), on top of <c>target</c>.</summary>
    public static readonly string[] Echoed =
    [
        "X-Forwarded-For", "X-Real-IP", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "X-Forwarded-Port",
        "Host", "Forwarded", "X-Paduraksa-Internal", "Cookie", "Authorization", "X-Request-Id", "traceparent", .. CloudflareHeaders.StripWhenUntrusted, "Cf-Access-Authenticated-User-Email", "Cf-Access-Jwt-Assertion", "cf-ipcity",
        "Remote-User", "Remote-Groups", "X-authentik-username", "X-authentik-uid", "X-Auth-Request-User",
        "Remote_User", "Remote_Groups", "X_authentik_username", "X-Other",
    ];

    /// <summary>Starts the upstream on an ephemeral loopback port.</summary>
    /// <param name="onResponse">Runs before the body is written, e.g. to set response headers.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <param name="certificate">Serves HTTPS with it; null serves plain HTTP.</param>
    /// <param name="onHandshake">Called for every TLS handshake with the server name it asked for (null when it sent none).</param>
    /// <returns>The started host and its base address, with a trailing <c>/</c>.</returns>
    public static async Task<(IHost Host, string Address)> StartAsync(
        Action<HttpResponse>? onResponse, CancellationToken cancellationToken, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate = null,
        Action<string?>? onHandshake = null)
    {
        var host = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(kestrel =>
                {
                    kestrel.Listen(IPAddress.Loopback, 0, listen =>
                    {
                        if (certificate is not null)
                        {
                            listen.UseHttps(new HttpsConnectionAdapterOptions
                            {
                                ServerCertificateSelector = (connection, name) =>
                                {
                                    onHandshake?.Invoke(name);
                                    if (!string.IsNullOrEmpty(name) && connection is not null)
                                    {
                                        connection.Items[ServerNameItem] = name;
                                    }

                                    return certificate;
                                },
                            });
                        }
                    });
                    kestrel.AddServerHeader = false;
                })
                .Configure(app => app.Run(context =>
                {
                    onResponse?.Invoke(context.Response);
                    var serverName = context.Features.Get<IConnectionItemsFeature>()?.Items.TryGetValue(ServerNameItem, out var name) == true ? name as string : null;
                    return context.Response.WriteAsync(Render(context.Request, context.Features.Get<IHttpRequestFeature>()!.RawTarget, serverName));
                })))
            .Build();
        await host.StartAsync(cancellationToken);
        var address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return (host, address + "/");
    }

    /// <summary>
    /// The echoed headers of a response body, or of the body of a raw HTTP/1.1 response. Lines without
    /// <c>": "</c> are skipped.
    /// </summary>
    public static Dictionary<string, string> Parse(string body)
    {
        var head = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (head >= 0)
        {
            body = body[(head + 4)..];
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon]] = line[(colon + 2)..];
            }
        }

        return headers;
    }

    private const string ServerNameItem = "paduraksa.test.sni";

    private static string Render(HttpRequest request, string rawTarget, string? serverName)
    {
        List<string> lines = [$"target: {rawTarget}"];
        if (serverName is not null)
        {
            lines.Add($"sni: {serverName}");
        }

        foreach (var name in Echoed)
        {
            if (request.Headers.TryGetValue(name, out var values))
            {
                lines.Add($"{name}: {string.Join('|', values.ToArray())}");
            }
        }

        return string.Join('\n', lines);
    }
}
