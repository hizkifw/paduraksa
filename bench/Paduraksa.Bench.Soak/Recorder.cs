using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Paduraksa.Bench.Soak;

/// <summary>Which latency series a request belongs to: the small ones are the drift measure, the rest are reported next to them.</summary>
internal enum Series
{
    Small,
    Big,
    Ws,
    Admin,
    Limited,
}

/// <summary>One ten-second interval of what the clients saw.</summary>
internal sealed class Interval
{
    public long Requests;
    public long Ok;
    public long Expected4xx;
    public long Unexpected5xx;
    public long Wrong;
    public long ConnectionErrors;
    public long Timeouts;
    public long Tolerated;
    public long Skipped;
    public readonly Histogram Small = new();
    public readonly Histogram Big = new();
    public readonly Histogram Ws = new();
    public readonly Histogram Limited = new();
    public readonly Histogram Admin = new();
}

/// <summary>What went wrong, once, with enough to find it in the log.</summary>
internal sealed record ErrorRecord(double T, string Kind, string Host, string Path, string Protocol, string Detail, bool Tolerated);

/// <summary>
/// The clients' ledger. A request's outcome lands in the current interval (swapped every ten seconds by the sampler), in the window histograms the drift check
/// reads, and, when it is an error, in a capped list that the facts file reports. Errors inside a window the plan expects them in are counted apart (tolerated).
/// </summary>
internal sealed class Recorder(Events events, SoakPlan plan)
{
    private const int ErrorCap = 300;
    private int _toleratedListed;
    private int _unexpectedListed;

    private readonly ConcurrentQueue<ErrorRecord> _errors = new();
    private readonly ConcurrentDictionary<string, long> _errorKinds = new();
    private readonly long[] _limitedOk = new long[plan.DurationSeconds + 600];
    private readonly long[] _limited429 = new long[plan.DurationSeconds + 600];
    private Interval _current = new();

    public Histogram Baseline { get; } = new();

    public Histogram Final { get; } = new();

    public Histogram BaselineBig { get; } = new();

    public Histogram FinalBig { get; } = new();

    public Tolerance Tolerance { get; } = new();

    public long TotalRequests;
    public long TotalUnexpected;
    public long TotalTolerated;
    public long TotalSkipped;

    public IReadOnlyCollection<ErrorRecord> Errors => _errors;

    public IReadOnlyDictionary<string, long> ErrorKinds => _errorKinds;

    public (long[] Ok, long[] Refused) LimitedPerSecond => (_limitedOk, _limited429);

    public Interval Swap() => Interlocked.Exchange(ref _current, new Interval());

    public void Seconds(Interval interval, double from, double to)
    {
        if (from >= plan.BaselineStart && to <= plan.BaselineEnd)
        {
            interval.Small.MergeInto(Baseline);
            interval.Big.MergeInto(BaselineBig);
        }

        if (from >= plan.FinalStart && to <= plan.FinalEnd + 1)
        {
            interval.Small.MergeInto(Final);
            interval.Big.MergeInto(FinalBig);
        }
    }

    public void Skip()
    {
        Interlocked.Increment(ref _current.Skipped);
        Interlocked.Increment(ref TotalSkipped);
    }

    /// <summary>A request that came back as expected, or as one of the statuses the test wants.</summary>
    public void Done(Series series, double microseconds, int status, bool refusedByDesign, string host)
    {
        var current = _current;
        Interlocked.Increment(ref current.Requests);
        Interlocked.Increment(ref TotalRequests);
        if (refusedByDesign)
        {
            Interlocked.Increment(ref current.Expected4xx);
        }
        else
        {
            Interlocked.Increment(ref current.Ok);
        }

        switch (series)
        {
            case Series.Small:
                current.Small.Record(microseconds);
                break;
            case Series.Big:
                current.Big.Record(microseconds);
                break;
            case Series.Ws:
                current.Ws.Record(microseconds);
                break;
            case Series.Limited:
                current.Limited.Record(microseconds);
                var second = (int)Math.Max(0, events.ElapsedSeconds);
                if (second < _limitedOk.Length)
                {
                    Interlocked.Increment(ref status == 429 ? ref _limited429[second] : ref _limitedOk[second]);
                }

                break;
        }
    }

