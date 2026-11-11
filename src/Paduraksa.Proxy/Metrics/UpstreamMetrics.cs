namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// An upstream's slot and its destinations' slots, resolved once per compile and shared by every route that points at it.
/// A request finds its destination's slot by YARP's destination key, which the compiler formats from the destination's ID.
/// </summary>
public sealed class UpstreamMetrics
{
    private readonly string[] _destinationKeys;
    private readonly MetricSlot[] _destinationSlots;

    public UpstreamMetrics(MetricSlot slot, string[] destinationKeys, MetricSlot[] destinationSlots)
    {
        ArgumentNullException.ThrowIfNull(destinationKeys);
        ArgumentNullException.ThrowIfNull(destinationSlots);
        if (destinationKeys.Length != destinationSlots.Length)
        {
            throw new ArgumentException("Every destination key needs a slot.", nameof(destinationSlots));
        }

        Slot = slot;
        _destinationKeys = destinationKeys;
        _destinationSlots = destinationSlots;
    }

    public MetricSlot Slot { get; }

    /// <summary>The slot of the destination YARP calls <paramref name="destinationKey"/>, or none. A scan: an upstream has a handful, and the keys are the very strings YARP holds. 0 B.</summary>
    public MetricSlot DestinationSlot(string? destinationKey)
    {
        if (destinationKey is not null)
        {
            for (var i = 0; i < _destinationKeys.Length; i++)
            {
                if (string.Equals(_destinationKeys[i], destinationKey, StringComparison.OrdinalIgnoreCase))
                {
                    return _destinationSlots[i];
                }
            }
        }

        return MetricSlot.None;
    }
}
