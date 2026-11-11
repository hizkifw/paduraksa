using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Config;

/// <summary>
/// A rule set as its form edits it: a name and the rules as rows, in the order they are tried. A new set gets a fresh typed ID, a
/// copy of a preset gets one too, and the version the form opened with makes a concurrent edit a conflict.
/// </summary>
public sealed class RuleSetEditModel
{
    private readonly RuleSetConfig? _original;

    private RuleSetEditModel(RuleSetConfig? original, RuleSetId id, RuleSetConfig? source = null, string? name = null, bool copy = false)
    {
        _original = original;
        Id = id;
        source ??= original;
        Name = name ?? source?.Name ?? string.Empty;
        Notes = copy ? null : source?.Notes;
        foreach (var rule in source?.Rules ?? [])
        {
            Rules.Add(RuleEntry.From(rule));
        }
    }

    public RuleSetId Id { get; }

    public bool IsNew => _original is null;

    /// <summary>The version the form was opened with (0 for a new set).</summary>
    public long Version => _original?.Version ?? 0;

    public string Name { get; set; }

    public string? Notes { get; set; }

    public List<RuleEntry> Rules { get; } = [];

    public static RuleSetEditModel New() => new(null, RuleSetId.New());

    public static RuleSetEditModel From(RuleSetConfig set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new RuleSetEditModel(set, set.Id);
    }

    /// <summary>A form for a set made earlier and not applied yet (staged in a host's draft): still new, with what it was given.</summary>
    public static RuleSetEditModel FromDraft(RuleSetConfig set)
    {
        ArgumentNullException.ThrowIfNull(set);
        return new RuleSetEditModel(null, set.Id, set);
    }

    /// <summary>A new set that starts as <paramref name="preset"/>: the same rules under a name of its own, to edit. The preset's note says why
    /// it is built in, which isn't why this set exists, so it isn't copied.</summary>
    public static RuleSetEditModel CopyOf(RuleSetConfig preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return new RuleSetEditModel(null, RuleSetId.New(), preset, $"{preset.Name} (copy)", copy: true);
    }

    public RuleSetConfig ToConfig() => new(
        Id,
        Name.Trim(),
        Version,
        string.IsNullOrWhiteSpace(Notes) ? null : Notes,
        [.. Rules.Select(rule => rule.ToConfig())]);
}

/// <summary>One rule row of the form: a class, so the form can bind to it, move it and remove it by reference.</summary>
public sealed class RuleEntry
{
    /// <summary>Null: any path.</summary>
    public RulePathKind? PathKind { get; set; }

    public string Pattern { get; set; } = string.Empty;

    /// <summary>The methods the rule is limited to; none: any method.</summary>
    public List<string> Methods { get; set; } = [];

    /// <summary>User-agent substrings, one per line.</summary>
    public string AgentsText { get; set; } = string.Empty;

    /// <summary>Also match a request with no user agent.</summary>
    public bool EmptyAgent { get; set; }

    public RuleAction Action { get; set; } = RuleAction.Deny;

    /// <summary>The substrings of <see cref="AgentsText"/>: one per line, so a name with a space in it stays whole.</summary>
    public IReadOnlyList<string> Agents => SplitLines(AgentsText);

    public static RuleEntry From(RuleConfig rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var match = rule.Match ?? new RuleMatchConfig();
        return new RuleEntry
        {
            PathKind = match.Path?.Kind,
            Pattern = match.Path?.Pattern ?? string.Empty,
            Methods = [.. match.Methods ?? []],
            AgentsText = string.Join('\n', match.UserAgent?.Contains ?? []),
            EmptyAgent = match.UserAgent?.Empty ?? false,
            Action = rule.Action,
        };
    }

    /// <summary>The rule as the command sends it: a condition that isn't used is left out rather than sent empty, so the validator
    /// judges what the person sees.</summary>
    public RuleConfig ToConfig()
    {
        var agents = Agents;
        return new RuleConfig(
            Action,
            new RuleMatchConfig(
                PathKind is { } kind ? new RulePathConfig(kind, Pattern.Trim()) : null,
                Methods.Count == 0 ? null : [.. Methods],
                agents.Count > 0 || EmptyAgent ? new RuleUserAgentConfig(agents, EmptyAgent) : null));
    }

    private static string[] SplitLines(string text) =>
        text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
