using System.Security.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Tls;

/// <summary>Why a connection is weaker than a host's <see cref="TlsPolicy"/> (what a 421 means).</summary>
public enum TlsMismatch : byte
{
    /// <summary>The connection satisfies the policy.</summary>
    None = 0,

    /// <summary>The host isn't served over HTTPS at all (<c>CompiledHost.Https</c> is false).</summary>
    HostNotServedOverHttps,

    /// <summary>The request came over HTTP/3 and the host opted out of it.</summary>
    Http3Disabled,

    /// <summary>The request came over HTTP/2 and the host offers <c>http/1.1</c> only.</summary>
    Http2NotOffered,

    /// <summary>The host requires TLS 1.3 and this TCP connection negotiated less.</summary>
    TlsVersionTooLow,
}

/// <summary>
/// Enforces a host's <see cref="TlsPolicy"/> per <b>request</b>. The handshake applies the policy of
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
    public static bool Satisfies(HttpContext context, TlsPolicy policy) => Check(context, policy) == TlsMismatch.None;

    /// <summary>What <see cref="Satisfies(HttpContext, TlsPolicy)"/> finds wrong with the connection, or
    /// <see cref="TlsMismatch.None"/>. Call it for HTTPS requests only.</summary>
    public static TlsMismatch Check(HttpContext context, TlsPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);

        string protocol = context.Request.Protocol;
        SslProtocols negotiated = policy.MinimumVersion == TlsMinimumVersion.Tls13 && !HttpProtocol.IsHttp3(protocol)
            ? context.Features.Get<ITlsHandshakeFeature>()?.Protocol ?? SslProtocols.None
            : SslProtocols.None;
        return Check(policy, protocol, negotiated);
    }

    /// <summary>The comparison itself: <paramref name="protocol"/> is the HTTP protocol of the request,
    /// <paramref name="tcpVersion"/> the TLS version of a TCP connection (ignored for HTTP/3).</summary>
    public static bool Satisfies(TlsPolicy policy, string protocol, SslProtocols tcpVersion) =>
        Check(policy, protocol, tcpVersion) == TlsMismatch.None;

    /// <summary>The comparison itself, naming the first thing that doesn't match.</summary>
    public static TlsMismatch Check(TlsPolicy policy, string protocol, SslProtocols tcpVersion)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (HttpProtocol.IsHttp3(protocol))
        {
            // QUIC: TLS 1.3 and ALPN h3 by definition. The host must allow HTTP/3.
            return policy.Http3Enabled ? TlsMismatch.None : TlsMismatch.Http3Disabled;
        }

        if (policy.TcpAlpn == TcpAlpn.Http11Only && HttpProtocol.IsHttp2(protocol))
        {
            return TlsMismatch.Http2NotOffered;
        }

        if (policy.MinimumVersion == TlsMinimumVersion.Tls13 && tcpVersion != SslProtocols.Tls13)
        {
            return TlsMismatch.TlsVersionTooLow;
        }

        // TODO: a required client certificate (policy.ClientCertificate) must have been validated on this
        // connection too (ITlsConnectionFeature.ClientCertificate plus the validator's verdict), else 421.
        return TlsMismatch.None;
    }
}
