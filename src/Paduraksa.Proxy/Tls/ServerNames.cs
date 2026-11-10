using System.Diagnostics.CodeAnalysis;

using Paduraksa.Proxy.Net;

namespace Paduraksa.Proxy.Tls;

/// <summary>Validation and normalisation of configured server names (cold path) and SNI trimming (hot path).</summary>
/// <remarks>
/// The DNS rules are <see cref="DnsNames"/>, shared with the host table's <c>HostNames</c>. On top of them, a server
/// name must already be punycode (no Unicode input, no surrounding whitespace) and can't be an IP literal, which
/// clients never send as SNI (RFC 6066). The compiler only hands this table names the host table has normalised.
/// </remarks>
internal static class ServerNames
{
    /// <summary>
    /// Returns the canonical form (lowercase, no trailing dot). For <c>*.example.com</c>, returns the wildcard
    /// parent <c>example.com</c> and sets <paramref name="isWildcard"/>. For names with punycode (<c>xn--</c>) labels,
    /// <paramref name="unicode"/> is the decoded, lowercased form; otherwise null.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't a valid DNS name or single-level wildcard.</exception>
    public static string Normalize(string name, out bool isWildcard, out string? unicode)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (!DnsNames.TryParse(name, out var parsed, out var problem))
        {
            Reject(name, problem);
        }

        if (parsed.IsNumeric)
        {
            Reject(name, "the last label is numeric, so it is an IP address and can never match an SNI");
        }

        isWildcard = parsed.IsWildcard;
        unicode = parsed.Unicode;
        return parsed.Canonical;
    }

    [DoesNotReturn]
    private static void Reject(string name, string problem) =>
        throw new ArgumentException(
            $"Invalid server name '{name}': {problem}. Expected a DNS name such as 'app.example.com' or a single-level wildcard such as '*.example.com'.",
            nameof(name));

    /// <summary>Drops one trailing dot (fully qualified form). Allocation-free.</summary>
    public static ReadOnlySpan<char> TrimTrailingDot(ReadOnlySpan<char> name) =>
        name.Length > 0 && name[^1] == '.' ? name[..^1] : name;
}
