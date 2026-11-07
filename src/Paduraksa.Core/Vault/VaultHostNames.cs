using System.Globalization;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The one normal form of a host name that the vault compares: what <see cref="SecretOptions.AllowedHosts"/> and
/// <see cref="SecretInjection.AllowedHosts"/> hold, and what a request's <c>Uri.IdnHost</c> is compared with.
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

    private static ArgumentException Invalid(string host) =>
        new(string.Create(CultureInfo.InvariantCulture, $"'{host}' is not a plain host name (no scheme, port, path, wildcard or IPv6 literal)."), nameof(host));
}
