using System.Globalization;

namespace Paduraksa.Core.Vault;

/// <summary>How <see cref="SecretInjection"/> puts the secret on a request.</summary>
public enum SecretInjectionKind
{
    /// <summary><c>Authorization: &lt;scheme&gt; &lt;secret&gt;</c> (Bearer, or a provider's own scheme).</summary>
    Authorization = 0,

    /// <summary>A custom header whose whole value is the secret (e.g. <c>X-Auth-Token</c>).</summary>
    Header = 1,

    /// <summary><c>Authorization: Basic base64(username:secret)</c>; the secret is the password.</summary>
    BasicAuth = 2,
}

/// <summary>How an authenticating handler (<see cref="ISecretHttpHandlerFactory"/>) attaches a secret to outgoing
/// requests, and where it may.</summary>
/// <remarks>
/// <para>
/// <b>Every injection names the hosts it may talk to,</b> and the handler only talks https. A handler that attached the
/// token to any URL would turn a bug or a hostile redirect in provider code into a token leak, so the vault-owned handler
/// refuses a request to any other host, or over plain http, before sending anything. Host names are normalised by
/// <see cref="VaultHostNames.NormalizeAuthority"/> (no scheme, path or wildcard; IDN as punycode; no trailing dot; a port only if
/// written, <c>host:8443</c>) and compared exactly against the request's <c>Uri.IdnHost</c>, plus its port unless that is 443. The hosts the secret itself allows (<see cref="SecretOptions.AllowedHosts"/>,
/// set by the admin) are intersected with these: provider code can narrow, never widen.
/// </para>
/// <para>
/// A class, not a record: it holds a list, and record equality over lists would compare by reference.
/// </para>
/// </remarks>
public sealed class SecretInjection
{
    /// <summary>Hop-by-hop and framing headers (and credentials with their own kind) that <see cref="Header"/> refuses to carry a secret in.</summary>
    private static readonly HashSet<string> ReservedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Authorization", "Proxy-Authorization", "Proxy-Authenticate", "WWW-Authenticate", "Connection", "Proxy-Connection",
        "Keep-Alive", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length", "Content-Type", "Content-Encoding",
        "Expect", "Cookie", "Set-Cookie", "Via", "Forwarded", "Origin", "Referer", "User-Agent", "Accept", "Date",
    };

    private SecretInjection(SecretInjectionKind kind, string? scheme, string? headerName, string? username, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(allowedHosts);
        if (allowedHosts.Count == 0)
        {
            throw new ArgumentException("At least one allowed host is required.", nameof(allowedHosts));
        }

        Kind = kind;
        Scheme = scheme;
        HeaderName = headerName;
        Username = username;
        AllowedHosts = [.. allowedHosts.Select(VaultHostNames.NormalizeAuthority).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>How the secret is put on the request.</summary>
    public SecretInjectionKind Kind { get; }

    /// <summary>The scheme of the <c>Authorization</c> header; only for <see cref="SecretInjectionKind.Authorization"/>.</summary>
    public string? Scheme { get; }

    /// <summary>The header to set; only for <see cref="SecretInjectionKind.Header"/>.</summary>
    public string? HeaderName { get; }

    /// <summary>The (non-secret) user name; only for <see cref="SecretInjectionKind.BasicAuth"/>.</summary>
    public string? Username { get; }

    /// <summary>The normalised host names the caller wants the secret sent to (see the type's remarks for how they combine with the secret's own).</summary>
    public IReadOnlyList<string> AllowedHosts { get; }

    /// <summary><c>Authorization: &lt;scheme&gt; &lt;secret&gt;</c> for a provider with its own scheme (deSEC uses <c>Token</c>).</summary>
    /// <param name="scheme">An HTTP token (RFC 9110), e.g. <c>Token</c>.</param>
    /// <param name="allowedHosts">The hosts the credential may be sent to; at least one.</param>
    /// <exception cref="ArgumentException">The scheme is not an HTTP token, or a host is invalid.</exception>
    public static SecretInjection Authorization(string scheme, IReadOnlyList<string> allowedHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        RequireToken(scheme, nameof(scheme));
        return new(SecretInjectionKind.Authorization, scheme, null, null, allowedHosts);
    }

    /// <summary>Sugar for <see cref="Authorization"/> with the <c>Bearer</c> scheme.</summary>
    /// <param name="allowedHosts">The hosts the credential may be sent to; at least one.</param>
    public static SecretInjection Bearer(IReadOnlyList<string> allowedHosts) => Authorization("Bearer", allowedHosts);

    /// <summary>A custom header whose whole value is the secret.</summary>
    /// <param name="headerName">An HTTP token (RFC 9110) that is not <c>Host</c>, <c>Authorization</c> (use <see cref="Authorization"/>), a hop-by-hop, framing or proxy header, or <c>Cookie</c>.</param>
    /// <param name="allowedHosts">The hosts the credential may be sent to; at least one.</param>
    /// <exception cref="ArgumentException">The name is not allowed, or a host is invalid.</exception>
    public static SecretInjection Header(string headerName, IReadOnlyList<string> allowedHosts)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        var name = headerName.Trim();
        RequireToken(name, nameof(headerName));
        if (ReservedHeaders.Contains(name))
        {
            throw new ArgumentException($"'{name}' cannot carry a secret.", nameof(headerName));
        }

        return new(SecretInjectionKind.Header, null, name, null, allowedHosts);
    }

    /// <summary>HTTP Basic: the secret is the password.</summary>
    /// <param name="username">The user name. It cannot contain <c>:</c> (RFC 7617) or control characters.</param>
    /// <param name="allowedHosts">The hosts the credential may be sent to; at least one.</param>
    /// <exception cref="ArgumentException">The user name is invalid, or a host is invalid.</exception>
    public static SecretInjection BasicAuth(string username, IReadOnlyList<string> allowedHosts)
    {
        ArgumentNullException.ThrowIfNull(username);
        if (username.Contains(':', StringComparison.Ordinal) || username.Any(char.IsControl))
        {
            throw new ArgumentException("A Basic user name cannot contain ':' or control characters.", nameof(username));
        }

        return new(SecretInjectionKind.BasicAuth, null, null, username, allowedHosts);
    }

    private static void RequireToken(string value, string parameter)
    {
        // tchar, RFC 9110 §5.6.2.
        const string Specials = "!#$%&'*+-.^_`|~";
        foreach (var c in value)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || Specials.Contains(c, StringComparison.Ordinal)))
            {
                throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"'{value}' is not an HTTP token."), parameter);
            }
        }
    }
}
