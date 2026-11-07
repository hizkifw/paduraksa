using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary><see cref="ICertificateImport"/>: the formats it reads, the limits it keeps, and every way an upload can be refused.</summary>
public sealed class CertificateImportTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateImport Import => _vault.Get<ICertificateImport>();

    public void Dispose() => _vault.Dispose();

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string CertPem(params X509Certificate2[] certificates) => string.Concat(certificates.Select(c => c.ExportCertificatePem() + "\n"));

    private async Task<CertificateImportException> Refused(Stream data, string password = "")
    {
        var ex = await Assert.ThrowsAsync<CertificateImportException>(async () => await Import.ImportAsync(CertificateId.New(), data, password.AsMemory(), Ct));
        return ex;
    }

    [Fact]
    public async Task PlainPemWithAKeyAndAChainIsImported()
    {
        using var root = TestCa.CreateRoot();
        using var intermediate = root.CreateIntermediate();
        using var key = TestCa.NewKey();
        using var leaf = intermediate.IssueWithKey(key, "app.example.com", "www.example.com");

        var id = CertificateId.New();
        var info = await Import.ImportAsync(id, Stream(key.ExportPkcs8PrivateKeyPem() + "\n" + CertPem(leaf, intermediate.Certificate)), default, Ct);

        Assert.Equal(["app.example.com", "www.example.com"], info.SubjectAlternativeNames);
        Assert.Equal(Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)), info.Sha256Fingerprint);
        Assert.Equal([intermediate.Certificate.SubjectName.Name, root.Certificate.SubjectName.Name], info.ChainIssuers);
        var context = await _vault.Get<IServerCertificateSource>().LoadForServingAsync(id, Ct);
        Assert.NotNull(context);
        Assert.True(context.TargetCertificate.HasPrivateKey);
    }

    [Fact]
    public async Task ChainOrderDoesNotMatterAndTheRootIsDropped()
    {
        using var root = TestCa.CreateRoot();
        using var intermediate = root.CreateIntermediate();
        using var key = TestCa.NewKey();
        using var leaf = intermediate.IssueWithKey(key, "app.example.com");

        var info = await Import.ImportAsync(
            CertificateId.New(), Stream(CertPem(root.Certificate, intermediate.Certificate, leaf) + key.ExportPkcs8PrivateKeyPem()), default, Ct);

        Assert.Equal(Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)), info.Sha256Fingerprint);
        Assert.Equal(2, info.ChainIssuers.Count);
        Assert.DoesNotContain(root.Certificate.ExportCertificatePem(), info.PublicChainPem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EncryptedPkcs8PemIsImportedWithItsPassword()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "app.example.com");
        var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

        var info = await Import.ImportAsync(CertificateId.New(), Stream(CertPem(leaf) + encrypted), Password.AsMemory(), Ct);

        Assert.Equal(["app.example.com"], info.SubjectAlternativeNames);
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    public async Task EncryptedPemWithAMissingOrWrongPasswordIsWrongPassword(string password)
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "app.example.com");
        var encrypted = key.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

        var ex = await Refused(Stream(CertPem(leaf) + encrypted), password);

        Assert.Equal(CertificateImportFailure.WrongPassword, ex.Reason);
        Assert.DoesNotContain(Password, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RsaKeysAreImportedToo()
    {
        using var ca = TestCa.CreateRoot();
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=rsa.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var signed = ca.Issue(request.CreateSigningRequest(), names: ["rsa.example.com"]);

        var info = await Import.ImportAsync(CertificateId.New(), Stream(signed.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem()), default, Ct);

        Assert.Equal("RSA 2048", info.KeyAlgorithm);
    }

    [Fact]
    public async Task PfxIsImportedWithItsChain()
    {
        using var root = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = root.IssueWithKey(key, "pfx.example.com");
        var pfx = leaf.Export(X509ContentType.Pkcs12, Password);

        var info = await Import.ImportAsync(CertificateId.New(), new MemoryStream(pfx), Password.AsMemory(), Ct);

        Assert.Equal(["pfx.example.com"], info.SubjectAlternativeNames);
        Assert.Equal(Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)), info.Sha256Fingerprint);
    }

    [Fact]
    public async Task PfxWithoutAPasswordIsImported()
    {
        using var root = TestCa.CreateRoot();
        using var leaf = root.IssueWithKey("nopw.example.com");

        var info = await Import.ImportAsync(CertificateId.New(), new MemoryStream(leaf.Export(X509ContentType.Pkcs12)), default, Ct);

        Assert.Equal(["nopw.example.com"], info.SubjectAlternativeNames);
    }

    [Fact]
    public async Task PfxWithAWrongPasswordIsWrongPassword()
    {
        using var root = TestCa.CreateRoot();
        using var leaf = root.IssueWithKey("pfx.example.com");

        var ex = await Refused(new MemoryStream(leaf.Export(X509ContentType.Pkcs12, Password)), "nope");

        Assert.Equal(CertificateImportFailure.WrongPassword, ex.Reason);
    }

    [Fact]
    public async Task GarbageIsUnreadable()
    {
        var random = RandomNumberGenerator.GetBytes(2000);
        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(new MemoryStream(random))).Reason);
        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(new MemoryStream([]))).Reason);
        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(Stream("-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"))).Reason);
        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(Stream("-----BEGIN CERTIFICATE-----\n!!!!\n-----END CERTIFICATE-----\n"))).Reason);
    }

    [Fact]
    public async Task APemWithoutACertificateIsUnreadableAndWithoutAKeyIsNoMatchingKey()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "app.example.com");

        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(Stream(key.ExportPkcs8PrivateKeyPem()))).Reason);
        Assert.Equal(CertificateImportFailure.NoMatchingKey, (await Refused(Stream(CertPem(leaf)))).Reason);
    }

    [Fact]
    public async Task ALargerThanAMebibyteUploadIsTooLarge()
    {
        var oversize = new MemoryStream(new byte[CertificateImportLimits.MaxBytes + 1]);
        Assert.Equal(CertificateImportFailure.TooLarge, (await Refused(oversize)).Reason);

        // Exactly at the cap is read (and is then just not a certificate).
        Assert.Equal(CertificateImportFailure.Unreadable, (await Refused(new MemoryStream(new byte[CertificateImportLimits.MaxBytes]))).Reason);
    }

    [Fact]
    public async Task AKeyThatBelongsToNoCertificateIsNoMatchingKey()
    {
        using var ca = TestCa.CreateRoot();
        using var leaf = ca.IssueWithKey("app.example.com");
        using var otherKey = TestCa.NewKey();

        var ex = await Refused(Stream(CertPem(leaf) + otherKey.ExportPkcs8PrivateKeyPem()));

        Assert.Equal(CertificateImportFailure.NoMatchingKey, ex.Reason);
    }

    [Fact]
    public async Task AKeyOfAnotherTypeThanTheCertificatesIsNoMatchingKey()
    {
        using var ca = TestCa.CreateRoot();
        using var leaf = ca.IssueWithKey("app.example.com");
        using var rsa = RSA.Create(2048);

        var ex = await Refused(Stream(CertPem(leaf) + rsa.ExportPkcs8PrivateKeyPem()));

        Assert.Equal(CertificateImportFailure.NoMatchingKey, ex.Reason);
    }

    [Theory]
    [InlineData("RSA PRIVATE KEY")]
    [InlineData("EC PRIVATE KEY")]
    public async Task TraditionalKeyBlocksAreUnsupported(string label)
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "app.example.com");
        var block = PemEncoding.WriteString(label, key.ExportECPrivateKey());

        var ex = await Refused(Stream(CertPem(leaf) + block));

        Assert.Equal(CertificateImportFailure.UnsupportedKeyFormat, ex.Reason);
        Assert.Contains("openssl pkcs8 -topk8", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALeafWithoutNamesIsRefusedBeforeAnythingIsStored()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key);
        var id = CertificateId.New();

        var ex = await Assert.ThrowsAsync<CertificateImportException>(
            async () => await Import.ImportAsync(id, Stream(CertPem(leaf) + key.ExportPkcs8PrivateKeyPem()), default, Ct));

        Assert.Equal(CertificateImportFailure.NoUsableName, ex.Reason);
        Assert.Null(await _vault.Get<IServerCertificateSource>().LoadForServingAsync(id, Ct));
    }

    [Fact]
    public async Task ImportingOverAnExistingIdReplacesItAtomically()
    {
        using var ca = TestCa.CreateRoot();
        using var firstKey = TestCa.NewKey();
        using var secondKey = TestCa.NewKey();
        using var first = ca.IssueWithKey(firstKey, "app.example.com");
        using var second = ca.IssueWithKey(secondKey, "app.example.com", "new.example.com");
        var id = CertificateId.New();
        var source = _vault.Get<IServerCertificateSource>();

        var a = await Import.ImportAsync(id, Stream(CertPem(first) + firstKey.ExportPkcs8PrivateKeyPem()), default, Ct);
        var contextA = await source.LoadForServingAsync(id, Ct);

        // A failing import (the key does not match) leaves the old pair serving.
        using var stranger = TestCa.NewKey();
        await Refused(Stream(CertPem(second) + stranger.ExportPkcs8PrivateKeyPem()));
        Assert.Same(contextA, await source.LoadForServingAsync(id, Ct));

        var b = await Import.ImportAsync(id, Stream(CertPem(second) + secondKey.ExportPkcs8PrivateKeyPem()), default, Ct);
        var contextB = await source.LoadForServingAsync(id, Ct);

        Assert.NotEqual(a.Sha256Fingerprint, b.Sha256Fingerprint);
        Assert.NotSame(contextA, contextB);
        Assert.Equal(b.Sha256Fingerprint, Convert.ToHexStringLower(contextB!.TargetCertificate.GetCertHash(HashAlgorithmName.SHA256)));
    }

    [Fact]
    public async Task ImportingTheLocalCaRootIdIsRefused()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "app.example.com");
        var rootId = CertificateId.New();
        await _vault.Get<ICertificateIssuance>().EnsureLocalCaAsync(rootId, KeySpec.EcdsaP256, TimeSpan.FromDays(30), Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await Import.ImportAsync(rootId, Stream(CertPem(leaf) + key.ExportPkcs8PrivateKeyPem()), default, Ct));
    }

    [Fact]
    public async Task TheSerialNumberIsKeptAsDerContentBytes()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        var request = new CertificateRequest("CN=high.example.com", key, HashAlgorithmName.SHA256);
        using var signed = ca.Issue(request.CreateSigningRequest(), names: ["high.example.com"], serial: [0x00, 0x80, 0x01, 0x02]);

        var imported = await Import.ImportAsync(CertificateId.New(), Stream(signed.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem()), default, Ct);

        // The DER INTEGER 0x00 0x80 0x01 0x02: the sign byte is part of the content bytes and is kept.
        Assert.Equal("00800102", imported.SerialNumber);
    }

    [Fact]
    public async Task TheImportedKeyIsNotInTheDatabaseFiles()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "plain.example.com");
        var pkcs8 = key.ExportPkcs8PrivateKey();
        await Import.ImportAsync(CertificateId.New(), Stream(leaf.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem()), default, Ct);

        _vault.Restart();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var files = Directory.GetFiles(_vault.Data.Path, "vault.db*").Select(File.ReadAllBytes).ToList();

        Assert.NotEmpty(files);
        Assert.All(files, file => Assert.True(file.AsSpan().IndexOf(pkcs8) < 0, "the imported key's PKCS#8 is in the database file"));
    }
}
