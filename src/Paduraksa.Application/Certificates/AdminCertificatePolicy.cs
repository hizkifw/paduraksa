using Paduraksa.Contracts.Certificates;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Certificates;

/// <summary>
/// When the process-managed admin certificate must be re-issued: its names changed, or it is near expiry.
/// Pure arithmetic over the inventory's public facts, so it is decided from what is stored and unit-testable with a fake clock.
/// </summary>
public static class AdminCertificatePolicy
{
    /// <summary>
    /// Re-issue <paramref name="current"/> when the set of names it covers is no longer exactly <paramref name="desiredNames"/>
    /// (order-insensitive), or when a third or less of its lifetime is left (<c>NotAfter</c> - (lifetime ÷ 3) reached). An
    /// already expired certificate always qualifies. A certificate not yet valid does not.
    /// </summary>
    public static bool ShouldReissue(CertificateSummary current, IReadOnlyList<string> desiredNames, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desiredNames);
        if (!SameNames(current.Names, desiredNames))
        {
            return true;
        }

        TimeSpan lifetime = current.NotAfter - current.NotBefore;
        return now >= current.NotAfter - (lifetime / 3);
    }

    /// <summary>
    /// Whether a leaf the vault holds can be served at <paramref name="now"/>: not expired, and not starting so far in the
    /// future that a handshake would fail. Used where the certificate is served without the inventory, so a
    /// failed inventory step never discards a certificate the vault already holds.
    /// </summary>
    public static bool IsServable(CertificateInfo info, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(info);
        // The inventory's own tolerance for clock skew, so both judge "valid yet" the same way.
        return now < info.NotAfter && now >= info.NotBefore - CertificateInventoryLoader.NotBeforeTolerance;
    }

    /// <summary>
    /// The two name sets are equal, ignoring order and case (both are normalised, so case only guards a surprise). The
    /// admin flow uses it to decide whether a leaf the vault holds is exactly the one it would ask for.
    /// </summary>
    public static bool SameNames(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        if (left.Count != right.Count)
        {
            return false;
        }

        List<string> a = [.. left.Order(StringComparer.OrdinalIgnoreCase)];
        List<string> b = [.. right.Order(StringComparer.OrdinalIgnoreCase)];
        return a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase);
    }
}
