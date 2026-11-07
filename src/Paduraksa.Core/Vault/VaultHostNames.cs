using System.Globalization;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The one normal form of a host name that the vault compares: what <see cref="SecretOptions.AllowedHosts"/> and
/// <see cref="SecretInjection.AllowedHosts"/> hold, and what a request's <c>Uri.IdnHost</c> is compared with. An entry may
/// also name a port (<see cref="NormalizeAuthority"/>): without one only port 443 matches.
/// </summary>
public static class VaultHostNames
{
    private static readonly IdnMapping Idn = new();
    private static readonly System.Buffers.SearchValues<char> NotInAHost = System.Buffers.SearchValues.Create("/\\:*@?#[] \t");

    /// <summary>
    /// Normalises a bare host name: trimmed, lowercase, IDN as punycode, no trailing dot. A name that is not a plain host
    /// is refused rather than repaired: a scheme (<c>https://</c>), port, path, query, user info, wildcard and IPv6 literal
    /// are all errors, because each would let a looser string pass for a host.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a plain host name.</exception>
    public static string Normalize(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var value = host.Trim();
        if (value.EndsWith('.'))
        {
            value = value[..^1];
        }

        if (value.Length == 0 || value.Length > 253 || value.AsSpan().IndexOfAny(NotInAHost) >= 0 || value.Any(char.IsControl))
        {
            throw Invalid(host);
        }

        try
        {
            // Lowercased first: with invariant globalization IdnMapping does not case-fold non-ASCII letters.
            return Idn.GetAscii(value.ToLowerInvariant());
        }
        catch (ArgumentException)
        {
            throw Invalid(host);
        }
    }

    /// <summary>
    /// Normalises an allowed destination: a plain host (<see cref="Normalize"/>), optionally with an explicit port
    /// (<c>host:8443</c>). Port 443, the https default, is the same as no port and is written without one; any other port is
    /// part of the entry, and a request to it is allowed only by an entry that names it. IPv6 literals stay refused.
    /// </summary>
    /// <exception cref="ArgumentException">The value is not a plain host name with an optional port in 1 to 65535.</exception>
    public static string NormalizeAuthority(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var value = authority.Trim();
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return Normalize(value);
        }

        var portText = value[(colon + 1)..];
        if (value.IndexOf(':', colon + 1) >= 0
            || portText.Length is 0 or > 5
            || !portText.All(char.IsAsciiDigit)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw Invalid(authority);
        }

        var host = Normalize(value[..colon]);
        return port == DefaultHttpsPort ? host : string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");
    }

    /// <summary>The https default port, which an authority is written without.</summary>
    public const int DefaultHttpsPort = 443;

    private static ArgumentException Invalid(string host) =>
        new(string.Create(CultureInfo.InvariantCulture, $"'{host}' is not a plain host name (no scheme, port, path, wildcard or IPv6 literal)."), nameof(host));
}
