using System.Buffers;

namespace Paduraksa.Core.Model;

/// <summary>
/// Which header names a forward-auth provider may be told to send to its endpoint or to copy from its answer, shared by the validator and
/// the compiler. The proxy owns the framing and forwarding headers (hop-by-hop, <c>Host</c>, <c>Content-Length</c>, the <c>X-Forwarded-*</c> set,
/// <c>X-Real-IP</c>, <c>X-Request-Id</c>), so a provider can neither ask for a client's copy of one nor have its own replace ours.
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
