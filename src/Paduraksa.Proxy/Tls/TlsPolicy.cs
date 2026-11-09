using System.Net.Security;
using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Per-host TLS settings. A value type in spirit: two hosts with equal policies (and the same
/// certificate) share one prebuilt <see cref="SslServerAuthenticationOptions"/> per transport. Undefined enum values
/// throw on init, rather than silently falling back to the weakest setting when the options are built.
/// </summary>
public sealed record TlsPolicy
{
    /// <summary>TLS 1.2+, <c>h2</c> + <c>http/1.1</c>, H3 enabled, no client certificate.</summary>
    public static TlsPolicy Default { get; } = new();

    /// <exception cref="ArgumentOutOfRangeException">On init, a value that isn't a defined <see cref="TlsMinimumVersion"/>.</exception>
    public TlsMinimumVersion MinimumVersion
    {
        get;
        init => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined TlsMinimumVersion.");
    } = TlsMinimumVersion.Tls12;

    /// <exception cref="ArgumentOutOfRangeException">On init, a value that isn't a defined <see cref="TcpAlpn"/>.</exception>
    public TcpAlpn TcpAlpn
    {
        get;
        init => field = Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "Not a defined TcpAlpn.");
    } = TcpAlpn.Http2AndHttp11;

    /// <summary>
    /// Whether the host is served over HTTP/3. When false the host has no QUIC entry, so a QUIC handshake naming it is
    /// rejected (never served by the wildcard or default certificate), it gets no <c>Alt-Svc</c>, and an HTTP/3 request
    /// for it on a QUIC connection negotiated for another name gets 421 (<see cref="ConnectionTlsCheck"/>).
    /// </summary>
    public bool Http3Enabled { get; init; } = true;

    /// <summary>Client-certificate requirement, or null to not request one (e.g. Cloudflare Authenticated Origin Pulls).</summary>
    public ClientCertificatePolicy? ClientCertificate { get; init; }
}

/// <summary>
/// Requests a client certificate during the handshake. The shape exists for Cloudflare Authenticated Origin Pulls; the
/// validator for that origin-pull CA is not built yet.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>No certificate presented: the handshake succeeds only when <see cref="Required"/> is false.</item>
/// <item>A certificate presented: <see cref="Validator"/> decides; without one, the platform's chain validation must
/// report no errors.</item>
/// </list>
/// Delegates compare by reference, so reuse one validator instance across hosts to keep their options shared.
/// Every combination of values is valid, so there is nothing to check on init.
/// </remarks>
public sealed record ClientCertificatePolicy
{
    public bool Required { get; init; } = true;

    public RemoteCertificateValidationCallback? Validator { get; init; }
}
