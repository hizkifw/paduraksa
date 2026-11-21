using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Honeypot;

/// <summary>
/// What the trap needs from outside the immutable snapshot: where hits go, the site factory, the counters and the cap on answers
/// generated at once. One instance per process; the snapshot compiler and the gate get the same one.
/// </summary>
public sealed partial class HoneypotRuntime
{
    private readonly ILogger? _logger;
    private int _inFlight;
    private int _reportedKeyUnavailable;

    public HoneypotRuntime(
        IHoneypotSink? sink = null,
        IHoneypotSiteFactory? siteFactory = null,
        int maxConcurrent = HoneypotLimits.DefaultMaxConcurrent,
        TimeProvider? time = null,
        ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrent, 1);
        Sink = sink ?? NullHoneypotSink.Instance;
        SiteFactory = siteFactory ?? new DefaultHoneypotSiteFactory();
        MaxConcurrent = maxConcurrent;
        Time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>What a compile or a gate uses when no runtime is given: it records nothing.</summary>
    public static HoneypotRuntime Default { get; } = new();

    public IHoneypotSink Sink { get; }

    public IHoneypotSiteFactory SiteFactory { get; }

    public HoneypotStats Stats { get; } = new();

    /// <summary>The most trap answers generated at once.</summary>
    public int MaxConcurrent { get; }

    public TimeProvider Time { get; }

    /// <summary>Takes one of the <see cref="MaxConcurrent"/> places; false when none is free. A taker that got true calls <see cref="Exit"/>.
    /// The handler releases its place once the page is rendered, before the body is written: the cap bounds generation, the CPU cost,
    /// and a slow reader holds no place.</summary>
    internal bool TryEnter()
    {
        if (Interlocked.Increment(ref _inFlight) <= MaxConcurrent)
        {
            return true;
        }

        Interlocked.Decrement(ref _inFlight);
        return false;
    }

    internal void Exit() => Interlocked.Decrement(ref _inFlight);

    /// <summary>
    /// Reports once per process that a host's trap can't exist because the proof-of-work key isn't open: its prefix and pages come
    /// from that key. The browser check says the same for its own routes.
    /// </summary>
    internal void ReportKeyUnavailable()
    {
        if (_logger is not null && Interlocked.Exchange(ref _reportedKeyUnavailable, 1) == 0)
        {
            LogKeyUnavailable(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The proof-of-work HMAC key is unavailable, so a host's honeypot can't derive its prefix and pages: one with a configured path answers 503 there, and one without is off. Check the vault and the startup log.")]
    private static partial void LogKeyUnavailable(ILogger logger);
}
