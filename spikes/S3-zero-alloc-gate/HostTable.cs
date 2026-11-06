using System.Collections.Frozen;
using System.Globalization;

namespace S3;

/// <summary>
/// Host name → value, case-insensitive, looked up from the raw Host header span without allocating.
/// Exact names first, then a single-label wildcard (<c>*.parent</c> matches <c>x.parent</c>, not <c>a.b.parent</c>,
/// like TLS wildcards). Keys are stored as ASCII (IDN names converted to punycode at compile time).
/// </summary>
public sealed class HostTable<T> where T : class
{
    private readonly FrozenDictionary<string, T> _exact;
    private readonly FrozenDictionary<string, T> _wild;
    private readonly FrozenDictionary<string, T>.AlternateLookup<ReadOnlySpan<char>> _exactBySpan;
    private readonly FrozenDictionary<string, T>.AlternateLookup<ReadOnlySpan<char>> _wildBySpan;
    private readonly bool _hasWild;

    private static readonly IdnMapping Idn = new();

    public HostTable(IEnumerable<KeyValuePair<string, T>> entries)
    {
        var exact = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var wild = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in entries)
        {
            // Compile-time normalisation: IDN → punycode, trailing dot dropped. Runtime never converts.
            var n = name.TrimEnd('.');
            if (n.StartsWith("*.", StringComparison.Ordinal)) wild[Idn.GetAscii(n[2..])] = value;
            else exact[Idn.GetAscii(n)] = value;
        }
        _exact = exact.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _wild = wild.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _exactBySpan = _exact.GetAlternateLookup<ReadOnlySpan<char>>();
        _wildBySpan = _wild.GetAlternateLookup<ReadOnlySpan<char>>();
        _hasWild = _wild.Count > 0;
    }

    public int Count => _exact.Count + _wild.Count;

    /// <summary>Raw Host header value (may carry a port, an IPv6 literal, a trailing dot, any case).</summary>
    public T? Find(ReadOnlySpan<char> hostHeader)
    {
        var host = HostName(hostHeader);
        if (_exactBySpan.TryGetValue(host, out var v)) return v;
        if (_hasWild)
        {
            var dot = host.IndexOf('.');
            if (dot > 0 && _wildBySpan.TryGetValue(host[(dot + 1)..], out v)) return v;
        }
        return null;
    }

    /// <summary>Strips the port and a trailing dot. "[::1]:80" → "[::1]", "a.test.:8080" → "a.test".</summary>
    public static ReadOnlySpan<char> HostName(ReadOnlySpan<char> h)
    {
        if (h.Length > 0 && h[0] == '[')
        {
            var close = h.IndexOf(']');
            return close < 0 ? h : h[..(close + 1)];
        }
        var colon = h.LastIndexOf(':');
        if (colon >= 0) h = h[..colon];
        if (h.Length > 0 && h[^1] == '.') h = h[..^1];
        return h;
    }

    // ---- baselines for the benchmark ----
    public FrozenDictionary<string, T> Exact => _exact;
}
