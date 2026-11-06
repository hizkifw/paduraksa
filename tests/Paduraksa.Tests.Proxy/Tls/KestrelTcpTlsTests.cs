using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Tests.Proxy.Tls;

/// <summary>Per-SNI selection, chain, rejection and hot swap on a real Kestrel TCP listener (H1 and H2).</summary>
public sealed class KestrelTcpTlsTests : IDisposable
{
    private readonly TestPki _pki = TestPki.Create();
    private readonly ServerCertificate _a;
    private readonly ServerCertificate _b;
    private readonly ServerCertificate _wild;
    private readonly ServerCertificate _default;

    public KestrelTcpTlsTests()
    {
        _a = _pki.Leaf("a.test v1", "a.test");
        _b = _pki.Leaf("b.test v1", "b.test");
        _wild = _pki.Leaf("*.wild.test v1", "*.wild.test");
        _default = _pki.Leaf("default.test", "default.test");
    }

    public static TheoryData<string> TcpVersions => ["1.1", "2.0"];

    public void Dispose() => _pki.Dispose();

    private CertificateTable Table(bool withDefault = true)
    {
        var builder = new CertificateTableBuilder()
            .Add(["a.test"], _a, TlsPolicy.Default)
            .Add(["b.test"], _b, TlsPolicy.Default)
            .Add(["*.wild.test"], _wild, TlsPolicy.Default);
        if (withDefault)
        {
            builder.SetDefault(_default, TlsPolicy.Default);
        }

        return builder.Build();
    }

