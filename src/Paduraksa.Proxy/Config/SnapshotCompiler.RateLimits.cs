using Paduraksa.Core.Model;
using Paduraksa.Proxy.Gate;
using Paduraksa.Proxy.RateLimiting;

namespace Paduraksa.Proxy.Config;

/// <summary>The named rate-limit policies, and the (policy, host) pairs the compiled hosts and routes use.</summary>
public static partial class SnapshotCompiler
{
    /// <summary>The document's policies by ID; a duplicate (invalid) keeps the first.</summary>
    private static Dictionary<RateLimitPolicyId, RateLimitPolicySpec> CompileRateLimitPolicies(IReadOnlyList<RateLimitPolicySpec> specs)
    {
        Dictionary<RateLimitPolicyId, RateLimitPolicySpec> byId = [];
        foreach (var spec in specs)
        {
            byId.TryAdd(spec.Id, spec);
        }

        return byId;
    }

    /// <summary>
    /// One (policy, host) pair as a route policy: the slot is the pair's stable registry slot, so two hosts that name
    /// the same policy keep separate buckets while routes of one host that resolve to it share theirs. A reference to a
    /// policy the model doesn't carry is a compile error (the validator reports it first).
    /// </summary>
    private static RateLimitPolicy? CompileRateLimitPolicy(
        CompileContext context,
        HostSpec host,
        RateLimitPolicyId policyId,
        bool routeOverride,
        CompileErrorSource where,
        Dictionary<RateLimitPolicyId, RateLimitPolicy> cache)
    {
        if (cache.TryGetValue(policyId, out var existing))
        {
            return existing;
        }

        if (!context.RateLimitPolicies.TryGetValue(policyId, out var spec))
        {
            context.Errors.Add(new(where, $"rate-limit policy '{policyId}' doesn't exist"));
            return null;
        }

        // Stable identities only: the policy's ID and the host's, never an array position, so a pair keeps its buckets
        // across every publish (docs/design/config.md "State that survives a publish").
        var identity = policyId.ToString() + "/" + host.Id.ToString();
        // Slot 0 is a placeholder for a compile that isn't published: the policy reports a new, full bucket and the gate never runs this snapshot.
        if (!context.TryTakeSlot(where, () => context.RateLimits.SlotFor(identity), () => context.RateLimits.Slots.TryGetSlot(identity, out var found) ? found : (ushort)0, out var slot))
        {
            return null;
        }

        var policy = new RateLimitPolicy(
            spec,
            slot,
            spec.Header,
            routeOverride,
            context.RateLimits.Table,
            RateLimitPartition.Shared,
            context.RateLimits.Stats);
        cache.Add(policyId, policy);
        if (slot != 0)
        {
            context.RateLimitRegistrations.Add(new RateLimitRegistration(slot, spec.Parameters, identity));
        }

        return policy;
    }
}
