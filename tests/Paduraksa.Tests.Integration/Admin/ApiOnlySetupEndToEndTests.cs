using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Hosting;

using Paduraksa.Admin.Certificates;
using Paduraksa.Admin.Json;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Api;
using Paduraksa.Contracts.Audit;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Server.Hosting;
using Paduraksa.Tests.Integration.Config;
using Paduraksa.Tests.Integration.Hosting;
using Paduraksa.Tests.Shared;

namespace Paduraksa.Tests.Integration.Admin;

/// <summary>
/// PLAN §14 M4's exit criterion: an API-only end-to-end setup. A real host on a fresh data directory is taken from first
/// start to a proxied HTTPS host using nothing but HTTP calls on the admin listener (plus traffic on the public listener):
/// the closed setup gate, the Owner, TOTP, an API token, a certificate, an upstream, a host, the proxy, the simulator,
/// revisions and the audit chain. No service is reached directly except the setup-token file a headless install reads too.
/// </summary>
public sealed class ApiOnlySetupEndToEndTests
{
    private const string AppName = "app.test";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AWholeFirstRunCanBeWalkedOverTheApiAndEndsWithAProxiedHttpsHost()
    {
        await using AdminHarness h = await AdminHarness.StartAsync();
        (IHost echoHost, string upstreamAddress) = await HeaderEchoUpstreamHost.StartAsync(null, Ct);
        using IHost upstreamHost = echoHost;

        // 1. The gate is shut: setup reports incomplete, a protected endpoint is refused, and the local CA root can be
        //    downloaded (anonymously, as the wizard needs) and is the root of the certificate the admin listener serves.
        using (AdminResponse setup = await h.GetAsync("/api/v1/setup"))
        {
            Assert.Equal(HttpStatusCode.OK, setup.Status);
            Assert.False(setup.Json.GetProperty("complete").GetBoolean());
        }

        using (AdminResponse refused = await h.GetAsync("/api/v1/hosts"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
            Assert.Equal("setup.required", refused.Json.GetProperty("code").GetString());
        }

        string caFingerprint;
        using (AdminResponse ca = await h.GetAsync("/api/v1/ca"))
        {
            Assert.Equal(HttpStatusCode.OK, ca.Status);
            caFingerprint = ca.Json.GetProperty("sha256Fingerprint").GetString()!;
        }

        X509Certificate2 root;
        using (AdminResponse pem = await h.GetAsync("/api/v1/ca/root.pem"))
        {
            Assert.Equal(HttpStatusCode.OK, pem.Status);
            root = X509CertificateLoader.LoadCertificate(CaRoot.PemToDer(pem.Body));
        }

        Assert.Equal(Fingerprint(root), caFingerprint);
        (X509Certificate2 adminLeaf, bool adminChains) = await HandshakeAsync(h.Host, "127.0.0.1", root);
        Assert.True(adminChains, "the admin listener's certificate chains to the local CA root the API serves");
        Assert.Equal(root.Subject, adminLeaf.Issuer);

        // 2. The Owner: the token is the file a headless install reads, and the setup call signs the Owner in.
        string setupToken = (await File.ReadAllTextAsync(Path.Combine(h.Host.Options.DataDirectory, SetupService.TokenFileName), Ct)).Trim();
        string ownerCookie;
        using (AdminResponse created = await h.PostAsync("/api/v1/setup", Body.Setup(setupToken, AdminHarness.OwnerName, AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.Created, created.Status);
            Assert.Equal("owner", created.Json.GetProperty("owner").GetProperty("username").GetString());
            Assert.DoesNotContain(setupToken, created.Body, StringComparison.Ordinal);
            Assert.DoesNotContain(AdminHarness.Password, created.Body, StringComparison.Ordinal);
            ownerCookie = created.SessionCookie!;
        }

        // 3. A second factor, then a token that carries every permission the rest of the walk needs. The token is the only
        //    credential used from here on (a token may not manage tokens or sessions, so it is issued while the session lives).
        string provisioningUri;
        using (AdminResponse begin = await h.PostAsync("/api/v1/me/totp/enrollment", Body.Password(AdminHarness.Password), ownerCookie))
        {
            Assert.Equal(HttpStatusCode.OK, begin.Status);
            provisioningUri = begin.Json.GetProperty("provisioningUri").GetString()!;
            Assert.StartsWith("otpauth://totp/", provisioningUri, StringComparison.Ordinal);
        }

        using (AdminResponse confirm = await h.PostAsync(
            "/api/v1/me/totp/enrollment/confirm", $$"""{"code":"{{TotpOracle.CodeFor(provisioningUri)}}"}""", ownerCookie))
        {
            Assert.Equal(HttpStatusCode.OK, confirm.Status);
            Assert.Equal(10, confirm.Json.GetProperty("codes").GetArrayLength());
        }

        using (AdminResponse logout = await h.PostAsync("/api/v1/auth/logout", cookie: ownerCookie))
        {
            Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        }

        string partialCookie;
        using (AdminResponse password = await h.PostAsync("/api/v1/auth/login", Body.Login(AdminHarness.OwnerName, AdminHarness.Password)))
        {
            Assert.Equal(HttpStatusCode.OK, password.Status);
            Assert.True(password.Json.GetProperty("secondFactorRequired").GetBoolean());
            partialCookie = password.SessionCookie!;
        }

        string sessionCookie;
        using (AdminResponse secondFactor = await h.PostAsync(
            "/api/v1/auth/second-factor", $$"""{"code":"{{TotpOracle.CodeFor(provisioningUri, 1)}}","recoveryCode":null}""", partialCookie))
        {
            Assert.Equal(HttpStatusCode.OK, secondFactor.Status);
            Assert.False(secondFactor.Json.GetProperty("secondFactorRequired").GetBoolean());
            sessionCookie = secondFactor.SessionCookie!;
        }

        (string tokenId, string token) = await h.IssueTokenAsync(
            sessionCookie, "config:read", "config:write", "certificates:read", "certificates:write", "simulate", "audit:read");
        Assert.StartsWith("pdk_", token, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(tokenId));

        // 4. A self-signed leaf for app.test (no CA needed) and an upstream on an ephemeral loopback port.
        using (AdminResponse issued = await h.PostAsync(
            "/api/v1/certificates/self-signed",
            JsonSerializer.Serialize(new SelfSignedCertificateRequest([AppName], Label: "api-only"), AdminJsonContext.Default.SelfSignedCertificateRequest),
            bearer: token))
        {
            Assert.Equal(HttpStatusCode.Created, issued.Status);
            CertificateSummary certificate = Read(issued, AdminJsonContext.Default.CertificateOperationResult).Certificate!;
            Assert.Equal(CertificateSource.SelfSigned, certificate.Source);
            Assert.Contains(AppName, certificate.Names);

            UpstreamConfig upstream = ConfigTestSupport.Upstream("app", upstreamAddress);
            using (AdminResponse upstreamCreated = await h.PostAsync(
                "/api/v1/upstreams", JsonSerializer.Serialize(upstream, AdminJsonContext.Default.UpstreamConfig), bearer: token))
            {
                Assert.Equal(HttpStatusCode.Created, upstreamCreated.Status);
                Assert.Equal(ConfigCommandStatus.Committed, Read(upstreamCreated, AdminJsonContext.Default.ConfigCommandResult).Status);
            }

            // 5. The host: dry run first (no host, no revision), then the real commit, then a versioned update that proves
            //    If-Match is the write's precondition.
            HostConfig host = ConfigTestSupport.ProxyHost("app", AppName, "app") with
            {
                Certificate = new CertificateBinding { Mode = CertificateBindingMode.Specific, CertificateId = certificate.Id },
            };
            string hostJson = JsonSerializer.Serialize(host, AdminJsonContext.Default.HostConfig);
            int revisionsBeforeDryRun = await RevisionCountAsync(h, token);

            using (AdminResponse dryRun = await h.PostAsync("/api/v1/hosts?dryRun=true", hostJson, bearer: token))
            {
                Assert.Equal(HttpStatusCode.OK, dryRun.Status);
                ConfigCommandResult plan = Read(dryRun, AdminJsonContext.Default.ConfigCommandResult);
                Assert.Equal(ConfigCommandStatus.DryRun, plan.Status);
                Assert.Contains(plan.Changes, change => change is HostChange { Kind: ConfigChangeKind.Added });
            }

            using (AdminResponse hosts = await h.GetAsync("/api/v1/hosts", bearer: token))
            {
                Assert.Empty(Read(hosts, AdminJsonContext.Default.IReadOnlyListHostConfig));
            }

            Assert.Equal(revisionsBeforeDryRun, await RevisionCountAsync(h, token));

            using (AdminResponse created = await h.PostAsync("/api/v1/hosts", hostJson, bearer: token))
            {
                Assert.Equal(HttpStatusCode.Created, created.Status);
                Assert.Equal($"/api/v1/hosts/{host.Id}", created.Headers.Location!.ToString());
                Assert.Equal(ConfigCommandStatus.Committed, Read(created, AdminJsonContext.Default.ConfigCommandResult).Status);
            }

            using (AdminResponse read = await h.GetAsync($"/api/v1/hosts/{host.Id}", bearer: token))
            {
                Assert.Equal(HttpStatusCode.OK, read.Status);
                HostConfig stored = Read(read, AdminJsonContext.Default.HostConfig);
                Assert.Equal(certificate.Id, stored.Certificate.CertificateId);
                string version = read.Headers.ETag?.Tag ?? throw new InvalidOperationException("The host response carried no ETag.");
                using AdminResponse updated = await h.SendAsync(
                    HttpMethod.Put,
                    $"/api/v1/hosts/{host.Id}",
                    JsonSerializer.Serialize(stored with { Notes = "created through the API alone" }, AdminJsonContext.Default.HostConfig),
                    bearer: token,
                    headers: new Dictionary<string, string> { ["If-Match"] = version });
                Assert.Equal(HttpStatusCode.OK, updated.Status);
                Assert.Equal(ConfigCommandStatus.Committed, Read(updated, AdminJsonContext.Default.ConfigCommandResult).Status);
            }

            // 6. The public listener: a TLS handshake for app.test (SNI from the Host header) gets this certificate, and
            //    the request reaches the upstream behind the route.
            using HttpClient publicClient = CreatePublicClient(out StrongBox<string?> served);
            using (HttpRequestMessage request = new(HttpMethod.Get, new Uri(TestHost.Endpoint(h.Host, HostRole.Public, "https"), "/hello")))
            {
                request.Headers.Host = AppName;
                using HttpResponseMessage response = await publicClient.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Contains("target: /hello", await response.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
                Assert.Equal(certificate.Sha256Fingerprint, served.Value);
            }

            // 7. The simulator agrees with the live gate: the same HTTPS request is forwarded to this upstream.
            SimulateBody simulate = new(new SimulationRequest($"https://{AppName}/hello", "203.0.113.9"));
            using (AdminResponse simulated = await h.PostAsync(
                "/api/v1/simulate", JsonSerializer.Serialize(simulate, AdminJsonContext.Default.SimulateBody), bearer: token))
            {
                Assert.Equal(HttpStatusCode.OK, simulated.Status);
                SimulationResult result = Read(simulated, AdminJsonContext.Default.SimulationResult);
                Assert.Equal(SimulationStatus.Simulated, result.Status);
                Assert.Equal(SimulationOutcomeKind.ForwardToUpstream, result.Outcome!.Kind);
                Assert.Equal(TestIds.Upstream("app"), result.Upstream!.Id);
            }

            // 8. The three commits are revisions (newest first) and the audit chain over the whole walk is intact.
            using (AdminResponse revisions = await h.GetAsync("/api/v1/revisions", bearer: token))
            {
                Assert.Equal(HttpStatusCode.OK, revisions.Status);
                Page<RevisionSummary> page = Read(revisions, AdminJsonContext.Default.PageRevisionSummary);
                Assert.Equal([3L, 2L, 1L], page.Items.Select(revision => revision.Seq));
            }

            using (AdminResponse verify = await h.GetAsync("/api/v1/audit/verify", bearer: token))
            {
                Assert.Equal(HttpStatusCode.OK, verify.Status);
                AuditVerification verification = Read(verify, AdminJsonContext.Default.AuditVerification);
                Assert.True(verification.Intact, verification.Break?.Description);
                Assert.True(verification.VerifiedEvents > 0);
            }
        }
    }

    private static T Read<T>(AdminResponse response, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(response.Body, typeInfo) ?? throw new InvalidOperationException($"Empty body for {typeof(T).Name}.");

    private static async Task<int> RevisionCountAsync(AdminHarness h, string bearer)
    {
        using AdminResponse revisions = await h.GetAsync("/api/v1/revisions", bearer: bearer);
        Assert.Equal(HttpStatusCode.OK, revisions.Status);
        return Read(revisions, AdminJsonContext.Default.PageRevisionSummary).Items.Count;
    }

    private static string Fingerprint(X509Certificate2 certificate) => Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256));

    /// <summary>
    /// A TLS handshake with the admin listener for <paramref name="serverName"/>: the certificate it presented, and whether it
    /// chains to <paramref name="root"/> (the public root the API serves).
    /// </summary>
    private static async Task<(X509Certificate2 Certificate, bool ChainsToRoot)> HandshakeAsync(
        PaduraksaHost host, string serverName, X509Certificate2 root)
    {
        using TcpClient tcp = new();
        await tcp.ConnectAsync(IPAddress.Loopback, TestHost.Endpoint(host, HostRole.Admin, "https").Port, Ct);
        X509Certificate2? presented = null;
        bool chains = false;
        await using SslStream ssl = new(
            tcp.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, _, _) =>
            {
                presented = new X509Certificate2(certificate!);
                using X509Chain verify = new();
                verify.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                verify.ChainPolicy.CustomTrustStore.Add(root);
                verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                verify.ChainPolicy.DisableCertificateDownloads = true;
                chains = verify.Build(presented);
                return certificate is not null;
            });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = serverName }, Ct);
        return (presented!, chains);
    }

    /// <summary>
    /// An HTTPS client for the public listener that records the leaf the server presented (any certificate is accepted; the
    /// test asserts which one it must be). <see cref="SocketsHttpHandler"/> takes the TLS target host from the <c>Host</c>
    /// header, so setting that header to <c>app.test</c> is what puts the name in the ClientHello.
    /// </summary>
    private static HttpClient CreatePublicClient(out StrongBox<string?> servedFingerprint)
    {
        StrongBox<string?> box = new();
        servedFingerprint = box;
        SocketsHttpHandler handler = new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions =
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    box.Value = certificate is X509Certificate2 leaf ? Fingerprint(leaf) : null;
                    return certificate is not null;
                },
            },
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
}
