namespace Paduraksa.Proxy.Net;

/// <summary>
/// PROXY protocol settings for one TCP listener (PLAN §7.2 A). Enabling it makes the header <b>mandatory</b>:
/// a connection from outside <see cref="TrustedSources"/> is dropped without reading anything, and a trusted
/// source must send a valid header within <see cref="HeaderTimeout"/> or be dropped.
/// </summary>
public sealed class ProxyProtocolOptions
{
    /// <summary>Upper bound for <see cref="HeaderTimeout"/>. A header that takes longer is a broken or hostile peer
    /// (and a <see cref="CancellationTokenSource"/> can't take more than ~49.7 days anyway).</summary>
    public static readonly TimeSpan MaxHeaderTimeout = TimeSpan.FromMinutes(1);

    private CidrSet _trustedSources;

    public ProxyProtocolOptions(CidrSet trustedSources)
    {
        ArgumentNullException.ThrowIfNull(trustedSources);
        _trustedSources = trustedSources;
    }

    /// <summary>Socket peers allowed to send a header (the load balancers). Each new connection reads the current set
    /// once, so it can be swapped at runtime; in M1 nothing does yet (the listener config is fixed at startup), and the
    /// swap on publish arrives with the config store (M2/M7).</summary>
    public CidrSet TrustedSources
    {
        get => Volatile.Read(ref _trustedSources);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _trustedSources, value);
        }
    }

    /// <summary>How long a trusted source has to deliver the complete header. Default 3 s: load balancers send it
    /// immediately, so this only bounds slow or broken peers holding a connection slot. Must be greater than zero
    /// and at most <see cref="MaxHeaderTimeout"/>.</summary>
    public TimeSpan HeaderTimeout
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, MaxHeaderTimeout);
            field = value;
        }
    } = TimeSpan.FromSeconds(3);

    /// <summary>Cap on a whole v2 header including TLVs (see <see cref="ProxyProtocolParser.DefaultMaxV2Length"/>). At
    /// least <see cref="ProxyProtocolParser.V2MaxLengthWithoutTlvs"/>, so every address family still fits.</summary>
    public int MaxV2Length
    {
        get;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, ProxyProtocolParser.V2MaxLengthWithoutTlvs);
            field = value;
        }
    } = ProxyProtocolParser.DefaultMaxV2Length;

    /// <summary>Outcome counters for this listener (process lifetime; read by metrics).</summary>
    public ProxyProtocolCounters Counters { get; } = new();
}

/// <summary>Lock-free outcome counters for a PROXY protocol listener.</summary>
public sealed class ProxyProtocolCounters
{
    private long _accepted;
    private long _acceptedWithoutAddress;
    private long _untrustedSource;
    private long _invalid;
    private long _timedOut;
    private long _closed;

    /// <summary>Headers that carried a client address.</summary>
    public long Accepted => Volatile.Read(ref _accepted);

    /// <summary>Valid headers without an address (v2 LOCAL / UNSPEC / UNIX, v1 UNKNOWN).</summary>
    public long AcceptedWithoutAddress => Volatile.Read(ref _acceptedWithoutAddress);

    /// <summary>Connections dropped because the socket peer isn't a trusted source.</summary>
    public long UntrustedSource => Volatile.Read(ref _untrustedSource);

    /// <summary>Connections dropped for a malformed or oversized header.</summary>
    public long Invalid => Volatile.Read(ref _invalid);

    /// <summary>Connections dropped because the header didn't arrive in time.</summary>
    public long TimedOut => Volatile.Read(ref _timedOut);

    /// <summary>Connections closed by the peer before a complete header.</summary>
    public long Closed => Volatile.Read(ref _closed);

    internal void Count(ProxyProtocolReadStatus status, bool hasAddresses)
    {
        switch (status)
        {
            case ProxyProtocolReadStatus.Success when hasAddresses:
                Interlocked.Increment(ref _accepted);
                break;
            case ProxyProtocolReadStatus.Success:
                Interlocked.Increment(ref _acceptedWithoutAddress);
                break;
            case ProxyProtocolReadStatus.Invalid:
                Interlocked.Increment(ref _invalid);
                break;
            case ProxyProtocolReadStatus.TimedOut:
                Interlocked.Increment(ref _timedOut);
                break;
            default:
                Interlocked.Increment(ref _closed);
                break;
        }
    }

    internal void CountUntrustedSource() => Interlocked.Increment(ref _untrustedSource);
}
