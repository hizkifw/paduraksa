namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// The parameters of the live rate-limit policies by slot: the compiler builds one of these per publish, and the
/// registry swaps it in atomically, so a reader (the sweeper) sees one whole set and never a half-updated one.
/// </summary>
public sealed class RateLimitPolicyParameters
{
    public static RateLimitPolicyParameters Empty { get; } = new([]);

    private readonly RateLimitParameters[] _bySlot;

    /// <remarks>The array is taken as it is, without a copy, and must not be mutated or kept by the caller: it is the
    /// published version from the moment it is handed to <see cref="RateLimitPolicySlots.Publish"/>.</remarks>
    public RateLimitPolicyParameters(RateLimitParameters[] bySlot)
    {
        ArgumentNullException.ThrowIfNull(bySlot);
        _bySlot = bySlot;
    }

    /// <summary>The array's length: the highest used slot plus one, not the number of policies.</summary>
    public int Count => _bySlot.Length;

    /// <summary>False for a slot past the array's end and for one whose entry is <c>default</c> (no policy).</summary>
    public bool TryGet(ushort slot, out RateLimitParameters parameters)
    {
        if (slot < _bySlot.Length && _bySlot[slot].Burst > 0)
        {
            parameters = _bySlot[slot];
            return true;
        }

        parameters = default;
        return false;
    }
}
