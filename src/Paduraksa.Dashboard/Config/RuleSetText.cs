using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>A rule set in the dashboard's words: one line for a set, one for a rule, and the phrase a picker's inherit choice
/// carries. A set is never named by ID.</summary>
public static class RuleSetText
{
    /// <summary>The methods the rule form offers; a rule loaded with another keeps it.</summary>
    public static IReadOnlyList<string> CommonMethods { get; } = ["GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "TRACE", "CONNECT"];

    /// <summary>The rules as a list row reads them: "no rules", "1 rule: deny", "3 rules: 2 deny, 1 allow".</summary>
    public static string Summary(RuleSetConfig set)
    {
        ArgumentNullException.ThrowIfNull(set);
        if (set.Rules.Count == 0)
        {
            return "no rules";
        }

        var counts = string.Join(
            ", ",
            set.Rules.GroupBy(rule => rule.Action).OrderBy(group => group.Key).Select(group => $"{(set.Rules.Count == 1 ? string.Empty : group.Count().ToString(System.Globalization.CultureInfo.InvariantCulture) + " ")}{ActionWord(group.Key)}"));
        return $"{set.Rules.Count} {(set.Rules.Count == 1 ? "rule" : "rules")}: {counts}";
    }

    /// <summary>One rule as a sentence: "Deny when path starts with /admin and method is POST".</summary>
    public static string Describe(RuleConfig rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var match = rule.Match ?? new RuleMatchConfig();
        List<string> when = [];
        if (match.Path is { } path)
        {
            when.Add($"path {PathPhrase(path.Kind)} {path.Pattern}");
        }

        if (match.Methods is { Count: > 0 } methods)
        {
            when.Add($"method is {string.Join(" or ", methods)}");
        }

        if (match.UserAgent is { } agent)
        {
            var names = string.Join(" or ", agent.Contains.Select(token => $"\"{token}\""));
            when.Add(agent.Contains.Count == 0 ? "user agent is empty" : agent.Empty ? $"user agent contains {names} or is empty" : $"user agent contains {names}");
        }

        var action = ActionWord(rule.Action);
        return string.Concat(char.ToUpperInvariant(action[0]).ToString(), action.AsSpan(1).ToString(), when.Count == 0 ? string.Empty : " when " + string.Join(" and ", when));
    }

    public static string ActionWord(RuleAction action) => action switch
    {
        RuleAction.Deny => "deny",
        RuleAction.Drop => "drop",
        _ => "allow",
    };

    /// <summary>What an action does, in the labels the editor offers.</summary>
    public static string ActionLabel(RuleAction action) => action switch
    {
        RuleAction.Deny => "Deny (403 page)",
        RuleAction.Drop => "Drop the connection",
        _ => "Allow (skip the later rules)",
    };

    public static string PathLabel(RulePathKind? kind) => kind switch
    {
        null => "Any path",
        RulePathKind.Exact => "Is exactly",
        RulePathKind.Prefix => "Starts with",
        RulePathKind.Glob => "Matches glob",
        _ => "Matches regex",
    };

    /// <summary>What a layer would inherit, in the words of the picker's first choice: "no rule set", or the set's name. A switch
    /// that is off is no set whatever is still remembered under it.</summary>
    public static string Inherited(RuleSetSettings? inherited, IReadOnlyList<RuleSetConfig> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (inherited is null || inherited.Enabled == false || inherited.RuleSetId is not { IsEmpty: false } id)
        {
            return "no rule set";
        }

        return NameOf(id, sets) ?? "a set that isn't here";
    }

    /// <summary>The set's name among <paramref name="sets"/> and the built-in presets; null when neither has it.</summary>
    public static string? NameOf(RuleSetId id, IReadOnlyList<RuleSetConfig> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);
        return sets.FirstOrDefault(set => set.Id == id)?.Name ?? (RuleSetPresets.TryGet(id, out var preset) ? preset.Name : null);
    }

    private static string PathPhrase(RulePathKind kind) => kind switch
    {
        RulePathKind.Exact => "is",
        RulePathKind.Prefix => "starts with",
        RulePathKind.Glob => "matches glob",
        _ => "matches regex",
    };
}
