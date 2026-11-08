using System.Collections.Frozen;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>Which of the host table's name tables a <c>Host</c> header matched.</summary>
public enum HostMatchKind : byte
{
    /// <summary>No host takes the name.</summary>
    None = 0,

    /// <summary>An exact name.</summary>
    Exact,

    /// <summary>A single-label wildcard (<c>*.example.com</c>).</summary>
    Wildcard,

    /// <summary>An IPv6 literal (<c>[2001:db8::1]</c>).</summary>
    IPv6Literal,
}

/// <summary>
/// Host name → <see cref="CompiledHost"/>, looked up from the raw <c>Host</c> header span without allocating
/// (spike S3 §1, PLAN §7.1). Exact names first, then a single-label wildcard: <c>*.example.com</c> matches
/// <c>a.example.com</c> but neither <c>example.com</c> nor <c>a.b.example.com</c> (the same rule as TLS wildcards).
/// Both tables are <see cref="FrozenDictionary{TKey, TValue}"/>s with <see cref="StringComparer.OrdinalIgnoreCase"/>
/// and a cached <see cref="ReadOnlySpan{T}"/> alternate lookup. IPv6 literals sit in a third table keyed by address.
/// </summary>
public sealed class HostTable
{
    private readonly FrozenDictionary<string, CompiledHost> _exact;
    private readonly FrozenDictionary<string, CompiledHost> _wildcard;
    private readonly FrozenDictionary<UInt128, CompiledHost> _ipv6Literals;
    private readonly FrozenDictionary<string, CompiledHost>.AlternateLookup<ReadOnlySpan<char>> _exactBySpan;
    private readonly FrozenDictionary<string, CompiledHost>.AlternateLookup<ReadOnlySpan<char>> _wildcardBySpan;

    private HostTable(Dictionary<string, CompiledHost> exact, Dictionary<string, CompiledHost> wildcard, Dictionary<UInt128, CompiledHost> ipv6Literals)
    {
        _exact = exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _wildcard = wildcard.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _ipv6Literals = ipv6Literals.ToFrozenDictionary();
        _exactBySpan = _exact.GetAlternateLookup<ReadOnlySpan<char>>();
        _wildcardBySpan = _wildcard.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static HostTable Empty { get; } = new Builder().Build();

    /// <summary>Number of names (exact + wildcard + IPv6 literal).</summary>
    public int Count => _exact.Count + _wildcard.Count + _ipv6Literals.Count;

    /// <summary>
    /// Resolves a raw <c>Host</c> header value: any case, with or without a port, a trailing dot, or an IPv6 literal.
    /// 0 B, except for IPv6-literal hosts (see <see cref="HostNames.TryParseIPv6Literal"/>).
    /// </summary>
    public CompiledHost? Find(ReadOnlySpan<char> hostHeader) => Find(hostHeader, out _);

    /// <summary>The same lookup, also saying which table matched (for the request simulator's trace).</summary>
    public CompiledHost? Find(ReadOnlySpan<char> hostHeader, out HostMatchKind kind)
    {
        kind = HostMatchKind.None;
        ReadOnlySpan<char> host = HostName(hostHeader);
        if (host.IsEmpty)
        {
            return null;
        }

        if (host[0] == '[')
        {
            if (_ipv6Literals.Count > 0 && host.Length > 2 && host[^1] == ']'
                && HostNames.TryParseIPv6Literal(host[1..^1], out UInt128 key)
                && _ipv6Literals.TryGetValue(key, out CompiledHost? literal))
            {
                kind = HostMatchKind.IPv6Literal;
                return literal;
            }

            return null;
        }

        if (_exactBySpan.TryGetValue(host, out CompiledHost? exact))
        {
            kind = HostMatchKind.Exact;
            return exact;
        }

        if (_wildcard.Count > 0)
        {
            // Wildcard parent: everything after the first dot, so exactly one label is covered. The covered label must
            // be a plain ASCII label too, which keeps odd Host values out of redirect Locations. (CertificateTable.Find
            // can't check that: SslStream hands the TCP callback IDN labels decoded to Unicode.)
            int dot = host.IndexOf('.');
            if (dot > 0 && !host[..dot].ContainsAnyExcept(DnsNames.LabelChars)
                && _wildcardBySpan.TryGetValue(host[(dot + 1)..], out CompiledHost? wildcard))
            {
                kind = HostMatchKind.Wildcard;
                return wildcard;
            }
        }

        return null;
    }

    /// <summary>
    /// The host part of a <c>Host</c> header: <c>[::1]:80</c> → <c>[::1]</c>, <c>a.test.:8080</c> → <c>a.test</c>.
    /// Strips the port and one trailing dot; an IPv6 literal is kept whole up to its <c>]</c>.
    /// </summary>
    public static ReadOnlySpan<char> HostName(ReadOnlySpan<char> hostHeader)
    {
        ReadOnlySpan<char> host = hostHeader;
        if (!host.IsEmpty && host[0] == '[')
        {
            int close = host.IndexOf(']');
            return close < 0 ? host : host[..(close + 1)];
        }

        int colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            host = host[..colon];
        }

        if (!host.IsEmpty && host[^1] == '.')
        {
            host = host[..^1];
        }

        return host;
    }

    /// <summary>
    /// Collects names for a new table. Rejects invalid names and duplicates, including names that only collide after
    /// normalisation (case, trailing dot, punycode, IPv6 spelling); a silent last-wins would hide a config error.
    /// </summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, CompiledHost> _exact = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CompiledHost> _wildcard = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<UInt128, CompiledHost> _ipv6Literals = [];

        /// <summary>Adds <paramref name="name"/>; returns <see langword="false"/> with a reason if it's invalid or taken.</summary>
        public bool TryAdd(string name, CompiledHost host, out string? error)
        {
            ArgumentNullException.ThrowIfNull(host);
            if (!HostNames.TryNormalize(name, out string normalized, out HostNameKind kind, out error))
            {
                return false;
            }

            CompiledHost? existing;
            bool added;
            switch (kind)
            {
                case HostNameKind.IPv6Literal:
                    _ = HostNames.TryParseIPv6Literal(normalized.AsSpan(1, normalized.Length - 2), out UInt128 key);
                    added = _ipv6Literals.TryAdd(key, host);
                    existing = added ? null : _ipv6Literals[key];
                    break;
                case HostNameKind.Wildcard:
                    added = _wildcard.TryAdd(normalized[2..], host);
                    existing = added ? null : _wildcard[normalized[2..]];
                    break;
                default:
                    added = _exact.TryAdd(normalized, host);
                    existing = added ? null : _exact[normalized];
                    break;
            }

            if (!added)
            {
                error = $"duplicate host name '{normalized}' (hosts '{existing!.Id}' and '{host.Id}')";
            }

            return added;
        }

        public HostTable Build() => new(_exact, _wildcard, _ipv6Literals);
    }
}
