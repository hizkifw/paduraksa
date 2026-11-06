using System.Security.Authentication;

namespace Paduraksa.Proxy.Tls;

/// <summary>Why a handshake was refused by Paduraksa (as opposed to failing inside TLS itself).</summary>
public enum TlsRejectionReason
{
    /// <summary>No exact, wildcard or default entry for the SNI (or no SNI and no default).</summary>
    UnknownServerName = 0,

    /// <summary>QUIC only: the matching host opted out of HTTP/3.</summary>
    Http3Disabled = 1,

    /// <summary>QUIC only: the connection-stage gate denied the remote endpoint.</summary>
    ConnectionDenied = 2,
}

/// <summary>
/// Thrown from a handshake callback to reject the connection. Throwing is the supported way to reject (spike S1, Q6):
/// TCP clients see EOF, QUIC clients a <c>user_canceled</c> TLS alert, and the server stays healthy. Returning null
/// instead crashes into a <see cref="NullReferenceException"/> inside Kestrel.
/// </summary>
/// <remarks>
/// The client-supplied SNI is kept in <see cref="ServerName"/>, not in the message, so it can't end up in log text
/// unescaped.
/// </remarks>
public sealed class TlsHandshakeRejectedException : AuthenticationException
{
    public TlsHandshakeRejectedException()
        : this(TlsRejectionReason.UnknownServerName, string.Empty)
    {
    }

    public TlsHandshakeRejectedException(string message)
        : base(message)
    {
        ServerName = string.Empty;
    }

    public TlsHandshakeRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ServerName = string.Empty;
    }

    public TlsHandshakeRejectedException(TlsRejectionReason reason, string serverName)
        : base(MessageFor(reason))
    {
        Reason = reason;
        ServerName = serverName;
    }

    public TlsRejectionReason Reason { get; }

    /// <summary>The SNI the client sent (empty when none).</summary>
    public string ServerName { get; }

    private static string MessageFor(TlsRejectionReason reason) => reason switch
    {
        TlsRejectionReason.UnknownServerName => "TLS handshake rejected: no certificate for the requested server name.",
        TlsRejectionReason.Http3Disabled => "TLS handshake rejected: HTTP/3 is disabled for the requested server name.",
        TlsRejectionReason.ConnectionDenied => "TLS handshake rejected: connection denied.",
        _ => "TLS handshake rejected.",
    };
}
