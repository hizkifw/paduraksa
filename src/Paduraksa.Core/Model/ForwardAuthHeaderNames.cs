using System.Buffers;

namespace Paduraksa.Core.Model;

/// <summary>
/// Which header names a forward-auth provider may be told to send to its endpoint or to copy from its answer, shared by the validator and
/// the compiler. The proxy owns the framing and forwarding headers (hop-by-hop, <c>Host</c>, <c>Content-Length</c>, the <c>X-Forwarded-*</c> set,
/// <c>X-Real-IP</c>, <c>X-Request-Id</c>), so a provider can neither ask for a client's copy of one nor have its own replace ours. A name the provider
/// owns is also removed from every client request to the host, so an <em>answer</em> may not name the client's own credentials (<c>Authorization</c>,
/// <c>Cookie</c>, <c>Proxy-Authorization</c>): listing one would strip the bearer token or the session cookie from every route of the host, those that
/// aren't behind the provider included. They are still sent <em>to</em> the provider (the request list), and the upstream gets them as the client sent them.
/// </summary>
public static class ForwardAuthHeaderNames
{
    private static readonly SearchValues<char> TokenChars =
        SearchValues.Create("!#$%&'+-.^_`|~0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ");

    private static readonly string[] Reserved =
    [
        "Connection", "Content-Length", "Content-Encoding", "Forwarded", "Host", "Keep-Alive", "Proxy-Connection", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade", "X-Real-IP", "X-Request-Id",
    ];

    private static readonly string[] ClientCredentials = ["Authorization", "Cookie", "Proxy-Authorization"];

    /// <summary>Whether a response name is, or as a prefix would own, one of the client's credential headers. The validator gives it a code of its own.</summary>
    public static bool NamesClientCredential(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var prefix = name.EndsWith('*');
        var stem = prefix ? name[..^1] : name;
        foreach (var credential in ClientCredentials)
        {
            if (prefix ? stem.Length >= 3 && credential.StartsWith(stem, StringComparison.OrdinalIgnoreCase) : string.Equals(stem, credential, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Why <paramref name="name"/> can't be named, or null when it can. A response name may end in <c>*</c>, which makes it a prefix.</summary>
    /// <param name="name">The header name as written.</param>
    /// <param name="response">True for the names copied from the provider's answer (a trailing <c>*</c> allowed), false for the extra request headers.</param>
    public static string? Problem(string name, bool response)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stem = response && name.EndsWith('*') ? name[..^1] : name;
        if (stem.Length == 0 && !(response && name.Length > 0))
        {
            return "a header name can't be empty";
        }

        if (name.Length > ForwardAuthLimits.MaxHeaderNameLength)
        {
            return $"a header name can be at most {ForwardAuthLimits.MaxHeaderNameLength} characters";
        }

        if (stem.AsSpan().ContainsAnyExcept(TokenChars))
        {
            return $"'{name}' isn't a valid header name";
        }

        // A bare "*" would own every header; a prefix shorter than 3 characters is nearly as wide.
        if (response && name.EndsWith('*') && stem.Length < 3)
        {
            return "a prefix must have at least three characters before the *";
        }

        if (stem.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || (response && name.EndsWith('*') && IsPrefixOfReserved(stem)))
        {
            return $"'{name}' is a header the proxy sets itself";
        }

        foreach (var reserved in Reserved)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{name}' is a header the proxy sets itself";
            }
        }

        if (response && NamesClientCredential(name))
        {
            return $"'{name}' is a credential the client sends: it is forwarded as the client sent it, and an identity header is removed from every request to the host";
        }

        return null;
    }

    private static bool IsPrefixOfReserved(string stem)
    {
        if ("X-Forwarded-".StartsWith(stem, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var reserved in Reserved)
        {
            if (reserved.StartsWith(stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
