using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Fuzz;

/// <summary>
/// A certificate and key an admin uploads (PEM, or PKCS#12) through <see cref="ICertificateImport"/> on a real vault over a scratch directory, with a fixed clock and a fixed
/// certificate ID, so a success replaces the one row instead of growing the database. The input is a byte that picks the password (none, the right one, a wrong one), then the upload.
/// Properties: nothing but a <see cref="CertificateImportException"/> escapes, whatever the bytes (a malformed certificate, a key of another kind, a hostile key-derivation cost, a
/// deep or repeated structure: the framework's exceptions are the vault's to map); the same upload gives the same verdict twice; an import takes a bounded time and memory (the
/// key-derivation cost and the number of blocks are capped before they run); a refusal never repeats the password; and what a success reports is true: the leaf is one of the PEM blocks
/// as an independent reading finds them (its SHA-256 matches), it has a name, is no CA, and the public chain that comes back holds no key.
/// </summary>
internal static class CertificateImportTarget
{
    private const string Password = "Zq7-fuzz-password-Lm9";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private static readonly CertificateId Id = CertificateId.New();

    private static readonly Lazy<Scratch> Vault = new(static () => new Scratch());

    public static FuzzTarget Target { get; } = new(
        "certificate-import",
        "an uploaded PEM or PKCS#12 certificate and key through the vault's import: the parse, the key-derivation bound and the checks on the leaf",
        Run,
        Seeds);

    private static void Run(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty)
        {
            return;
        }

        var password = (input[0] % 3) switch
        {
            0 => string.Empty,
            1 => Password,
            _ => "not-the-password",
        };
        var upload = input[1..].ToArray();

        var watch = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(precise: false);
        var first = Import(upload, password);
        watch.Stop();
        allocated = GC.GetTotalAllocatedBytes(precise: false) - allocated;

        // A key derivation capped at 600 000 iterations is a fraction of a second; a minute is a derivation that was not capped.
        Checks.That(watch.Elapsed < TimeSpan.FromSeconds(30), $"an import of {upload.Length} bytes took {watch.Elapsed.TotalSeconds:F1} s");
        Checks.That(allocated < 512L * 1024 * 1024, $"an import of {upload.Length} bytes allocated {allocated >> 20} MiB");

        var second = Import(upload, password);
        Checks.That(first.Describe() == second.Describe(), $"the same upload gave '{first.Describe()}' and then '{second.Describe()}'");

        if (first.Failure is { } failure)
        {
            Checks.That(Enum.IsDefined(failure.Reason) && !string.IsNullOrWhiteSpace(failure.Message), "a refusal with no known reason or no message");
            Checks.That(password.Length == 0 || !failure.Message.Contains(password, StringComparison.Ordinal), "a refusal repeats the password");
            return;
        }

