using System.Text;

using Microsoft.Extensions.Primitives;

using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Keeps the admin session cookie out of proxied requests (PLAN §3.1). A <c>__Host-</c> cookie is bound to the host name, not the port, so
/// a browser that signed in to the admin at <c>https://proxy.example.com:9443</c> sends <c>__Host-paduraksa-session</c> to every proxied
/// host on that name, and a hostile or merely logging upstream would receive a live admin session. This request transform removes that
/// one cookie and leaves every other cookie, and its order, as sent.
/// </summary>
/// <remarks>
/// Cost when the cookie is absent (the usual case): one ordinal search of each <c>Cookie</c> header value for the name, no allocation, and
/// the proxy request is not touched. Only when it is present is the header rebuilt without it.
/// </remarks>
internal static class SessionCookieTransform
{
    /// <summary>The admin session cookie's name (<c>AdminSessionCookie.Name</c>; the Proxy layer cannot reference Admin, a test holds the two equal).</summary>
    internal const string CookieName = "__Host-paduraksa-session";

    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        StringValues cookies = context.HttpContext.Request.Headers.Cookie;
        if (!ContainsName(cookies))
        {
            return ValueTask.CompletedTask;
        }

        context.ProxyRequest.Headers.Remove("Cookie");
        string rebuilt = Without(cookies);
        if (rebuilt.Length > 0)
        {
            context.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", rebuilt);
        }

        return ValueTask.CompletedTask;
    }

    private static bool ContainsName(StringValues cookies)
    {
        foreach (string? value in cookies)
        {
            if (value is not null && value.Contains(CookieName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The cookies of all <c>Cookie</c> header values but the session cookie, joined with <c>; </c>.</summary>
    internal static string Without(StringValues cookies)
    {
        StringBuilder kept = new();
        foreach (string? value in cookies)
        {
            if (value is null)
            {
                continue;
            }

            foreach (Range part in value.AsSpan().Split(';'))
            {
                ReadOnlySpan<char> pair = value.AsSpan(part).Trim();
                if (pair.IsEmpty || IsSessionCookie(pair))
                {
                    continue;
                }

                if (kept.Length > 0)
                {
                    kept.Append("; ");
                }

                kept.Append(pair);
            }
        }

        return kept.ToString();
    }

    private static bool IsSessionCookie(ReadOnlySpan<char> pair)
    {
        int eq = pair.IndexOf('=');
        ReadOnlySpan<char> name = eq < 0 ? pair : pair[..eq];
        return name.Trim().SequenceEqual(CookieName);
    }
}
