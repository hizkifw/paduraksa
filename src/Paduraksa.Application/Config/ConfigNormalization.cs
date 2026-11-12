using System.Diagnostics.CodeAnalysis;

using Paduraksa.Proxy.Net;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Application.Config;

/// <summary>
/// The normal forms of the document's natural keys and labels. Host names use the proxy's own rules
/// (<see cref="HostNames"/>), so a name the document accepts is one the compiler accepts, spelled the way the
/// host table stores it.
/// </summary>
internal static class ConfigNormalization
{
    /// <summary>Longest tag, after normalisation.</summary>
    public const int MaxTagLength = 64;

    /// <summary>Longest profile or upstream name, after trimming.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The default forwarding header of generic trusted proxies.</summary>
    public const string DefaultForwardingHeader = "x-forwarded-for";

    /// <summary>
    /// Normalises a host name: trimmed, lowercased, IDN → punycode, no trailing dot; a wildcard only as the whole
    /// leftmost <c>*.</c> label over a parent of two or more labels; IPv4 literals as a canonical dotted quad and IPv6
    /// literals bracketed and compressed. On failure <paramref name="error"/> says why.
    /// </summary>
    public static bool TryNormalizeHostName(string? name, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        if (HostNames.TryNormalize(name, out var value, out _, out error))
        {
            normalized = value;
            return true;
        }

        normalized = null;
        error ??= $"'{name}' isn't a valid host name";
        return false;
    }

    /// <summary>
    /// The canonical spelling of a host name: its normal form, or, when it has none, the trimmed input (left for
    /// validation to report).
    /// </summary>
    public static string CanonicalHostName(string? name) =>
        TryNormalizeHostName(name, out var normalized, out _) ? normalized : (name ?? string.Empty).Trim();

    /// <summary>
    /// A custom upstream <c>Host</c>: a host name or IP literal as <see cref="TryNormalizeHostName"/> spells it (no wildcard),
    /// then <c>:port</c> (1-65535) when one is given, so it is exactly the string the proxy sends.
    /// </summary>
    public static bool TryNormalizeUpstreamHost(string? value, [NotNullWhen(true)] out string? normalized, [NotNullWhen(false)] out string? error)
    {
        normalized = null;
        var text = (value ?? string.Empty).Trim();
        var portStart = text.StartsWith('[') ? text.IndexOf("]:", StringComparison.Ordinal) + 1 : text.IndexOf(':', StringComparison.Ordinal);
        var host = portStart > 0 ? text[..portStart] : text;
        string? port = portStart > 0 ? text[(portStart + 1)..] : null;
        if (!text.StartsWith('[') && text.Count(c => c == ':') > 1)
        {
            error = $"'{text}': write an IPv6 address in brackets, like [::1]:8080";
            return false;
        }

        if (port is not null && (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535))
        {
            error = $"'{text}': the port must be a number from 1 to 65535";
            return false;
        }

        if (!HostNames.TryNormalize(host, out var name, out var kind, out var hostError))
        {
            error = hostError ?? $"'{text}' isn't a valid host name";
            return false;
        }

        if (kind == HostNameKind.Wildcard)
        {
            error = $"'{text}': a wildcard can't be a Host header";
            return false;
        }

        normalized = port is null ? name : $"{name}:{int.Parse(port, System.Globalization.CultureInfo.InvariantCulture)}";
        error = null;
        return true;
    }

    /// <summary>A country code upper-case and trimmed (<c>de</c> becomes <c>DE</c>); validation reports one that isn't a known code.</summary>
    public static string CanonicalCountry(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// An address or CIDR in the form the parser prints: lowercase, compressed IPv6, IPv4-mapped blocks as IPv4, and a
    /// full-length prefix as the bare address (<c>10.0.0.1/32</c> → <c>10.0.0.1</c>). Entries that don't parse are only
    /// trimmed, for validation to report.
    /// </summary>
    public static string CanonicalAddress(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (!IpNetwork.TryParse(trimmed, out var network))
        {
            return trimmed;
        }

        return network.Prefix128 == 128 ? network.First.ToString() : network.ToString();
    }

    /// <summary>A trusted-proxy header in its normal form: trimmed and lowercase (header names ignore case); null when
    /// blank or the default (<see cref="DefaultForwardingHeader"/>), which is what null means.</summary>
    public static string? CanonicalForwardingHeader(string? header)
    {
        var value = (header ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 || value == DefaultForwardingHeader ? null : value;
    }

    /// <summary>A rate-limit partition header in its normal form: trimmed and lowercase (header names ignore case); null
    /// when blank, which is how a header-keyed policy says it has none.</summary>
    public static string? CanonicalRateLimitHeader(string? header)
    {
        var value = (header ?? string.Empty).Trim().ToLowerInvariant();
        return value.Length == 0 ? null : value;
    }

    /// <summary>A tag's normal form: trimmed and lowercase.</summary>
    public static string NormalizeTag(string? tag) => (tag ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>A normalised tag is 1-<see cref="MaxTagLength"/> letters, digits or <c>- _ . : /</c>.</summary>
    public static bool IsValidTag(string tag) =>
        tag.Length is > 0 and <= MaxTagLength && tag.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '/');

    /// <summary>A profile or upstream name's normal form: trimmed. Names compare ignoring case.</summary>
    public static string NormalizeName(string? name) => (name ?? string.Empty).Trim();

    /// <summary>Notes keep their text; blank notes are no notes.</summary>
    public static string? NormalizeNotes(string? notes) => string.IsNullOrWhiteSpace(notes) ? null : notes;
}
