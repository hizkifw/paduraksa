using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Secrets;

namespace Paduraksa.Tests.Vault.Secrets;

/// <summary>
/// The authenticating handler over the vault's real <see cref="SocketsHttpHandler"/> against a local Kestrel HTTPS server:
/// the credential really is on the wire, a redirect really is not followed, and a request to the redirect's target is
/// refused. The one thing the test changes is certificate validation (the server's certificate is self-signed), through the
/// vault's internal transport seam.
/// </summary>
public sealed class AuthenticatingHandlerWireTests : IAsyncLifetime
{
    private const string Token = "wire-token-s3cr3t";

    private readonly VaultHarness _vault = new();

    private readonly ConcurrentQueue<(string Path, string? Authorization, string Host)> _received = new();

    private readonly ConcurrentQueue<string?> _serverNames = new();

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
                ServerCertificateSelector = (_, name) =>
                {
                    _serverNames.Enqueue(name);
                    return certificate;
                },
            })));
        _server = builder.Build();
        _server.Run(async context =>
        {
            _received.Enqueue((context.Request.Path.Value!, context.Request.Headers.Authorization.ToString(), context.Request.Host.Value ?? ""));
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
        Assert.Equal(("/zones", $"Bearer {Token}", $"localhost:{_port}"), Assert.Single(_received));
        Assert.Equal("localhost", Assert.Single(_serverNames));
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
        Assert.Single(_received);

        // A provider that follows it by hand goes through the same check, which refuses the other host.
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetAsync(response.Headers.Location, Ct));
        Assert.Single(_received);
    }

    [Fact]
    public async Task AFrontingHostHeaderNeverReachesTheWireNotAsSniNorAsHost()
    {
        using var client = await Client($"localhost:{_port}", "attacker.example");
        using var fronted = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_port}/zones");
        fronted.Headers.Host = "attacker.example";

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.SendAsync(fronted, Ct));

        // Nothing was sent: no TLS handshake for any name, no request.
        Assert.Empty(_serverNames);
        Assert.Empty(_received);

        // The honest spelling of the same destination goes out with the URL's own SNI and Host.
        using var honest = new HttpRequestMessage(HttpMethod.Get, $"https://localhost:{_port}/zones");
        honest.Headers.Host = $"LOCALHOST:{_port}";
        using var response = await client.SendAsync(honest, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("localhost", Assert.Single(_serverNames));
        Assert.Equal(($"/zones", $"Bearer {Token}", $"localhost:{_port}"), Assert.Single(_received));
    }

    [Fact]
    public async Task PlainHttpToTheSameServerIsRefusedAndNothingArrives()
    {
        using var client = await Client($"localhost:{_port}");

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await client.GetAsync($"http://localhost:{_port}/x", Ct));

        Assert.Empty(_received);
    }
}
