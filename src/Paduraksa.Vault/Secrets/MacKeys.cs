using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Vault.Crypto;

namespace Paduraksa.Vault.Secrets;

/// <summary>
/// <see cref="IMacKeySource"/>: a handle is a reference to the secret, so opening one reads only metadata, and the key
/// material lives in <see cref="MacKeyCache"/>.
/// </summary>
internal sealed class MacKeySource(VaultRuntime runtime, MacKeyCache cache) : IMacKeySource
{
    public async ValueTask<IMacKey?> OpenAsync(SecretId id, SecretKind expected, CancellationToken cancellationToken)
    {
        if (!SecretKinds.IsMac(expected))
        {
            return null;
        }

        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        var context = await runtime.CreateContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var found = await context.Secrets.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => new { s.Kind, s.MacAlgorithm })
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return found is { MacAlgorithm: { } algorithm } && found.Kind == expected ? new MacKeyHandle(id, algorithm, cache) : null;
        }
    }
}

/// <summary>An <see cref="IMacKey"/>: the secret's ID, its fixed algorithm, and the cache that holds the current key.</summary>
internal sealed class MacKeyHandle(SecretId id, MacAlgorithm algorithm, MacKeyCache cache) : IMacKey
{
    public MacAlgorithm Algorithm => algorithm;

    public int TagLength => MacKeyMaterial.TagLengthOf(algorithm);

    public int Sign(ReadOnlySpan<byte> data, Span<byte> tag)
    {
        var length = TagLength;
        if (tag.Length < length)
        {
            throw new ArgumentException($"The tag buffer must hold at least {length} bytes.", nameof(tag));
        }

        var state = cache.Acquire(id);
        try
        {
            return MacKeyMaterial.Compute(algorithm, state.Current, data, tag);
        }
        finally
        {
            state.Release();
        }
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> tag)
    {
        var length = TagLength;
        if (tag.Length != length)
        {
            return false;
        }

        var state = cache.Acquire(id);
        try
        {
            Span<byte> expected = stackalloc byte[MacKeyMaterial.MaxTagLength];
            expected = expected[..MacKeyMaterial.Compute(algorithm, state.Current, data, expected)];
            var matches = CryptographicOperations.FixedTimeEquals(expected, tag);

            // Both candidates are always computed when there is a previous version in its window, so the time taken
            // does not tell which version made the tag.
            if (state.HasPrevious && cache.IsWithinGrace(state))
            {
                Span<byte> old = stackalloc byte[MacKeyMaterial.MaxTagLength];
                old = old[..MacKeyMaterial.Compute(algorithm, state.Previous, data, old)];
                matches |= CryptographicOperations.FixedTimeEquals(old, tag);
            }

            return matches;
        }
        finally
        {
            state.Release();
        }
    }
}

/// <summary>The HMAC computations, by algorithm.</summary>
internal static class MacKeyMaterial
{
    public const int MaxTagLength = 64;

    public static int TagLengthOf(MacAlgorithm algorithm) => algorithm switch
    {
        MacAlgorithm.HmacSha256 => HMACSHA256.HashSizeInBytes,
        MacAlgorithm.HmacSha384 => HMACSHA384.HashSizeInBytes,
        MacAlgorithm.HmacSha512 => HMACSHA512.HashSizeInBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Undefined MAC algorithm."),
    };

    public static int Compute(MacAlgorithm algorithm, ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> destination) => algorithm switch
    {
        MacAlgorithm.HmacSha256 => HMACSHA256.HashData(key, data, destination),
        MacAlgorithm.HmacSha384 => HMACSHA384.HashData(key, data, destination),
        MacAlgorithm.HmacSha512 => HMACSHA512.HashData(key, data, destination),
        _ => throw new ArgumentOutOfRangeException(nameof(algorithm), algorithm, "Undefined MAC algorithm."),
    };
}

