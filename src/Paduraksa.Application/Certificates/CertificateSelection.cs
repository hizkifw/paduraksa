using System.Net;

using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Certificates;

/// <summary>How well a certificate's names cover one host name.</summary>
internal enum NameMatch
{
    /// <summary>Not covered.</summary>
    None = 0,

    /// <summary>Covered by a <c>*.parent</c> wildcard name.</summary>
    Wildcard = 1,

    /// <summary>Covered by exactly that name (a wildcard host name by the same wildcard).</summary>
    Exact = 2,
}

/// <summary>
/// Which certificate serves which name, as pure functions over normalised names.
/// </summary>
/// <remarks>
/// <para><b>Covering.</b> A certificate covers a host name when one of its names is that name, or is the wildcard of its
/// parent: <c>*.example.com</c> covers <c>www.example.com</c> but not <c>example.com</c> or <c>a.b.example.com</c>, like a
/// TLS wildcard. A wildcard host name (<c>*.example.com</c>) is covered by the same wildcard name only. A name
/// covered exactly beats one covered by a wildcard.</para>
/// <para><b>Choosing.</b> Among the candidates that cover a name, the best is the one with the best match, then the latest
/// <c>NotAfter</c>, then the latest <c>NotBefore</c>, then the smallest ID, so the choice is deterministic and a renewed
/// certificate (later <c>NotAfter</c>) takes over from the one it replaces.</para>
/// <para><b>Per name, not per host.</b> The proxy's certificate table maps names to certificates, so each name of a
/// multi-name host gets its own best certificate: <c>example.com</c> from an exact certificate, <c>www.example.com</c>
/// from a wildcard, with no need for one certificate covering all of them. A name nothing covers gets none of its own.</para>
/// </remarks>
internal static class CertificateSelection
{
    /// <summary>How well <paramref name="certificateNames"/> cover <paramref name="hostName"/> (both normalised).</summary>
    public static NameMatch Match(IReadOnlyList<string> certificateNames, string hostName)
    {
        NameMatch best = NameMatch.None;
        bool hostIsWildcard = hostName.StartsWith("*.", StringComparison.Ordinal);
        int firstDot = hostName.IndexOf('.', StringComparison.Ordinal);
        foreach (string name in certificateNames)
        {
            if (string.Equals(name, hostName, StringComparison.Ordinal))
            {
                return NameMatch.Exact;
            }

            if (!hostIsWildcard && firstDot > 0 && name.StartsWith("*.", StringComparison.Ordinal)
                && name.AsSpan(2).SequenceEqual(hostName.AsSpan(firstDot + 1)))
            {
                best = NameMatch.Wildcard;
            }
        }

        return best;
    }

    /// <summary>The best of <paramref name="candidates"/> covering <paramref name="hostName"/>, or null.</summary>
    public static InventoryCertificate? Best(IEnumerable<InventoryCertificate> candidates, string hostName)
    {
        InventoryCertificate? best = null;
        NameMatch bestMatch = NameMatch.None;
        foreach (InventoryCertificate candidate in candidates)
        {
            NameMatch match = Match(candidate.Names, hostName);
            if (match != NameMatch.None && (best is null || IsBetter(candidate, match, best, bestMatch)))
            {
                best = candidate;
                bestMatch = match;
            }
        }

        return best;
    }

    /// <summary>
    /// A host's DNS names, normalised: the names that can arrive as SNI and so the ones certificates are chosen for.
    /// IP literals and invalid names are left out (clients send no SNI for an IP, and the validator rejects the rest).
    /// </summary>
    public static IReadOnlyList<string> ServerNames(IReadOnlyList<string> hostNames)
    {
        List<string> names = new(hostNames.Count);
        foreach (string name in hostNames)
        {
            if (HostNames.TryNormalize(name, out string normalized, out HostNameKind kind, out _)
                && kind != HostNameKind.IPv6Literal
                && !(kind == HostNameKind.Exact && IPAddress.TryParse(normalized, out _))
                && !names.Contains(normalized))
            {
                names.Add(normalized);
            }
        }

        return names;
    }

    /// <summary>
    /// The certificates the config can choose among <paramref name="valid"/> ones: the named default, a Specific
    /// binding's, and the best for each name of every enabled Auto host (the compiler picks from exactly these, so it is
    /// all the loader has to load).
    /// </summary>
    public static HashSet<CertificateId> Needed(ConfigDocument document, IReadOnlyList<InventoryCertificate> valid, CertificateId? fallbackDefault)
    {
        HashSet<CertificateId> ids = [];
        if (document.Settings.DefaultCertificateId is { } defaultId && valid.Any(c => c.Id == defaultId))
        {
            ids.Add(defaultId);
        }
        else if (fallbackDefault is { } fallback && valid.Any(c => c.Id == fallback))
        {
            ids.Add(fallback);
        }

        foreach (HostConfig host in document.Hosts)
        {
            if (!host.Enabled)
            {
                continue;
            }

            CertificateBinding binding = host.Certificate;
            if (binding.Mode == CertificateBindingMode.Specific && binding.CertificateId is { } specific)
            {
                if (valid.Any(c => c.Id == specific))
                {
                    ids.Add(specific);
                }
            }
            else if (binding.Mode == CertificateBindingMode.Auto)
            {
                foreach (string name in ServerNames(host.Names))
                {
                    if (Best(valid, name) is { } best)
                    {
                        ids.Add(best.Id);
                    }
                }
            }
        }

        return ids;
    }

    private static bool IsBetter(InventoryCertificate candidate, NameMatch match, InventoryCertificate best, NameMatch bestMatch)
    {
        if (match != bestMatch)
        {
            return match > bestMatch;
        }

        int byNotAfter = candidate.NotAfter.CompareTo(best.NotAfter);
        if (byNotAfter != 0)
        {
            return byNotAfter > 0;
        }

        int byNotBefore = candidate.NotBefore.CompareTo(best.NotBefore);
        return byNotBefore != 0 ? byNotBefore > 0 : candidate.Id.CompareTo(best.Id) < 0;
    }
}
