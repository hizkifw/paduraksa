namespace Paduraksa.Proxy.Metrics;

/// <summary>
/// The objects a compiled snapshot holds slots for, as the compiler resolved them: what the publisher hands the registry just
/// before the snapshot goes live (it re-asserts these slots and retires every other live object's).
/// </summary>
public sealed class MetricRegistrations
{
    /// <summary>A snapshot with no metrics: publishing it retires every slot.</summary>
    public static MetricRegistrations Empty { get; } = new();

    private readonly Dictionary<Guid, int>[] _bySubject = [[], [], [], []];

    internal bool Add(MetricSubject subject, Guid id, int slot) => _bySubject[(int)subject].TryAdd(id, slot);

    internal KeyValuePair<Guid, int>[] Of(MetricSubject subject)
    {
        var entries = _bySubject[(int)subject];
        var copy = new KeyValuePair<Guid, int>[entries.Count];
        ((ICollection<KeyValuePair<Guid, int>>)entries).CopyTo(copy, 0);
        return copy;
    }

    public int Count(MetricSubject subject) => _bySubject[(int)subject].Count;
}
