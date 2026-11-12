namespace Paduraksa.Application.Analytics;

/// <summary>What the analytics job is told (<c>Paduraksa:Analytics</c>, validated at startup by the server).</summary>
public sealed record AnalyticsOptions
{
    /// <summary>How long a stretch is kept; older rows are deleted. Five-minute rows are kept only <see cref="FineRetention"/> (or this, if shorter), the hourly rows the whole of it.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(30);

    /// <summary>How often closed buckets are written, and hours that have passed are rolled up.</summary>
    public TimeSpan WriteInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often rows past retention are deleted.</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>How long the five-minute rows are kept once their hour has been rolled up. The day range reads them; the longer ranges read the hourly rows.</summary>
    public TimeSpan FineRetention { get; init; } = TimeSpan.FromDays(2);
}
