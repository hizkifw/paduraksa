using Microsoft.Extensions.Hosting;

namespace Paduraksa.Proxy.RateLimiting;

/// <summary>
/// Walks <see cref="RateLimitTable"/> periodically and clears what no request will use again: the buckets of a retired
/// policy's slot (at once) and those untouched for longer than their policy's full-refill window (burst / rate), where
/// the bucket would be full again anyway (docs/design/waf.md "Rate limiting").
/// </summary>
/// <remarks>
/// <para>The sweeper reads the table without locking, like a request does, and clears an entry only if its state is still
/// the one it judged stale (see <see cref="RateLimitTable.ClearIfUnchanged"/>). So a request that arrives between the read
/// and the clear wins, and the sweep's cost per entry is one plain read; an entry it decides to clear costs one CAS. A
/// pass over the default table reads about 8 MiB, on the order of a millisecond, once every <see cref="SweepInterval"/>.</para>
/// <para>A slot that is live but has no published parameters yet is left alone: the compiler publishes the parameters
/// before its snapshot can route a request, so there is no window in which its buckets leak.</para>
/// </remarks>
public sealed class RateLimitSweeper : BackgroundService
{
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(30);

    private readonly RateLimitTable _table;
    private readonly RateLimitPolicySlots _slots;

    public RateLimitSweeper(RateLimitTable table, RateLimitPolicySlots slots)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(slots);
        _table = table;
        _slots = slots;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
                Sweep(RateLimitClock.NowMs);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping: the table goes with the process, so there is nothing left to clear.
        }
    }

    /// <summary>One pass at <paramref name="nowMs"/>; returns how many entries it cleared. Public so tests drive it.</summary>
    public int Sweep(long nowMs)
    {
        var parameters = _slots.Parameters;
        var cleared = 0;

        for (var i = 0; i < _table.Capacity; i++)
        {
            var key = _table.KeyAt(i);
            if (key == 0)
            {
                continue;
            }

            var slot = RateLimitTable.SlotOf(key);
            if (!_slots.IsLive(slot))
            {
                if (_table.ClearIfUnchanged(i, key, _table.StateAt(i)))
                {
                    cleared++;
                }

                continue;
            }

            if (!parameters.TryGet(slot, out var policy))
            {
                continue;
            }

            var state = _table.StateAt(i);
            // An entry touched after this pass read the clock has a time ahead of nowMs: not stale, whatever the mask says.
            var diff = nowMs - RateLimitTable.TimeOf(state);
            var age = diff < 0 ? 0 : diff & RateLimitTable.MaxTimeMs;
            if (age > policy.FullRefillMs && _table.ClearIfUnchanged(i, key, state))
            {
                cleared++;
            }
        }

        return cleared;
    }
}
