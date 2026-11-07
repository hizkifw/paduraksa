using System.Collections.Frozen;
using System.Net.Security;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// SNI → prebuilt TLS options, immutable, built once per snapshot (PLAN §6.2, §7.2 B).
/// Lookup order: exact name, then the wildcard parent (<c>*.example.com</c> covers exactly one extra label), then the
/// default entry; no default means "reject". Case-insensitive, trailing dot ignored, 0 B per lookup.
/// </summary>
public sealed class CertificateTable
{
    private readonly FrozenDictionary<string, CertificateTableEntry>.AlternateLookup<ReadOnlySpan<char>> _exact;
    private readonly FrozenDictionary<string, CertificateTableEntry>.AlternateLookup<ReadOnlySpan<char>> _wildcard;
    private readonly bool _hasWildcards;

    internal CertificateTable(
        FrozenDictionary<string, CertificateTableEntry> exact,
        FrozenDictionary<string, CertificateTableEntry> wildcard,
        string[] exactNames,
        string[] wildcardParents,
        CertificateTableEntry? defaultEntry,
        CertificateTableEntry[] entries,
        SslServerAuthenticationOptions[] options)
    {
        _exact = exact.GetAlternateLookup<ReadOnlySpan<char>>();
        _wildcard = wildcard.GetAlternateLookup<ReadOnlySpan<char>>();
        _hasWildcards = wildcard.Count > 0;
        ExactNames = exactNames;
        WildcardParents = wildcardParents;
        Default = defaultEntry;
        Entries = entries;
        Options = options;
    }

    /// <summary>A table with no names and no default: every handshake is rejected.</summary>
    public static CertificateTable Empty { get; } = new CertificateTableBuilder().Build();

    /// <summary>Used for unknown or absent SNI; null means such handshakes are rejected.</summary>
    public CertificateTableEntry? Default { get; }

    /// <summary>Canonical exact names as configured (lowercase, no trailing dot, punycode for IDNs).</summary>
    public IReadOnlyList<string> ExactNames { get; }

    /// <summary>Wildcard parents: <c>example.com</c> for <c>*.example.com</c>.</summary>
    public IReadOnlyList<string> WildcardParents { get; }

    /// <summary>Distinct entries, one per (certificate, policy), default included.</summary>
    public IReadOnlyList<CertificateTableEntry> Entries { get; }

    /// <summary>Every distinct prebuilt options object (TCP and QUIC). None has a null or empty ALPN list.</summary>
    public IReadOnlyList<SslServerAuthenticationOptions> Options { get; }

    /// <summary>
    /// Finds the entry for a client's SNI: exact, then wildcard parent, then <see cref="Default"/> (may be null).
    /// An empty name (no SNI, e.g. an IP-literal URL) goes straight to the default.
    /// </summary>
    public CertificateTableEntry? Find(ReadOnlySpan<char> serverName)
    {
        var name = ServerNames.TrimTrailingDot(serverName);
        if (name.IsEmpty)
        {
            return Default;
        }

        if (_exact.TryGetValue(name, out var entry))
        {
            return entry;
        }

        if (_hasWildcards)
        {
            // Wildcard parent: everything after the first dot, so exactly one label is covered. Unlike HostTable.Find,
            // the covered label isn't checked against DnsNames.LabelChars: SslStream hands the TCP callback IDN labels
            // decoded to Unicode (x.bücher.example), and nothing here ends up in a response.
            var dot = name.IndexOf('.');
            if (dot > 0 && _wildcard.TryGetValue(name[(dot + 1)..], out entry))
            {
                return entry;
            }
        }

        return Default;
    }
}

/// <summary>
/// One (certificate, policy) pair with its prebuilt options. Shared by every name (and host) that maps to the same
/// pair, so a wildcard or default certificate used by many hosts with one policy is a single options object per
/// transport.
/// </summary>
public sealed class CertificateTableEntry
{
    internal CertificateTableEntry(
        ServerCertificate certificate,
        TlsPolicy policy,
        SslServerAuthenticationOptions tcpOptions,
        SslServerAuthenticationOptions? quicOptions)
    {
        Certificate = certificate;
        Policy = policy;
        TcpOptions = tcpOptions;
        QuicOptions = quicOptions;
    }

    public ServerCertificate Certificate { get; }

    public TlsPolicy Policy { get; }

    /// <summary>TCP (H1/H2) options. ALPN: <c>[h2, http/1.1]</c> or <c>[http/1.1]</c>.</summary>
    public SslServerAuthenticationOptions TcpOptions { get; }

    /// <summary>QUIC (H3) options with ALPN <c>[h3]</c>, or null when the host opted out of H3.</summary>
    public SslServerAuthenticationOptions? QuicOptions { get; }
}
