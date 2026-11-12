using System.Text.Json;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Config;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Storage;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Commands;

/// <summary>
/// What a command does to the current document, decided without touching the database or the clock: the arrangement to validate,
/// or the early result (a conflict, not found) with the audit outcome it is recorded as. Pure, so each command's rules are tested
/// on documents alone.
/// </summary>
internal static class ConfigCommandPlanner
{
    public const string SettingsList = "settings";

    /// <summary>What a rejected command is audited as: its action name and target.</summary>
    internal sealed record Audited(string Action, string? Target, bool Force = false);

    /// <summary>The top-level object an upsert sends: its list (or <see cref="SettingsList"/>) and ID string.</summary>
    internal sealed record ObjectKey(string List, string Id);

    /// <summary>
    /// What a command does to the current document: the document <see cref="Arranged"/> for validation, or an
    /// <see cref="Early"/> result (a conflict still carries the arrangement, for its change list).
    /// </summary>
    internal sealed record Plan(
        ConfigDocument? Arranged,
        bool IsObjectCommand = true,
        ObjectKey? Own = null,
        string? DefaultMessage = null)
    {
        public ConfigCommandResult? Early { get; init; }

        /// <summary>The password verifiers an earlier build kept in a document this command restores (a rollback target): to be
        /// imported into the vault before the document goes live.</summary>
        public IReadOnlyList<LegacyCredential> Legacy { get; init; } = [];

        public AuditOutcome EarlyOutcome { get; init; }

        public static Plan Stop(ConfigCommandResult result, AuditOutcome outcome, ConfigDocument? arranged = null) =>
            new(arranged) { Early = result, EarlyOutcome = outcome };
    }

    internal static Plan Upsert<T>(
        RevisionRef? head,
        T sent,
        string list,
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        Func<T, long> versionOf,
        Func<IReadOnlyList<T>, ConfigDocument> withList)
        where T : class
    {
        var id = idOf(sent);
        var existing = items.FirstOrDefault(item => idOf(item) == id);
        // The sent object goes last, so that a clash with another object (a name, an ID) is reported on it.
        var arranged = withList([.. items.Where(item => idOf(item) != id), sent]);
        var version = versionOf(sent);
        if (existing is null)
        {
            return version == 0
                ? new Plan(arranged, Own: new ObjectKey(list, id))
                : Plan.Stop(new(ConfigCommandStatus.NotFound), AuditOutcome.Conflict);
        }

        return versionOf(existing) == version
            ? new Plan(arranged, Own: new ObjectKey(list, id))
            : Plan.Stop(Conflict(head, versionOf(existing)), AuditOutcome.Conflict, arranged);
    }

    internal static Plan Delete<T>(
        RevisionRef? head,
        string id,
        long version,
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        Func<T, long> versionOf,
        Func<IReadOnlyList<T>, ConfigDocument> withList)
        where T : class
    {
        var existing = items.FirstOrDefault(item => idOf(item) == id);
        if (existing is null)
        {
            return Plan.Stop(new(ConfigCommandStatus.NotFound), AuditOutcome.Conflict);
        }

        var arranged = withList([.. items.Where(item => idOf(item) != id)]);
        return versionOf(existing) == version
            ? new Plan(arranged)
            : Plan.Stop(Conflict(head, versionOf(existing)), AuditOutcome.Conflict, arranged);
    }

