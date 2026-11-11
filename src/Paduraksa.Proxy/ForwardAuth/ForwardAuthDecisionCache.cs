namespace Paduraksa.Proxy.ForwardAuth;

/// <summary>A header the provider returned with an allow: copied to the upstream request.</summary>
public readonly record struct ForwardAuthHeader(string Name, string Value);

/// <summary>A remembered allow: what the provider said, until when. Immutable, so a reader that holds one needs no lock.</summary>
internal sealed class ForwardAuthAllowance(ulong key, long expiresAt, ForwardAuthHeader[] headers)
{
    public ulong Key { get; } = key;

    /// <summary>A <see cref="TimeProvider.GetTimestamp"/> value.</summary>
    public long ExpiresAt { get; } = expiresAt;

    public ForwardAuthHeader[] Headers { get; } = headers;
}

/// <summary>
/// The allows one provider has given, as a fixed table of immutable entries: a probe of four slots from the key's low bits, a lookup that
/// reads each slot once and believes an entry only if its key is the key asked for and it hasn't expired. A writer replaces an entry of the
/// same key, else an empty or expired slot, else the one that would lapse first, so memory is bounded and nothing is ever swept. Only an
/// allow is stored: a refusal is the provider's answer to a request with no session, and remembering it would keep a user at the sign-in
/// page after they signed in.
/// </summary>
/// <remarks>
/// The key is a keyed hash (<see cref="ForwardAuthKey"/>), so a client can't pick a request whose key collides with another's; the table
/// stores the whole 64-bit key and compares it, so the odds of a false hit are 2^-64 per probe. The table is per provider and per
/// configuration (<see cref="ForwardAuthProviderState"/>): a changed provider starts with an empty one.
/// </remarks>
internal sealed class ForwardAuthDecisionCache
{
    /// <summary>Entries per provider: a few hundred KiB at most, and thousands of sessions' worth of recent requests.</summary>
    internal const int Capacity = 4096;

    private const int Probe = 4;

    private readonly ForwardAuthAllowance?[] _slots = new ForwardAuthAllowance?[Capacity];

    public bool TryGet(ulong key, long now, out ForwardAuthAllowance allowance)
    {
        var start = (int)(key & (Capacity - 1));
        for (var i = 0; i < Probe; i++)
        {
            var entry = Volatile.Read(ref _slots[(start + i) & (Capacity - 1)]);
            if (entry is not null && entry.Key == key)
            {
                if (entry.ExpiresAt > now)
                {
                    allowance = entry;
                    return true;
                }

                break;
            }
        }

        allowance = null!;
        return false;
    }

    public void Add(ForwardAuthAllowance allowance, long now)
    {
        var start = (int)(allowance.Key & (Capacity - 1));
        var victim = start;
        var victimExpiry = long.MaxValue;
        for (var i = 0; i < Probe; i++)
        {
            var index = (start + i) & (Capacity - 1);
            var entry = Volatile.Read(ref _slots[index]);
            if (entry is null || entry.Key == allowance.Key || entry.ExpiresAt <= now)
            {
                Volatile.Write(ref _slots[index], allowance);
                return;
            }

            if (entry.ExpiresAt < victimExpiry)
            {
                victim = index;
                victimExpiry = entry.ExpiresAt;
            }
        }

        Volatile.Write(ref _slots[victim], allowance);
    }

    /// <summary>The entries that haven't expired, for the overview and tests: a walk of the table, never on the hot path.</summary>
    public int Count(long now)
    {
        var live = 0;
        foreach (var slot in _slots)
        {
            if (Volatile.Read(in slot) is { } entry && entry.ExpiresAt > now)
            {
                live++;
            }
        }

        return live;
    }
}