/// <summary>
/// The decrypted MAC keys the handles use, because <see cref="IMacKey.Sign"/> is synchronous and allocation-free and a
/// database read plus two AES-GCM operations per call would be neither. This is the one deliberate exception to "the
/// vault unseals per use": a MAC key's plaintext stays resident, pinned, while its handles are in use. It stays inside this
/// class and is zeroed when replaced, deleted or the process ends.
/// </summary>
/// <remarks>
/// An entry is re-read when this process replaces or deletes the secret (<see cref="Invalidate"/>) and otherwise after
/// <see cref="VaultPolicy.MacCacheMaxAge"/>, so a change another process made is seen within that time; the re-read is a
/// version check, and the key is only unsealed again if the version moved.
///
/// <b>Rotation does not stop MAC operations.</b> A KEK rotation re-wraps keys but changes no MAC plaintext, so a cached key
/// stays correct throughout. A refresh needs the vault's gate (it unseals under the KEK); if a rotation holds it, the
/// refresh of an entry that already exists does not wait and happens on a later call, and only the very first use of a
/// secret waits.
///
/// <b>Concurrency.</b> Reads (<see cref="Acquire"/>) take no lock; every change to the table happens under one lock, so each
/// state is retired exactly once, by whoever removed it. A refresh reads the database outside the lock and remembers the
/// <i>epoch</i> it started in; <see cref="Invalidate"/> advances the epoch, and a refresh that finds it changed throws its
/// state away instead of installing it (it may be older than the replacement or deletion the invalidate was for). States are
/// immutable and reference-counted: a <c>Sign</c> in flight holds a count, so the key is zeroed only when the last user is
/// done, and a retired state can no longer be acquired.
/// </remarks>
internal sealed class MacKeyCache(SecretReader reader, VaultRuntime runtime) : IDisposable
{
    private readonly ConcurrentDictionary<SecretId, MacKeyState> _entries = new();

    private readonly Lock _install = new();

    private long _epoch;

    /// <summary>The state of the secret with a count taken for the caller, who must <see cref="MacKeyState.Release"/> it.</summary>
    /// <exception cref="InvalidOperationException">The secret no longer exists.</exception>
    public MacKeyState Acquire(SecretId id)
    {
        while (true)
        {
            _entries.TryGetValue(id, out var state);
            if (state is null || IsStale(state))
            {
                state = Refresh(id, state);
            }

            if (state is not null && state.TryAcquire())
            {
                return state;
            }

            // The state was retired under us, or a refresh lost a race with an invalidate: look again.
        }
    }

    /// <summary>Drops the entry for a secret that was replaced or deleted here, so the next use reads it again.</summary>
    public void Invalidate(SecretId id)
    {
        lock (_install)
        {
            _epoch++;
            if (_entries.TryGetValue(id, out var state) && _entries.TryRemove(new KeyValuePair<SecretId, MacKeyState>(id, state)))
            {
                state.Retire();
            }
        }
    }

    public bool IsWithinGrace(MacKeyState state) => runtime.Time.GetUtcNow() < state.PreviousUntil;

    public void Dispose()
    {
        foreach (var id in _entries.Keys)
        {
            Invalidate(id);
        }
    }

    private bool IsStale(MacKeyState state) =>
        runtime.Time.GetElapsedTime(state.CheckedAt) >= runtime.Policy.MacCacheMaxAge
        || (state.HasPrevious && !IsWithinGrace(state));

