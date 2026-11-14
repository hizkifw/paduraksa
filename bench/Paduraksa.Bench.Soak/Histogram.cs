namespace Paduraksa.Bench.Soak;

/// <summary>
/// A fixed-size log-linear latency histogram (about 3% steps from 1 microsecond to 100 seconds), so a ten-second interval and a ten-minute window cost the same
/// and merge exactly. Values are microseconds.
/// </summary>
internal sealed class Histogram
{
    private const double Step = 1.03;
    private static readonly double LogStep = Math.Log(Step);
    private const int Buckets = 640;

    private readonly long[] _counts = new long[Buckets];
    private long _total;

    public long Count => Interlocked.Read(ref _total);

    public void Record(double microseconds)
    {
        var bucket = microseconds <= 1 ? 0 : Math.Min(Buckets - 1, (int)(Math.Log(microseconds) / LogStep));
        Interlocked.Increment(ref _counts[bucket]);
        Interlocked.Increment(ref _total);
    }

    public void MergeInto(Histogram target)
    {
        for (var i = 0; i < Buckets; i++)
        {
            var count = Interlocked.Read(ref _counts[i]);
            if (count != 0)
            {
                Interlocked.Add(ref target._counts[i], count);
                Interlocked.Add(ref target._total, count);
            }
        }
    }

    public double Percentile(double fraction)
    {
        var total = Interlocked.Read(ref _total);
        if (total == 0)
        {
            return double.NaN;
        }

        var target = Math.Max(1, (long)Math.Ceiling(total * fraction));
        long seen = 0;
        for (var i = 0; i < Buckets; i++)
        {
            seen += Interlocked.Read(ref _counts[i]);
            if (seen >= target)
            {
                // The bucket's upper edge: a percentile is never reported lower than it was.
                return Math.Pow(Step, i + 1);
            }
        }

        return Math.Pow(Step, Buckets);
    }
}
