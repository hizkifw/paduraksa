namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// One (policy, host) bucket scope as the compiler resolved it: the slot the pair's buckets live under, the policy's
/// numbers, and the stable identity the slot belongs to.
/// </summary>
/// <param name="Slot">The pair's table slot (never 0).</param>
/// <param name="Parameters">The policy's rate and burst.</param>
/// <param name="Identity">The stable slot identity: the policy ID's string form, '/' and the host ID's.</param>
public readonly record struct RateLimitRegistration(ushort Slot, RateLimitParameters Parameters, string Identity);

/// <summary>
/// The process's rate-limit state: the bucket table, the stable slot registry and the counters. One instance per
/// process, shared by the compiler (which asks for slots), the publisher (which publishes the live parameters and
/// retires the pairs that are gone) and every compiled policy (which takes or peeks a token).
/// </summary>
/// <remarks>
/// <para><b>Slots are the compiler's, parameters are the publisher's.</b> A compile assigns every (policy, host) pair a
/// slot through the append-only registry, so a pair keeps its buckets across publishes. Only a snapshot that actually
/// goes live publishes its slots' parameters and retires the pairs it no longer has; a dry run or a draft therefore
/// leaves the live set alone.</para>
/// <para><b>Retire and reuse.</b> A retired pair's slot is not reusable until the registry's grace period has passed,
/// and the buckets of the slot are cleared before it is handed out again (PLAN §6.3).</para>
/// </remarks>
public sealed class RateLimitRuntime
{
    /// <summary>The runtime a compiler without one uses: a table of its own, so a policy still works in a test.</summary>
    public static RateLimitRuntime Default { get; } = new();

    private readonly Lock _publishGate = new();
    private string[] _published = [];

    public RateLimitRuntime()
        : this(new RateLimitTable())
    {
    }

    public RateLimitRuntime(RateLimitTable table, RateLimitPolicySlots? slots = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        Table = table;
        Slots = slots ?? new RateLimitPolicySlots(table);
    }

    public RateLimitTable Table { get; }

    public RateLimitPolicySlots Slots { get; }

    /// <summary>Process-lifetime counters, shared by every policy; the security page will read them.</summary>
    public RateLimitStats Stats { get; } = new();

    /// <summary>The pair's stable slot, assigning one on the compiler's cold path.</summary>
    public ushort SlotFor(string identity) => Slots.GetOrAdd(identity, RateLimitClock.NowMs);

    /// <summary>
    /// Makes <paramref name="registrations"/> the live set: re-asserts each pair's slot (an older publish may have retired
    /// it), publishes their parameters in one swap, and retires every pair the previous set had and this one doesn't, so
    /// the sweeper clears those buckets. Called by the publisher at its swap, just before the snapshot goes live; an
    /// empty set retires everything.
    /// </summary>
    /// <returns>The pairs whose slot could not be re-asserted because another identity now holds it. Their parameters
    /// are left out of the published set, so the sweeper doesn't clear another policy's buckets by their window; the
    /// pair starts on a fresh slot at the next compile. The caller logs the count.</returns>
    public int Publish(ReadOnlySpan<RateLimitRegistration> registrations)
    {
        lock (_publishGate)
        {
            var highest = 0;
            foreach (var registration in registrations)
            {
                highest = Math.Max(highest, registration.Slot);
            }

            var bySlot = new RateLimitParameters[highest + 1];
            var identities = new string[registrations.Length];
            var now = RateLimitClock.NowMs;
            var conflicts = 0;
            for (var i = 0; i < registrations.Length; i++)
            {
                var registration = registrations[i];
                identities[i] = registration.Identity;
                if (Slots.Reclaim(registration.Identity, registration.Slot, now))
                {
                    bySlot[registration.Slot] = registration.Parameters;
                }
                else
                {
                    conflicts++;
                }
            }

            Slots.Publish(new RateLimitPolicyParameters(bySlot));

            foreach (var identity in _published)
            {
                if (Array.IndexOf(identities, identity) < 0)
                {
                    Slots.Retire(identity, now);
                }
            }

            _published = identities;
            return conflicts;
        }
    }
}
