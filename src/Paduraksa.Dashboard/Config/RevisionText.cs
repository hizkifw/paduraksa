using System.Globalization;

using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>How the history describes a revision: who made it, through what, when, and what it changed in a line.</summary>
public static class RevisionText
{
    /// <summary>What a change list does, counted: "1 host added · 2 routes changed".</summary>
    public static string Summary(IReadOnlyList<ConfigChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return "No changes";
        }

        var parts = changes
            .GroupBy(change => (Noun(change), change.Kind))
            .OrderBy(group => Rank(group.First()))
            .ThenBy(group => group.Key.Kind)
            .Select(group => $"{Count(group.Count(), group.Key.Item1)} {Verb(group.Key.Kind)}");
        return string.Join(" · ", parts);
    }

    /// <summary>Who made a revision: "You", a user's display name, "An API token", "Paduraksa", "The CLI".</summary>
    public static string Actor(ActorRef actor, IReadOnlyDictionary<UserId, string> users, UserId? me)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(users);
        return actor.Kind switch
        {
            ActorKind.User when actor.User == me => "You",
            ActorKind.User when actor.User is { } user && users.TryGetValue(user, out var name) => name,
            ActorKind.User => "Another user",
            ActorKind.Token => "An API token",
            ActorKind.Cli => "The CLI",
            _ => "Paduraksa",
        };
    }

    /// <summary>Where a change came in, in a word.</summary>
    public static string Channel(Channel channel) => channel switch
    {
        Core.Model.Channel.Dashboard => "Dashboard",
        Core.Model.Channel.Api => "API",
        Core.Model.Channel.Mcp => "MCP",
        Core.Model.Channel.Cli => "CLI",
        _ => "System",
    };

    /// <summary>"just now", "5 min ago", "3 h ago", "yesterday", else the date.</summary>
    public static string Ago(DateTimeOffset at, DateTimeOffset now)
    {
        var age = now - at;
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return age < TimeSpan.FromDays(2) ? "yesterday" : at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>A timestamp in full, for a tooltip (UTC, so every admin reads the same).</summary>
    public static string Exact(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

    private static string Noun(ConfigChange change) => change switch
    {
        HostChange => "host",
        RouteChange => "route",
        UpstreamChange => "upstream",
        DestinationChange => "destination",
        ProfileChange => "profile",
        SettingsChange => "global settings",
        _ => "format",
    };

    private static int Rank(ConfigChange change) => change switch
    {
        HostChange => 0,
        RouteChange => 1,
        UpstreamChange => 2,
        DestinationChange => 3,
        ProfileChange => 4,
        _ => 5,
    };

    private static string Verb(ConfigChangeKind kind) => kind switch
    {
        ConfigChangeKind.Added => "added",
        ConfigChangeKind.Removed => "removed",
        _ => "changed",
    };

    private static string Count(int count, string noun) =>
        noun == "global settings" ? noun : count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
