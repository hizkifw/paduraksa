using System.Formats.Asn1;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// A stand-in for the vault's <see cref="ICertificateIssuance"/> that makes real CSRs (a P-256 key per staging, held in the test
/// process) and reads the issued chain the way the vault will, so a real CA such as Pebble accepts the finalize and the
/// metadata (serial content bytes, authority key identifier) is what the ARI certificate ID needs. Only for tests: the real
/// vault arrives in its own branch.
/// </summary>
internal sealed class RealCsrIssuance : ICertificateIssuance, IDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<CertificateId, (StagedKeyId Token, ECDsa Key)> _staged = [];

    public void Dispose()
    {
        foreach (var (_, key) in _staged.Values)
        {
            key.Dispose();
        }
    }

    public ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subject = names[0].Length <= 64 && !names[0].StartsWith('*') && !IPAddress.TryParse(names[0], out _) ? $"CN={names[0]}" : string.Empty;
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256);
        SubjectAlternativeNameBuilder san = new();
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

        request.CertificateExtensions.Add(san.Build());
        var der = request.CreateSigningRequest();
        var token = StagedKeyId.New();
        lock (_gate)
        {
            if (_staged.Remove(id, out var previous))
            {
                previous.Key.Dispose();
            }

            _staged[id] = (token, key);
        }

        return ValueTask.FromResult(new CsrResult(token, der, new string(PemEncoding.Write("CERTIFICATE REQUEST", der))));
    }

    public ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken)
    {
        ECDsa key;
        lock (_gate)
        {
            if (!_staged.TryGetValue(id, out var entry) || entry.Token != stagedKey)
            {
                throw new StagedKeyMismatchException("The staged key is not the current one.");
            }

            key = entry.Key;
        }

        var pem = Encoding.ASCII.GetString(pemChain.Span);
        List<X509Certificate2> chain = [];
        for (ReadOnlySpan<char> rest = pem; PemEncoding.TryFind(rest, out var fields); rest = rest[fields.Location.End..])
        {
            chain.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(new string(rest[fields.Base64Data]))));
        }

        var leaf = chain[0];
        if (!leaf.GetECDsaPublicKey()!.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(key.ExportSubjectPublicKeyInfo()))
        {
            throw new CryptographicException("The chain does not match the staged key.");
        }

        lock (_gate)
        {
            _staged.Remove(id);
        }

        string? aki = null;
        List<string> names = [];
        foreach (var extension in leaf.Extensions)
        {
            switch (extension.Oid?.Value)
            {
                case "2.5.29.35":
                    aki = Convert.ToHexStringLower(new X509AuthorityKeyIdentifierExtension(extension.RawData, extension.Critical).KeyIdentifier!.Value.Span);
                    break;
                case "2.5.29.17":
                    X509SubjectAlternativeNameExtension sans = new(extension.RawData, extension.Critical);
                    names.AddRange(sans.EnumerateDnsNames());
                    names.AddRange(sans.EnumerateIPAddresses().Select(a => a.ToString()));
                    break;
            }
        }

        return ValueTask.FromResult(new CertificateInfo(
            leaf.Subject,
            names,
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime()),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime()),
            leaf.Issuer,
            Convert.ToHexString(SerialContent(leaf)),
            aki,
            Convert.ToHexStringLower(leaf.GetCertHash(HashAlgorithmName.SHA256)),
            "ECDSA P-256",
            [.. chain.Select(c => c.Issuer)],
            pem));
    }

    public ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken) => throw new NotSupportedException();

    /// <summary>The serial number as the DER INTEGER's content bytes (a leading 00 kept), which is what ARI names a certificate by.</summary>
    private static byte[] SerialContent(X509Certificate2 certificate)
    {
        var tbs = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence().ReadSequence();
        if (tbs.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
        {
            _ = tbs.ReadEncodedValue();
        }

        return tbs.ReadIntegerBytes().ToArray();
    }
}
