namespace Paduraksa.Core.Options;

/// <summary>
/// A consumer's options that may be replaced while the process runs (a server setting changed from the dashboard): the consumer reads
/// <see cref="Current"/> each time it acts, never a copy taken at construction. A plain options instance converts to one that never changes,
/// which is what a test or a fixed composition passes.
/// </summary>
public sealed class LiveOptions<T>
    where T : class
{
    private T _current;

    public LiveOptions(T initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The options in force; replaced as a whole, so one read sees one consistent set.</summary>
    public T Current => Volatile.Read(ref _current);

    /// <summary>Raised after <see cref="Set"/> replaced the options, on the caller's thread; a handler must not block.</summary>
    public event Action<T>? Changed;

    public void Set(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Volatile.Write(ref _current, value);
        Changed?.Invoke(value);
    }

#pragma warning disable CA1000, CA2225 // The conversion is the point: a fixed options instance stands in for live ones, and `new` is the named alternative.
    public static implicit operator LiveOptions<T>(T value) => new(value);
#pragma warning restore CA1000, CA2225
}
