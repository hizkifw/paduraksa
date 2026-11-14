using Paduraksa.Contracts.Notifications;
using Paduraksa.Core.Model;
using Paduraksa.Core.Options;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Notifications;

/// <summary>What a reported health change is about.</summary>
internal enum HealthScope
{
    Destination,
    Upstream,
}

/// <summary>A state change worth an audit entry and a notification, after the debounce and the flap guard.</summary>
/// <param name="Scope">The whole upstream or one destination.</param>
/// <param name="Upstream">The upstream.</param>
/// <param name="Destination">The destination, for <see cref="HealthScope.Destination"/>.</param>
/// <param name="Address">The destination's address.</param>
/// <param name="Down">The new state.</param>
/// <param name="Since">When the new state began (before the hold that confirmed it).</param>
/// <param name="Reason">Why, where YARP says.</param>
/// <param name="Flapping">The object just began flapping: this is the last report until it settles.</param>
/// <param name="Settled">A flapping object has been still long enough: this is the state it settled in.</param>
/// <param name="Readings">The upstream's destinations as they stood.</param>
internal sealed record HealthChange(
    HealthScope Scope,
    UpstreamId Upstream,
    DestinationId? Destination,
    string? Address,
    bool Down,
    DateTimeOffset Since,
    string? Reason,
    bool Flapping,
    bool Settled,
    IReadOnlyList<DestinationReading> Readings);

/// <summary>
/// Turns YARP's state, read every second, into the changes worth reporting. Pure: the clock is a parameter and nothing here does I/O.
/// </summary>
/// <remarks>
/// <para>
/// <b>Debounce.</b> A new state is reported only after it has held for <see cref="NotificationOptions.HealthHold"/>, so a blip of one failed
/// probe or a passive verdict that lapses and returns reports nothing. <b>Flap guard.</b> An object (a destination or an upstream) that changes
/// <see cref="NotificationOptions.FlapThreshold"/> times within <see cref="NotificationOptions.FlapWindow"/> is reported once as flapping, and then
/// stays silent until it has been still for <see cref="NotificationOptions.FlapSettle"/>, when its state is reported once more. A
/// dead destination that YARP retries every minute (passive health) therefore costs a handful of messages, not one an hour per minute.
/// </para>
/// <para>
/// <b>What is not a change.</b> A destination first seen healthy or unknown is nothing, so a new destination (which starts Unknown, and which YARP
/// routes to) and a publish that keeps the IDs (which keeps YARP's state) report nothing; one first seen unhealthy is down. A removed destination
/// or upstream is forgotten without a word, so re-adding it cannot report a recovery. An upstream with no health check configured is never tracked.
/// An upstream's change replaces its destinations' changes of the same direction in the same tick (one message says which destinations).
/// </para>
/// </remarks>
internal sealed class HealthTracker(LiveOptions<NotificationOptions> options)
{
    private readonly Dictionary<UpstreamId, UpstreamTrack> _upstreams = [];

    /// <summary>The changes to report as of <paramref name="now"/>, and every checked upstream's live view.</summary>
    public (IReadOnlyList<HealthChange> Changes, IReadOnlyList<UpstreamHealthView> Views) Observe(IReadOnlyList<UpstreamReading> readings, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(readings);
        List<HealthChange> changes = [];
        List<UpstreamHealthView> views = [];
        HashSet<UpstreamId> seen = [];
        foreach (var reading in readings)
        {
            if (!reading.HasChecks)
            {
                _upstreams.Remove(reading.Id);
                views.Add(new UpstreamHealthView(reading.Id, UpstreamHealthState.Unchecked, [.. reading.Destinations.Select(d => ViewOf(d, null))], null));
                continue;
            }

            seen.Add(reading.Id);
            if (!_upstreams.TryGetValue(reading.Id, out var track))
            {
                track = new UpstreamTrack(options);
                _upstreams[reading.Id] = track;
            }

            var upstreamStep = track.Whole.Step(reading.NoneAvailable, now, null);
            List<HealthChange> destinationChanges = [];
            HashSet<DestinationId> present = [];
            foreach (var destination in reading.Destinations)
            {
                present.Add(destination.Id);
                if (!track.Destinations.TryGetValue(destination.Id, out var machine))
                {
                    machine = new Machine(options);
                    track.Destinations[destination.Id] = machine;
                }

                var unhealthy = destination.Current == DestinationVerdict.Unhealthy;
                var step = machine.Step(unhealthy, now, ReasonOf(destination, unhealthy));
                if (step is not null)
                {
                    destinationChanges.Add(new HealthChange(
                        HealthScope.Destination, reading.Id, destination.Id, destination.Address, step.Down, step.Since, step.Reason, step.Flapping, step.Settled, reading.Destinations));
                }
            }

            foreach (var gone in track.Destinations.Keys.Where(id => !present.Contains(id)).ToList())
            {
                track.Destinations.Remove(gone);
            }

            if (upstreamStep is not null)
            {
                changes.Add(new HealthChange(
                    HealthScope.Upstream, reading.Id, null, null, upstreamStep.Down, upstreamStep.Since, null, upstreamStep.Flapping, upstreamStep.Settled, reading.Destinations));
                changes.AddRange(destinationChanges.Where(c => c.Down != upstreamStep.Down));
            }
            else
            {
                changes.AddRange(destinationChanges);
            }

            views.Add(ViewOf(reading, track));
        }

        foreach (var gone in _upstreams.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            _upstreams.Remove(gone);
        }

        return (changes, views);
    }

