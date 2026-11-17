using Paduraksa.Core.Model;

namespace Paduraksa.Proxy.Metrics;

/// <summary>The security counterpart of <see cref="MetricHistory"/>.</summary>
internal sealed class SecurityHistory : RollupHistory<SecurityRollupSubject, SecurityPoint>
{
    protected override bool TryOwner(SecurityRollupSubject subject, string subjectId, out Guid owner)
    {
        owner = Guid.Empty;
        switch (subject)
        {
            case SecurityRollupSubject.Total when subjectId.Length == 0:
                return true;
            case SecurityRollupSubject.AuthProvider when AuthProviderId.TryParse(subjectId, null, out var provider):
                owner = provider.Value;
                return true;
            case SecurityRollupSubject.RuleSet when RuleSetId.TryParse(subjectId, null, out var set):
                owner = set.Value;
                return true;
            default:
                return false;
        }
    }
}
