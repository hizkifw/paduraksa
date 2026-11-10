using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Microsoft.Extensions.Hosting;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Config;
using Paduraksa.Tests.Proxy.Testing;
using Paduraksa.Tests.Shared;

using static Paduraksa.Tests.Proxy.Testing.TestSnapshots;

namespace Paduraksa.Tests.Proxy.EndToEnd;

/// <summary>An HTTPS upstream with a self-signed certificate for another name, the homelab case, under each trust mode.</summary>
public sealed class UpstreamTlsTrustTests : IAsyncLifetime
{
    private X509Certificate2? _certificate;
    private IHost? _upstream;
    private string _address = string.Empty;

    private string Fingerprint => Convert.ToHexStringLower(_certificate!.GetCertHash(HashAlgorithmName.SHA256));

    public async ValueTask InitializeAsync()
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback upstream; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new("CN=nas.lan", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        _certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning restore RS0030
        (_upstream, _address) = await HeaderEchoUpstreamHost.StartAsync(onResponse: null, TestContext.Current.CancellationToken, _certificate);
        Assert.StartsWith("https://", _address, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_upstream is not null)
        {
            await _upstream.StopAsync(TestContext.Current.CancellationToken);
            _upstream.Dispose();
        }

        _certificate?.Dispose();
    }

    [Fact]
    public async Task SystemTrustRefusesTheSelfSignedCertificate() =>
        Assert.Equal(HttpStatusCode.BadGateway, (await SendAsync(UpstreamTlsTrust.System, [])).Status);

    [Fact]
    public async Task PinnedTrustAcceptsTheCertificateItNames()
    {
        var (status, seen) = await SendAsync(UpstreamTlsTrust.Pinned, [Fingerprint]);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("app.test", seen["Host"]); // the name doesn't matter to a pin: the certificate is for nas.lan
    }

    [Fact]
    public async Task PinnedTrustRefusesAnyOtherCertificate() =>
        Assert.Equal(HttpStatusCode.BadGateway, (await SendAsync(UpstreamTlsTrust.Pinned, [new string('0', 64)])).Status);

    [Fact]
    public async Task AnyTrustAcceptsIt() =>
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(UpstreamTlsTrust.Any, [])).Status);

    private async Task<(HttpStatusCode Status, Dictionary<string, string> Seen)> SendAsync(UpstreamTlsTrust trust, string[] pins)
    {
        ProxyConfigModel model = new()
        {
            Upstreams = [Upstream("nas", _address) with { TlsTrust = trust, PinnedSha256 = pins }],
            Hosts = [ProxyHost("app", "app.test", Route("app-root", "nas"))],
        };
        await using var server = await ProxyTestServer.StartAsync(model);
        using var client = ProxyTestServer.CreateClient();
        using var request = server.Request("app.test", "/");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return (response.StatusCode, response.IsSuccessStatusCode ? HeaderEchoUpstreamHost.Parse(body) : []);
    }
}
