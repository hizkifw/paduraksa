namespace Paduraksa.Application.Metrics;

/// <summary>What the rollup job is told (<c>Paduraksa:Metrics</c>, validated at startup by the server).</summary>
public sealed record MetricRollupOptions
{
    /// <summary>The longest a minute is kept: older rows are deleted. At about 150 series and a few hundred bytes a row, a day is a few tens of MiB.</summary>
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(14);

    /// <summary>How often the finished minutes waiting in memory are written to the database.</summary>
    public TimeSpan WriteInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often rows past <see cref="Retention"/> are deleted.</summary>
    public TimeSpan PruneInterval { get; init; } = TimeSpan.FromHours(1);
}
