using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Paduraksa.Proxy.Tls;

/// <summary>Validation and normalisation of configured server names (cold path) and SNI trimming (hot path).</summary>
internal static class ServerNames
{
    private const int MaxNameLength = 253;
    private const int MaxLabelLength = 63;

    // The same mapping SslStream uses to decode the SNI it hands the TCP callback, so the aliases match exactly.
    private static readonly IdnMapping Idn = new();

    /// <summary>
    /// Returns the canonical form (lowercase, no trailing dot). For <c>*.example.com</c>, returns the wildcard
    /// parent <c>example.com</c> and sets <paramref name="isWildcard"/>. For names with punycode (<c>xn--</c>) labels,
    /// <paramref name="unicode"/> is the decoded, lowercased form; otherwise null.
    /// </summary>
    /// <exception cref="ArgumentException">The name isn't a valid DNS name or single-level wildcard.</exception>
    public static string Normalize(string name, out bool isWildcard, out string? unicode)
    {
        ArgumentNullException.ThrowIfNull(name);

        var span = TrimTrailingDot(name.AsSpan());
        isWildcard = span.StartsWith("*.", StringComparison.Ordinal);
        if (isWildcard)
        {
            span = span[2..];
        }

        if (!IsValidHostName(span, out var problem))
        {
            Reject(name, problem);
        }

        // No TLD is all-numeric: such a name is an IP literal, and clients never send IPs as SNI (RFC 6066).
        var lastLabel = span[(span.LastIndexOf('.') + 1)..];
        if (!lastLabel.ContainsAnyExceptInRange('0', '9'))
        {
            Reject(name, "the last label is numeric, so it is an IP address and can never match an SNI");
        }

        if (isWildcard && !span.Contains('.'))
        {
            Reject(name, "a wildcard needs a parent of at least two labels ('*.example.com', not '*.com')");
        }

        var canonical = span.ToString().ToLowerInvariant();
        unicode = null;
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
                throw new ArgumentException(
                    $"Invalid server name '{name}': a punycode ('xn--') label doesn't decode ({error.Message}).",
                    nameof(name),
                    error);
            }
        }

        return canonical;
    }

    [DoesNotReturn]
    private static void Reject(string name, string problem) =>
        throw new ArgumentException(
            $"Invalid server name '{name}': {problem}. Expected a DNS name such as 'app.example.com' or a single-level wildcard such as '*.example.com'.",
            nameof(name));

    /// <summary>Drops one trailing dot (fully qualified form). Allocation-free.</summary>
    public static ReadOnlySpan<char> TrimTrailingDot(ReadOnlySpan<char> name) =>
        name.Length > 0 && name[^1] == '.' ? name[..^1] : name;

    private static bool IsValidHostName(ReadOnlySpan<char> name, out string problem)
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

        var labelStart = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            if (i < name.Length && name[i] != '.')
            {
                var c = name[i];
                if (c == '*')
                {
                    problem = "'*' is only allowed as the whole first label ('*.example.com')";
                    return false;
                }

                if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                {
                    problem = $"'{c}' is not allowed (letters, digits, '-' and '_' only; use punycode for IDNs)";
                    return false;
                }

                continue;
            }

            var label = name[labelStart..i];
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

            if (label[0] == '-' || label[^1] == '-')
            {
                problem = "a label starts or ends with '-'";
                return false;
            }

            labelStart = i + 1;
        }

        problem = string.Empty;
        return true;
    }
}