    /// <summary>The refreshed (or confirmed) state, or null if it must be looked up again.</summary>
    private MacKeyState? Refresh(SecretId id, MacKeyState? seen)
    {
        var epoch = Volatile.Read(ref _epoch);

        UnsealedSecret? secret;
        if (seen is null)
        {
            secret = reader.Open(id, includePrevious: true);
        }
        else
        {
            secret = reader.OpenWithoutWaiting(id, includePrevious: true, out var busy);
            if (busy)
            {
                // A rotation holds the gate and does not change MAC plaintext: keep using what is cached.
                return seen;
            }
        }

        if (secret is null)
        {
            Invalidate(id);
            throw new InvalidOperationException($"The secret {id} no longer exists.");
        }

        using (secret)
        {
            var row = secret.Row;
            if (seen is not null && seen.Version == row.Version && seen.HasPrevious == (secret.Previous is not null))
            {
                seen.MarkChecked(runtime.Time.GetTimestamp());
                return seen;
            }

            var fresh = new MacKeyState(
                row.Version,
                secret.Current,
                secret.Previous,
                row.PreviousRetiredAt is { } retiredAt ? retiredAt + runtime.Policy.MacGraceWindow : DateTimeOffset.MinValue,
                runtime.Time.GetTimestamp());

            var installed = false;
            lock (_install)
            {
                if (Volatile.Read(ref _epoch) == epoch)
                {
                    _entries.TryGetValue(id, out var existing);
                    if (ReferenceEquals(existing, seen))
                    {
                        _entries[id] = fresh;
                        existing?.Retire();
                        installed = true;
                    }
                }
            }

            if (!installed)
            {
                // Lost: someone installed or invalidated meanwhile. Nothing else holds this state, so it is ours to zero.
                fresh.Retire();
                return null;
            }

            if (secret.Previous is null && row.PreviousSealed is not null)
            {
                // Its grace window has ended: take the stale previous value out of the database too.
                runtime.PurgeExpiredPrevious();
            }

            return fresh;
        }
    }
}

/// <summary>One immutable snapshot of a MAC secret's keys: the current one and, within its window, the one before.</summary>
internal sealed class MacKeyState
{
    private readonly byte[] _current;

    private readonly byte[]? _previous;

    private int _references = 1;

    private int _retired;

    private long _checkedAt;

    public MacKeyState(int version, SecretBuffer current, SecretBuffer? previous, DateTimeOffset previousUntil, long checkedAt)
    {
        Version = version;
        PreviousUntil = previousUntil;
        _checkedAt = checkedAt;
        _current = GC.AllocateArray<byte>(current.Length, pinned: true);
        current.Span.CopyTo(_current);
        if (previous is not null)
        {
            _previous = GC.AllocateArray<byte>(previous.Length, pinned: true);
            previous.Span.CopyTo(_previous);
        }
    }

    public int Version { get; }

    /// <summary>When the previous key stops verifying.</summary>
    public DateTimeOffset PreviousUntil { get; }

    public long CheckedAt => Interlocked.Read(ref _checkedAt);

    public bool HasPrevious => _previous is not null;

    public ReadOnlySpan<byte> Current => _current;

    public ReadOnlySpan<byte> Previous => _previous;

    public void MarkChecked(long timestamp) => Interlocked.Exchange(ref _checkedAt, timestamp);

    /// <summary>Takes a count; fails if the state has been retired or its keys are already zeroed.</summary>
    public bool TryAcquire()
    {
        if (Volatile.Read(ref _retired) != 0)
        {
            return false;
        }

        int seen;
        do
        {
            seen = Volatile.Read(ref _references);
            if (seen <= 0)
            {
                return false;
            }
        }
        while (Interlocked.CompareExchange(ref _references, seen + 1, seen) != seen);

        // Retired between the check and the count: give it back, the caller must not use it.
        if (Volatile.Read(ref _retired) != 0)
        {
            Release();
            return false;
        }

        return true;
    }

    /// <summary>Gives back a count taken by <see cref="TryAcquire"/>. Never goes below zero, so the keys cannot be zeroed twice or while counted.</summary>
    public void Release()
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref _references);
            if (seen <= 0)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _references, seen - 1, seen) != seen);

        if (seen == 1)
        {
            CryptographicOperations.ZeroMemory(_current);
            if (_previous is not null)
            {
                CryptographicOperations.ZeroMemory(_previous);
            }
        }
    }

    /// <summary>The table's own count: no new user can acquire the state afterwards, and the keys are zeroed once the last one releases. Idempotent.</summary>
    public void Retire()
    {
        if (Interlocked.Exchange(ref _retired, 1) == 0)
        {
            Release();
        }
    }
}
