using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Paduraksa.Acme;

namespace Paduraksa.Tests.Acme;

/// <summary>
/// <see cref="AcmeHttp.CreateHandler(IReadOnlyCollection{X509Certificate2})"/> over a real TLS handshake with a loopback server: what a private CA's
/// roots make acceptable, and everything they must not.
/// </summary>
public sealed class AcmeTrustedRootsTests
{
    private static readonly Oid ServerAuth = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuth = new("1.3.6.1.5.5.7.3.2");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Ca : IDisposable
    {
        private Ca(X509Certificate2 certificate, ECDsa key)
        {
            Certificate = certificate;
            Key = key;
        }

        public X509Certificate2 Certificate { get; }

        public ECDsa Key { get; }

        public static Ca Root(string name)
        {
            var key = ECDsa.Create();
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            return new Ca(request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(300)), key);
        }

        public Ca Intermediate(string name)
        {
            var key = ECDsa.Create();
            var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
            var certificate = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(Key), DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(200), RandomNumberGenerator.GetBytes(8));
            return new Ca(certificate, key);
        }

        /// <summary>A server certificate for <c>127.0.0.1</c> (or <paramref name="dnsName"/> only), with its private key for the test server.</summary>
        public X509Certificate2 Leaf(Oid usage, bool expired = false, string? dnsName = null)
        {
            using var leafKey = ECDsa.Create();
            var request = new CertificateRequest("CN=acme.test", leafKey, HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder();
            if (dnsName is null)
            {
                names.AddIpAddress(IPAddress.Loopback);
            }
            else
            {
                names.AddDnsName(dnsName);
            }

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([usage], false));
            var from = expired ? DateTimeOffset.UtcNow.AddDays(-10) : DateTimeOffset.UtcNow.AddDays(-1);
            var to = expired ? DateTimeOffset.UtcNow.AddDays(-1) : DateTimeOffset.UtcNow.AddDays(30);
            using var signed = request.Create(Certificate.SubjectName, X509SignatureGenerator.CreateForECDsa(Key), from, to, RandomNumberGenerator.GetBytes(8));
            return signed.CopyWithPrivateKey(leafKey);
        }

        public void Dispose()
        {
            Certificate.Dispose();
            Key.Dispose();
        }
    }

    /// <summary>A TLS server on a loopback port that answers one HTTP request per connection with <c>200 {}</c>.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly SslStreamCertificateContext _context;

        public TlsServer(X509Certificate2 leaf, params X509Certificate2[] sent)
        {
            _context = SslStreamCertificateContext.Create(leaf, [.. sent], offline: true);
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public Uri Url => new($"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/");

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    await using var ssl = new SslStream(client.GetStream(), false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = _context }, _stop.Token);
                    var buffer = new byte[4096];
                    _ = await ssl.ReadAsync(buffer, _stop.Token);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}"), _stop.Token);
                    await ssl.FlushAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    // The client refused the certificate (or went away): that is what some of these tests are about.
                }
            }
        }
    }

    /// <summary>Whether a client with <paramref name="roots"/> accepts the server: the response status, or the exception the handshake ended in.</summary>
    private static async Task<(HttpStatusCode? Status, Exception? Failure)> TryAsync(TlsServer server, params X509Certificate2[] roots)
    {
        using var http = new HttpClient(AcmeHttp.CreateHandler(roots)) { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var response = await http.GetAsync(server.Url, Token);
            return (response.StatusCode, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, ex);
        }
    }

    [Fact]
    public async Task AChainThatEndsAtTheRootThroughAnIntermediateTheServerSendsIsAccepted()
    {
        using var root = Ca.Root("Test Root");
        using var intermediate = root.Intermediate("Test Intermediate");
        using var leaf = intermediate.Leaf(ServerAuth);
        using var server = new TlsServer(leaf, intermediate.Certificate);

        var (status, failure) = await TryAsync(server, root.Certificate);

        Assert.Null(failure);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task ANameThatIsNotTheServersIsRefusedEvenWithTheRightRoot()
    {
        using var root = Ca.Root("Test Root");
        using var leaf = root.Leaf(ServerAuth, dnsName: "other.test");
        using var server = new TlsServer(leaf);

        var (status, failure) = await TryAsync(server, root.Certificate);

        Assert.Null(status);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task AnExpiredLeafIsRefused()
    {
        using var root = Ca.Root("Test Root");
        using var leaf = root.Leaf(ServerAuth, expired: true);
        using var server = new TlsServer(leaf);

        var (status, failure) = await TryAsync(server, root.Certificate);

        Assert.Null(status);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task AChainToAnotherRootIsRefused()
    {
        using var root = Ca.Root("Test Root");
        using var other = Ca.Root("Another Root");
        using var leaf = root.Leaf(ServerAuth);
        using var server = new TlsServer(leaf);

        var (status, failure) = await TryAsync(server, other.Certificate);

        Assert.Null(status);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task ALeafThatAllowsOnlyClientAuthenticationIsRefused()
    {
        using var root = Ca.Root("Test Root");
        using var leaf = root.Leaf(ClientAuth);
        using var server = new TlsServer(leaf);

        var (status, failure) = await TryAsync(server, root.Certificate);

        Assert.Null(status);
        Assert.NotNull(failure);
    }

    [Fact]
    public async Task AnIntermediateTheServerDoesNotSendIsNotFoundAndTheChainIsRefused()
    {
        using var root = Ca.Root("Test Root");
        using var intermediate = root.Intermediate("Test Intermediate");
        using var leaf = intermediate.Leaf(ServerAuth);
        using var server = new TlsServer(leaf);

        var (status, failure) = await TryAsync(server, root.Certificate);

        Assert.Null(status);
        Assert.NotNull(failure);
    }

    [Fact]
    public void AnIntermediateOrALeafIsNotARootAndIsRefusedAsOne()
    {
        using var root = Ca.Root("Test Root");
        using var intermediate = root.Intermediate("Test Intermediate");
        using var leaf = intermediate.Leaf(ServerAuth);

        Assert.Throws<ArgumentException>(() => AcmeHttp.CreateHandler([intermediate.Certificate]));
        Assert.Throws<ArgumentException>(() => AcmeHttp.CreateHandler([leaf]));
        Assert.True(AcmeHttp.IsSelfSignedCa(root.Certificate));
        Assert.False(AcmeHttp.IsSelfSignedCa(intermediate.Certificate));
        Assert.False(AcmeHttp.IsSelfSignedCa(leaf));
    }
}
