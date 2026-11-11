using Microsoft.AspNetCore.Http;

using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;

namespace Paduraksa.Proxy.Metrics;

/// <summary>Settles a request's <see cref="GateOutcome"/> when it ends.</summary>
public static class GateOutcomes
{
    /// <summary>
    /// What the gate did: the outcome a policy or the forward recorded on the request state, else one worked out from what the
    /// request is - the connection was dropped, no host matched, the host answers by itself (a Redirect or Respond host, the
    /// HTTPS redirect, robots.txt), or the status says which limit or lookup refused it. 0 B.
    /// </summary>
    public static GateOutcome Classify(GateRequestState state, int status)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Outcome != GateOutcome.Unset)
        {
            return state.Outcome;
        }

        if (state.Aborted)
        {
            return state.Host is null ? GateOutcome.UnknownHost : GateOutcome.Dropped;
        }

        if (state.Host is not { } host)
        {
            return GateOutcome.UnknownHost;
        }

        if (host.Kind is not (HostKind.Proxy or HostKind.Admin))
        {
            return GateOutcome.Answered;
        }

        // Only the gate's own answers reach here: a forward set its outcome, a policy's refusal set its kind.
        return status switch
        {
            StatusCodes.Status421MisdirectedRequest => GateOutcome.RefusedConnection,
            StatusCodes.Status403Forbidden when host.OriginLockdown != OriginLockdownMode.Off => GateOutcome.RefusedConnection,
            StatusCodes.Status413PayloadTooLarge or StatusCodes.Status414UriTooLong or StatusCodes.Status431RequestHeaderFieldsTooLarge => GateOutcome.RefusedRequestLimits,
            StatusCodes.Status404NotFound when state.Route is null => GateOutcome.NoRoute,
            StatusCodes.Status503ServiceUnavailable => GateOutcome.Unavailable,
            _ => GateOutcome.Answered,
        };
    }
}
