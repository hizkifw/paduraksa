using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Config;

namespace Paduraksa.Dashboard.SettingsArea;

/// <summary>What <see cref="RefField{TSettings, TConfig, TId}"/> needs to pick one kind of named object: its words, how its settings record and
/// its objects are read, and where they live.</summary>
public sealed record RefFieldKind<TSettings, TConfig, TId>
    where TSettings : class
    where TConfig : class
    where TId : struct, ITypedId<TId>
{
    public required string Label { get; init; }

    /// <summary>The picker's <c>data-field</c>.</summary>
    public required string Field { get; init; }

    public required string NewLabel { get; init; }

    public required string OffLabel { get; init; }

    public required string OffHint { get; init; }

    public required string MissingLabel { get; init; }

    public required string MissingHint { get; init; }

    public required Func<TSettings, bool?> Enabled { get; init; }

    public required Func<TSettings, TId?> IdOf { get; init; }

    /// <summary>A settings record with the switch and the ID given.</summary>
    public required Func<bool?, TId?, TSettings> Make { get; init; }

    public required Func<TConfig, TId> ConfigId { get; init; }

    public required Func<TConfig, string> Name { get; init; }

    public required Func<TConfig, string> Summary { get; init; }

    public required Func<TSettings, IReadOnlyList<TConfig>, string> Inherited { get; init; }

    public required Func<TId, string> DraftTarget { get; init; }

    /// <summary>The page an object opens at.</summary>
    public required Func<TId, string> Href { get; init; }

    /// <summary>Objects every document has, listed after its own (rule-set presets).</summary>
    public IReadOnlyList<TConfig> BuiltIn { get; init; } = [];
}

// Razor can't declare a component abstract; a part of the class can.
public abstract partial class RefField<TSettings, TConfig, TId>
    where TSettings : class
    where TConfig : class
    where TId : struct, ITypedId<TId>;

public sealed class AccessListField : RefField<AccessListSettings, AccessListConfig, AccessListId>
{
    private static readonly RefFieldKind<AccessListSettings, AccessListConfig, AccessListId> Describes = new()
    {
        Label = "Access list",
        Field = "access-list",
        NewLabel = "New access list...",
        OffLabel = "No access list",
        OffHint = "anyone may reach it",
        MissingLabel = "A list that isn't here",
        MissingHint = "choose another, or none",
        Enabled = settings => settings.Enabled,
        IdOf = settings => settings.AccessListId,
        Make = (enabled, id) => new AccessListSettings { Enabled = enabled, AccessListId = id },
        ConfigId = list => list.Id,
        Name = list => list.Name,
        Summary = AccessListText.Summary,
        Inherited = AccessListText.Inherited,
        DraftTarget = DraftTargets.AccessList,
        Href = DashboardRoutes.AccessList,
    };

    protected override RefFieldKind<AccessListSettings, AccessListConfig, AccessListId> Kind => Describes;
}

public sealed class RateLimitField : RefField<RateLimitSettings, RateLimitPolicyConfig, RateLimitPolicyId>
{
    private static readonly RefFieldKind<RateLimitSettings, RateLimitPolicyConfig, RateLimitPolicyId> Describes = new()
    {
        Label = "Rate limit",
        Field = "rate-limit",
        NewLabel = "New rate limit...",
        OffLabel = "No limit",
        OffHint = "no requests are refused",
        MissingLabel = "A policy that isn't here",
        MissingHint = "choose another, or no limit",
        Enabled = settings => settings.Enabled,
        IdOf = settings => settings.PolicyId,
        Make = (enabled, id) => new RateLimitSettings { Enabled = enabled, PolicyId = id },
        ConfigId = policy => policy.Id,
        Name = policy => policy.Name,
        Summary = RateLimitText.Summary,
        Inherited = RateLimitText.Inherited,
        DraftTarget = DraftTargets.RateLimit,
        Href = DashboardRoutes.RateLimit,
    };

    protected override RefFieldKind<RateLimitSettings, RateLimitPolicyConfig, RateLimitPolicyId> Kind => Describes;
}

public sealed class AuthProviderField : RefField<AuthProviderSettings, AuthProviderConfig, AuthProviderId>
{
    private static readonly RefFieldKind<AuthProviderSettings, AuthProviderConfig, AuthProviderId> Describes = new()
    {
        Label = "Sign-in",
        Field = "auth-provider",
        NewLabel = "New auth provider...",
        OffLabel = "No sign-in",
        OffHint = "anyone may reach it",
        MissingLabel = "A provider that isn't here",
        MissingHint = "choose another, or none",
        Enabled = settings => settings.Enabled,
        IdOf = settings => settings.AuthProviderId,
        Make = (enabled, id) => new AuthProviderSettings { Enabled = enabled, AuthProviderId = id },
        ConfigId = provider => provider.Id,
        Name = provider => provider.Name,
        Summary = AuthProviderText.Summary,
        Inherited = AuthProviderText.Inherited,
        DraftTarget = DraftTargets.AuthProvider,
        Href = DashboardRoutes.AuthProvider,
    };

    protected override RefFieldKind<AuthProviderSettings, AuthProviderConfig, AuthProviderId> Kind => Describes;
}

public sealed class RuleSetField : RefField<RuleSetSettings, RuleSetConfig, RuleSetId>
{
    private static readonly RefFieldKind<RuleSetSettings, RuleSetConfig, RuleSetId> Describes = new()
    {
        Label = "Rule set",
        Field = "rule-set",
        NewLabel = "New rule set...",
        OffLabel = "No rule set",
        OffHint = "every request goes on",
        MissingLabel = "A set that isn't here",
        MissingHint = "choose another, or none",
        Enabled = settings => settings.Enabled,
        IdOf = settings => settings.RuleSetId,
        Make = (enabled, id) => new RuleSetSettings { Enabled = enabled, RuleSetId = id },
        ConfigId = set => set.Id,
        Name = set => set.Name,
        Summary = RuleSetText.Summary,
        Inherited = RuleSetText.Inherited,
        DraftTarget = DraftTargets.RuleSet,
        // A preset has no page of its own: the list shows it.
        Href = id => RuleSetPresets.IsPreset(id) ? DashboardRoutes.RuleSets : DashboardRoutes.RuleSet(id),
        BuiltIn = RuleSetPresets.All,
    };

    protected override RefFieldKind<RuleSetSettings, RuleSetConfig, RuleSetId> Kind => Describes;
}
