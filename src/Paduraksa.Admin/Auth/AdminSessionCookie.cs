using Microsoft.AspNetCore.Http;

namespace Paduraksa.Admin.Auth;

/// <summary>
/// The admin session cookie: <c>__Host-paduraksa-session</c>, <c>Secure</c>, <c>HttpOnly</c>, <c>SameSite=Strict</c>,
/// <c>Path=/</c>, no <c>Domain</c>. The value is the opaque 256-bit session secret and nothing else.
/// </summary>
/// <remarks>
/// <c>Secure</c> is set on every response, including the loopback plain-HTTP fallback listener: ASP.NET Core does not check the scheme,
/// and the browsers that matter (Chromium, Firefox) accept and send a <c>Secure</c> cookie to <c>http://localhost</c> and
/// <c>http://127.0.0.1</c>, which they treat as a secure context. Safari does not, so on that listener use another browser, or an SSH
/// tunnel to the HTTPS listener. A client that is not a browser must send the <c>Cookie</c> header itself.
/// </remarks>
public static class AdminSessionCookie
{
    /// <summary>The cookie's name; the <c>__Host-</c> prefix makes browsers require <c>Secure</c>, <c>Path=/</c> and no <c>Domain</c>.</summary>
    public const string Name = "__Host-paduraksa-session";

    /// <summary>Sets the cookie to <paramref name="value"/> until <paramref name="expiresAt"/>.</summary>
    public static void Set(HttpResponse response, string value, DateTimeOffset expiresAt)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Cookies.Append(Name, value, Options(expiresAt));
    }

    /// <summary>Tells the browser to drop the cookie.</summary>
    public static void Clear(HttpResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        CookieOptions options = Options(DateTimeOffset.UnixEpoch);
        options.MaxAge = TimeSpan.Zero;
        response.Cookies.Append(Name, string.Empty, options);
    }

    /// <summary>
    /// The value of the session cookie, <c>Present</c> false when the request carries none. A request that carries it twice is
    /// <c>Ambiguous</c>: which one is meant cannot be known, so it is refused rather than guessed.
    /// </summary>
    public static (bool Present, bool Ambiguous, string? Value) Read(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? found = null;
        int count = 0;
        foreach (string? header in request.Headers.Cookie)
        {
            if (header is null)
            {
                continue;
            }

            foreach (Range part in header.AsSpan().Split(';'))
            {
                ReadOnlySpan<char> pair = header.AsSpan(part).Trim();
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq].Trim().SequenceEqual(Name))
                {
                    count++;
                    found = pair[(eq + 1)..].Trim().Trim('"').ToString();
                }
            }
        }

        return (count > 0, count > 1, found);
    }

    private static CookieOptions Options(DateTimeOffset expiresAt) => new()
    {
        Secure = true,
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/",
        Expires = expiresAt,
    };
}
