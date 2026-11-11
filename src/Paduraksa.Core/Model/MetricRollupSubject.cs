namespace Paduraksa.Core.Model;

/// <summary>What a metric rollup row (a minute of figures) is about. Stored as its number: never renumber.</summary>
public enum MetricRollupSubject
{
    /// <summary>The whole process: every host and the requests no host owns.</summary>
    Total = 0,

    /// <summary>One host by its <see cref="HostId"/>; an empty ID is the requests no host owns.</summary>
    Host = 1,

    /// <summary>One upstream by its <see cref="UpstreamId"/>.</summary>
    Upstream = 2,
}