    /// <summary>Why a destination is in the state it is in, in words for a person.</summary>
    internal static string? ReasonOf(DestinationReading destination, bool down)
    {
        if (down)
        {
            return destination.Active == DestinationVerdict.Unhealthy
                ? destination.LastProbe is { Succeeded: false } probe ? $"active probe: {probe.Reason}" : "active probe failed"
                : destination.Passive == DestinationVerdict.Unhealthy ? "passive check: too many failed requests" : null;
        }

        return destination.Active == DestinationVerdict.Healthy && destination.LastProbe is { } passed
            ? $"active probe: {passed.Reason}"
            : destination.Passive == DestinationVerdict.Healthy ? "passive check: requests are succeeding" : null;
    }

    private static UpstreamHealthView ViewOf(UpstreamReading reading, UpstreamTrack track)
    {
        var destinations = reading.Destinations.Select(d => ViewOf(d, track.Destinations.GetValueOrDefault(d.Id))).ToList();
        var state = reading.NoneAvailable ? UpstreamHealthState.Down
            : destinations.Any(d => d.State == DestinationHealthState.Unhealthy) ? UpstreamHealthState.Degraded
            : destinations.Count == 0 || destinations.All(d => d.State == DestinationHealthState.Unknown) ? UpstreamHealthState.Unknown
            : UpstreamHealthState.Healthy;

        HealthChangeView? last = null;
        if (track.Whole.Last is { } whole)
        {
            last = new HealthChangeView(whole.At, whole.Down ? "Down" : "Back up", whole.Reason);
        }

        foreach (var destination in reading.Destinations)
        {
            if (track.Destinations.GetValueOrDefault(destination.Id)?.Last is { } change && (last is null || change.At > last.At))
            {
                last = new HealthChangeView(change.At, $"{destination.Address} {(change.Down ? "down" : "back up")}", change.Reason);
            }
        }

        return new UpstreamHealthView(reading.Id, state, destinations, last);
    }

    private static DestinationHealthView ViewOf(DestinationReading destination, Machine? machine)
    {
        var state = destination.Current switch
        {
            DestinationVerdict.Healthy => DestinationHealthState.Healthy,
            DestinationVerdict.Unhealthy => DestinationHealthState.Unhealthy,
            _ => DestinationHealthState.Unknown,
        };
        return new DestinationHealthView(destination.Id, destination.Address, state, ReasonOf(destination, state == DestinationHealthState.Unhealthy), machine?.Last?.At, machine?.Flapping ?? false);
    }

    private sealed class UpstreamTrack(LiveOptions<NotificationOptions> options)
    {
        public Machine Whole { get; } = new(options);

        public Dictionary<DestinationId, Machine> Destinations { get; } = [];
    }

    /// <summary>A reportable step of one <see cref="Machine"/>.</summary>
    internal sealed record Step(bool Down, DateTimeOffset Since, string? Reason, bool Flapping, bool Settled);

    /// <summary>The last accepted change of an object, for the "last change" line.</summary>
    internal sealed record Accepted(DateTimeOffset At, bool Down, string? Reason);

    /// <summary>One up/down state with its debounce and flap guard (see the type's remarks).</summary>
    internal sealed class Machine(LiveOptions<NotificationOptions> options)
    {
        private readonly Queue<DateTimeOffset> _changes = new();
        private bool? _down;
        private bool? _pending;
        private DateTimeOffset _pendingSince;
        private DateTimeOffset _lastChange;

        public bool Flapping { get; private set; }

        public Accepted? Last { get; private set; }

        public Step? Step(bool down, DateTimeOffset now, string? reason)
        {
            if (_down is null)
            {
                _down = down;
                _lastChange = now;
                if (!down)
                {
                    return null;
                }

                Last = new Accepted(now, true, reason);
                return new Step(true, now, reason, Flapping: false, Settled: false);
            }

            if (Flapping)
            {
                if (down != _down)
                {
                    _down = down;
                    _lastChange = now;
                    Last = new Accepted(now, down, reason);
                }

                if (now - _lastChange < options.Current.FlapSettle)
                {
                    return null;
                }

                Flapping = false;
                _changes.Clear();
                _pending = null;
                return new Step(_down.Value, _lastChange, Last?.Reason, Flapping: false, Settled: true);
            }

            if (down == _down)
            {
                _pending = null;
                return null;
            }

            if (_pending != down)
            {
                _pending = down;
                _pendingSince = now;
            }

            if (now - _pendingSince < options.Current.HealthHold)
            {
                return null;
            }

            _down = down;
            _pending = null;
            _lastChange = now;
            Last = new Accepted(_pendingSince, down, reason);
            _changes.Enqueue(now);
            while (_changes.Count > 0 && now - _changes.Peek() > options.Current.FlapWindow)
            {
                _changes.Dequeue();
            }

            if (_changes.Count >= options.Current.FlapThreshold)
            {
                Flapping = true;
                return new Step(down, _pendingSince, reason, Flapping: true, Settled: false);
            }

            return new Step(down, _pendingSince, reason, Flapping: false, Settled: false);
        }
    }
}