        CheckSuccess(upload, first.Info!);
    }

    private sealed record Result(CertificateInfo? Info, CertificateImportException? Failure)
    {
        public string Describe() => Failure is { } failure
            ? $"refused {failure.Reason}: {failure.Message}"
            : $"{Info!.Sha256Fingerprint} {string.Join(',', Info.SubjectAlternativeNames)} {string.Join('|', Info.ChainIssuers)} {Info.NotBefore:O} {Info.NotAfter:O}";
    }

    private static Result Import(byte[] upload, string password)
    {
        try
        {
            using MemoryStream stream = new(upload);
            var info = Vault.Value.Import.ImportAsync(Id, stream, password.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return new Result(info, null);
        }
        catch (CertificateImportException ex)
        {
            return new Result(null, ex);
        }
    }

    private static void CheckSuccess(byte[] upload, CertificateInfo info)
    {
        Checks.That(info.Sha256Fingerprint.Length == 64 && info.Sha256Fingerprint.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')), $"'{info.Sha256Fingerprint}' is not a lower-case SHA-256");
        Checks.That(info.SubjectAlternativeNames.Count > 0, "an accepted certificate has no name");
        Checks.That(!info.PublicChainPem.Contains("PRIVATE", StringComparison.Ordinal), "the public chain holds a key block");
        Checks.That(info.PublicChainPem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal), "the public chain holds no certificate");
        Checks.That(info.ChainIssuers.Count <= 64, "a chain of more certificates than an upload may hold");

        // The leaf is one of the certificates of a PEM upload, found here without the vault's reading of it.
        if (Encoding.Latin1.GetString(upload).Contains("-----BEGIN ", StringComparison.Ordinal))
        {
            var found = new List<string>();
            var rest = upload.AsSpan();
            while (PemEncoding.TryFindUtf8(rest, out var fields))
            {
                if (Encoding.ASCII.GetString(rest[fields.Label]) == "CERTIFICATE")
                {
                    try
                    {
                        using var certificate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(Encoding.ASCII.GetString(rest[fields.Base64Data]).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("\t", string.Empty, StringComparison.Ordinal)));
                        found.Add(Convert.ToHexStringLower(certificate.GetCertHash(HashAlgorithmName.SHA256)));
                    }
                    catch (Exception ex) when (ex is CryptographicException or FormatException)
                    {
                        // A block the vault read and this reading can't, or the other way round: neither is a claim about the leaf.
                        return;
                    }
                }

                rest = rest[fields.Location.End..];
            }

            Checks.That(found.Contains(info.Sha256Fingerprint), $"the leaf {info.Sha256Fingerprint} is not among the {found.Count} certificates of the upload");
        }
    }

    /// <summary>The vault the imports run against: a scratch directory, the clock fixed, a key of its own. Removed when the process ends.</summary>
    private sealed class Scratch
    {
        private readonly string _directory = Directory.CreateTempSubdirectory("paduraksa-fuzz-vault-").FullName;

        public Scratch()
        {
            using var kek = Kek.Generate();
            var key = new byte[Kek.TextLength];
            kek.WriteEncoded(key);
            var variables = new Dictionary<string, string?> { [KekProvider.KeyVariable] = Encoding.ASCII.GetString(key) };
            ServiceCollection services = new();
            VaultServiceCollectionExtensions.AddVault(
                services,
                new VaultOptions(_directory, variables.GetValueOrDefault, new FixedTime(), VaultPolicy.Default, Paduraksa.Vault.Secrets.TerminalHandlers.CreateSockets),
                new Names());
            var provider = services.BuildServiceProvider();
            Import = provider.GetRequiredService<ICertificateImport>();
            // The provider is not disposed: at exit libFuzzer may be tearing the process down under it, and the directory is all there is to clean.
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                }
                catch (IOException)
                {
                    // A scratch directory left in the temp folder is all that costs.
                }
            };
        }

        public ICertificateImport Import { get; }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The inventory's rule in short: a DNS name or an IP address.</summary>
    private sealed class Names : ICertificateNameRules
    {
        public string? Normalize(string name) => string.IsNullOrWhiteSpace(name) ? null : name.ToLowerInvariant();
    }

    private static IEnumerable<(string Name, byte[] Bytes)> Seeds()
    {
        static byte[] Case(int password, byte[] upload) => [(byte)password, .. upload];

        static (X509Certificate2 Root, X509Certificate2 Leaf, AsymmetricAlgorithm Key) Make(int rsaBits, bool ca = false, params string[] names)
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Fuzz Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var rootSelf = rootRequest.CreateSelfSigned(Now.AddDays(-30), Now.AddYears(5));
            var root = X509CertificateLoader.LoadCertificate(rootSelf.RawData);

            AsymmetricAlgorithm key = rsaBits > 0 ? RSA.Create(rsaBits) : ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = rsaBits > 0
                ? new CertificateRequest($"CN={(names.Length > 0 ? names[0] : "no-names")}", (RSA)key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                : new CertificateRequest($"CN={(names.Length > 0 ? names[0] : "no-names")}", (ECDsa)key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var name in names)
            {
                if (IPAddress.TryParse(name, out var ip))
                {
                    san.AddIpAddress(ip);
                }
                else
                {
                    san.AddDnsName(name);
                }
            }

            if (names.Length > 0)
            {
                request.CertificateExtensions.Add(san.Build());
            }

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
            var serial = new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF };
            using var signed = request.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), Now.AddDays(-1), Now.AddDays(90), serial);
            return (root, X509CertificateLoader.LoadCertificate(signed.RawData), key);
        }

        var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 2048);

        var (root, ec, ecKey) = Make(0, false, "app.example.com", "www.example.com", "192.0.2.7");
        var ecKeyPem = ((ECDsa)ecKey).ExportPkcs8PrivateKeyPem();
        var rootPem = root.ExportCertificatePem();
        var ecPem = ec.ExportCertificatePem();
        static byte[] Pem(params string[] blocks) => Encoding.ASCII.GetBytes(string.Join('\n', blocks) + "\n");

        var encryptedKeyPem = ((ECDsa)ecKey).ExportEncryptedPkcs8PrivateKeyPem(Password, pbe);
        yield return ("ec-key-then-chain", Case(0, Pem(ecKeyPem, ecPem, rootPem)));
        yield return ("ec-chain-then-key", Case(0, Pem(ecPem, rootPem, ecKeyPem)));
        yield return ("ec-leaf-only", Case(0, Pem(ecKeyPem, ecPem)));
        yield return ("ec-no-key", Case(0, Pem(ecPem)));
        yield return ("ec-no-certificate", Case(0, Pem(ecKeyPem)));
        yield return ("ec-encrypted-key", Case(1, Pem(encryptedKeyPem, ecPem)));
        yield return ("ec-encrypted-key-wrong-password", Case(2, Pem(encryptedKeyPem, ecPem)));
        yield return ("ec-encrypted-key-no-password", Case(0, Pem(encryptedKeyPem, ecPem)));
        yield return ("ec-hostile-iterations", Case(1, Pem(((ECDsa)ecKey).ExportEncryptedPkcs8PrivateKeyPem(Password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 700_000)), ecPem)));
        yield return ("ec-traditional-key", Case(0, Pem(ecKeyPem.Replace("PRIVATE KEY", "EC PRIVATE KEY", StringComparison.Ordinal), ecPem)));
        yield return ("ec-two-keys", Case(0, Pem(ecKeyPem, ecKeyPem, ecPem)));
        yield return ("ec-with-text-around", Case(0, Pem("# my certificate", ecKeyPem, "Some notes between the blocks", ecPem)));
        yield return ("ec-bad-base64", Case(0, Pem(ecKeyPem, ecPem.Replace("MII", "M!I", StringComparison.Ordinal))));
        yield return ("ec-pfx", Case(1, ExportPfx(ec, ecKey, root)));
        yield return ("ec-pfx-wrong-password", Case(2, ExportPfx(ec, ecKey, root)));
        yield return ("ec-pfx-no-password", Case(0, ExportPfx(ec, ecKey, root)));

        var (_, rsaLeaf, rsaKey) = Make(2048, false, "rsa.example.com");
        yield return ("rsa-2048", Case(0, Pem(((RSA)rsaKey).ExportPkcs8PrivateKeyPem(), rsaLeaf.ExportCertificatePem())));
        var (_, weak, weakKey) = Make(1024, false, "weak.example.com");
        yield return ("rsa-1024-weak", Case(0, Pem(((RSA)weakKey).ExportPkcs8PrivateKeyPem(), weak.ExportCertificatePem())));
        var (_, authority, authorityKey) = Make(0, true, "ca.example.com");
        yield return ("ca-certificate", Case(0, Pem(((ECDsa)authorityKey).ExportPkcs8PrivateKeyPem(), authority.ExportCertificatePem())));
        var (_, nameless, namelessKey) = Make(0, false);
        yield return ("no-names", Case(0, Pem(((ECDsa)namelessKey).ExportPkcs8PrivateKeyPem(), nameless.ExportCertificatePem())));
        var (_, other, _) = Make(0, false, "other.example.com");
        yield return ("key-and-certificate-mismatch", Case(0, Pem(ecKeyPem, other.ExportCertificatePem())));
        yield return ("not-a-certificate", Case(0, "-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"u8.ToArray()));
        yield return ("garbage", Case(0, "this is not a certificate\n"u8.ToArray()));
        yield return ("nul-bytes", Case(0, new byte[64]));
        yield return ("empty-upload", [0]);

        static byte[] ExportPfx(X509Certificate2 leaf, AsymmetricAlgorithm key, X509Certificate2 root)
        {
            using var withKey = leaf.CopyWithPrivateKey((ECDsa)key);
            X509Certificate2Collection collection = [withKey, root];
            return collection.Export(X509ContentType.Pkcs12, Password) ?? [];
        }
    }
}
