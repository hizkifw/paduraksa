using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Net;

/// <summary>Where a request's effective scheme came from.</summary>
public enum SchemeSource : byte
{
    /// <summary>Nothing trusted said otherwise: the scheme of the connection to Paduraksa (the zero value).</summary>
    Connection = 0,

    /// <summary><c>CF-Visitor</c> from a trusted Cloudflare peer (the scheme between the visitor and Cloudflare).</summary>
    CfVisitor = 1,

    /// <summary><c>X-Forwarded-Proto</c> from a trusted peer.</summary>
    ForwardedProto = 2,
}

/// <summary>
/// The scheme the client used, as far as the trusted proxies say (PLAN §7.6): <see cref="IsHttps"/> is meaningful only
/// when <see cref="Source"/> isn't <see cref="SchemeSource.Connection"/>; otherwise the connection's own scheme applies.
/// </summary>
public readonly record struct RequestScheme(bool IsHttps, SchemeSource Source)
{
    /// <summary>The request's effective scheme: the forwarded one if any, else the connection's.</summary>
    public bool EffectiveIsHttps(bool connectionIsHttps) => Source == SchemeSource.Connection ? connectionIsHttps : IsHttps;
}

/// <summary>
/// Resolves the trusted request scheme (PLAN §7.2 C, §7.6), once per request next to the client IP. Both the HTTP→HTTPS
/// redirect and the upstream <c>X-Forwarded-Proto</c> use it, so behind a TLS-terminating proxy (cloudflared on port 80,
/// Cloudflare "Flexible", a generic TLS offloader) a forced-HTTPS host doesn't redirect forever. Pure and 0 B.
/// <list type="bullet">
/// <item>Cloudflare modes with a trusted peer (<see cref="ClientIpResolution.CloudflareHeadersTrusted"/>):
/// <c>CF-Visitor</c>, which must be exactly <c>{"scheme":"https"}</c> or <c>{"scheme":"http"}</c> (anything else is
/// invalid and ignored); without a valid one, the peer's <c>X-Forwarded-Proto</c>.</item>
/// <item>Generic mode with a trusted peer: <c>X-Forwarded-Proto</c>, only when the policy opts in
/// (<see cref="TrustedProxyPolicy.TrustForwardedProto"/>), since many proxies pass a client's header through.</item>
/// <item>Otherwise, or when the header is absent or not exactly one value of <c>http</c>/<c>https</c> (case-insensitive):
/// <see cref="SchemeSource.Connection"/>.</item>
/// </list>
/// </summary>
public static class ForwardedScheme
{
    public const string XForwardedProto = "X-Forwarded-Proto";

    public static RequestScheme Resolve(in ClientIpResolution client, IHeaderDictionary headers, TrustedProxyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(policy);

        if (client.CloudflareHeadersTrusted)
        {
            if (TryParseVisitor(headers[CloudflareHeaders.Visitor], out bool visitorHttps))
            {
                return new RequestScheme(visitorHttps, SchemeSource.CfVisitor);
            }

            return FromForwardedProto(headers);
        }

        return client.PeerTrusted && policy.Mode == TrustedProxyMode.Generic && policy.TrustForwardedProto
            ? FromForwardedProto(headers)
            : default;
    }

    /// <summary>Strict: exactly one value, <c>{"scheme":"https"}</c> or <c>{"scheme":"http"}</c>, surrounding blanks allowed.</summary>
    public static bool TryParseVisitor(StringValues values, out bool https)
    {
        https = false;
        if (values.Count != 1)
        {
            return false;
        }

        ReadOnlySpan<char> value = values[0].AsSpan().Trim(" \t");
        if (value.SequenceEqual("{\"scheme\":\"https\"}"))
        {
            https = true;
            return true;
        }

        return value.SequenceEqual("{\"scheme\":\"http\"}");
    }

    private static RequestScheme FromForwardedProto(IHeaderDictionary headers)
    {
        StringValues values = headers[XForwardedProto];
        if (values.Count == 1)
        {
            ReadOnlySpan<char> value = values[0].AsSpan().Trim(" \t");
            if (value.Equals("https", StringComparison.OrdinalIgnoreCase))
            {
                return new RequestScheme(true, SchemeSource.ForwardedProto);
            }

            if (value.Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                return new RequestScheme(false, SchemeSource.ForwardedProto);
            }
        }

        return default;
    }
}
