using System.Text.Json.Serialization;

using Paduraksa.Core.Model;

namespace Paduraksa.Contracts.Config;

/// <summary>
/// A named, ordered list of request rules that hosts and routes apply. Each rule has a path, methods and a user agent that a
/// request must all match, and an action for a request that does. The first matching rule decides. If no rule matches, the request
/// goes on through the rest of the chain. It is a top-level object, so one set can guard several hosts and routes.
/// </summary>
/// <param name="Id">Identity. The built-in presets (<see cref="RuleSetPresets"/>) have reserved IDs and are never part of a document.</param>
/// <param name="Name">Human label, unique ignoring case.</param>
/// <param name="Version">Concurrency version.</param>
/// <param name="Notes">Free text.</param>
/// <param name="Rules">Tried in order; at most <see cref="RuleSetLimits.MaxRules"/>.</param>
public sealed record RuleSetConfig(
    RuleSetId Id,
    string Name,
    long Version = 0,
    string? Notes = null,
    IReadOnlyList<RuleConfig>? Rules = null)
{
    public IReadOnlyList<RuleConfig> Rules { get; init; } = Rules ?? [];
}

/// <summary>One rule: every condition in <see cref="Match"/> must hold for it to apply.</summary>
/// <param name="Action">What happens to a request the rule matches.</param>
/// <param name="Match">The conditions. A rule must state at least one: a rule that matches everything is a mistake.</param>
public sealed record RuleConfig(RuleAction Action, RuleMatchConfig? Match = null)
{
    public RuleMatchConfig Match { get; init; } = Match ?? new();
}

/// <summary>The conditions of a rule; a condition left out isn't tested.</summary>
/// <param name="Path">The path pattern.</param>
/// <param name="Methods">HTTP methods, upper case (canonical order: sorted, no duplicates); null = any.</param>
/// <param name="UserAgent">The user-agent condition.</param>
public sealed record RuleMatchConfig(
    RulePathConfig? Path = null,
    IReadOnlyList<string>? Methods = null,
    RuleUserAgentConfig? UserAgent = null);

/// <summary>A path condition: how to read <paramref name="Pattern"/> and the pattern itself.</summary>
/// <param name="Kind">Exact, prefix, glob or regex.</param>
/// <param name="Pattern">Exact, prefix and glob start with <c>/</c> and are written decoded, in the canonical form a route's
/// path takes. A regex is matched without backtracking against the canonical path, ignoring case.</param>
public sealed record RulePathConfig(RulePathKind Kind, string Pattern);

/// <summary>A user-agent condition: it holds when the header contains any of <paramref name="Contains"/>, or when
/// <paramref name="Empty"/> is set and the request has no user agent or an empty one.</summary>
/// <param name="Contains">Substrings compared ignoring ASCII case (canonical order: sorted, no duplicates); null = none.</param>
/// <param name="Empty">Also match a missing or empty user agent.</param>
public sealed record RuleUserAgentConfig(IReadOnlyList<string>? Contains = null, bool Empty = false)
{
    public IReadOnlyList<string> Contains { get; init; } = Contains ?? [];
}

/// <summary>Which rule set a layer applies. <see cref="RuleSetId"/> names a set the document declares or a built-in preset.
/// Both fields inherit on their own, so a host can turn an inherited set off and still remember which one it was, and a route
/// can inherit its host's set, name another, or turn it off. If a layer names a set without stating <see cref="Enabled"/>,
/// the switch takes the built-in default, which is on. A layer has one set; several concerns are several rules in that set.</summary>
public sealed record RuleSetSettings : ISettingsLayer
{
    /// <summary>Apply the resolved set. Built-in default: on. Off lifts an inherited set.</summary>
    public bool? Enabled { get; init; }

    /// <summary>The set to apply; null = inherit. With <see cref="Enabled"/> off the reference is kept but inert.</summary>
    public RuleSetId? RuleSetId { get; init; }

    [JsonIgnore]
    public bool SetsNothing => Enabled is null && RuleSetId is null;
}