    /// <summary>
    /// What an object command (an upsert or delete of a host, upstream, profile, rate limit, access list, rule set, auth provider, or the
    /// settings) does to <paramref name="current"/>; null for a whole-document command. A draft replays its entries through this, one at
    /// a time on the document the earlier ones left, so a version check there is the one a live command makes.
    /// </summary>
    internal static Plan? PlanObject(ConfigCommand command, ConfigDocument current, RevisionRef? head) => command switch
    {
        UpsertHostCommand c => Upsert(head, Required(c.Host), "hosts", current.Hosts, h => h.Id.ToString(),
            h => h.Version, l => current with { Hosts = l }),
        DeleteHostCommand c => Delete(head, c.HostId.ToString(), c.Version, current.Hosts, h => h.Id.ToString(),
            h => h.Version, l => current with { Hosts = l }),
        UpsertUpstreamCommand c => Upsert(head, Required(c.Upstream), "upstreams", current.Upstreams,
            u => u.Id.ToString(), u => u.Version, l => current with { Upstreams = l }),
        DeleteUpstreamCommand c => Delete(head, c.UpstreamId.ToString(), c.Version, current.Upstreams,
            u => u.Id.ToString(), u => u.Version, l => current with { Upstreams = l }),
        UpsertRateLimitPolicyCommand c => Upsert(head, Required(c.Policy), "rateLimitPolicies", current.RateLimitPolicies,
            p => p.Id.ToString(), p => p.Version, l => current with { RateLimitPolicies = l }),
        DeleteRateLimitPolicyCommand c => Delete(head, c.RateLimitPolicyId.ToString(), c.Version, current.RateLimitPolicies,
            p => p.Id.ToString(), p => p.Version, l => current with { RateLimitPolicies = l }),
        UpsertAccessListCommand c => Upsert(head, Required(c.AccessList), "accessLists", current.AccessLists,
            l => l.Id.ToString(), l => l.Version, l => current with { AccessLists = l }),
        DeleteAccessListCommand c => Delete(head, c.AccessListId.ToString(), c.Version, current.AccessLists,
            l => l.Id.ToString(), l => l.Version, l => current with { AccessLists = l }),
        UpsertRuleSetCommand c => Upsert(head, Required(c.RuleSet), "ruleSets", current.RuleSets,
            r => r.Id.ToString(), r => r.Version, l => current with { RuleSets = l }),
        DeleteRuleSetCommand c => Delete(head, c.RuleSetId.ToString(), c.Version, current.RuleSets,
            r => r.Id.ToString(), r => r.Version, l => current with { RuleSets = l }),
        UpsertAuthProviderCommand c => Upsert(head, Required(c.AuthProvider), "authProviders", current.AuthProviders,
            p => p.Id.ToString(), p => p.Version, l => current with { AuthProviders = l }),
        DeleteAuthProviderCommand c => Delete(head, c.AuthProviderId.ToString(), c.Version, current.AuthProviders,
            p => p.Id.ToString(), p => p.Version, l => current with { AuthProviders = l }),
        UpsertProfileCommand c => Upsert(head, Required(c.Profile), "profiles", current.Profiles,
            p => p.Id.ToString(), p => p.Version, l => current with { Profiles = l }),
        DeleteProfileCommand c => Delete(head, c.ProfileId.ToString(), c.Version, current.Profiles,
            p => p.Id.ToString(), p => p.Version, l => current with { Profiles = l }),
        UpdateSettingsCommand c => UpdateSettings(current, head, Required(c.Settings)),
        _ => null,
    };

    internal static Plan UpdateSettings(ConfigDocument current, RevisionRef? head, GlobalSettings sent)
    {
        var arranged = current with { Settings = sent };
        return current.Settings.Version == sent.Version
            ? new Plan(arranged, Own: new ObjectKey(SettingsList, string.Empty))
            : Plan.Stop(Conflict(head, current.Settings.Version), AuditOutcome.Conflict, arranged);
    }

    internal static async Task<Plan> ReplaceAsync(
        ConfigDbContext context,
        ConfigDocument current,
        RevisionRef? head,
        ConfigDocument document,
        ConfigRevisionId? baseRevision,
        string? defaultMessage,
        CancellationToken cancellationToken)
    {
        var based = baseRevision is { IsEmpty: true } ? null : baseRevision;
        if (based == head?.Id)
        {
            return new Plan(document, IsObjectCommand: false, DefaultMessage: defaultMessage);
        }

        IReadOnlyList<ConfigChange> sinceBase = [];
        if (based is { } id
            && await ConfigRevisionStore.GetAsync(context, id, cancellationToken).ConfigureAwait(false) is { } stored
            && CanonicalConfigJson.TryDeserialize(stored.Document.Span, out var baseDocument, out _))
        {
            sinceBase = ConfigDiff.Compare(baseDocument, current);
        }

        ConfigCommandResult conflict = new(ConfigCommandStatus.Conflict, Conflict: new ConfigConflict(sinceBase, head));
        return Plan.Stop(conflict, AuditOutcome.Conflict, document) with { IsObjectCommand = false };
    }

    internal static async Task<Plan> RollbackAsync(
        ConfigDbContext context, ConfigDocument current, RevisionRef? head, RollbackConfigCommand command,
        CancellationToken cancellationToken)
    {
        if (await ConfigRevisionStore.GetAsync(context, command.Target, cancellationToken).ConfigureAwait(false) is not
            { } target)
        {
            return Plan.Stop(new(ConfigCommandStatus.NotFound), AuditOutcome.Invalid) with { IsObjectCommand = false };
        }

        // A document this build no longer reads (it can't be newer: downgrades are unsupported) is invalid, not broken.
        if (!CanonicalConfigJson.TryDeserialize(target.Document.Span, out var document, out var error, out var upgrade))
        {
            return Plan.Stop(new(ConfigCommandStatus.Invalid, Errors: [error]), AuditOutcome.Invalid) with
            {
                IsObjectCommand = false
            };
        }

        var plan = await ReplaceAsync(context, current, head, document, command.BaseRevision,
                $"Rollback to {target.Info.Label}", cancellationToken)
            .ConfigureAwait(false);
        return plan with { Legacy = upgrade.Credentials };
    }

