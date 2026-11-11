using System.Globalization;

namespace Paduraksa.Proxy.Net;

/// <summary>
/// The process's current list of Cloudflare's edge ranges, and how the last refreshes went. It is state, not config: a
/// refresh changes what the next compile reads without making a revision, and a snapshot only carries the list it was
/// compiled with. The refresh job lives outside Proxy (Proxy never touches the network or the disk for this); it calls
/// <see cref="Replace"/> and <see cref="RecordFailure"/>, and the compiler reads <see cref="Current"/>.
/// </summary>
public sealed class CloudflareEdgeRangeSource
{
    /// <summary>A list older than this is reported as stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(30);

    /// <summary>This many refreshes in a row failing is reported, whatever the list's age.</summary>
    public const int FailuresBeforeWarning = 3;

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private EdgeRangeList _current = EdgeRangeList.BuiltIn;
    private int _failures;
    private string? _lastFailure;

    public CloudflareEdgeRangeSource(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>The list in force: the built-in one until a fetched list is stored.</summary>
    public EdgeRangeList Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    /// <summary>Refreshes in a row that failed since the last good one.</summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _failures;
            }
        }
    }

    /// <summary>Why the last refresh failed; null after a good one.</summary>
    public string? LastFailure
    {
        get
        {
            lock (_lock)
            {
                return _lastFailure;
            }
        }
    }

    /// <summary>
    /// Takes <paramref name="list"/> as the current one and clears the failures. Returns true when its blocks differ from
    /// the list it replaced, which is when a recompile is worth it: a refetch of the same list only moves its age.
    /// </summary>
    public bool Replace(EdgeRangeList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        lock (_lock)
        {
            var changed = !_current.SameBlocksAs(list);
            _current = list;
            _failures = 0;
            _lastFailure = null;
            return changed;
        }
    }

    /// <summary>Notes a refresh that failed; the list in force stays.</summary>
    public void RecordFailure(string reason)
    {
        lock (_lock)
        {
            _failures++;
            _lastFailure = reason;
        }
    }

    /// <summary>The state to show: the list in force, its age and what to say about it.</summary>
    public EdgeRangeStatus Status()
    {
        EdgeRangeList list;
        int failures;
        string? lastFailure;
        lock (_lock)
        {
            list = _current;
            failures = _failures;
            lastFailure = _lastFailure;
        }

        return new EdgeRangeStatus(list, _time.GetUtcNow() - list.FetchedAt, failures, lastFailure);
    }
}

/// <summary>What <see cref="CloudflareEdgeRangeSource"/> reports at one moment.</summary>
/// <param name="List">The list in force.</param>
/// <param name="Age">How long ago it was fetched.</param>
/// <param name="ConsecutiveFailures">Refreshes in a row that failed since the last good one.</param>
/// <param name="LastFailure">Why the last one failed.</param>
public sealed record EdgeRangeStatus(EdgeRangeList List, TimeSpan Age, int ConsecutiveFailures, string? LastFailure)
{
    /// <summary>The list is older than <see cref="CloudflareEdgeRangeSource.StaleAfter"/>.</summary>
    public bool Stale => Age > CloudflareEdgeRangeSource.StaleAfter;

    /// <summary>Several refreshes in a row failed.</summary>
    public bool Failing => ConsecutiveFailures >= CloudflareEdgeRangeSource.FailuresBeforeWarning;

    /// <summary>The warning for a config that trusts Cloudflare's edge, or null when the list is fine. A stale or failing
    /// list lets a new Cloudflare range through as an untrusted peer: its visitors get the edge's address, and under
    /// origin lockdown a refusal.</summary>
    public string? Warning
    {
        get
        {
            if (!Stale && !Failing)
            {
                return null;
            }

            var which = List.Origin == EdgeRangeOrigin.BuiltIn ? "built-in list" : "stored list";
            var age = string.Create(CultureInfo.InvariantCulture, $"{(int)Age.TotalDays} days old");

            // No count: the text is part of a compile's warning, which is only compiled again when it changes.
            var failing = Failing ? $"; refreshes keep failing ({Cut(LastFailure)})" : string.Empty;
            return $"Cloudflare's edge ranges are the {which}, {age}{failing}. A new Cloudflare range would be treated as an untrusted peer";
        }
    }

    private static string Cut(string? text) => text is null ? "no reason" : text.Length <= 160 ? text : string.Concat(text.AsSpan(0, 160), "...");
}
