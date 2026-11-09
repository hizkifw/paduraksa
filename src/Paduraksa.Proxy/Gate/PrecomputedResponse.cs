using System.Text;

using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Gate;

/// <summary>
/// A response the gate answers by itself, built once at snapshot compile time: the status,
/// the encoded body and the header values as <see cref="StringValues"/> shared by every request. Immutable, so one
/// instance is safely reused across requests, connections and threads.
/// </summary>
public sealed class PrecomputedResponse
{
    private static readonly StringValues TextPlain = new("text/plain; charset=utf-8");
    private static readonly StringValues NoStore = new("no-store");

    private PrecomputedResponse(int statusCode, byte[] body, StringValues contentType, StringValues cacheControl, StringValues location, RedirectTarget? redirect)
    {
        StatusCode = statusCode;
        Body = body;
        ContentType = contentType;
        CacheControl = cacheControl;
        Location = location;
        Redirect = redirect;
    }

    public int StatusCode { get; }

    /// <summary>
    /// The encoded body; never written for <c>HEAD</c>, but its length is still sent as <c>Content-Length</c>.
    /// Always empty for 204, 205 and 304, which can't carry one.
    /// </summary>
    public byte[] Body { get; }

    /// <summary>Empty = no <c>Content-Type</c> header.</summary>
    public StringValues ContentType { get; }

    /// <summary>Empty = no <c>Cache-Control</c> header.</summary>
    public StringValues CacheControl { get; }

    /// <summary>A fixed <c>Location</c>. Empty when there is none, or when <see cref="Redirect"/> builds it per request.</summary>
    public StringValues Location { get; }

    /// <summary>
    /// Set when the <c>Location</c> depends on the request (the HTTP→HTTPS redirect, a redirect host that keeps the
    /// path). Building that string is the only allocation of a gate-answered response, and it happens in the write
    /// step, after the gate's decision (see <see cref="ResponseWriter"/>).
    /// </summary>
    internal RedirectTarget? Redirect { get; }

    /// <summary>A plain-text response, e.g. a Respond host, a policy denial or the default-host 404.</summary>
    public static PrecomputedResponse Text(int statusCode, string body, bool noStore = true)
    {
        ArgumentNullException.ThrowIfNull(body);
        ValidateStatus(statusCode, body.Length, nameof(body));
        return new(statusCode, Encoding.UTF8.GetBytes(body), TextPlain, noStore ? NoStore : default, default, null);
    }

    /// <summary>A response with an arbitrary body and content type.</summary>
    public static PrecomputedResponse Create(int statusCode, ReadOnlySpan<byte> body, string? contentType, string? cacheControl = "no-store")
    {
        ValidateStatus(statusCode, body.Length, nameof(body));
        return new(statusCode, body.ToArray(), Values(contentType), Values(cacheControl), default, null);
    }

    /// <summary>A redirect to a fixed URL.</summary>
    public static PrecomputedResponse RedirectTo(int statusCode, string location)
    {
        ArgumentException.ThrowIfNullOrEmpty(location);
        ValidateRedirectStatus(statusCode);
        return new(statusCode, RedirectBody, TextPlain, NoStore, new StringValues(location), null);
    }

    /// <summary>A redirect whose <c>Location</c> is built per request from <paramref name="target"/>.</summary>
    internal static PrecomputedResponse RedirectTo(int statusCode, RedirectTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateRedirectStatus(statusCode);
        return target.IsFixed
            ? RedirectTo(statusCode, target.Prefix)
            : new(statusCode, RedirectBody, TextPlain, NoStore, default, target);
    }

    private static readonly byte[] RedirectBody = "Redirecting\n"u8.ToArray();

    private static StringValues Values(string? value) => string.IsNullOrEmpty(value) ? default : new StringValues(value);

    /// <summary>204, 205 and 304 can't carry a body; Kestrel fails the response if one is written.</summary>
    internal static bool ForbidsBody(int statusCode) => statusCode is 204 or 205 or 304;

    // bodyParameter: the public method's body parameter name, so the exception names what the caller passed.
    private static void ValidateStatus(int statusCode, int bodyLength, string bodyParameter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(statusCode, 200, nameof(statusCode));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(statusCode, 599, nameof(statusCode));
        if (bodyLength > 0 && ForbidsBody(statusCode))
        {
            throw new ArgumentException($"A {statusCode} response can't have a body.", bodyParameter);
        }
    }

    private static void ValidateRedirectStatus(int statusCode)
    {
        if (statusCode is not (301 or 302 or 303 or 307 or 308))
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode), statusCode, "A redirect status must be 301, 302, 303, 307 or 308.");
        }
    }
}

/// <summary>
/// How a per-request <c>Location</c> is put together: <c>Prefix</c> [+ request host + <c>PortSuffix</c>]
/// [+ the request's path and query].
/// </summary>
/// <param name="Prefix">
/// The scheme part when <paramref name="IncludeRequestHost"/> is set (<c>https://</c>), otherwise the absolute base
/// URL without a trailing slash (<c>https://new.example.com</c>).
/// </param>
/// <param name="IncludeRequestHost">Append the request's host name (port stripped) after the prefix.</param>
/// <param name="PortSuffix">Appended after the request host, e.g. <c>:8443</c>; empty for the scheme's default port.</param>
/// <param name="AppendPathAndQuery">Append the request target (path and query) as received.</param>
internal sealed record RedirectTarget(string Prefix, bool IncludeRequestHost, string PortSuffix, bool AppendPathAndQuery)
{
    /// <summary>Neither host nor path is taken from the request, so the whole <c>Location</c> is <see cref="Prefix"/>.</summary>
    public bool IsFixed => !IncludeRequestHost && !AppendPathAndQuery;

    /// <summary>The HTTP→HTTPS redirect: same host and target, HTTPS on the <b>public</b> port.</summary>
    public static RedirectTarget Https(int publicHttpsPort)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(publicHttpsPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(publicHttpsPort, 65535);
        return new("https://", IncludeRequestHost: true, publicHttpsPort == 443 ? string.Empty : ":" + publicHttpsPort.ToString(System.Globalization.CultureInfo.InvariantCulture), AppendPathAndQuery: true);
    }
}
