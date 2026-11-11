using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;
using static Paduraksa.Application.Commands.ConfigCommandPlanner;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The config write path's steps for one command, run by whoever holds the writer lock: authorize, plan on the latest revision,
/// validate, compile, guard against lock-out, then commit the revision with its audit event in one transaction and queue its
/// publish. A dry run or a simulation stops before the commit; a failure of any step persists nothing. Rejected commands are
/// audited in a transaction of their own, and the rejection is reported whatever happens to that write.
/// </summary>
internal sealed partial class ConfigPipeline
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly IConfigCompiler _compiler;
    private readonly CertificateInventoryLoader _certificates;
    private readonly CertificateInventoryLoader _simulationCertificates;
    private readonly SnapshotPublisher _publisher;
    private readonly IPermissionEvaluator _permissions;
    private readonly ConfigRevisionStore _revisions;
    private readonly ConfigAudit _audit;
    private readonly ConfigPublishing _publishing;
    private readonly ConfigChangeFeed _changes;
    private readonly IPasswordCredentials _passwords;
    private readonly ILogger _logger;

    public ConfigPipeline(
        IDbContextFactory<ConfigDbContext> contexts, IConfigCompiler compiler, CertificateInventoryLoader certificates,
        CertificateInventoryLoader simulationCertificates, SnapshotPublisher publisher, IPermissionEvaluator permissions,
        ConfigRevisionStore revisions, ConfigAudit audit, ConfigPublishing publishing, ConfigChangeFeed changes, IPasswordCredentials passwords,
        ILoggerFactory loggerFactory)
    {
        _contexts = contexts;
        _compiler = compiler;
        _certificates = certificates;
        _simulationCertificates = simulationCertificates;
        _publisher = publisher;
        _permissions = permissions;
        _revisions = revisions;
        _audit = audit;
        _publishing = publishing;
        _changes = changes;
        _passwords = passwords;
        _logger = loggerFactory.CreateLogger<ConfigPipeline>();
    }

    /// <summary>
    /// Authorizes <paramref name="command"/> and attempts it, under the caller's writer lock. A rejection is audited and
    /// returned as a result.
    /// </summary>
    internal async Task<Execution> ExecuteExclusiveAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken)
    {
        var audited = Describe(command);
        if (!await _permissions.IsAllowedAsync(origin, OperationPermissions.ForConfigCommand, cancellationToken).ConfigureAwait(false))
        {
            return await RejectAsync(command, origin, audited, new(ConfigCommandStatus.Denied), AuditOutcome.Denied)
                .ConfigureAwait(false);
        }

        var message = string.IsNullOrWhiteSpace(command.Message) ? null : command.Message.Trim();
        if (MessageProblem(message) is { } problem)
        {
            ConfigCommandResult invalid = new(ConfigCommandStatus.Invalid,
                Errors: [new ConfigError(string.Empty, ConfigService.MessageInvalidCode, problem)]);
            return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
        }

        return await AttemptAsync(command, origin, audited, message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Load, plan, validate, compile and commit; a dry run or a simulation stops before the commit.</summary>
    internal async Task<Execution> AttemptAsync(
        ConfigCommand command, CommandOrigin origin, Audited audited, string? message,
        CancellationToken cancellationToken, bool simulation = false)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var (latest, current, headLegacy) = await ConfigHead.LoadForCommandAsync(context, cancellationToken).ConfigureAwait(false);
            var head = latest is null ? null : ConfigHead.Ref(latest.Info);

            // Before anything validates, stores or audits: a plain password leaves the command (the vault gets it once the command
            // is certain) and the document keeps the ID of its credential.
            var prepared = AccessListPasswords.Prepare(command, current);
            command = prepared.Command;
            var plan = command switch
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
                ReplaceConfigCommand c => await ReplaceAsync(context, current, head, Required(c.Document),
                    c.BaseRevision, null, cancellationToken).ConfigureAwait(false),
                RollbackConfigCommand c => await RollbackAsync(context, current, head, c, cancellationToken)
                    .ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
            };

            var changes = plan.Arranged is null
                ? []
                : AccessListPasswords.WithPasswordChanges(ConfigDiff.Compare(current, plan.Arranged), plan.Arranged, prepared);
            IReadOnlyList<LegacyCredential> legacy = [.. headLegacy, .. plan.Legacy];
            var commits = !command.DryRun && !simulation;
            if (plan.Early is { } early)
            {
                return await RejectAsync(command, origin, audited, early with { Changes = changes }, plan.EarlyOutcome)
                    .ConfigureAwait(false);
            }

            if (changes.Count == 0)
            {
                // A draft that changes nothing is simulated as the head this attempt planned on, not one a later commit made.
                return simulation
                    ? await UnchangedCandidateAsync(context, current, latest, cancellationToken).ConfigureAwait(false)
                    : new Execution(new ConfigCommandResult(ConfigCommandStatus.Unchanged));
            }

            var next = (latest?.Info.Seq ?? 0) + 1;
            long? version = plan.Own is null ? null : next;
            var stamped = ConfigVersions.Stamp(plan.Arranged!, current, changes, next);
            var candidate = CanonicalConfigJson.Canonicalize(stamped);
            var errors = ConfigValidator.Validate(stamped);
            if (errors.Count > 0)
            {
                var invalid = Invalid(plan, stamped, candidate, errors, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            var revisionId = command.DryRun ? ConfigRevisionId.Empty : _revisions.NextId();
            var inventory = await (simulation ? _simulationCertificates : _certificates)
                .LoadAsync(context, candidate, cancellationToken).ConfigureAwait(false);
            // What the compile may assume about passwords the vault doesn't hold: for a command that will commit, that they are about to be set;
            // for one that won't, handles that carry them for this compile alone, so a draft's passwords can be tried and nothing is stored.
            var pending = commits
                ? new PendingPasswords(new HashSet<PasswordCredentialId>(prepared.Ids.Concat(legacy.Select(static c => c.Id))), PendingPasswords.None.Overlay)
                : await AccessListPasswords.OverlayAsync(_passwords, prepared.Passwords, legacy, cancellationToken).ConfigureAwait(false);
            var compilation = _compiler.Compile(candidate, revisionId, inventory, commits, pending);
            if (compilation.Snapshot is not { } snapshot)
            {
                var compileErrors = compilation.Errors.Count > 0
                    ? compilation.Errors
                    : [new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, "the config didn't compile")];
                var invalid = Invalid(plan, candidate, candidate, compileErrors, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            // An ordinary command may not introduce a binding to a certificate that can't be used; it may leave one that
            // went bad. A rollback and the system's restore after a lost database put back a config that already was
            // accepted once: like startup, they degrade with warnings (the inventory may be gone with the database).
            var restoring = command is RollbackConfigCommand ||
                            (command is ReplaceConfigCommand && origin.Actor.Kind == ActorKind.System);
            if (!restoring && CertificateBindingGuard.IntroducedUnusable(current, candidate, compilation.Warnings) is
                { Count: > 0 } unusable)
            {
                var invalid = Invalid(plan, candidate, candidate, unusable, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            // The lock-out guard: put the caller's own request through the real gate over the current and the candidate
            // config. If it currently reaches the admin app but the change would stop that, the change cuts the caller off;
            // `force` is the explicit override. A draft simulator run never commits, so it is not guarded.
            if (!simulation && origin.ViaAdminProxy && !command.Force && origin.AdminRequest is { } requestFacts)
            {
                var before = await CurrentGuardSnapshotAsync(current, inventory, cancellationToken)
                    .ConfigureAwait(false);
                if (AdminLockoutGuard.AdminAccessLost(current, candidate, before, snapshot, requestFacts, compilation.Warnings) is
                    { } lockout)
                {
                    ConfigCommandResult refused = new(ConfigCommandStatus.Invalid, Changes: changes, Errors: [lockout]);
                    return await RejectAsync(command, origin, audited, refused, AuditOutcome.Invalid)
                        .ConfigureAwait(false);
                }
            }

            if (command.DryRun)
            {
                return new Execution(
                    new ConfigCommandResult(ConfigCommandStatus.DryRun, Changes: changes, Version: version,
                        Warnings: compilation.Warnings), Candidate: snapshot);
            }

            // Past every check: the passwords the command carries (and the verifiers of a restored document) reach the vault now,
            // so the snapshot that publishes finds them. The vault is not part of the commit's transaction, so a failed commit
            // takes the passwords back (the previous rows exactly), and says so in the audit log if even that fails.
            var passwordChanges = await AccessListPasswords.SetAsync(_passwords, prepared.Passwords, cancellationToken).ConfigureAwait(false);
            Execution execution;
            try
            {
                await AccessListPasswords.ImportAsync(_passwords, legacy, cancellationToken).ConfigureAwait(false);
                execution = await CommitAsync(context, origin, audited, candidate, changes, revisionId, compilation,
                        message ?? plan.DefaultMessage, version, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await TakePasswordsBackAsync(origin, candidate, prepared, passwordChanges).ConfigureAwait(false);
                throw;
            }

            _passwords.Keep(passwordChanges);
            await AccessListPasswords.DeleteUnreferencedAsync(_passwords, current, candidate, _logger, cancellationToken).ConfigureAwait(false);
            return execution;
        }
    }

    // Restores what the command's SetAsync calls replaced. A restore that fails is logged at Error and audited by itself, naming the
    // lists and users whose password may be in an unknown state and never the passwords, because the commit's own audit event
    // was never written.
    private async Task TakePasswordsBackAsync(
        CommandOrigin origin, ConfigDocument candidate, AccessListPasswords.Prepared prepared, IReadOnlyList<PasswordChange> passwordChanges)
    {
        if (passwordChanges.Count == 0)
        {
            return;
        }

        try
        {
            await _passwords.RevertAsync(passwordChanges, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var users = AccessListPasswords.UsersOf(candidate, prepared);
            LogPasswordRestoreFailed(ex, string.Join(", ", users.Select(static u => $"{u.Username} on {u.List}")));
            AuditEntry entry = new(origin.Actor, origin.Channel, "config.access_list.password.restore_failed", AuditOutcome.Failed)
            {
                Target = users.Count > 0 ? users[0].List.ToString() : null,
                ClientIp = origin.ClientIp,
                ViaAdminProxy = origin.ViaAdminProxy,
                Details = AuditDetails.Json(w =>
                {
                    w.WriteStartArray("users");
                    foreach (var (list, username) in users)
                    {
                        w.WriteStartObject();
                        w.WriteString("list", list.ToString());
                        w.WriteString("username", username);
                        w.WriteEndObject();
                    }

                    w.WriteEndArray();
                }),
            };
            await _audit.TryAppendAsync(entry).ConfigureAwait(false);
        }
    }

    /// <summary>Commits the revision and its audit event in one transaction, then queues the publish; the caller still holds the writer lock, so snapshots go live in commit order.</summary>
    internal async Task<Execution> CommitAsync(
        ConfigDbContext context,
        CommandOrigin origin,
        Audited audited,
        ConfigDocument candidate,
        IReadOnlyList<ConfigChange> changes,
        ConfigRevisionId revisionId,
        ConfigCompilation compilation,
        string? message,
        long? version,
        CancellationToken cancellationToken)
    {
        var document = CanonicalConfigJson.Serialize(candidate);
        var summary = JsonSerializer.Serialize(changes, ConfigJsonContext.Default.IReadOnlyListConfigChange);
        RevisionInfo info;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            info = await _revisions.AppendAsync(context,
                    new NewRevision(document, summary, origin.Actor, origin.Channel, message) { Id = revisionId },
                    cancellationToken)
                .ConfigureAwait(false);
            var entry = Entry(origin, audited, AuditOutcome.Success) with
            {
                RevisionId = info.Id,
                Details = AuditDetails.Json(w =>
                {
                    w.WriteNumber("seq", info.Seq);
                    w.WriteNumber("changes", changes.Count);
                }),
            };
            await _audit.AppendAsync(context, entry, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        var revision = ConfigHead.Ref(info);
        LogCommitted(revision.Seq, info.Id, audited.Action, origin.Actor, origin.Channel, changes.Count);
        PublishTicket ticket = new(revision, _publishing.Start(compilation.Snapshot!, compilation),
            new LastKnownGoodEntry(revision, document, info.DocumentHash));
        _publishing.Latest = ticket;
        _changes.Publish(new ConfigCommitted(revision, changes));
        return new Execution(
            new ConfigCommandResult(ConfigCommandStatus.Committed, revision, changes, Version: version,
                Warnings: compilation.Warnings), ticket);
    }

    /// <summary>
    /// The snapshot a lock-out probe uses for the current config: the live snapshot when it already serves an admin route, else
    /// the current document compiled with the same inventory (the test hosts publish only through their attached proxy). A
    /// compile failure means the guard cannot prove the caller has access now, so it does not refuse.
    /// </summary>
    private async Task<RuntimeSnapshot> CurrentGuardSnapshotAsync(
        ConfigDocument current, CertificateInventory inventory, CancellationToken cancellationToken)
    {
        var live = _publisher.Store.Current;
        if (live.HostsByIndex.Any(static host => host.Routes.Any(static route => route.Admin)))
        {
            return live;
        }

        var compilation = _compiler.Compile(current, ConfigRevisionId.Empty, inventory, publish: false);
        return compilation.Snapshot ?? RuntimeSnapshot.Bootstrap;
    }

    /// <summary>Audits a rejected command (not dry runs) and returns its result, whether or not the audit write works.</summary>
    private async Task<Execution> RejectAsync(ConfigCommand command, CommandOrigin origin, Audited audited,
        ConfigCommandResult result, AuditOutcome outcome)
    {
        if (command.DryRun)
        {
            return new Execution(result);
        }

        var entry = Entry(origin, audited, outcome) with
        {
            Details = AuditDetails.Json(w =>
            {
                w.WriteString("status", DocumentNames.Of(result.Status));
                string[] codes =
                [
                    .. result.Errors.Concat(result.RelatedErrors).Select(e => e.Code)
                        .Distinct(StringComparer.Ordinal).Take(ConfigAudit.MaxAuditedCodes)
                ];
                if (codes.Length > 0)
                {
                    w.WriteStartArray("errors");
                    foreach (var code in codes)
                    {
                        w.WriteStringValue(code);
                    }

                    w.WriteEndArray();
                }

                if (result.Conflict?.CurrentVersion is { } currentVersion)
                {
                    w.WriteNumber("currentVersion", currentVersion);
                }
            }),
        };
        await _audit.TryAppendAsync(entry).ConfigureAwait(false);

        return new Execution(result);
    }

    private static AuditEntry Entry(CommandOrigin origin, Audited audited, AuditOutcome outcome) =>
        new(origin.Actor, origin.Channel, audited.Action, outcome)
        {
            Target = audited.Target,
            ClientIp = origin.ClientIp,
            Force = audited.Force,
            ViaAdminProxy = origin.ViaAdminProxy,
        };

    /// <summary>A draft that changes nothing, compiled as the head its attempt planned on.</summary>
    private async Task<Execution> UnchangedCandidateAsync(ConfigDbContext context, ConfigDocument current, StoredRevision? latest, CancellationToken cancellationToken)
    {
        var inventory = await _simulationCertificates.LoadAsync(context, current, cancellationToken).ConfigureAwait(false);
        var compilation = _compiler.Compile(current, latest?.Info.Id ?? ConfigRevisionId.Empty, inventory, publish: false);
        return compilation.Snapshot is { } snapshot
            ? new Execution(new ConfigCommandResult(ConfigCommandStatus.Unchanged, Warnings: compilation.Warnings), Candidate: snapshot)
            : new Execution(new ConfigCommandResult(
                ConfigCommandStatus.Invalid,
                Errors: compilation.Errors.Count > 0 ? compilation.Errors : [new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, "the config didn't compile")]));
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "A config commit failed after its passwords were set, and putting them back failed too, so these users' passwords may not be what they were: {Users}.")]
    private partial void LogPasswordRestoreFailed(Exception exception, string users);

    [LoggerMessage(Level = LogLevel.Information,
        Message =
            "Committed config revision #{Seq} ({Revision}): {Action} by {Actor} via {Channel}, {Changes} change(s).")]
    private partial void LogCommitted(long seq, ConfigRevisionId revision, string action, Actor actor, Channel channel,
        int changes);
}
