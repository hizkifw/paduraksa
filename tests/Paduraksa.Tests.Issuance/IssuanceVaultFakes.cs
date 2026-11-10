using System.Security.Cryptography;
using System.Text;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Tests.Issuance;

/// <summary>
/// An <see cref="ICertificateIssuance"/> that stages a "key" (only a token here) and answers the chain with the facts a
/// vault would report, dated by the test clock. It models the vault's staging rules: attaching needs the current token for
/// the ID, and a spent token is refused.
/// </summary>
internal sealed class FakeCertificateIssuance(TimeProvider time) : ICertificateIssuance, ICertificateCatalog
{
    private readonly Lock _gate = new();
    private readonly Dictionary<CertificateId, (StagedKeyId Key, IReadOnlyList<string> Names)> _staged = [];
    private readonly Dictionary<CertificateId, CertificateInfo> _held = [];
    private readonly HashSet<CertificateId> _roots = [];
    private int _serial = 0x1000;

    public List<(CertificateId Id, KeySpec Spec, IReadOnlyList<string> Names)> Csrs { get; } = [];

    public List<(CertificateId Id, string Chain)> Attached { get; } = [];

    /// <summary>What <see cref="AttachChainAsync"/> reported, in order.</summary>
    public List<CertificateInfo> Issued { get; } = [];

    /// <summary>How long a certificate this fake "attaches" is valid, from the test clock's now.</summary>
    public TimeSpan Validity { get; set; } = TimeSpan.FromDays(90);

    public string AuthorityKeyIdentifier { get; set; } = "abcd1234";

    /// <summary>Runs right after an attach succeeded (to cancel the caller at the moment the vault holds the new chain).</summary>
    public Action? AfterAttach { get; set; }

    /// <summary>Thrown by the next <see cref="AttachChainAsync"/> instead of attaching.</summary>
    public Exception? AttachFailure { get; set; }

    public StagedKeyId? StagedFor(CertificateId id)
    {
        lock (_gate)
        {
            return _staged.TryGetValue(id, out var entry) ? entry.Key : null;
        }
    }

    /// <summary>The ARI certificate ID of what <see cref="AttachChainAsync"/> reported.</summary>
    public static string AriId(CertificateInfo info) => Paduraksa.Acme.AcmeRenewalInfoId.Create(info);

    public ValueTask<CsrResult> CreateKeyAndCsrAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            var key = StagedKeyId.New();
            _staged[id] = (key, names);
            Csrs.Add((id, spec, names));
            byte[] der = [0x30, 0x03, 0x02, 0x01, (byte)Csrs.Count];
            return ValueTask.FromResult(new CsrResult(key, der, "-----BEGIN CERTIFICATE REQUEST-----\nMAMCAQE=\n-----END CERTIFICATE REQUEST-----\n"));
        }
    }

    public ValueTask<CertificateInfo> AttachChainAsync(CertificateId id, StagedKeyId stagedKey, ReadOnlyMemory<byte> pemChain, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (AttachFailure is { } failure)
            {
                AttachFailure = null;
                throw failure;
            }

            if (!_staged.TryGetValue(id, out var entry) || entry.Key != stagedKey)
            {
                throw new StagedKeyMismatchException("The staged key is not the current one.");
            }

            _staged.Remove(id);
            var chain = Encoding.ASCII.GetString(pemChain.Span);
            Attached.Add((id, chain));
            var now = time.GetUtcNow();
            var serial = (_serial++).ToString("X4", System.Globalization.CultureInfo.InvariantCulture);
            CertificateInfo info = new(
                $"CN={entry.Names[0]}",
                entry.Names,
                now.AddMinutes(-1),
                now + Validity,
                "CN=E5",
                serial,
                AuthorityKeyIdentifier,
                Convert.ToHexStringLower(SHA256.HashData(Paduraksa.Acme.AcmePem.LeafDer(chain))),
                "ECDSA P-256",
                ["CN=E5", "CN=ISRG Root X2"],
                chain);
            Issued.Add(info);
            _held[id] = info;
            AfterAttach?.Invoke();
            return ValueTask.FromResult(info);
        }
    }

    /// <summary>The IDs <see cref="DeleteAsync"/> was asked to delete, in order.</summary>
    public List<CertificateId> Deleted { get; } = [];

    /// <summary>Makes the vault hold <paramref name="info"/> under <paramref name="id"/>, as if another chain had been attached (or, with <paramref name="isLocalCaRoot"/>, the local CA root created).</summary>
    public void Hold(CertificateId id, CertificateInfo info, bool isLocalCaRoot = false)
    {
        lock (_gate)
        {
            _held[id] = info;
            if (isLocalCaRoot)
            {
                _roots.Add(id);
            }
        }
    }

    public ValueTask<IReadOnlyList<VaultCertificate>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult<IReadOnlyList<VaultCertificate>>([.. _held.Select(h => new VaultCertificate(h.Key, _roots.Contains(h.Key), h.Value))]);
        }
    }

    public ValueTask<VaultCertificate?> DescribeAsync(CertificateId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_held.TryGetValue(id, out var info) ? new VaultCertificate(id, _roots.Contains(id), info) : null);
        }
    }

    public ValueTask<CertificateInfo> CreateSelfSignedAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<CertificateInfo> EnsureLocalCaAsync(CertificateId id, KeySpec spec, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<CertificateInfo> IssueFromLocalCaAsync(CertificateId id, KeySpec spec, IReadOnlyList<string> names, TimeSpan validity, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<bool> DeleteAsync(CertificateId id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Deleted.Add(id);
            _staged.Remove(id);
            _roots.Remove(id);
            return ValueTask.FromResult(_held.Remove(id));
        }
    }
}
