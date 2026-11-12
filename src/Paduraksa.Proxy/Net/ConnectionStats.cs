namespace Paduraksa.Proxy.Net;

/// <summary>Which transport a connection-stage decision was made for.</summary>
public enum ConnectionTransport : byte
{
    Tcp = 0,
    Quic,
}

/// <summary>
/// Process-lifetime counters of the connection stage, lock-free and read through <see cref="Volatile"/>: the security
/// overview and <c>/metrics</c> read them. A count is one <see cref="Interlocked"/> operation and allocates nothing.
/// </summary>
public sealed class ConnectionStats
{
    private long _deniedTcp;
    private long _deniedQuic;
    private long _overCapTcp;
    private long _overCapQuic;
    private long _tableFull;
    private long _open;

    /// <summary>TCP connections refused for being in the connection deny list.</summary>
    public long DeniedTcp => Volatile.Read(ref _deniedTcp);

    /// <summary>QUIC handshakes refused for being in the connection deny list.</summary>
    public long DeniedQuic => Volatile.Read(ref _deniedQuic);

    /// <summary>TCP connections refused because their address already held the per-IP cap.</summary>
    public long OverCapTcp => Volatile.Read(ref _overCapTcp);

    /// <summary>QUIC connections refused for the same reason.</summary>
    public long OverCapQuic => Volatile.Read(ref _overCapQuic);

    /// <summary>Connections let through uncounted because the table had no entry for their address.</summary>
    public long TableFull => Volatile.Read(ref _tableFull);

    /// <summary>Connections the table counts now (a level, not a total).</summary>
    public long Open => Volatile.Read(ref _open);

    public long Denied => DeniedTcp + DeniedQuic;

    public long OverCap => OverCapTcp + OverCapQuic;

    internal void CountDenied(ConnectionTransport transport) =>
        Interlocked.Increment(ref transport == ConnectionTransport.Tcp ? ref _deniedTcp : ref _deniedQuic);

    internal void CountOverCap(ConnectionTransport transport) =>
        Interlocked.Increment(ref transport == ConnectionTransport.Tcp ? ref _overCapTcp : ref _overCapQuic);

    internal void CountTableFull() => Interlocked.Increment(ref _tableFull);

    internal void Opened() => Interlocked.Increment(ref _open);

    internal void Closed() => Interlocked.Decrement(ref _open);
}
