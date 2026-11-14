using Paduraksa.Contracts.Notifications;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.Components;

/// <summary>How live upstream health reads on the dashboard: tones, words and the "last change" line.</summary>
public static class HealthDisplay
{
    public static Tone ToneOf(UpstreamHealthState state) => state switch
    {
        UpstreamHealthState.Down => Tone.Error,
        UpstreamHealthState.Degraded => Tone.Warning,
        UpstreamHealthState.Healthy => Tone.Success,
        _ => Tone.Default,
    };

    public static Tone ToneOf(DestinationHealthState state) => state switch
    {
        DestinationHealthState.Unhealthy => Tone.Error,
        DestinationHealthState.Healthy => Tone.Success,
        _ => Tone.Default,
    };

    public static string Word(UpstreamHealthState state) => state switch
    {
        UpstreamHealthState.Down => "Down",
        UpstreamHealthState.Degraded => "Degraded",
        UpstreamHealthState.Healthy => "Healthy",
        UpstreamHealthState.Unknown => "Checking",
        _ => "Not health-checked",
    };

    public static string Word(DestinationHealthState state) => state switch
    {
        DestinationHealthState.Unhealthy => "Unhealthy",
        DestinationHealthState.Healthy => "Healthy",
        _ => "Not checked yet",
    };

    /// <summary>"Last change: Down, 3 min ago (active probe: HTTP 503)"; the exact time is the tooltip's.</summary>
    public static string LastChange(HealthChangeView change, DateTimeOffset now, DisplayFormat? format = null)
    {
        ArgumentNullException.ThrowIfNull(change);
        var reason = change.Reason is { Length: > 0 } why ? $" ({why})" : string.Empty;
        return $"Last change: {change.Summary}, {(format ?? DisplayFormat.Invariant).Ago(change.At, now)}{reason}";
    }

    /// <summary>Whether two readings say the same, so a poll that found nothing new renders nothing.</summary>
    public static bool Same(IReadOnlyList<UpstreamHealthView>? a, IReadOnlyList<UpstreamHealthView>? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Id != y.Id || x.State != y.State || x.LastChange != y.LastChange || x.Destinations.Count != y.Destinations.Count)
            {
                return false;
            }

            for (var d = 0; d < x.Destinations.Count; d++)
            {
                if (x.Destinations[d] != y.Destinations[d])
                {
                    return false;
                }
            }
        }

        return true;
    }
}
