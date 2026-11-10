using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Secrets;

public sealed class AuthenticatingHandlerWireTests : IAsyncLifetime
{
    private const string Token = "wire-token-s3cr3t";

    private readonly VaultHarness _vault = new();

    // Other things on the machine (a dev tool probing new loopback ports over HTTPS, say) can connect to this server too, so nothing
    // is asserted about "all connections": requests carry the ID of their connection, the SNI is recorded per connection, and the
    // assertions look only at the connections that carried a request meant for this test (Host: localhost...).
    private readonly ConcurrentQueue<(string Path, string? Authorization, string Host, string Connection)> _received = new();

    private readonly ConcurrentDictionary<string, string?> _serverNames = new();

    private WebApplication? _server;

    private int _port;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        using var rsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null);
        var expected = certificate.GetCertHashString();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(
            IPAddress.Loopback,
            0,
            l => l.UseHttps(new HttpsConnectionAdapterOptions
            {
                // What the client puts in the TLS handshake (SNI) is recorded: it is the other half of "where did it go".
                ServerCertificateSelector = (connection, name) =>
                {
                    _serverNames[connection?.ConnectionId ?? string.Empty] = name;
                    return certificate;
                },
            })));
        _server = builder.Build();
        _server.Run(async context =>
        {
            _received.Enqueue((context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(), context.Request.Host.Value ?? "", context.Connection.Id));
            if (context.Request.Path == "/redirect")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "https://evil.example/stolen";
                return;
            }

            await context.Response.WriteAsync("ok", Ct);
        });
        await _server.StartAsync(Ct);
        _port = new Uri(_server.Urls.First()).Port;

        _vault.Terminal = options =>
        {
            var handler = TerminalHandlers.CreateSockets(options);
            // Trust exactly the one self-signed certificate of the test server, nothing else.
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate?.GetCertHashString() == expected;
            return handler;
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }

        _vault.Dispose();
    }

    /// <summary>The requests this test sent (to <c>localhost</c>, or to the fronting name), as opposed to anything else that reached the server.</summary>
    private List<(string Path, string? Authorization, string Host, string Connection)> Mine() =>
        [.. _received.Where(r => r.Host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || r.Host.StartsWith("attacker.example", StringComparison.OrdinalIgnoreCase))];

    /// <summary>The SNI of the connection that carried <paramref name="request"/>.</summary>
    private string? ServerNameOf((string Path, string? Authorization, string Host, string Connection) request) => _serverNames[request.Connection];

    private async Task<HttpClient> Client(params string[] hosts)
    {
        var id = await _vault.Get<ISecretStore>().PutAsync(SecretKind.DnsApiToken, Encoding.UTF8.GetBytes(Token), SecretStoreTests.Dns(hosts), Ct);
        return new HttpClient(_vault.Get<ISecretHttpHandlerFactory>().CreateAuthenticatingHandler(
            id, SecretInjection.Bearer(hosts), SecretHttpTransportOptions.Default));
    }

    [Fact]
    public async Task TheCredentialIsOnTheWireAndNotInTheCallersRequestOrResponse()
    {
        using var client = await Client($"localhost:{_port}");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_port}/zones");

        using var response = await client.SendAsync(request, Ct);

        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Ct));
        var only = Assert.Single(Mine());
        Assert.Equal(("/zones", $"Bearer {Token}", $"localhost:{_port}"), (only.Path, only.Authorization, only.Host));
        Assert.Equal("localhost", ServerNameOf(only));
        Assert.Same(request, response.RequestMessage);
        Assert.False(request.Headers.Contains("Authorization"));
        Assert.False(response.RequestMessage!.Headers.Contains("Authorization"));
        Assert.DoesNotContain(Token, response.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARedirectIsReturnedNotFollowedAndItsTargetIsRefused()
    {
        using var client = await Client($"localhost:{_port}");

        using var response = await client.GetAsync($"https://localhost:{_port}/redirect", Ct);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://evil.example/stolen", response.Headers.Location?.ToString());
        Assert.Single(Mine());

        // A provider that follows it by hand goes through the same check, which refuses the other host.
        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync(response.Headers.Location, Ct));
        Assert.Single(Mine());
    }

    [Fact]
    public async Task AFrontingHostHeaderNeverReachesTheWireNotAsSniNorAsHost()
    {
        using var client = await Client($"localhost:{_port}", "attacker.example");
        using var fronted = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_port}/zones");
        fronted.Headers.Host = "attacker.example";

        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.SendAsync(fronted, Ct));

        // Nothing was sent: no TLS handshake for the fronting name, no request for it.
        Assert.DoesNotContain(_serverNames.Values, name => name == "attacker.example");
        Assert.Empty(Mine());

        // The honest spelling of the same destination goes out with the URL's own SNI and Host.
        using var honest = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_port}/zones");
        honest.Headers.Host = $"LOCALHOST:{_port}";
        using var response = await client.SendAsync(honest, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(Mine());
        Assert.Equal("localhost", ServerNameOf(sent));
        Assert.Equal(($"/zones", $"Bearer {Token}", $"localhost:{_port}"), (sent.Path, sent.Authorization, sent.Host));
    }

    [Fact]
    public async Task PlainHttpToTheSameServerIsRefusedAndNothingArrives()
    {
        using var client = await Client($"localhost:{_port}");

        await Assert.ThrowsAsync<SecretTransportRefusedException>(async () => await client.GetAsync($"http://localhost:{_port}/x", Ct));

        Assert.Empty(Mine());
    }
}
