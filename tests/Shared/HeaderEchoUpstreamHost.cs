// Linked into Paduraksa.Tests.Proxy and Paduraksa.Tests.Integration (<Compile Include Link>), so it compiles into each
// test assembly as an internal type of its own.
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
using Paduraksa.Proxy.Net;

namespace Paduraksa.Tests.Shared;

/// <summary>
/// A loopback upstream that answers with the forwarding-related request headers it received, one <c>name: value</c>
/// per line (repeated lines joined with <c>|</c>), plus <c>target</c>: the raw request target.
/// </summary>
internal static class HeaderEchoUpstreamHost
{
    /// <summary>The request headers echoed back (when present), on top of <c>target</c>.</summary>
    public static readonly string[] Echoed =
    [
        "X-Forwarded-For", "X-Real-IP", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Prefix", "X-Forwarded-Port",
        "Forwarded", "X-Paduraksa-Internal", "Cookie", "X-Request-Id", .. CloudflareHeaders.StripWhenUntrusted, "Cf-Access-Authenticated-User-Email", "Cf-Access-Jwt-Assertion", "cf-ipcity",
    ];

    /// <summary>Starts the upstream on an ephemeral loopback port.</summary>
    /// <param name="onResponse">Runs before the body is written, e.g. to set response headers.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    /// <returns>The started host and its base address, with a trailing <c>/</c>.</returns>
    public static async Task<(IHost Host, string Address)> StartAsync(Action<HttpResponse>? onResponse, CancellationToken cancellationToken)
    {
        IHost host = new HostBuilder()
            .ConfigureLogging(static logging => logging.ClearProviders())
            .ConfigureWebHost(web => web
                .UseKestrel(static kestrel => kestrel.Listen(IPAddress.Loopback, 0))
                .Configure(app => app.Run(context =>
                {
                    onResponse?.Invoke(context.Response);
                    return context.Response.WriteAsync(Render(context.Request, context.Features.Get<IHttpRequestFeature>()!.RawTarget));
                })))
            .Build();
        await host.StartAsync(cancellationToken);
        string address = Assert.Single(host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return (host, address + "/");
    }

    /// <summary>
    /// The echoed headers of a response body, or of the body of a raw HTTP/1.1 response. Lines without
    /// <c>": "</c> are skipped.
    /// </summary>
    public static Dictionary<string, string> Parse(string body)
    {
        int head = body.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (head >= 0)
        {
            body = body[(head + 4)..];
        }

        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (string line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon]] = line[(colon + 2)..];
            }
        }

        return headers;
    }

    private static string Render(HttpRequest request, string rawTarget)
    {
        List<string> lines = [$"target: {rawTarget}"];
        foreach (string name in Echoed)
        {
            if (request.Headers.TryGetValue(name, out var values))
            {
                lines.Add($"{name}: {string.Join('|', values.ToArray())}");
            }
        }

        return string.Join('\n', lines);
    }
}
