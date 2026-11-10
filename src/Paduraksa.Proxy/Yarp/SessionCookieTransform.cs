using System.Net.Http.Headers;
using System.Text;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Gate;
using Yarp.ReverseProxy.Transforms;

namespace Paduraksa.Proxy.Yarp;

/// <summary>
/// Keeps the admin session cookie out of proxied requests, and a hostile upstream from planting one in a response.
/// A <c>__Host-</c> cookie is bound to the host name, not the port, so a browser that signed in to the admin at
/// <c>https://proxy.example.com:9443</c> sends <c>__Host-paduraksa-session</c> to every proxied host on that name, and a
/// hostile or merely logging upstream would receive a live admin session. This request transform removes that one cookie and
/// leaves every other cookie, and its order, as sent.
/// </summary>
/// <remarks>
/// <para><b>The admin route is the exception:</b> an admin host's purpose is to carry that cookie to the admin app, so the
/// transform leaves the request alone there. The response transform is the mirror image: a <c>Set-Cookie</c> for the session
/// cookie coming back from any <i>other</i> upstream is removed (cookie tossing: an upstream on the same name could otherwise
/// replace the owner's admin session). The admin's own <c>Set-Cookie</c> passes through.</para>
/// <para>Cost when the cookie is absent (the usual case): one ordinal search of each <c>Cookie</c> header value for the name, no allocation, and
/// the proxy request is not touched. Only when it is present is the header rebuilt without it; likewise a response without the
/// name pays one header lookup.</para>
/// </remarks>
internal static class SessionCookieTransform
{
    /// <summary>The admin session cookie's name (<c>AdminSessionCookie.Name</c>; the Proxy layer cannot reference Admin, a test holds the two equal).</summary>
    internal const string CookieName = "__Host-paduraksa-session";

    private const string SetCookie = "Set-Cookie";

    public static ValueTask ApplyAsync(RequestTransformContext context)
    {
        if (IsAdminRoute(context.HttpContext))
        {
            return ValueTask.CompletedTask;
        }

        var cookies = context.HttpContext.Request.Headers.Cookie;
        if (!ContainsName(cookies))
        {
            return ValueTask.CompletedTask;
        }

        context.ProxyRequest.Headers.Remove("Cookie");
        var rebuilt = Without(cookies);
        if (rebuilt.Length > 0)
        {
            context.ProxyRequest.Headers.TryAddWithoutValidation("Cookie", rebuilt);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes a <c>Set-Cookie</c> for the admin session cookie from a non-admin upstream's response (cookie tossing).</summary>
    public static ValueTask ApplyResponseAsync(ResponseTransformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.ProxyResponse is null || IsAdminRoute(context.HttpContext))
        {
            return ValueTask.CompletedTask;
        }

        // Response transforms may run before or after YARP copies the headers; operate on whichever header collection
        // currently holds the client-facing value (ResponseTransformContext.HeadersCopied says which).
        if (context.HeadersCopied)
        {
            RemoveFrom(context.HttpContext.Response.Headers);
        }
        else
        {
            RemoveFrom(context.ProxyResponse.Headers);
        }

        return ValueTask.CompletedTask;
    }

    private static void RemoveFrom(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(SetCookie, out var values))
        {
            return;
        }

        var removed = false;
        foreach (var value in values)
        {
            if (value is not null && IsSessionCookie(value.AsSpan().TrimStart()))
            {
                removed = true;
                break;
            }
        }

        if (!removed)
        {
            return;
        }

        List<string>? kept = null;
        foreach (var value in values)
        {
            if (value is not null && IsSessionCookie(value.AsSpan().TrimStart()))
            {
                continue;
            }

            (kept ??= []).Add(value ?? string.Empty);
        }

        headers[SetCookie] = kept is null ? StringValues.Empty : new StringValues([.. kept]);
    }

    private static void RemoveFrom(HttpResponseHeaders headers)
    {
        var found = false;
        var removed = false;
        foreach (var header in headers.NonValidated)
        {
            if (!string.Equals(header.Key, SetCookie, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            found = true;
            foreach (var value in header.Value)
            {
                if (IsSessionCookie(value.AsSpan().TrimStart()))
                {
                    removed = true;
                    break;
                }
            }

            break;
        }

        if (!found || !removed)
        {
            return;
        }

        List<string>? kept = null;
        foreach (var header in headers.NonValidated)
        {
            if (!string.Equals(header.Key, SetCookie, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!IsSessionCookie(value.AsSpan().TrimStart()))
                {
                    (kept ??= []).Add(value);
                }
            }
        }

        headers.Remove(SetCookie);
        if (kept is not null)
        {
            headers.TryAddWithoutValidation(SetCookie, kept);
        }
    }

    /// <summary>Whether the request is being forwarded to an admin host (<see cref="Paduraksa.Core.Model.HostKind.Admin"/>), where the cookie belongs.</summary>
    internal static bool IsAdminRoute(HttpContext context) =>
        context.Features.Get<IGateFeature>()?.State.Route is { Admin: true };

    private static bool ContainsName(StringValues cookies)
    {
        foreach (var value in cookies)
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
        foreach (var value in cookies)
        {
            if (value is null)
            {
                continue;
            }

            foreach (var part in value.AsSpan().Split(';'))
            {
                var pair = value.AsSpan(part).Trim();
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

    /// <summary>Whether a <c>name=value</c> pair (a cookie or one <c>Set-Cookie</c> line) sets the session cookie.</summary>
    private static bool IsSessionCookie(ReadOnlySpan<char> pair)
    {
        var eq = pair.IndexOf('=');
        var name = eq < 0 ? pair : pair[..eq];
        return name.Trim().SequenceEqual(CookieName);
    }
}
