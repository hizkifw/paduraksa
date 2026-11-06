namespace Paduraksa.Proxy.Tls;

/// <summary>
/// Where the handshake callbacks read the current <see cref="CertificateTable"/> from: one volatile read per
/// handshake. The snapshot store implements this (returning its current snapshot's table); swapping the table only
/// affects new handshakes, never listeners or established connections.
/// </summary>
public interface ICertificateTableSource
{
    CertificateTable Current { get; }
}

/// <summary>A standalone <see cref="ICertificateTableSource"/>: a volatile reference swapped atomically.</summary>
public sealed class CertificateTableSource : ICertificateTableSource
{
    private volatile CertificateTable _current;

    public CertificateTableSource(CertificateTable initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    public CertificateTable Current => _current;

    /// <summary>Publishes <paramref name="table"/> for every handshake that starts afterwards; returns the previous table.</summary>
    public CertificateTable Swap(CertificateTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Interlocked.Exchange(ref _current, table);
    }
}
