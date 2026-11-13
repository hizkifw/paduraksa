using Microsoft.Extensions.Logging;

using Paduraksa.Proxy.RateLimiting;
using Paduraksa.Proxy.Snapshot;

namespace Paduraksa.Proxy.Net;

/// <summary>What the connection stage decided about one connection.</summary>
public enum ConnectionDecision : byte
{
    /// <summary>Let through, and nothing to give back: the stage is off for it, the peer is a trusted proxy, or the table had no entry.</summary>
    Admitted = 0,

    /// <summary>Let through and counted: <see cref="ConnectionRuntime.Release"/> must follow when it closes.</summary>
    Counted,

    /// <summary>Refused: the address is in the connection deny list.</summary>
    Denied,

    /// <summary>Refused: the address already holds the per-IP cap.</summary>
    OverCap,
}

/// <summary>
/// The process's connection-stage state: the per-address table and the counters, one per process like the rate-limit
/// runtime and shared by the TCP middleware, the QUIC gate and the security overview. The snapshot holds only the
/// <see cref="ConnectionPolicy"/> (the cap and the deny list); a publish never touches what is counted (docs/design/config.md "State that survives a publish").
/// </summary>
/// <remarks>
/// Refusals are counted for every connection and logged at most once per <see cref="LogInterval"/> and reason, with
/// how many there were, so a flood doesn't become a log flood.
/// </remarks>
public sealed partial class ConnectionRuntime
{
    /// <summary>The shortest time between two log lines of one reason.</summary>
    public static readonly TimeSpan LogInterval = TimeSpan.FromSeconds(10);

    private const int Denies = 0;
    private const int Caps = 1;

    private readonly ILogger? _logger;
    private readonly TimeProvider _time;
    private readonly long[] _lastLogged = new long[2];
    private readonly long[] _sinceLog = new long[2];

    public ConnectionRuntime(ConnectionTable? table = null, ILogger? logger = null, TimeProvider? time = null)
    {
        Table = table ?? new ConnectionTable();
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The runtime a compiler or a test without one uses.</summary>
    public static ConnectionRuntime Default { get; } = new();

    public ConnectionTable Table { get; }

    public ConnectionStats Stats { get; } = new();

    /// <summary>
    /// Judges a connection that is about to be served, and counts it when it is capped. Order: the deny list (everyone, a
    /// trusted proxy too: it is the operator's explicit list), then the cap, which a trusted proxy is exempt from. 0 B.
    /// </summary>
    /// <param name="snapshot">The live snapshot.</param>
    /// <param name="address">The connection's address: the PROXY protocol source when there is one, else the socket's.</param>
    /// <param name="transport">The listener's transport, for the counters.</param>
    /// <param name="hash">What <see cref="Release"/> takes back when the result is <see cref="ConnectionDecision.Counted"/>.</param>
    public ConnectionDecision Admit(RuntimeSnapshot snapshot, IpKey address, ConnectionTransport transport, out ulong hash)
    {
        hash = 0;
        var policy = snapshot.Connections;
        if (IsDenied(policy, address, transport))
        {
            return ConnectionDecision.Denied;
        }

        if (policy.MaxPerIp == 0 || snapshot.TrustedProxies.IsTrustedPeer(address))
        {
            return ConnectionDecision.Admitted;
        }

        hash = RateLimitPartition.Shared.ForIp(address);
        switch (Table.TryAcquire(hash, policy.MaxPerIp))
        {
            case ConnectionSlot.Acquired:
                Stats.Opened();
                return ConnectionDecision.Counted;
            case ConnectionSlot.OverCap:
                Refused(Caps, transport, address, policy.MaxPerIp);
                return ConnectionDecision.OverCap;
            default:
                Stats.CountTableFull();
                return ConnectionDecision.Admitted;
        }
    }

    /// <summary>
    /// The decision for a QUIC handshake, which can't be counted there (Kestrel never says when a handshake that fails past
    /// the gate ends, <see cref="Paduraksa.Proxy.Tls.QuicConnectionGate"/>): the deny list, and the cap read without taking a
    /// place. The connection is counted when it is established (<see cref="Admit"/> in the connection middleware).
    /// </summary>
    public ConnectionDecision Check(RuntimeSnapshot snapshot, IpKey address, ConnectionTransport transport)
    {
        var policy = snapshot.Connections;
        if (IsDenied(policy, address, transport))
        {
            return ConnectionDecision.Denied;
        }

        if (policy.MaxPerIp == 0 || snapshot.TrustedProxies.IsTrustedPeer(address)
            || Table.Count(RateLimitPartition.Shared.ForIp(address)) < policy.MaxPerIp)
        {
            return ConnectionDecision.Admitted;
        }

        Refused(Caps, transport, address, policy.MaxPerIp);
        return ConnectionDecision.OverCap;
    }

    /// <summary>Gives back a connection <see cref="Admit"/> counted.</summary>
    public void Release(ulong hash)
    {
        Table.Release(hash);
        Stats.Closed();
    }

    private bool IsDenied(ConnectionPolicy policy, IpKey address, ConnectionTransport transport)
    {
        if (policy.Deny.IsEmpty || !policy.Deny.Contains(address))
        {
            return false;
        }

        Refused(Denies, transport, address, policy.MaxPerIp, policy.DenyListName);
        return true;
    }

    private void Refused(int reason, ConnectionTransport transport, IpKey address, int cap, string? list = null)
    {
        if (reason == Denies)
        {
            Stats.CountDenied(transport);
        }
        else
        {
            Stats.CountOverCap(transport);
        }

        if (_logger is null)
        {
            return;
        }

        Interlocked.Increment(ref _sinceLog[reason]);
        var now = _time.GetTimestamp();
        var last = Volatile.Read(ref _lastLogged[reason]);
        if ((last != 0 && _time.GetElapsedTime(last, now) < LogInterval)
            || Interlocked.CompareExchange(ref _lastLogged[reason], now, last) != last)
        {
            return;
        }

        var count = Interlocked.Exchange(ref _sinceLog[reason], 0);
        if (reason == Denies)
        {
            LogDenied(_logger, count, (int)LogInterval.TotalSeconds, address, list ?? string.Empty);
        }
        else
        {
            LogOverCap(_logger, count, (int)LogInterval.TotalSeconds, address, cap);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused {Count} connection(s) before TLS in the last {Seconds} s because the address is in the connection deny list '{List}'; the latest was from {Address}")]
    private static partial void LogDenied(ILogger logger, long count, int seconds, IpKey address, string list);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refused {Count} connection(s) in the last {Seconds} s because their address already holds the per-IP limit of {Limit}; the latest was from {Address}")]
    private static partial void LogOverCap(ILogger logger, long count, int seconds, IpKey address, int limit);
}
