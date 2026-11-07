using System.Security.Authentication;

using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Enforces a host's <see cref="TlsPolicy"/> per <b>request</b> (PLAN §7.2 B/C). The handshake applies the policy of
/// the host named in the SNI, but clients reuse a connection for every name its certificate covers (H2 and H3
/// coalescing on SAN or wildcard certificates), so a request's <c>Host</c>/<c>:authority</c> can name a host with
/// stricter settings than the one the connection was negotiated for. The gate checks the negotiated facts after host
/// lookup and answers 421 Misdirected Request, which makes browsers retry on a connection of the host's own.
/// </summary>
/// <remarks>
/// The facts are per connection and already cached by Kestrel: the transport and ALPN show in the request protocol
/// (<c>HTTP/3</c> = QUIC, always TLS 1.3), the TCP TLS version in <see cref="ITlsHandshakeFeature.Protocol"/>.
/// A few comparisons, 0 B; the feature is only read for hosts that require TLS 1.3.
/// </remarks>
public static class ConnectionTlsCheck
{
    /// <summary>Whether the request's connection satisfies <paramref name="policy"/>. Call it for HTTPS requests only.</summary>
    public static bool Satisfies(HttpContext context, TlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        string protocol = context.Request.Protocol;
        SslProtocols negotiated = policy.MinimumVersion == TlsMinimumVersion.Tls13 && !HttpProtocol.IsHttp3(protocol)
            ? context.Features.Get<ITlsHandshakeFeature>()?.Protocol ?? SslProtocols.None
            : SslProtocols.None;
        return Satisfies(policy, protocol, negotiated);
    }

    /// <summary>The comparison itself: <paramref name="protocol"/> is the HTTP protocol of the request,
    /// <paramref name="tcpVersion"/> the TLS version of a TCP connection (ignored for HTTP/3).</summary>
    public static bool Satisfies(TlsPolicy policy, string protocol, SslProtocols tcpVersion)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (HttpProtocol.IsHttp3(protocol))
        {
            // QUIC: TLS 1.3 and ALPN h3 by definition. The host must allow HTTP/3.
            return policy.Http3Enabled;
        }

        if (policy.TcpAlpn == TcpAlpn.Http11Only && HttpProtocol.IsHttp2(protocol))
        {
            return false;
        }

        if (policy.MinimumVersion == TlsMinimumVersion.Tls13 && tcpVersion != SslProtocols.Tls13)
        {
            return false;
        }

        // TODO(M6): a required client certificate (policy.ClientCertificate) must have been validated on this
        // connection too (ITlsConnectionFeature.ClientCertificate plus the validator's verdict), else 421.
        return true;
    }
}