    internal static Task<TlsTestServer> StartTcpAsync(TlsHandshakeSelector selector, RequestDelegate? handler = null) =>
        TlsTestServer.StartAsync(
            static web => web.UseKestrelHttpsConfiguration(),
            kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(selector.CreateTcpCallbackOptions());
            }),
            handler);

    [Theory]
    [MemberData(nameof(TcpVersions))]
    public async Task ServesEachNameItsCertificateWithTheIntermediate(string versionText)
    {
        var version = Version.Parse(versionText);
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table()));
        await using var server = await StartTcpAsync(selector);

        (string? Sni, string Expected)[] cases =
        [
            ("a.test", "a.test v1"),
            ("b.test", "b.test v1"),
            ("x.wild.test", "*.wild.test v1"),
            ("unknown.test", "default.test"),
            (null, "default.test"), // IP-literal URL: no SNI at all
        ];
        foreach (var (sni, expected) in cases)
        {
            var result = await TlsProbe.GetAsync(_pki, server.Port, sni, version);

            Assert.Equal(expected, result.ServedSubject);
            Assert.Equal(version, result.Version);
            Assert.Equal(version.Major == 2 ? "HTTP/2" : "HTTP/1.1", result.Body);
            // The server sent leaf + intermediate, and that is enough to reach the (client-trusted) root.
            Assert.Equal(2, result.Received.Count);
            Assert.Contains(_pki.Intermediate.Thumbprint, result.Received);
            Assert.True(result.ChainTrusted);
        }

        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);
    }

    [Theory]
    [MemberData(nameof(TcpVersions))]
    public async Task RejectsUnknownNamesWithoutDefaultAndStaysHealthy(string versionText)
    {
        var version = Version.Parse(versionText);
        var selector = new TlsHandshakeSelector(new CertificateTableSource(Table(withDefault: false)));
        await using var server = await StartTcpAsync(selector);

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "unknown.test", version));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "a.b.wild.test", version));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, null, version));

        Assert.Equal(3, selector.RejectedUnknownServerNameTcp);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", version)).ServedSubject);
    }

    [Fact]
    public async Task HotSwapAffectsNewConnectionsOnly()
    {
        var source = new CertificateTableSource(Table());
        var selector = new TlsHandshakeSelector(source);
        await using var server = await StartTcpAsync(selector);

        // An established H2 connection, opened before the swap.
        using var handler = TlsProbe.CreateHandler(_pki, out var observed);
        using var client = new HttpClient(handler);
        using (var before = await TlsProbe.SendAsync(client, server.Port, "a.test", HttpVersion.Version20))
        {
            before.EnsureSuccessStatusCode();
        }

        Assert.Equal("a.test v1", observed.Value!.Value.Subject);

        var aV2 = _pki.Leaf("a.test v2", "a.test");
        var wildV2 = _pki.Leaf("*.wild.test v2", "*.wild.test");
        source.Swap(new CertificateTableBuilder()
            .Add(["a.test"], aV2, TlsPolicy.Default)
            .Add(["*.wild.test"], wildV2, TlsPolicy.Default)
            .SetDefault(_default, TlsPolicy.Default)
            .Build()); // b.test removed → default

        Assert.Equal("a.test v2", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20)).ServedSubject);
        Assert.Equal("*.wild.test v2", (await TlsProbe.GetAsync(_pki, server.Port, "x.wild.test", HttpVersion.Version11)).ServedSubject);
        Assert.Equal("default.test", (await TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version20)).ServedSubject);

        // The old connection keeps working without a new handshake (the callback would have recorded v2).
        observed.Value = null;
        using (var after = await TlsProbe.SendAsync(client, server.Port, "a.test", HttpVersion.Version20))
        {
            after.EnsureSuccessStatusCode();
        }

        Assert.Null(observed.Value);
    }

    [Theory]
    [MemberData(nameof(TcpVersions))]
    public async Task IdnNamesMatchTheSniSslStreamDecodes(string versionText)
    {
        // The client sends punycode; SslStream hands the callback "bücher.test" / "x.café.test".
        var version = Version.Parse(versionText);
        var table = new CertificateTableBuilder()
            .Add(["xn--bcher-kva.test"], _pki.Leaf("idn exact", "xn--bcher-kva.test"), TlsPolicy.Default)
            .Add(["*.xn--caf-dma.test"], _pki.Leaf("idn wildcard", "*.xn--caf-dma.test"), TlsPolicy.Default)
            .Build(); // no default: a miss is a rejection, not a silent fallback
        var selector = new TlsHandshakeSelector(new CertificateTableSource(table));
        await using var server = await StartTcpAsync(selector);

        Assert.Equal("idn exact", (await TlsProbe.GetAsync(_pki, server.Port, "xn--bcher-kva.test", version)).ServedSubject);
        Assert.Equal("idn wildcard", (await TlsProbe.GetAsync(_pki, server.Port, "x.xn--caf-dma.test", version)).ServedSubject);
        Assert.Equal(0, selector.RejectedUnknownServerNameTcp);
    }

    [Fact]
    public async Task ClientCertificateChainIsBuiltWithoutDownloads()
    {
        // An unauthenticated client controls the AIA URL of the certificate it presents. Building its chain must not
        // fetch that URL (SSRF), even though the issuer is missing.
        await using var aia = new HitCounter();
        var client = _pki.ClientCertificateWithAia("aia-client", aia.Url);
        var policy = new TlsPolicy
        {
            ClientCertificate = new ClientCertificatePolicy { Validator = static (_, certificate, _, _) => certificate is not null },
        };
        var table = new CertificateTableBuilder().Add(["locked.test"], _a, policy).Build();
        await using var server = await StartTcpAsync(
            new TlsHandshakeSelector(new CertificateTableSource(table)),
            static context => context.Response.WriteAsync(context.Connection.ClientCertificate?.Thumbprint ?? "none"));

        var result = await TlsProbe.GetAsync(
            _pki,
            server.Port,
            "locked.test",
            HttpVersion.Version20,
            // Offline context on the client too: otherwise the *client* chases the AIA while preparing to send its cert.
            configureSsl: options => options.ClientCertificateContext = SslStreamCertificateContext.Create(client, null, offline: true));

        Assert.Equal(client.Thumbprint, result.Body); // the server did build and validate a chain for it
        Assert.Equal(0, aia.Hits);
    }

    [Fact]
    public async Task Http11OnlyHostNegotiatesHttp11()
    {
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _a, new TlsPolicy { TcpAlpn = TcpAlpn.Http11Only })
            .Add(["b.test"], _b, TlsPolicy.Default)
            .Build();
        await using var server = await StartTcpAsync(new TlsHandshakeSelector(new CertificateTableSource(table)));

        var a = await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower);
        var b = await TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version20, HttpVersionPolicy.RequestVersionOrLower);

        Assert.Equal(HttpVersion.Version11, a.Version);
        Assert.Equal(HttpVersion.Version20, b.Version);
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version20));
    }

    [Fact]
    public async Task Tls13MinimumRejectsTls12Clients()
    {
        var table = new CertificateTableBuilder()
            .Add(["a.test"], _a, new TlsPolicy { MinimumVersion = TlsMinimumVersion.Tls13 })
            .Add(["b.test"], _b, TlsPolicy.Default)
            .Build();
        await using var server = await StartTcpAsync(new TlsHandshakeSelector(new CertificateTableSource(table)));
        static void Tls12Only(SslClientAuthenticationOptions options) => options.EnabledSslProtocols = SslProtocols.Tls12;

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version11, configureSsl: Tls12Only));
        Assert.Equal("b.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "b.test", HttpVersion.Version11, configureSsl: Tls12Only)).ServedSubject);
        Assert.Equal("a.test v1", (await TlsProbe.GetAsync(_pki, server.Port, "a.test", HttpVersion.Version11)).ServedSubject);
    }

    [Fact]
    public async Task RequiredClientCertificateIsEnforcedPerHost()
    {
        var trusted = _pki.ClientCertificate("origin-pull");
        var untrusted = _pki.ClientCertificate("someone-else");
        var thumbprint = trusted.Thumbprint;
        var policy = new TlsPolicy
        {
            ClientCertificate = new ClientCertificatePolicy
            {
                Validator = (_, certificate, _, _) => certificate is X509Certificate2 c && c.Thumbprint == thumbprint,
            },
        };
        var table = new CertificateTableBuilder()
            .Add(["locked.test"], _a, policy)
            .Add(["open.test"], _b, TlsPolicy.Default)
            .Build();
        await using var server = await StartTcpAsync(
            new TlsHandshakeSelector(new CertificateTableSource(table)),
            static context => context.Response.WriteAsync(context.Connection.ClientCertificate?.Thumbprint ?? "none"));
        Action<SslClientAuthenticationOptions> Present(X509Certificate2 certificate) =>
            options => options.LocalCertificateSelectionCallback = (_, _, _, _, _) => certificate;

        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version20));
        await Assert.ThrowsAsync<HttpRequestException>(() => TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version20, configureSsl: Present(untrusted)));
        Assert.Equal(thumbprint, (await TlsProbe.GetAsync(_pki, server.Port, "locked.test", HttpVersion.Version20, configureSsl: Present(trusted))).Body);
        Assert.Equal("none", (await TlsProbe.GetAsync(_pki, server.Port, "open.test", HttpVersion.Version20)).Body);
    }
}
