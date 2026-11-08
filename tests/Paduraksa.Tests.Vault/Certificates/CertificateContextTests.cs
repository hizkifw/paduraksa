using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Crypto;
using Paduraksa.Vault.Persistence;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary>
/// The serving and upstream egresses (<see cref="IServerCertificateSource"/>, <see cref="IClientCertificateSource"/>), and the
/// guarantees around the key at rest: sealed, rotated with the KEK, and unreachable through any public surface.
/// </summary>
public sealed class CertificateContextTests : IDisposable
{
    private static readonly string[] AppNames = ["app.example.com"];

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateIssuance Issuance => _vault.Get<ICertificateIssuance>();

    private IServerCertificateSource Serving => _vault.Get<IServerCertificateSource>();

    public void Dispose() => _vault.Dispose();

    [Fact]
    public async Task AnUnchangedCertificateYieldsTheSameInstanceEveryTime()
    {
        var id = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        var first = await Serving.LoadForServingAsync(id, Ct);
        var second = await Serving.LoadForServingAsync(id, Ct);
        var upstream = await _vault.Get<IClientCertificateSource>().LoadForUpstreamAsync(id, Ct);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Same(first, upstream);
    }

    [Fact]
    public async Task ConcurrentFirstLoadsAgreeOnOneInstance()
    {
        var id = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        var loads = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Serving.LoadForServingAsync(id, Ct).AsTask()));

        Assert.All(loads, l => Assert.Same(loads[0], l));
    }

    [Fact]
    public async Task AReplacementOrADeletionDropsTheCachedInstance()
    {
        var id = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var before = await Serving.LoadForServingAsync(id, Ct);

        await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        var replaced = await Serving.LoadForServingAsync(id, Ct);
        Assert.NotSame(before, replaced);
        Assert.Same(replaced, await Serving.LoadForServingAsync(id, Ct));

        await Issuance.DeleteAsync(id, Ct);
        Assert.Null(await Serving.LoadForServingAsync(id, Ct));
    }

    [Fact]
    public async Task AnUnknownCertificateIsNull()
    {
        Assert.Null(await Serving.LoadForServingAsync(CertificateId.New(), Ct));
        Assert.Null(await _vault.Get<IClientCertificateSource>().LoadForUpstreamAsync(CertificateId.New(), Ct));
    }

    [Fact]
    public async Task ACertificateSurvivesARestartAndIsServedFromTheDatabase()
    {
        var id = CertificateId.New();
        var info = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        _vault.Restart();
        var context = await _vault.Get<IServerCertificateSource>().LoadForServingAsync(id, Ct);

        Assert.NotNull(context);
        Assert.Equal(info.Sha256Fingerprint, Convert.ToHexStringLower(context.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
    }

    [Fact]
    public async Task TheContextServesTlsToAClientThatTrustsTheLocalCa()
    {
        var rootInfo = await Issuance.EnsureLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, TimeSpan.FromDays(365), Ct);
        var id = CertificateId.New();
        var info = await Issuance.IssueFromLocalCaAsync(id, KeySpec.EcdsaP256, ["tls.example.com"], TimeSpan.FromDays(30), Ct);
        var context = await Serving.LoadForServingAsync(id, Ct);
        using var root = X509Certificate2.CreateFromPem(rootInfo.PublicChainPem);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(
            async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(Ct);
                await using var ssl = new SslStream(accepted.GetStream());
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context }, Ct);
                await ssl.WriteAsync("hello"u8.ToArray(), Ct);
                await ssl.FlushAsync(Ct);
            },
            Ct);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, Ct);
        await using var clientSsl = new SslStream(
            client.GetStream(),
            leaveInnerStreamOpen: false,
            (_, certificate, chain, _) =>
            {
                using var verify = new X509Chain();
                verify.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                verify.ChainPolicy.CustomTrustStore.Add(root);
                verify.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                verify.ChainPolicy.VerificationTime = VaultHarness.Epoch.UtcDateTime; // the vault's clock, not the wall clock
                foreach (var extra in chain!.ChainElements.Skip(1))
                {
                    verify.ChainPolicy.ExtraStore.Add(extra.Certificate);
                }

                return verify.Build(new X509Certificate2(certificate!));
            });
        await clientSsl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "tls.example.com" }, Ct);

        Assert.Equal(info.Sha256Fingerprint, Convert.ToHexStringLower(clientSsl.RemoteCertificate!.GetCertHash(HashAlgorithmName.SHA256)));
        var buffer = new byte[5];
        await clientSsl.ReadExactlyAsync(buffer, Ct);
        Assert.Equal("hello", Encoding.ASCII.GetString(buffer));
        await server;
    }

    [Fact]
    public async Task CertificateKeysAreRewrappedByAKekRotationAndStillServe()
    {
        var id = CertificateId.New();
        var info = await Issuance.CreateSelfSignedAsync(id, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        using var staged = TestCa.CreateRoot();
        var stagedId = CertificateId.New();
        var csr = await Issuance.CreateKeyAndCsrAsync(stagedId, KeySpec.EcdsaP256, AppNames, Ct);

        using var newKek = Kek.Generate();
        var result = await _vault.Get<IVaultKekRotation>().RotateKekAsync(newKek.Encode().AsMemory(), Ct);

        Assert.Equal(2, result.KeysRewrapped);

        // Restart under the new key: the certificate is still served, and the staged key still takes its chain.
        _vault.Variables[Paduraksa.Vault.Crypto.KekProvider.KeyVariable] = newKek.Encode();
        _vault.Restart();
        var context = await _vault.Get<IServerCertificateSource>().LoadForServingAsync(id, Ct);
        Assert.Equal(info.Sha256Fingerprint, Convert.ToHexStringLower(context!.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
        using var leaf = staged.Issue(csr.Der.ToArray());
        await _vault.Get<ICertificateIssuance>().AttachChainAsync(stagedId, csr.StagedKey, Encoding.UTF8.GetBytes(leaf.ExportCertificatePem()), Ct);
    }

    [Fact]
    public async Task KeysAreSealedAtRestAndAKeyRowMovedToAnotherCertificateDoesNotOpen()
    {
        var a = CertificateId.New();
        var b = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(a, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);
        await Issuance.CreateSelfSignedAsync(b, KeySpec.EcdsaP256, ["b.example.com"], TimeSpan.FromDays(30), Ct);

        var factory = _vault.Get<IDbContextFactory<VaultDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var rowA = await db.CertificateKeys.SingleAsync(k => k.Id == a, Ct);
            var rowB = await db.CertificateKeys.SingleAsync(k => k.Id == b, Ct);
            rowA.Sealed = rowB.Sealed;
            await db.SaveChangesAsync(Ct);
        }

        _vault.Restart();
        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await _vault.Get<IServerCertificateSource>().LoadForServingAsync(a, Ct));
        Assert.NotNull(await _vault.Get<IServerCertificateSource>().LoadForServingAsync(b, Ct));
    }

    [Fact]
    public async Task ALeafRowEditedIntoTheLocalCaDoesNotOpenAsTheSigningKey()
    {
        var rootId = CertificateId.New();
        await Issuance.EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(30), Ct);
        var leafId = CertificateId.New();
        await Issuance.CreateSelfSignedAsync(leafId, KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(30), Ct);

        var factory = _vault.Get<IDbContextFactory<VaultDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            var root = await db.CertificateKeys.SingleAsync(k => k.Id == rootId, Ct);
            var leaf = await db.CertificateKeys.SingleAsync(k => k.Id == leafId, Ct);
            root.Sealed = leaf.Sealed;
            root.ChainPem = leaf.ChainPem;
            await db.SaveChangesAsync(Ct);
        }

        _vault.Restart();
        await Assert.ThrowsAnyAsync<CryptographicException>(
            async () => await _vault.Get<ICertificateIssuance>().IssueFromLocalCaAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct));
    }

    [Fact]
    public async Task NoKeyBytesAreInTheDatabaseFiles()
    {
        await Issuance.CreateSelfSignedAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, TimeSpan.FromDays(1), Ct);
        await Issuance.CreateKeyAndCsrAsync(CertificateId.New(), KeySpec.EcdsaP256, AppNames, Ct);

        _vault.Restart();
        SqliteClear();
        var files = Directory.GetFiles(_vault.Data.Path, "vault.db*").Select(File.ReadAllBytes).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            Assert.True(file.AsSpan().IndexOf("PRIVATE KEY"u8) < 0, "a PEM key block is in the database file");

            // A PKCS#8 EC key starts with a fixed DER prefix (SEQUENCE, version 0, the ecPublicKey algorithm).
            Assert.True(file.AsSpan().IndexOf(new byte[] { 0x30, 0x81, 0x87, 0x02, 0x01, 0x00, 0x30, 0x13, 0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01 }) < 0, "a plain PKCS#8 EC key is in the database file");
        }
    }

    [Fact]
    public void NoCoreVaultContractReturnsAKeyOrACertificateObject()
    {
        var capabilities = typeof(ICertificateIssuance).Assembly.GetExportedTypes()
            .Where(t => t is { IsInterface: true, Namespace: "Paduraksa.Core.Vault" })
            .ToList();
        Assert.Contains(typeof(ICertificateImport), capabilities);

        // Only the two egresses may return a certificate object, and then only as the prebuilt TLS context.
        foreach (var method in capabilities.SelectMany(t => t.GetMethods()))
        {
            var returned = Unwrap(method.ReturnType);
            Assert.False(typeof(AsymmetricAlgorithm).IsAssignableFrom(returned), $"{method.DeclaringType!.Name}.{method.Name} returns a key");
            Assert.False(typeof(X509Certificate).IsAssignableFrom(returned), $"{method.DeclaringType!.Name}.{method.Name} returns a certificate");
            if (returned == typeof(SslStreamCertificateContext))
            {
                Assert.Contains(method.DeclaringType, new[] { typeof(IServerCertificateSource), typeof(IClientCertificateSource) });
            }

            Assert.All(method.GetParameters().Where(p => p.ParameterType.IsByRef), p => Assert.Fail($"{method.Name} has an out/ref parameter {p.Name}"));
        }

        // Records the capabilities return (CertificateInfo, CsrResult) carry public data only: no byte[] with a key.
        foreach (var record in new[] { typeof(CertificateInfo), typeof(CsrResult) })
        {
            Assert.DoesNotContain(record.GetProperties(), p => typeof(AsymmetricAlgorithm).IsAssignableFrom(p.PropertyType) || typeof(X509Certificate).IsAssignableFrom(p.PropertyType));
        }

        Assert.DoesNotContain(typeof(CertificateInfo).GetProperties(), p => p.Name.Contains("Private", StringComparison.OrdinalIgnoreCase) || p.PropertyType == typeof(byte[]));
    }

    private static Type Unwrap(Type type)
    {
        var current = type;
        while (current.IsGenericType)
        {
            current = current.GetGenericArguments()[0];
        }

        return current;
    }

    private static void SqliteClear() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