    public void Admin(double microseconds) => _current.Admin.Record(microseconds);

    public void Fail(string kind, string host, string path, string protocol, string detail)
    {
        var current = _current;
        Interlocked.Increment(ref current.Requests);
        Interlocked.Increment(ref TotalRequests);
        var t = events.ElapsedSeconds;
        var tolerated = Tolerance.Applies(t, host);
        if (tolerated)
        {
            Interlocked.Increment(ref current.Tolerated);
            Interlocked.Increment(ref TotalTolerated);
        }
        else
        {
            Interlocked.Increment(ref TotalUnexpected);
            switch (kind)
            {
                case "status-5xx":
                    Interlocked.Increment(ref current.Unexpected5xx);
                    break;
                case "timeout":
                    Interlocked.Increment(ref current.Timeouts);
                    break;
                case "wrong" or "status":
                    Interlocked.Increment(ref current.Wrong);
                    break;
                default:
                    Interlocked.Increment(ref current.ConnectionErrors);
                    break;
            }
        }

        _errorKinds.AddOrUpdate((tolerated ? "tolerated:" : "unexpected:") + kind, 1, static (_, n) => n + 1);
        // The unexpected ones are what a reader looks for, so they have a list of their own and the tolerated ones cannot crowd them out.
        if (tolerated ? _toleratedListed < ErrorCap / 3 : _unexpectedListed < ErrorCap)
        {
            _ = tolerated ? Interlocked.Increment(ref _toleratedListed) : Interlocked.Increment(ref _unexpectedListed);
            _errors.Enqueue(new ErrorRecord(Math.Round(t, 2), kind, host, path, protocol, detail, tolerated));
        }
    }

    public static string KindOf(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            switch (e)
            {
                case SocketException socket:
                    return socket.SocketErrorCode switch
                    {
                        SocketError.ConnectionRefused => "connection-refused",
                        SocketError.ConnectionReset => "connection-reset",
                        SocketError.TimedOut => "timeout",
                        _ => "socket-" + socket.SocketErrorCode,
                    };
                case TaskCanceledException or OperationCanceledException:
                    return "timeout";
            }
        }

        return ex is HttpRequestException ? "http-error" : "io-error";
    }

    public JsonObject Summary() => new()
    {
        ["requests"] = Interlocked.Read(ref TotalRequests),
        ["unexpected"] = Interlocked.Read(ref TotalUnexpected),
        ["tolerated"] = Interlocked.Read(ref TotalTolerated),
        ["skipped"] = Interlocked.Read(ref TotalSkipped),
    };
}

/// <summary>The windows in which errors are expected: the restart (from the stop signal to ready, plus a grace) and, for the flap hosts, the flap and its recovery.</summary>
internal sealed class Tolerance
{
    private volatile int _restartFrom = int.MaxValue;
    private volatile int _restartTo = int.MaxValue;
    private volatile int _flapFrom = int.MaxValue;
    private volatile int _flapTo = int.MaxValue;
    private volatile HashSet<string> _flapHosts = [];

    public void Restart(int fromSeconds, int toSeconds)
    {
        _restartFrom = fromSeconds;
        _restartTo = toSeconds;
    }

    public void Flap(int fromSeconds, int toSeconds, IEnumerable<string> hosts)
    {
        _flapHosts = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _flapFrom = fromSeconds;
        _flapTo = toSeconds;
    }

    public bool Applies(double t, string host) =>
        (t >= _restartFrom - 1 && t <= _restartTo) || (t >= _flapFrom - 1 && t <= _flapTo && _flapHosts.Contains(host));
}
