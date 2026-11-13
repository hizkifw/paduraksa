using System.Net;
using Microsoft.AspNetCore.Connections;

namespace Paduraksa.ProxyProtocol.AspNetCore;

/// <summary>What the middleware decided about one connection; reported to <see cref="ProxyProtocolOptions.OnOutcome"/>.</summary>
public enum ProxyProtocolOutcome
{
    /// <summary>A header with a client address was accepted.</summary>
    Accepted,

    /// <summary>A valid header without an address (v2 <c>LOCAL</c>, <c>UNSPEC</c>, <c>UNIX</c>; v1 <c>UNKNOWN</c>) was accepted; the socket's endpoints stay.</summary>
    AcceptedWithoutAddress,

    /// <summary>No header, and <see cref="ProxyProtocolOptions.Required"/> is false: the connection went on unchanged.</summary>
    NoHeader,

    /// <summary>The socket peer isn't in <see cref="ProxyProtocolOptions.TrustedProxies"/> (or it is a Unix socket and
    /// <see cref="ProxyProtocolOptions.TrustUnixSockets"/> is false). With <see cref="ProxyProtocolOptions.Required"/> the
    /// connection was aborted unread; without it, the connection went on unchanged and any header it sent was not honoured.</summary>
    UntrustedSource,

    /// <summary>A malformed or oversized header: the connection was aborted.</summary>
    Invalid,

    /// <summary>The header did not arrive within <see cref="ProxyProtocolOptions.HeaderTimeout"/>: the connection was aborted.</summary>
    TimedOut,

    /// <summary>The peer closed the connection before a complete header: nothing more to do.</summary>
    Closed,
}

/// <summary>Settings of <see cref="ProxyProtocolListenOptionsExtensions.UseProxyProtocol"/>. They are read once, when it is called.</summary>
public sealed class ProxyProtocolOptions
{
    /// <summary>Upper bound for <see cref="HeaderTimeout"/>.</summary>
    public static readonly TimeSpan MaxHeaderTimeout = TimeSpan.FromMinutes(1);

    private TimeSpan _headerTimeout = TimeSpan.FromSeconds(3);
    private int _maxV2Length = ProxyProtocolParser.DefaultMaxV2Length;

    /// <summary>
    /// The socket peers allowed to send a header: your load balancers. <b>Empty by default, which refuses every connection</b>
    /// when <see cref="Required"/> is true (and honours no header when it is false): a PROXY header from anyone else would let
    /// them claim any client address. An IPv4-mapped IPv6 peer (<c>::ffff:10.0.0.1</c>) is matched as IPv4.
    /// A Unix socket listener has no peer address: see <see cref="TrustUnixSockets"/>.
    /// </summary>
    public IList<IPNetwork> TrustedProxies { get; set; } = [];

    /// <summary>
    /// Whether a connection on a Unix domain socket listener may send a header. False by default, which refuses every such
    /// connection when <see cref="Required"/> is true: a Unix socket peer has no address for <see cref="TrustedProxies"/> to
    /// match. Set it when the socket's file permissions let only your proxy connect; they are the whole check.
    /// </summary>
    public bool TrustUnixSockets { get; set; }

    /// <summary>
    /// How long a trusted peer has to deliver the complete header; also how long it may stay silent when <see cref="Required"/> is
    /// false, because the first bytes decide whether a header is there. Default 3 s. Must be greater than zero and at most
    /// <see cref="MaxHeaderTimeout"/>.
    /// </summary>
    public TimeSpan HeaderTimeout
    {
        get => _headerTimeout;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxHeaderTimeout);
            _headerTimeout = value;
        }
    }

    /// <summary>Cap on a whole v2 header, TLVs included. Default <see cref="ProxyProtocolParser.DefaultMaxV2Length"/>; at least
    /// <see cref="ProxyProtocolParser.V2MaxLengthWithoutTlvs"/>, so every address family fits.</summary>
    public int MaxV2Length
    {
        get => _maxV2Length;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, ProxyProtocolParser.V2MaxLengthWithoutTlvs);
            _maxV2Length = value;
        }
    }

    /// <summary>
    /// True (the default): a connection without a valid header is aborted, and so is one from outside <see cref="TrustedProxies"/>.
    /// False: the header is optional. A trusted peer whose first bytes don't begin a header (or an untrusted one) goes on
    /// unchanged, with the socket's endpoints; a header that begins correctly and then breaks still aborts.
    /// </summary>
    public bool Required { get; set; } = true;

    /// <summary>Called once per connection with what was decided and the connection, for counters and logs. Keep it fast and make it thread-safe.
    /// For a refused connection <c>RemoteEndPoint</c> is the socket peer that was refused; for an accepted one the endpoints are already the
    /// header's, and <see cref="IProxyProtocolFeature.ProxyEndPoint"/> is the proxy. An exception it throws is swallowed (and logged at debug
    /// level when the host has a logger): a counter doesn't decide the fate of a connection.</summary>
    public Action<ProxyProtocolOutcome, ConnectionContext>? OnOutcome { get; set; }
}
