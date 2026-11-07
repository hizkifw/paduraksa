using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Tests.Vault.Testing;
using Paduraksa.Vault.Certificates;

namespace Paduraksa.Tests.Vault.Certificates;

/// <summary>What an upload may not do to the vault: leak an exception, cost unbounded CPU, or bring in a certificate nothing can use.</summary>
public sealed class CertificateImportHardeningTests : IDisposable
{
    private const string Password = "correct horse";

    private readonly VaultHarness _vault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICertificateImport Import => _vault.Get<ICertificateImport>();

    public void Dispose() => _vault.Dispose();

    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Upload(X509Certificate2 certificate, ECDsa key) => certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem();

    private async Task<CertificateImportException> Refused(string upload)
    {
        var id = CertificateId.New();
        var ex = await Assert.ThrowsAsync<CertificateImportException>(async () => await Import.ImportAsync(id, Stream(upload), default, Ct));
        Assert.Null(await _vault.Get<IServerCertificateSource>().LoadForServingAsync(id, Ct));
        return ex;
    }

    private static CertificateRequest RequestFor(ECDsa key, params X509Extension[] extensions)
    {
        var request = new CertificateRequest("CN=x.example.com", key, HashAlgorithmName.SHA256);
        foreach (var extension in extensions)
        {
            request.CertificateExtensions.Add(extension);
        }

        return request;
    }

    private static X509Extension San(params string[] names)
    {
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names)
        {
            san.AddDnsName(name);
        }

        return san.Build();
    }

    [Fact]
    public async Task AMalformedSanIsUnreadableNotAnException()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        var garbage = new X509Extension(new Oid("2.5.29.17"), [0x30, 0x05, 0x82, 0x10, 0x41], critical: false);
        using var leaf = ca.Sign(RequestFor(key, garbage));

        var ex = await Refused(Upload(leaf, key));

        Assert.Equal(CertificateImportFailure.Unreadable, ex.Reason);
    }

    [Fact]
    public async Task AMalformedAuthorityKeyIdentifierIsUnreadableNotAnException()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        var badAki = new X509Extension(new Oid("2.5.29.35"), [0x30, 0x03, 0x80, 0x09, 0x01], critical: false);
        using var leaf = ca.Sign(RequestFor(key, San("x.example.com"), badAki));

        var ex = await Refused(Upload(leaf, key));

        Assert.Equal(CertificateImportFailure.Unreadable, ex.Reason);
    }

    [Fact]
    public async Task OnlyNamesTheInventoryCanUseCount()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var junk = ca.Sign(RequestFor(key, San("not a host!")));

        Assert.Equal(CertificateImportFailure.NoUsableName, (await Refused(Upload(junk, key))).Reason);
    }

    [Fact]
    public async Task TheInventorysRuleDecidesWhichNamesAreUsable()
    {
        _vault.NameRules = new RejectEverything();
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.Sign(RequestFor(key, San("fine.example.com")));

        Assert.Equal(CertificateImportFailure.NoUsableName, (await Refused(Upload(leaf, key))).Reason);
    }

    [Fact]
    public async Task ACaCertificateIsNotAServerCertificate()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.Sign(RequestFor(key, San("ca.example.com"), new X509BasicConstraintsExtension(true, false, 0, true)));

        Assert.Equal(CertificateImportFailure.CaCertificate, (await Refused(Upload(leaf, key))).Reason);
    }

    [Fact]
    public async Task AnRsaKeyUnder2048BitsIsWeak()
    {
        using var ca = TestCa.CreateRoot();
        using var rsa = RSA.Create(1024);
        var request = new CertificateRequest("CN=weak.example.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(San("weak.example.com"));
        using var leaf = ca.Sign(request);

        Assert.Equal(CertificateImportFailure.WeakKey, (await Refused(leaf.ExportCertificatePem() + "\n" + rsa.ExportPkcs8PrivateKeyPem())).Reason);
    }

    [Fact]
    public async Task AnExpiredLeafIsAcceptedAndDescribedAsExpired()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.Sign(RequestFor(key, San("old.example.com")), notBefore: DateTimeOffset.UtcNow.AddDays(-100), validity: TimeSpan.FromDays(10));

        var info = await Import.ImportAsync(CertificateId.New(), Stream(Upload(leaf, key)), default, Ct);

        Assert.True(info.NotAfter < DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task AnEncryptedKeyWithAnExcessiveIterationCountIsRefusedBeforeAnyDerivation()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "slow.example.com");
        var heavy = key.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 700_000));

        var ex = await Refused(leaf.ExportCertificatePem() + "\n" + heavy);

        // The count is read from the structure and refused as such: no password derivation is attempted (that would be "wrong password").
        Assert.Equal(CertificateImportFailure.Unreadable, ex.Reason);
        Assert.Contains("iterations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEncryptedKeyAtTheLimitStillImports()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "limit.example.com");
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));

        var info = await Import.ImportAsync(CertificateId.New(), Stream(leaf.ExportCertificatePem() + "\n" + pem), Password.AsMemory(), Ct);

        Assert.Equal(["limit.example.com"], info.SubjectAlternativeNames);
    }

    [Fact]
    public async Task AWrongPasswordIsAWrongPasswordNotAnythingElse()
    {
        using var ca = TestCa.CreateRoot();
        using var key = TestCa.NewKey();
        using var leaf = ca.IssueWithKey(key, "wrong.example.com");
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10_000));

        var ex = await Refused(leaf.ExportCertificatePem() + "\n" + pem);

        Assert.Equal(CertificateImportFailure.WrongPassword, ex.Reason);
    }

    [Fact]
    public void AnEcdsaOnlyUploadTriesOneKeyTypeSoTheDerivationRunsOnce()
    {
        using var ca = TestCa.CreateRoot();
        using var leaf = ca.IssueWithKey("one.example.com");

        var creators = CertificateImport.CandidateKeyTypes([leaf]);

        using var created = Assert.Single(creators)();
        Assert.IsAssignableFrom<ECDsa>(created);
    }

    [Fact]
    public void AMixedUploadTriesEcdsaThenRsaAndOneWithNeitherTriesNothing()
    {
        using var ca = TestCa.CreateRoot();
        using var ecdsa = ca.IssueWithKey("ec.example.com");
        using var rsaKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=rsa.example.com", rsaKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var rsa = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        var creators = CertificateImport.CandidateKeyTypes([ecdsa, rsa]);

        Assert.Equal(2, creators.Count);
        using (var first = creators[0]())
        {
            Assert.IsAssignableFrom<ECDsa>(first);
        }

        using (var second = creators[1]())
        {
            Assert.IsAssignableFrom<RSA>(second);
        }

        Assert.Empty(CertificateImport.CandidateKeyTypes([]));
    }

    private sealed class RejectEverything : ICertificateNameRules
    {
        public string? Normalize(string name) => null;
    }
}
