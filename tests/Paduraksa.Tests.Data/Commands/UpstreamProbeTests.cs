using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Probes;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Tests.Shared;
using static Paduraksa.Tests.Data.Config.TestConfigDatabase;

namespace Paduraksa.Tests.Data.Commands;

/// <summary>The first-run wizard's upstream check: a bare TCP connection, reachable or not, for a caller who may add a host.</summary>
public sealed class UpstreamProbeTests
{
    [Fact]
    public async Task AListeningPortIsReachable()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).CheckAsync($"http://127.0.0.1:{port}/app", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Reachable, result.Outcome);
        Assert.True(result.Reachable);
    }

    [Fact]
    public async Task AClosedPortIsUnreachable()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        int port;
        using (TcpListener listener = new(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).CheckAsync($"http://127.0.0.1:{port}", ConfigServiceHarness.Admin, Ct);

        Assert.Equal((UpstreamProbeOutcome.Unreachable, null), (result.Outcome, result.Message));
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.20:8080")]
    [InlineData("ftp://192.168.1.20")]
    [InlineData("http://user:secret@192.168.1.20")]
    public async Task AnythingButAnHttpUrlIsNotTried(string address)
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).CheckAsync(address, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Invalid, result.Outcome);
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task ACallerWhoMayNotAddHostsIsRefusedAndAudited()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var result = await new UpstreamProbe(h.Service, new Nobody()).CheckAsync("http://127.0.0.1:1", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamProbeOutcome.Denied, result.Outcome);
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM AuditEvents WHERE Action = 'upstream.probe' AND Outcome = 'denied'"));
    }

    [Fact]
    public async Task AnHttpsDestinationsCertificateIsFetchedForPinning()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        using var certificate = SelfSigned("nas.lan");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await using SslStream tls = new(client.GetStream());
            await tls.AuthenticateAsServerAsync(certificate);
        }, Ct);

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).FetchCertificateAsync($"https://127.0.0.1:{port}/", ConfigServiceHarness.Admin, Ct);
        await server;

        Assert.Equal(UpstreamCertificateOutcome.Fetched, result.Outcome);
        var fetched = result.Certificate!;
        Assert.Equal(Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)), fetched.Sha256);
        Assert.Equal("CN=nas.lan", fetched.Subject);
        Assert.True(fetched.SelfSigned);
        Assert.Equal(["nas.lan"], fetched.DnsNames);
    }

    [Fact]
    public async Task TheCertificateIsFetchedForTheServerNameAsked()
    {
        // The proxy sends the client's host name as SNI, and an upstream with several names picks its certificate by it:
        // the pin has to be the one presented for that name, not for the bare address.
        await using var h = await ConfigServiceHarness.CreateAsync();
        using var fallback = SelfSigned("default.lan");
        using var named = SelfSigned("app.example.test");
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(Ct);
                await using SslStream tls = new(client.GetStream());
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        ServerCertificateSelectionCallback = (_, name) => name == "app.example.test" ? named : fallback,
                    },
                    Ct);
            }
        }, Ct);
        var probe = new UpstreamProbe(h.Service, TestPermissions.AllowAll);

        var bare = await probe.FetchCertificateAsync($"https://127.0.0.1:{port}/", ConfigServiceHarness.Admin, Ct);
        var forName = await probe.FetchCertificateAsync($"https://127.0.0.1:{port}/", "app.example.test", ConfigServiceHarness.Admin, Ct);
        await server;

        Assert.Equal(Convert.ToHexStringLower(fallback.GetCertHash(HashAlgorithmName.SHA256)), bare.Certificate!.Sha256);
        Assert.Equal(Convert.ToHexStringLower(named.GetCertHash(HashAlgorithmName.SHA256)), forName.Certificate!.Sha256);
    }

    [Theory]
    [InlineData("*.example.test")]
    [InlineData("10.0.0.5")]
    [InlineData("not a name")]
    public async Task OnlyAHostNameIsAskedForAsTheServerName(string serverName)
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).FetchCertificateAsync("https://127.0.0.1:1", serverName, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamCertificateOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task APlainPortHasNoCertificateToFetch()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            // An HTTP server's answer to a TLS hello: not TLS.
            using var client = await listener.AcceptTcpClientAsync(Ct);
            await client.GetStream().WriteAsync("HTTP/1.1 400 Bad Request\r\n\r\n"u8.ToArray(), Ct);
        }, Ct);

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).FetchCertificateAsync($"https://127.0.0.1:{port}", ConfigServiceHarness.Admin, Ct);
        await server;

        Assert.Equal(UpstreamCertificateOutcome.NoTls, result.Outcome);
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("nas.lan:8443")]
    [InlineData("https://user:secret@nas.lan")]
    public async Task OnlyAnHttpsAddressIsFetched(string address)
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var result = await new UpstreamProbe(h.Service, TestPermissions.AllowAll).FetchCertificateAsync(address, ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamCertificateOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task FetchingIsRefusedAndAuditedForACallerWhoMayNotAddHosts()
    {
        await using var h = await ConfigServiceHarness.CreateAsync();

        var result = await new UpstreamProbe(h.Service, new Nobody()).FetchCertificateAsync("https://127.0.0.1:1", ConfigServiceHarness.Admin, Ct);

        Assert.Equal(UpstreamCertificateOutcome.Denied, result.Outcome);
        Assert.Equal(1L, await h.Db.ScalarAsync("SELECT count(*) FROM AuditEvents WHERE Action = 'upstream.certificate' AND Outcome = 'denied'"));
    }

    private static X509Certificate2 SelfSigned(string name)
    {
#pragma warning disable RS0030 // Test-only: an ephemeral key and self-signed cert for a loopback TLS server; nothing is exported or persisted.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CertificateRequest request = new($"CN={name}", key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
#pragma warning restore RS0030
    }

    private sealed class Nobody : IPermissionEvaluator
    {
        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(Actor actor, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlySet<Permission>>(new HashSet<Permission>());

        public ValueTask<bool> IsAllowedAsync(CommandOrigin origin, Permission permission, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
