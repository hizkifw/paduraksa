using System.Diagnostics;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Paduraksa.Proxy.Logging;

/// <summary>
/// Drains <see cref="AccessLogRing"/> into the <see cref="IAccessLogSink"/>: the one consumer of the ring, a
/// background service of the core host, so no request ever formats or writes a line.
/// </summary>
/// <remarks>
/// <para><b>Never a request's problem.</b> A sink that throws (a full disk, a closed file) is reported at Warning,
/// rate-limited, and the line skipped: the drain keeps going, so one bad write never stops the log.</para>
/// <para><b>Drops.</b> A full ring drops entries and counts them (<see cref="AccessLogRing.Dropped"/>). The count since the
/// last report goes to the process log at Warning at most once a minute, so a log that can't keep up is visible without
/// filling the process log with it.</para>
/// </remarks>
public sealed partial class AccessLogDrainService : BackgroundService
{
    /// <summary>How often the ring is drained. Short enough that a line appears while a person is still reading the log.</summary>
    public static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The least interval between two reports of the same failure (dropped lines, a failing sink).</summary>
    public static readonly TimeSpan ReportInterval = TimeSpan.FromMinutes(1);

    private readonly AccessLogRing _ring;
    private readonly IAccessLogSink _sink;
    private readonly AccessLogFormatOptions _options;
    private readonly ILogger<AccessLogDrainService> _logger;

    // The line buffer, reused: a request's line never allocates here.
    private readonly char[] _buffer = new char[AccessLogFormatter.MaxLineLength];

    private long _lastDropReport;
    private long _lastFailureReport;
    private long _pendingDrops;

    public AccessLogDrainService(AccessLogRing ring, IAccessLogSink sink, AccessLogFormatOptions options, ILogger<AccessLogDrainService> logger)
    {
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _ring = ring;
        _sink = sink;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(DrainInterval, stoppingToken).ConfigureAwait(false);
                Drain();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The process is stopping: what is left is written below.
        }
        finally
        {
            Drain();
            Flush();
        }
    }

    /// <summary>
    /// One pass: writes everything the ring holds, then reports what it had to report. Public so tests can drain on their
    /// own thread without a host (and without waiting for the interval).
    /// <para>A pass that throws - a formatter bug rather than a failing sink, which is handled per line - is reported like
    /// any other failure and the next pass carries on: the drain is a background service of the core host, and a faulted
    /// <see cref="BackgroundService"/> would stop that host.</para>
    /// </summary>
    public void Drain()
    {
        try
        {
            DrainRing();
        }
#pragma warning disable CA1031 // A pass that fails must not stop the drain (or the core host): report it, rate-limited, and carry on.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFailure(ex);
        }
    }

    /// <summary>The pass itself.</summary>
    private void DrainRing()
    {
        while (_ring.TryRead(out var entry))
        {
            var length = AccessLogFormatter.Format(in entry, _options, _buffer);
            if (length < 0)
            {
                // Only reachable with a buffer smaller than MaxLineLength.
                continue;
            }

            try
            {
                _sink.WriteLine(_buffer.AsSpan(0, length));
            }
#pragma warning disable CA1031 // A sink that fails must not stop the drain (or the process): report it, rate-limited, and carry on.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                ReportFailure(ex);
            }
        }

        _pendingDrops += _ring.TakeDropped();
        if (_pendingDrops == 0)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        if (_lastDropReport != 0 && Stopwatch.GetElapsedTime(_lastDropReport, now) < ReportInterval)
        {
            // Keep counting: the next report says how many were lost in the whole interval.
            return;
        }

        LogDropped(_logger, _pendingDrops);
        _pendingDrops = 0;
        _lastDropReport = now;
    }

    /// <summary>Pushes what the sink buffers to its destination, at shutdown.</summary>
    public void Flush()
    {
        try
        {
            _sink.Flush();
        }
#pragma warning disable CA1031 // Shutdown must finish; the failure is reported and the process still stops.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFailure(ex);
        }
    }

    private void ReportFailure(Exception exception)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFailureReport != 0 && Stopwatch.GetElapsedTime(_lastFailureReport, now) < ReportInterval)
        {
            return;
        }

        _lastFailureReport = now;
        LogFailing(_logger, exception);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The access log isn't being written; lines are being skipped until it recovers")]
    private static partial void LogFailing(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Dropped} access-log lines were dropped: the ring is full (the writer can't keep up, or the disk is slow)")]
    private static partial void LogDropped(ILogger logger, long dropped);
}
