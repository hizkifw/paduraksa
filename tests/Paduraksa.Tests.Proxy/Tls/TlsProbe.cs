using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>What a client saw on one fresh connection.</summary>
/// <param name="ServedSubject">Subject CN of the leaf the server presented.</param>
/// <param name="Received">Thumbprints the server actually sent (leaf + intermediates), as seen in the chain's ExtraStore.</param>
/// <param name="ChainTrusted">Whether the leaf chains to the test root using only what the server sent.</param>
/// <param name="Version">The negotiated HTTP version.</param>
/// <param name="Body">The response body (the test server writes the request protocol).</param>
/// <param name="AltSvc">The <c>Alt-Svc</c> response header, if any.</param>
internal sealed record TlsProbeResult(
    string ServedSubject,
    IReadOnlyList<string> Received,
    bool ChainTrusted,
    Version Version,
    string Body,
    string? AltSvc);

/// <summary>
/// HTTP client for the Kestrel TLS tests. Always connects to loopback by IP; the SNI comes from the <c>Host</c> header
/// (SocketsHttpHandler uses it as the TLS target host), so no DNS is involved on TCP or QUIC. Without a server name
/// the URL is an IP literal, which sends no SNI at all.
/// </summary>
internal static class TlsProbe
{
    public static async Task<TlsProbeResult> GetAsync(
        TestPki pki,
        int port,
        string? serverName,
        Version version,
        HttpVersionPolicy versionPolicy = HttpVersionPolicy.RequestVersionExact,
        Action<SslClientAuthenticationOptions>? configureSsl = null,
        IPAddress? address = null)
    {
        using var handler = CreateHandler(pki, out var observed);
        configureSsl?.Invoke(handler.SslOptions);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await SendAsync(client, port, serverName, version, versionPolicy, address);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        // SocketsHttpHandler parses Alt-Svc itself (H3 discovery) and drops the default "ma=86400"; use RawHttp11Async
        // to see the exact bytes.
        var altSvc = response.Headers.TryGetValues("Alt-Svc", out var values) ? string.Join(", ", values) : null;
        var (subject, received, trusted) = observed.Value ?? throw new InvalidOperationException("The certificate callback never ran.");
        return new TlsProbeResult(subject, received, trusted, response.Version, body, altSvc);
    }

    /// <summary>A handler that records the last server certificate it saw and validates it against the test root.</summary>
    public static SocketsHttpHandler CreateHandler(TestPki pki, out StrongBox<(string Subject, string[] Received, bool Trusted)?> observed)
    {
        var box = new StrongBox<(string, string[], bool)?>();
        observed = box;
        return new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                {
                    if (certificate is not X509Certificate2 leaf || chain is null)
                    {
                        return false;
                    }

                    var received = new string[chain.ChainPolicy.ExtraStore.Count];
                    for (var i = 0; i < received.Length; i++)
                    {
                        received[i] = chain.ChainPolicy.ExtraStore[i].Thumbprint;
                    }

                    // Trust only the test root and use only what the server sent: this fails unless the
                    // intermediate came over the wire. The host-name check is the test's job, not this callback's.
                    using var verify = new X509Chain();
                    verify.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    verify.ChainPolicy.CustomTrustStore.Add(pki.Root);
                    verify.ChainPolicy.ExtraStore.AddRange(chain.ChainPolicy.ExtraStore);
                    verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    verify.ChainPolicy.DisableCertificateDownloads = true;
                    var trusted = verify.Build(leaf);

                    box.Value = (leaf.GetNameInfo(X509NameType.SimpleName, forIssuer: false), received, trusted);
                    return trusted;
                },
            },
        };
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        int port,
        string? serverName,
        Version version,
        HttpVersionPolicy versionPolicy = HttpVersionPolicy.RequestVersionExact,
        IPAddress? address = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        var ip = address ?? IPAddress.Loopback;
        using var request = new HttpRequestMessage(HttpMethod.Get, new UriBuilder("https", ip.ToString(), port, "/").Uri)
        {
            Version = version,
            VersionPolicy = versionPolicy,
        };
        if (serverName is not null)
        {
            request.Headers.Host = serverName;
        }

        return await client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    /// <summary>One HTTP/1.1 GET over a raw TLS stream; returns the response head exactly as sent.</summary>
    public static async Task<string> RawHttp11HeadAsync(int port, string serverName)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(
            new SslClientAuthenticationOptions
            {
                TargetHost = serverName,
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = static (_, certificate, _, _) => certificate is not null,
            },
            TestContext.Current.CancellationToken);
        await tls.WriteAsync(Encoding.ASCII.GetBytes($"GET / HTTP/1.1\r\nHost: {serverName}\r\nConnection: close\r\n\r\n"), TestContext.Current.CancellationToken);
        using var reader = new StreamReader(tls, Encoding.ASCII);
        var response = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        var end = response.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        return end < 0 ? response : response[..end];
    }
}
