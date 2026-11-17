using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>The request-metric half of the startup history (docs/design/observability.md "Metrics").</summary>
internal sealed class MetricHistory : RollupHistory<MetricRollupSubject, MetricPoint>
{
    protected override bool TryOwner(MetricRollupSubject subject, string subjectId, out Guid owner)
    {
        owner = Guid.Empty;
        if (subjectId.Length == 0)
        {
            return true;
        }

        switch (subject)
        {
            case MetricRollupSubject.Host when HostId.TryParse(subjectId, null, out var host):
                owner = host.Value;
                return true;
            case MetricRollupSubject.Upstream when UpstreamId.TryParse(subjectId, null, out var upstream):
                owner = upstream.Value;
                return true;
            default:
                return false;
        }
    }
}
