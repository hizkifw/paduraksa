using Microsoft.Extensions.Primitives;

using Paduraksa.Proxy.Tls;

namespace Paduraksa.Proxy.Snapshot;

/// <summary>
/// The one live <see cref="RuntimeSnapshot"/> of the process (PLAN §6.2): a volatile reference, read lock-free once
/// per request, swapped by a single writer. Starts at <see cref="RuntimeSnapshot.Bootstrap"/>, so
/// <see cref="Current"/> is never <see langword="null"/>.
/// <para>It is also the TLS handshake selector's <see cref="ICertificateTableSource"/>: each handshake reads the live
/// snapshot's <see cref="RuntimeSnapshot.Certificates"/>, so certificates are hot-swapped by publishing.</para>
/// </summary>
public sealed class SnapshotStore : IDisposable, ICertificateTableSource
{
    private readonly Lock _publishLock = new();
    private volatile RuntimeSnapshot _current = RuntimeSnapshot.Bootstrap;
    private CancellationTokenSource _changed = new();

    /// <summary>The live snapshot. One volatile read; take it once per request and use only that instance.</summary>
    public RuntimeSnapshot Current => _current;

    /// <summary>The live snapshot's certificate table (one volatile read).</summary>
    CertificateTable ICertificateTableSource.Current => _current.Certificates;

    /// <summary>A snapshot has been published (the bootstrap one doesn't count).</summary>
    public bool HasPublished => _current.Generation > 0;

    /// <summary>Fires once, after the next publish. Get a fresh token after it fires.</summary>
    public IChangeToken GetChangeToken() => new CancellationChangeToken(Volatile.Read(ref _changed).Token);

    /// <summary>
    /// Makes <paramref name="snapshot"/> live: atomic reference swap, then the change token fires. The snapshot must
    /// be bound and its generation must be greater than the current one. Writers are serialised; readers never block.
    /// <c>SnapshotPublisher</c> is the normal caller.
    /// </summary>
    public void Publish(RuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsBound)
        {
            throw new ArgumentException("Only a bound snapshot (generation and YARP endpoints set) can be published.", nameof(snapshot));
        }

        CancellationTokenSource fired;
        lock (_publishLock)
        {
            if (snapshot.Generation <= _current.Generation)
            {
                throw new InvalidOperationException(
                    $"Generation {snapshot.Generation} isn't newer than the live generation {_current.Generation}.");
            }

            _current = snapshot;
            fired = Interlocked.Exchange(ref _changed, new CancellationTokenSource());
        }

        // Not disposed: a reader may still be turning the old source into a token. Without timers there's nothing to free.
        fired.Cancel();
    }

    /// <summary>Releases the pending change token's source. The live snapshot stays readable.</summary>
    public void Dispose() => Volatile.Read(ref _changed).Dispose();
}
