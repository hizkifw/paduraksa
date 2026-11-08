using Paduraksa.Core.Model;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// The admin listener's own certificate (PLAN §3, §10.1): one leaf the process issues from the local CA and keeps current,
/// so the admin UI is trusted by a browser that trusts the root once.
/// </summary>
/// <remarks>
/// <para>
/// <b>A stable ID, not a new source.</b> <see cref="Id"/> is the same in every process, so the inventory row, the vault key
/// and the served certificate always name one another across restarts, and a database loss is repaired by adopting the key
/// the vault still holds under that ID (never by making a new one, which would change the fingerprint the owner trusted). The
/// source stays <see cref="CertificateSource.LocalCa"/>: adding a source value would mean rebuilding the <c>Certificates</c>
/// table's <c>CHECK</c> constraint for no behavioural gain. What tells the certificate apart is the stable ID and
/// <see cref="Label"/>.
/// </para>
/// <para>
/// <b>Reserved for the admin port.</b> Deleting it (<c>certificate.managed</c>) and re-issuing it by hand are both refused, and
/// the config can neither bind it to a host nor name it as the default certificate: the process owns its lifecycle, and the
/// proxy never serves it. <see cref="CertificateOperations.EnsureAdminCertificateAsync"/> is what creates, adopts and renews
/// it.
/// </para>
/// <para>
/// Like the placeholder's well-known ID (PLAN §9.2), this is a fixed value a random <c>CertificateId.New()</c> practically
/// never reproduces, so no upload, issue or ACME order collides with it.
/// </para>
/// </remarks>
public static class AdminCertificate
{
    /// <summary>The ID the admin certificate is registered and issued under, stable across processes.</summary>
    public static CertificateId Id { get; } = CertificateId.FromValue(new Guid("0190a000-0000-7000-8000-61646d696e01"));

    /// <summary>The display label the inventory shows for it; the process keeps it set.</summary>
    public const string Label = "Admin (internal CA)";
}
