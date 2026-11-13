namespace Paduraksa.Core.Vault;

/// <summary>
/// The names a local CA root may issue for, written into the root as an X.509 name-constraints extension (PLAN §9.2 "As built:
/// local CA name constraints"): a client that trusts the root accepts a certificate from it only for these names, so a leaked
/// root key can't be used to impersonate a public site to a device that trusts it.
/// </summary>
/// <param name="Domains">DNS domains: a name is permitted when it is one of them or under one (<c>home.arpa</c> permits
/// <c>nas.home.arpa</c>), lowercase, without a leading dot or wildcard.</param>
/// <param name="Addresses">IP networks as CIDR text (<c>192.168.0.0/16</c>); a single address is <c>/32</c> or <c>/128</c>.</param>
public sealed record LocalCaConstraints(IReadOnlyList<string> Domains, IReadOnlyList<string> Addresses)
{
    /// <summary>The most entries in each list.</summary>
    public const int MaxEntries = 100;

    /// <summary>Domains reserved for private use, so a LAN's own names (<c>nas.lan</c>, <c>printer.home.arpa</c>) are covered with nothing configured.</summary>
    public static readonly IReadOnlyList<string> DefaultDomains =
        ["home.arpa", "lan", "local", "localdomain", "localhost", "internal", "home", "corp", "intranet", "private", "test"];

    /// <summary>Loopback, link-local, unique-local and private IPv4 ranges (RFC 1918, and 100.64.0.0/10 for CGNAT and Tailscale).</summary>
    public static readonly IReadOnlyList<string> PrivateRanges =
        ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "169.254.0.0/16", "127.0.0.0/8", "fc00::/7", "fe80::/10", "::1/128"];

    /// <summary>The private-use domains and the private ranges.</summary>
    public static LocalCaConstraints Default { get; } = new(DefaultDomains, PrivateRanges);

    /// <summary>The defaults plus these domains and addresses (an address without a prefix is one host).</summary>
    public static LocalCaConstraints Extend(IReadOnlyList<string>? domains, IReadOnlyList<string>? addresses = null) => new(
        [.. DefaultDomains.Concat(domains ?? []).Distinct(StringComparer.Ordinal)],
        [.. PrivateRanges.Concat(addresses ?? []).Distinct(StringComparer.Ordinal)]);
}

/// <summary>A local CA's name constraints don't permit a name it was asked to issue for.</summary>
public sealed class LocalCaNameNotPermittedException(string name, string permitted)
    : InvalidOperationException($"'{name}' is outside the names the local CA may issue for ({permitted}). Rotate the local CA with this name added.")
{
    /// <summary>The refused name.</summary>
    public string Name { get; } = name;
}

/// <summary>What <see cref="ICertificateIssuance.RotateLocalCaAsync"/> did.</summary>
/// <param name="Root">The new root, which signs from now on.</param>
/// <param name="RetiredId">The ID of the root it replaced.</param>
/// <param name="Retired">The replaced root's public facts. Its key is gone from the vault; what it signed stays valid until it expires.</param>
public sealed record LocalCaRotation(CertificateInfo Root, Paduraksa.Core.Model.CertificateId RetiredId, CertificateInfo Retired);
