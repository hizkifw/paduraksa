using System.Net.Security;

namespace Paduraksa.Proxy.Tls;

/// <summary>Lowest TLS version a host accepts over TCP. QUIC is always TLS 1.3, so this only affects TCP.</summary>
public enum TlsMinimumVersion
{
    Tls12 = 0,
    Tls13 = 1,
}

/// <summary>ALPN protocols a host offers over TCP. QUIC always offers <c>h3</c>.</summary>
public enum TcpAlpn
{
    /// <summary><c>h2</c>, then <c>http/1.1</c>.</summary>
    Http2AndHttp11 = 0,

    /// <summary><c>http/1.1</c> only (for clients or upstream setups that misbehave on HTTP/2).</summary>
    Http11Only = 1,
}

/// <summary>
/// Per-host TLS settings (PLAN §7.2 B). A value type in spirit: two hosts with equal policies (and the same
/// certificate) share one prebuilt <see cref="SslServerAuthenticationOptions"/> per transport.
/// </summary>
public sealed record TlsPolicy
{
    /// <summary>TLS 1.2+, <c>h2</c> + <c>http/1.1</c>, H3 enabled, no client certificate.</summary>
    public static TlsPolicy Default { get; } = new();

    public TlsMinimumVersion MinimumVersion { get; init; } = TlsMinimumVersion.Tls12;

    public TcpAlpn TcpAlpn { get; init; } = TcpAlpn.Http2AndHttp11;

    /// <summary>
    /// Whether the host is served over HTTP/3. When false the host has no QUIC entry, so a QUIC handshake for it
    /// is rejected (never served by the wildcard or default certificate), and it gets no <c>Alt-Svc</c>.
    /// </summary>
    public bool Http3Enabled { get; init; } = true;

    /// <summary>Client-certificate requirement, or null to not request one (e.g. Cloudflare Authenticated Origin Pulls).</summary>
    public ClientCertificatePolicy? ClientCertificate { get; init; }
}

/// <summary>
/// Requests a client certificate during the handshake. The shape exists for Cloudflare Authenticated Origin Pulls
/// (PLAN §7.6); the real validator (origin-pull CA) arrives in M6.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>No certificate presented: the handshake succeeds only when <see cref="Required"/> is false.</item>
/// <item>A certificate presented: <see cref="Validator"/> decides; without one, the platform's chain validation must
/// report no errors.</item>
/// </list>
/// Delegates compare by reference, so reuse one validator instance across hosts to keep their options shared.
/// </remarks>
public sealed record ClientCertificatePolicy
{
    public bool Required { get; init; } = true;

    public RemoteCertificateValidationCallback? Validator { get; init; }
}