    internal static ConfigCommandResult Conflict(RevisionRef? head, long currentVersion) =>
        new(ConfigCommandStatus.Conflict, Conflict: new ConfigConflict([], head, currentVersion));

    /// <summary>
    /// An invalid result. Whole-document commands report <paramref name="errors"/> as they are. Object commands split
    /// them: the sent object's, relative to it; the rest, as related errors pointing into <paramref name="canonical"/>.
    /// </summary>
    internal static ConfigCommandResult Invalid(
        Plan plan, ConfigDocument validated, ConfigDocument canonical, IReadOnlyList<ConfigError> errors,
        IReadOnlyList<ConfigChange> changes)
    {
        if (!plan.IsObjectCommand)
        {
            return new ConfigCommandResult(ConfigCommandStatus.Invalid, Changes: changes, Errors: errors);
        }

        var ownPath = plan.Own switch
        {
            null => null,
            { List: SettingsList } => "/settings",
            { } own => CommandErrors.PathOf(validated, own.List, own.Id),
        };
        var toCanonical = ReferenceEquals(validated, canonical)
            ? null
            : CommandErrors.IndexMap.Between(validated, canonical);
        var (mine, related) = CommandErrors.Split(errors, ownPath, toCanonical);
        return new ConfigCommandResult(ConfigCommandStatus.Invalid, Changes: changes, Errors: mine,
            RelatedErrors: related);
    }

    internal static Audited Describe(ConfigCommand command)
    {
        static string Verb(long version) => version == 0 ? "create" : "update";

        Audited audited = command switch
        {
            UpsertHostCommand c => new($"config.host.{Verb(Required(c.Host).Version)}", c.Host.Id.ToString()),
            DeleteHostCommand c => new("config.host.delete", c.HostId.ToString()),
            UpsertUpstreamCommand c => new($"config.upstream.{Verb(Required(c.Upstream).Version)}",
                c.Upstream.Id.ToString()),
            DeleteUpstreamCommand c => new("config.upstream.delete", c.UpstreamId.ToString()),
            UpsertRateLimitPolicyCommand c => new($"config.rate_limit_policy.{Verb(Required(c.Policy).Version)}",
                c.Policy.Id.ToString()),
            DeleteRateLimitPolicyCommand c => new("config.rate_limit_policy.delete", c.RateLimitPolicyId.ToString()),
            UpsertAccessListCommand c => new($"config.access_list.{Verb(Required(c.AccessList).Version)}", c.AccessList.Id.ToString()),
            DeleteAccessListCommand c => new("config.access_list.delete", c.AccessListId.ToString()),
            UpsertRuleSetCommand c => new($"config.rule_set.{Verb(Required(c.RuleSet).Version)}", c.RuleSet.Id.ToString()),
            DeleteRuleSetCommand c => new("config.rule_set.delete", c.RuleSetId.ToString()),
            UpsertAuthProviderCommand c => new($"config.auth_provider.{Verb(Required(c.AuthProvider).Version)}", c.AuthProvider.Id.ToString()),
            DeleteAuthProviderCommand c => new("config.auth_provider.delete", c.AuthProviderId.ToString()),
            UpsertProfileCommand c => new($"config.profile.{Verb(Required(c.Profile).Version)}",
                c.Profile.Id.ToString()),
            DeleteProfileCommand c => new("config.profile.delete", c.ProfileId.ToString()),
            UpdateSettingsCommand => new("config.settings.update", null),
            ReplaceConfigCommand => new("config.replace", null),
            RollbackConfigCommand c => new("config.rollback", c.Target.ToString()),
            _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
        };
        return audited with { Force = command.Force };
    }

    internal static string? MessageProblem(string? message)
    {
        if (message is null)
        {
            return null;
        }

        if (message.Length > ConfigRevisionStore.MaxMessageLength)
        {
            return $"the message is limited to {ConfigRevisionStore.MaxMessageLength} characters";
        }

        try
        {
            StoreChecks.StrictUtf8(message, nameof(message));
            return null;
        }
        catch (ArgumentException)
        {
            return "the message isn't valid text (it has a lone surrogate)";
        }
    }

    internal static T Required<T>(T? part)
        where T : class =>
        part ?? throw new ArgumentException($"The command's {typeof(T).Name} is null.", nameof(part));
}
