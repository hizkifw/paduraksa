using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Paduraksa.Proxy.Net;

/// <summary>A configured DNS name that passed <see cref="DnsNames.TryParse"/>.</summary>
/// <param name="Canonical">Lowercase ASCII, no trailing dot; for a wildcard, the parent (<c>example.com</c> for
/// <c>*.example.com</c>).</param>
/// <param name="IsWildcard">The name was <c>*.parent</c>: it covers exactly one extra leftmost label.</param>
/// <param name="IsNumeric">The last label is all digits, so the name can only be an IPv4 literal (no top-level domain
/// is numeric). Never set for a wildcard. Callers decide whether IP literals are allowed.</param>
/// <param name="Unicode">The decoded, lowercased form when a punycode (<c>xn--</c>) label decodes to something else;
/// otherwise null.</param>
internal readonly record struct DnsName(string Canonical, bool IsWildcard, bool IsNumeric, string? Unicode);

/// <summary>The DNS-name rules shared by the host table (<see cref="Snapshot.HostNames"/>) and the certificate table
/// (<see cref="Tls.ServerNames"/>), so a name one of them accepts can't be rejected later by the other.</summary>
/// <remarks>
/// Input is ASCII (IDNs in punycode), with at most one trailing dot and an optional leading <c>*.</c> label. Rules: at
/// most 253 characters; labels of 1-63 letters, digits, <c>-</c> or <c>_</c>, not starting or ending with <c>-</c>;
/// <c>*</c> only as the whole leftmost label; a wildcard's parent has at least two labels (<c>*.example.com</c>, not
/// <c>*.com</c> or <c>*.localhost</c>) and isn't numeric; every <c>xn--</c> label decodes. What each caller adds on top
/// (whitespace trimming, Unicode input, IP literals) is documented on that caller.
/// </remarks>
internal static class DnsNames
{
    public const int MaxNameLength = 253;

    public const int MaxLabelLength = 63;

    /// <summary>Characters allowed in a label. <c>_</c> is accepted for internal names.</summary>
    public static readonly SearchValues<char> LabelChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_");

    // The same mapping SslStream uses to decode the SNI it hands the TCP callback, so Unicode aliases match exactly.
    private static readonly IdnMapping Idn = new();

    /// <summary>Applies the shared rules to <paramref name="name"/>; on failure <paramref name="problem"/> says why
    /// (a clause without the name, e.g. "a label is longer than 63 characters").</summary>
    public static bool TryParse(ReadOnlySpan<char> name, out DnsName result, [NotNullWhen(false)] out string? problem)
    {
        result = default;
        var span = name.Length > 0 && name[^1] == '.' ? name[..^1] : name;
        var wildcard = span.StartsWith("*.", StringComparison.Ordinal);
        if (wildcard)
        {
            span = span[2..];
        }

        if (!TryValidateLabels(span, out problem))
        {
            return false;
        }

        var lastLabel = span[(span.LastIndexOf('.') + 1)..];
        var numeric = !lastLabel.ContainsAnyExceptInRange('0', '9');
        if (wildcard && numeric)
        {
            problem = "a wildcard can't cover an IP address";
            return false;
        }

        if (wildcard && !span.Contains('.'))
        {
            problem = "a wildcard needs a parent of at least two labels ('*.example.com', not '*.com')";
            return false;
        }

        var canonical = span.ToString().ToLowerInvariant();
        string? unicode = null;
        if (canonical.StartsWith("xn--", StringComparison.Ordinal) || canonical.Contains(".xn--", StringComparison.Ordinal))
        {
            try
            {
                // Lenient decoders return undecodable labels unchanged; then SslStream passes them unchanged too.
                var decoded = Idn.GetUnicode(canonical).ToLowerInvariant();
                unicode = decoded == canonical ? null : decoded;
            }
            catch (ArgumentException error)
            {
                problem = $"a punycode ('xn--') label doesn't decode ({error.Message})";
                return false;
            }
        }

        result = new DnsName(canonical, wildcard, numeric, unicode);
        return true;
    }

    private static bool TryValidateLabels(ReadOnlySpan<char> name, [NotNullWhen(false)] out string? problem)
    {
        if (name.IsEmpty)
        {
            problem = "it is empty";
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            problem = $"it is longer than {MaxNameLength} characters";
            return false;
        }

        foreach (var range in name.Split('.'))
        {
            var label = name[range];
            if (label.IsEmpty)
            {
                problem = "it has an empty label";
                return false;
            }

            if (label.Length > MaxLabelLength)
            {
                problem = $"a label is longer than {MaxLabelLength} characters";
                return false;
            }

            var bad = label.IndexOfAnyExcept(LabelChars);
            if (bad >= 0)
            {
                problem = label[bad] == '*'
                    ? "a wildcard ('*') is only allowed as the whole leftmost label ('*.example.com')"
                    : $"'{label[bad]}' is not allowed (letters, digits, '-' and '_' only; use punycode for IDNs)";
                return false;
            }

            if (label[0] == '-' || label[^1] == '-')
            {
                problem = "a label starts or ends with '-'";
                return false;
            }
        }

        problem = null;
        return true;
    }
}
