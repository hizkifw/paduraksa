using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// The <c>Alt-Svc</c> header Paduraksa emits instead of Kestrel's (PLAN §7.5). It's built once from the <b>public</b>
/// HTTPS port setting, not the bound port, so a port-mapped deployment can't advertise the wrong port (Kestrel's own
/// header uses the bound port and is disabled on our listeners). Every request reuses the same
/// <see cref="StringValues"/>, so applying it costs nothing.
/// </summary>
public sealed class AltSvcHeader
{
    /// <summary>24 hours, as browsers commonly use and spike S1 verified.</summary>
    public const int DefaultMaxAgeSeconds = 86_400;

    /// <param name="publicHttpsPort">The HTTPS port clients reach (settings), e.g. 443 even if bound to 8443.</param>
    /// <param name="http3Listening">Whether the H3 listener was added (<see cref="PublicListeners.ListenPublicHttps"/>).</param>
    /// <param name="maxAgeSeconds">The <c>ma</c> parameter.</param>
    public AltSvcHeader(int publicHttpsPort, bool http3Listening, int maxAgeSeconds = DefaultMaxAgeSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(publicHttpsPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(publicHttpsPort, 65_535);
        ArgumentOutOfRangeException.ThrowIfNegative(maxAgeSeconds);

        Value = http3Listening
            ? new StringValues(string.Create(CultureInfo.InvariantCulture, $"h3=\":{publicHttpsPort}\"; ma={maxAgeSeconds}"))
            : StringValues.Empty;
    }

    /// <summary>The header value, e.g. <c>h3=":443"; ma=86400</c>; empty when no H3 listener exists.</summary>
    public StringValues Value { get; }

    /// <summary>
    /// The precomputed value for one host (snapshot compile time): <see cref="Value"/> for H3-eligible hosts, empty for
    /// hosts that opted out of H3 (their QUIC handshakes are rejected, so advertising H3 would only cost a round trip).
    /// </summary>
    public StringValues ForHost(TlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return policy.Http3Enabled ? Value : StringValues.Empty;
    }

    /// <summary>
    /// Sets <c>Alt-Svc</c> to the host's precomputed value on HTTPS responses that aren't already HTTP/3. Called by the
    /// gate before the response starts. 0 B: no string is built, the shared value is stored as is.
    /// </summary>
    public static void Apply(HttpContext context, StringValues hostValue)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (StringValues.IsNullOrEmpty(hostValue))
        {
            return;
        }

        var request = context.Request;
        if (!request.IsHttps || HttpProtocol.IsHttp3(request.Protocol))
        {
            return;
        }

        context.Response.Headers.AltSvc = hostValue;
    }
}
