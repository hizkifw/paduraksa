using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Tls;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Proxy;

/// <summary>Self-signed server certificates with in-memory keys (never exported), disposed with the instance.</summary>
internal sealed class TestCertificates : IDisposable
{
    private readonly List<X509Certificate2> _owned = [];

    /// <summary>A certificate whose subject CN is <paramref name="commonName"/>, valid for <paramref name="dnsNames"/>.</summary>
    public ServerCertificate Create(string commonName, params string[] dnsNames)
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={commonName}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
        foreach (string dns in dnsNames)
        {
            san.AddDnsName(dns);
        }

        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _owned.Add(certificate);
        return ServerCertificate.Create(certificate, intermediates: null);
    }

    public void Dispose()
    {
        foreach (X509Certificate2 certificate in _owned)
        {
            certificate.Dispose();
        }
    }
}

/// <summary>
/// HTTPS client for the edge tests: connects to loopback by IP, takes the SNI from the <c>Host</c> header (no DNS), and
/// records the subject of the certificate the server presented. Any certificate is accepted; tests check the subject.
/// </summary>
internal static class EdgeClient
{
    public static HttpClient Create(out StrongBox<string?> servedSubject, SslProtocols protocols = SslProtocols.None)
    {
        StrongBox<string?> box = new();
        servedSubject = box;
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions =
            {
                EnabledSslProtocols = protocols,
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    box.Value = (certificate as X509Certificate2)?.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
                    return certificate is not null;
                },
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public static HttpRequestMessage Request(PaduraksaHost host, string scheme, string hostHeader, string pathAndQuery = "/", Version? version = null)
    {
        HttpRequestMessage request = new(HttpMethod.Get, new Uri(TestHost.Endpoint(host, HostRole.Public, scheme), pathAndQuery))
        {
            Version = version ?? HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        };
        request.Headers.Host = hostHeader;
        return request;
    }

    /// <summary>The subject CN served for <paramref name="serverName"/> (null = no SNI) on a fresh connection.</summary>
    public static async Task<string?> ServedSubjectAsync(PaduraksaHost host, string? serverName)
    {
        using HttpClient client = Create(out StrongBox<string?> subject);
        using HttpRequestMessage request = new(HttpMethod.Get, TestHost.Endpoint(host, HostRole.Public, "https"));
        if (serverName is not null)
        {
            request.Headers.Host = serverName;
        }

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return subject.Value;
    }

    /// <summary>Sends <paramref name="bytes"/> on a raw TCP connection to the public HTTP listener and reads until it closes.</summary>
    public static async Task<string> RawHttpAsync(PaduraksaHost host, byte[] bytes)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Public, "http").Port, ct);
        NetworkStream stream = tcp.GetStream();
        await stream.WriteAsync(bytes, ct);
        using StreamReader reader = new(stream, Encoding.ASCII);
        try
        {
            return await reader.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (IOException)
        {
            return string.Empty; // reset by the server
        }
    }

    /// <summary>
    /// Sends <paramref name="prefix"/> in clear on a raw TCP connection to the public HTTPS listener (a PROXY protocol
    /// header), then TLS with SNI <paramref name="serverName"/> and one HTTP/1.1 request; returns the response, or empty
    /// when the server drops the connection.
    /// </summary>
    public static async Task<string> RawHttpsAsync(PaduraksaHost host, byte[] prefix, string serverName, string pathAndQuery = "/")
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Public, "https").Port, ct);
        NetworkStream stream = tcp.GetStream();
        await stream.WriteAsync(prefix, ct);
        try
        {
            await using SslStream tls = new(stream);
            await tls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = serverName,
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
                },
                ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
            await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET {pathAndQuery} HTTP/1.1\r\nHost: {serverName}\r\nConnection: close\r\n\r\n"), ct);
            using StreamReader reader = new(tls, Encoding.ASCII);
            return await reader.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException)
        {
            return string.Empty; // dropped before or during the handshake
        }
    }
}

/// <summary>
/// The upstream of the edge tests: a <see cref="HeaderEchoUpstreamHost"/> that also sets its own <c>Alt-Svc</c>, which
/// must never reach the client.
/// </summary>
internal sealed class EdgeUpstream : IAsyncDisposable
{
    public const string UpstreamAltSvc = "h2=\":9999\"";

    private readonly IHost _host;

    private EdgeUpstream(IHost host, string address)
    {
        _host = host;
        Address = address;
    }

    public string Address { get; }

    public static async Task<EdgeUpstream> StartAsync()
    {
        (IHost host, string address) = await HeaderEchoUpstreamHost.StartAsync(
            static response => response.Headers.AltSvc = UpstreamAltSvc, TestContext.Current.CancellationToken);
        return new EdgeUpstream(host, address);
    }

    /// <summary>The echoed headers of a response body (or of the body of a raw HTTP/1.1 response).</summary>
    public static Dictionary<string, string> Parse(string body) => HeaderEchoUpstreamHost.Parse(body);

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync(CancellationToken.None);
        _host.Dispose();
    }
}

/// <summary>Records the connection's <see cref="ClientInfo"/> and protocol of every public request (before the gate).</summary>
internal sealed class ClientInfoProbe : IStartupFilter
{
    public ConcurrentQueue<(string Protocol, ClientInfo? Info)> Seen { get; } = new();

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            Seen.Enqueue((context.Request.Protocol, context.Features.Get<IClientInfoFeature>()?.ClientInfo));
            return nextMiddleware(context);
        });
        next(app);
    };
}

/// <summary>
/// QUIC needs libmsquic, which dev machines and CI lack: there the H3 tests are skipped. The Docker script
/// (<c>tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh</c>) sets <see cref="RequireVariable"/>, turning a missing QUIC
/// stack into a failure so a green Docker run can't be all skips.
/// </summary>
internal static class QuicSupport
{
    public const string RequireVariable = "PADURAKSA_REQUIRE_QUIC";

    public static void Require()
    {
        if (QuicListener.IsSupported && QuicConnection.IsSupported)
        {
            return;
        }

        if (Environment.GetEnvironmentVariable(RequireVariable) == "1")
        {
            Assert.Fail($"{RequireVariable}=1 but QUIC isn't supported here (libmsquic missing?).");
        }

        Assert.Skip("QUIC isn't supported here (libmsquic missing). Run tests/Paduraksa.Tests.Proxy/Tls/run-quic-tests.sh.");
    }

    /// <summary>A port free for both TCP and UDP on dual-mode <c>[::]</c> (or <c>0.0.0.0</c>), as the public listener binds.</summary>
    public static int FreeTcpAndUdpPort()
    {
        IPAddress bind = Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            using Socket tcp = new(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using Socket udp = new(bind.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            if (bind.Equals(IPAddress.IPv6Any))
            {
                tcp.DualMode = true;
                udp.DualMode = true;
            }

            tcp.Bind(new IPEndPoint(bind, 0));
            int port = ((IPEndPoint)tcp.LocalEndPoint!).Port;
            try
            {
                udp.Bind(new IPEndPoint(bind, port));
                return port;
            }
            catch (SocketException)
            {
                // Taken on UDP: try another.
            }
        }

        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"No port free for both TCP and UDP on {bind}."));
    }
}
