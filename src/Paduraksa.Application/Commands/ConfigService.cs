using System.Data.Common;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Projection;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Storage;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Simulation;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The config write path (PLAN §6.1, §6.4): the one entry point every surface (dashboard, API, MCP, CLI, YAML import)
/// sends <see cref="ConfigCommand"/>s to. One writer at a time; reads (<see cref="GetCurrentAsync"/>) never wait.
/// </summary>
/// <remarks>
/// <para><b>Steps.</b> (1) Authorize (<see cref="IConfigCommandAuthorizer"/>). (2) Load the latest revision's
/// document, apply the command, check the version it names (object commands) or its base revision (whole-document
/// commands), and set the versions (<see cref="ConfigVersions"/>). (3) Validate (<see cref="ConfigValidator"/>).
/// (4) Compile (<see cref="IConfigCompiler"/>). A dry run stops here; so does any failure, and nothing is persisted.
/// (5) In one transaction: the revision (canonical bytes, the change list as its summary), the projection
/// (<see cref="ConfigProjection"/>) and the audit event. (6) Publish the compiled snapshot (<see cref="SnapshotPublisher"/>).
/// (7) Notify <see cref="Changes"/>.</para>
/// <para><b>The empty config.</b> Before the first commit the config is <see cref="InitialDocument"/> (no objects,
/// default settings at version 0) and there is no revision: the first command creates revision #1. Startup writes
/// nothing, so every revision is someone's change.</para>
/// <para><b>Publishing.</b> The snapshot is queued with the publisher while the writer lock is held, so snapshots go
/// live in commit order. The command then waits for the outcome outside the lock, at most
/// <see cref="ConfigServiceOptions.PublishWait"/>. The publisher only publishes the latest snapshot: when a newer
/// commit supersedes this one's, the command waits for the newer publish (which carries this change too) and reports
/// that outcome. A failed publish doesn't undo the commit: the previous snapshot stays live, the command reports
/// <see cref="ConfigCommandStatus.PublishFailed"/>, the failure is logged and audited (<c>config.publish</c>, by the
/// command whose publish it was), and the change goes live with the next commit's publish (every snapshot is the whole
/// config) or the next start, which compiles the latest revision. There is no retry. A publish still pending when the
/// wait ends is followed in the background, and logged and audited if it then fails.</para>
/// <para><b>Concurrency.</b> Within the process the writer lock serialises commands. Another process writing the same
/// file (the CLI) can commit between a command's load and its commit; the commit transaction sees the moved head, and
/// the command is planned, validated and compiled once more on the new head. If the head moved again, it is a
/// conflict naming the real head.</para>
/// <para><b>Audit.</b> A commit's event (<c>config.host.update</c> and so on, outcome success, with the revision) is
/// written in the commit's transaction. Rejected commands are audited too, in a transaction of their own: denied,
/// invalid, conflict, and not found (as a conflict for an object, which someone else deleted, and as invalid for an
/// unknown rollback target). Once the decision is made the rejection is reported whatever happens to its audit event:
/// the event is written without the caller's cancellation, and a failure to write it (the database locked past its busy
/// timeout, which bounds how long the writer lock is held) is logged. Dry runs and commands that change nothing are
/// not audited: they change nothing, and previews (the dashboard's review sheet, MCP's <c>preview_*</c> tools) would
/// drown the log.</para>
/// <para><b>Startup.</b> A server runs <see cref="StartAsync"/> once before anything reads or writes config: the startup
/// recompile, with the last-known-good fallback and the degraded states it can leave the store in (<see cref="Status"/>).
/// While the store is unusable, commands get <see cref="ConfigCommandStatus.Unavailable"/>, unaudited. A process that
/// never starts the service (the CLI writing to a stopped server's database) accepts commands as usual.</para>
/// <para><b>Last-known-good.</b> With a <see cref="LastKnownGoodFile"/>, the document of every revision that goes live
/// (the startup one and each commit's) is written to it in the background, after the publish succeeds: never under the
/// writer lock, and never holding up the command's result.</para>
/// <para><b>Disposal.</b> <see cref="Dispose"/> stops new commands; commands in flight finish normally.</para>
/// </remarks>
public sealed partial class ConfigService : IDisposable
{
    /// <summary>Error code of a command message that is too long or not valid text.</summary>
    public const string MessageInvalidCode = "command.message.invalid";

    private const string SettingsList = "settings";
    private const int MaxAuditedCodes = 20;
    private const int MaxAuditedErrorLength = 500;

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly IConfigCompiler _compiler;
    private readonly CertificateInventoryLoader _certificates;
    private readonly CertificateInventoryLoader _simulationCertificates;
    private readonly SnapshotPublisher _publisher;
    private readonly IConfigCommandAuthorizer _authorizer;
    private readonly ConfigServiceOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ConfigRevisionStore _revisions;
    private readonly LastKnownGoodFile? _lastKnownGood;
    private readonly AuditLog _audit;
    private readonly ILogger _logger;

    // Never disposed: it holds no wait handle (AvailableWaitHandle is never used), and a command in flight when the
    // service is disposed must still be able to release it.
    private readonly SemaphoreSlim _writer = new(1, 1);

    // The latest commit's publish; written under the writer lock, right after it is queued.
    private volatile PublishTicket? _latestPublish;
    private volatile IReadOnlyList<ConfigWarning> _warnings = [];

    // The validity timer: one at a time, replaced whenever a newer snapshot goes live (see WentLiveWith).
    private readonly object _liveLock = new();
    private long _liveGeneration;
    private ITimer? _validityTimer;

    // Timer periods are limited to about 49 days; a longer wait fires early, republishes, and schedules the rest.
    private static readonly TimeSpan MaxTimerDelay = TimeSpan.FromDays(20);
    private volatile bool _disposed;

    /// <summary>Internal: the context factory is a Data type, which Application's public surface never shows. Register
    /// it with <see cref="ConfigServiceCollectionExtensions.AddPaduraksaConfigService"/>.</summary>
    internal ConfigService(
        IDbContextFactory<ConfigDbContext> contexts,
        IConfigCompiler compiler,
        SnapshotPublisher publisher,
        TimeProvider? timeProvider = null,
        ILoggerFactory? loggerFactory = null,
        IConfigCommandAuthorizer? authorizer = null,
        ConfigServiceOptions? options = null,
        LastKnownGoodFile? lastKnownGood = null,
        IServerCertificateSource? certificateSource = null,
        IBuiltInCertificates? builtInCertificates = null,
        ICertificateCatalog? certificateCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(publisher);
        _contexts = contexts;
        _compiler = compiler;
        _publisher = publisher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        loggerFactory ??= NullLoggerFactory.Instance;
        _authorizer = authorizer ?? AllowAllConfigCommands.Instance;
        _options = options ?? new ConfigServiceOptions();
        _revisions = new ConfigRevisionStore(_timeProvider);
        _lastKnownGood = lastKnownGood;
        _audit = new AuditLog(_timeProvider, loggerFactory.CreateLogger<AuditLog>());
        _logger = loggerFactory.CreateLogger<ConfigService>();
        _certificates = new CertificateInventoryLoader(certificateSource, builtInCertificates, _timeProvider, loggerFactory.CreateLogger<CertificateInventoryLoader>());
        // Drafts and old revisions never load the vault's keys: what the vault describes decides what is usable, and the
        // inventory serves placeholder certificates.
        _simulationCertificates = CertificateInventoryLoader.ForSimulation(certificateCatalog, builtInCertificates, _timeProvider, loggerFactory.CreateLogger<CertificateInventoryLoader>());
    }

    /// <summary>The config before the first commit: no objects, default settings, every version 0.</summary>
    public static ConfigDocument InitialDocument { get; } = CanonicalConfigJson.Canonicalize(new ConfigDocument());

    /// <summary>One notification per committed revision (PLAN §6.1 step 7).</summary>
    public ConfigChangeFeed Changes { get; } = new();

    /// <summary>The latest revision and its document (canonical, versions included); <see cref="InitialDocument"/>
    /// before the first commit.</summary>
    /// <exception cref="RevisionIntegrityException">The latest revision is corrupt.</exception>
    public async Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            (StoredRevision? latest, ConfigDocument document) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            return new CurrentConfig(document, latest is null ? null : Ref(latest.Info));
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> for <paramref name="origin"/> (see the remarks). A rejected command is a result,
    /// not an exception; exceptions mean the database failed (nothing is committed then) or a programming error.
    /// <paramref name="cancellationToken"/> stops a command until it is decided; once rejected or committed, it reports
    /// that.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The service is disposed.</exception>
    public async Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(origin);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Execution execution;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the lock: startup sets it under the lock too.
            execution = Status is { AcceptsCommands: false } unusable
                ? new Execution(Unavailable(command, unusable))
                : await ExecuteExclusiveAsync(command, origin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }

        return execution.Publish is null ? execution.Result : await FinishPublishAsync(execution, origin).ConfigureAwait(false);
    }

    /// <summary>Stops new commands (they throw <see cref="ObjectDisposedException"/>); commands in flight finish.</summary>
    public void Dispose()
    {
        _disposed = true;
        lock (_liveLock)
        {
            _validityTimer?.Dispose();
            _validityTimer = null;
        }
    }

    private async Task<Execution> ExecuteExclusiveAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken)
    {
        Audited audited = Describe(command);
        if (!await _authorizer.IsAllowedAsync(command, origin, cancellationToken).ConfigureAwait(false))
        {
            return await RejectAsync(command, origin, audited, new(ConfigCommandStatus.Denied), AuditOutcome.Denied).ConfigureAwait(false);
        }

        string? message = string.IsNullOrWhiteSpace(command.Message) ? null : command.Message.Trim();
        if (MessageProblem(message) is { } problem)
        {
            ConfigCommandResult invalid = new(ConfigCommandStatus.Invalid, Errors: [new ConfigError(string.Empty, MessageInvalidCode, problem)]);
            return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
        }

        if (await AttemptAsync(command, origin, audited, message, cancellationToken).ConfigureAwait(false) is { } first)
        {
            return first;
        }

        // Another process committed between the load and the commit: once more, on the new head.
        LogHeadMoved(audited.Action);
        if (await AttemptAsync(command, origin, audited, message, cancellationToken).ConfigureAwait(false) is { } second)
        {
            return second;
        }

        return await RejectHeadMovedAsync(command, origin, audited, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Load, plan, validate, compile and commit; null when the head moved before the commit (rolled back).</summary>
    private async Task<Execution?> AttemptAsync(
        ConfigCommand command, CommandOrigin origin, Audited audited, string? message, CancellationToken cancellationToken, bool simulation = false)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            (StoredRevision? latest, ConfigDocument current) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            RevisionRef? head = latest is null ? null : Ref(latest.Info);
            Plan plan = command switch
            {
                UpsertHostCommand c => Upsert(head, Required(c.Host), "hosts", current.Hosts, h => h.Id.ToString(), h => h.Version, l => current with { Hosts = l }),
                DeleteHostCommand c => Delete(head, c.HostId.ToString(), c.Version, current.Hosts, h => h.Id.ToString(), h => h.Version, l => current with { Hosts = l }),
                UpsertUpstreamCommand c => Upsert(head, Required(c.Upstream), "upstreams", current.Upstreams, u => u.Id.ToString(), u => u.Version, l => current with { Upstreams = l }),
                DeleteUpstreamCommand c => Delete(head, c.UpstreamId.ToString(), c.Version, current.Upstreams, u => u.Id.ToString(), u => u.Version, l => current with { Upstreams = l }),
                UpsertProfileCommand c => Upsert(head, Required(c.Profile), "profiles", current.Profiles, p => p.Id.ToString(), p => p.Version, l => current with { Profiles = l }),
                DeleteProfileCommand c => Delete(head, c.ProfileId.ToString(), c.Version, current.Profiles, p => p.Id.ToString(), p => p.Version, l => current with { Profiles = l }),
                UpdateSettingsCommand c => UpdateSettings(current, head, Required(c.Settings)),
                ReplaceConfigCommand c => await ReplaceAsync(context, current, head, Required(c.Document), c.BaseRevision, null, cancellationToken).ConfigureAwait(false),
                RollbackConfigCommand c => await RollbackAsync(context, current, head, c, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
            };

            IReadOnlyList<ConfigChange> changes = plan.Arranged is null ? [] : ConfigDiff.Compare(current, plan.Arranged);
            if (plan.Early is { } early)
            {
                return await RejectAsync(command, origin, audited, early with { Changes = changes }, plan.EarlyOutcome).ConfigureAwait(false);
            }

            if (changes.Count == 0)
            {
                // A draft that changes nothing is simulated as the head this attempt planned on, not one a later commit made.
                return simulation
                    ? await UnchangedCandidateAsync(context, current, latest, cancellationToken).ConfigureAwait(false)
                    : new Execution(new ConfigCommandResult(ConfigCommandStatus.Unchanged));
            }

            long next = (latest?.Info.Seq ?? 0) + 1;
            long? version = plan.Own is null ? null : next;
            ConfigDocument stamped = ConfigVersions.Stamp(plan.Arranged!, current, changes, next);
            ConfigDocument candidate = CanonicalConfigJson.Canonicalize(stamped);
            IReadOnlyList<ConfigError> errors = ConfigValidator.Validate(stamped);
            if (errors.Count > 0)
            {
                ConfigCommandResult invalid = Invalid(plan, stamped, candidate, errors, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            ConfigRevisionId revisionId = command.DryRun ? ConfigRevisionId.Empty : _revisions.NextId();
            CertificateInventory inventory = await (simulation ? _simulationCertificates : _certificates).LoadAsync(context, candidate, cancellationToken).ConfigureAwait(false);
            ConfigCompilation compilation = _compiler.Compile(candidate, revisionId, inventory);
            if (compilation.Snapshot is not { } snapshot)
            {
                IReadOnlyList<ConfigError> compileErrors = compilation.Errors.Count > 0
                    ? compilation.Errors
                    : [new ConfigError(string.Empty, ConfigErrorCodes.CompileFailed, "the config didn't compile")];
                ConfigCommandResult invalid = Invalid(plan, candidate, candidate, compileErrors, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            // An ordinary command may not introduce a binding to a certificate that can't be used; it may leave one that
            // went bad. A rollback and the system's restore after a lost database put back a config that already was
            // accepted once: like startup, they degrade with warnings (the inventory may be gone with the database).
            bool restoring = command is RollbackConfigCommand || (command is ReplaceConfigCommand && origin.Actor.Kind == ActorKind.System);
            if (!restoring && IntroducedUnusableBindings(current, candidate, compilation.Warnings) is { Count: > 0 } unusable)
            {
                ConfigCommandResult invalid = Invalid(plan, candidate, candidate, unusable, changes);
                return await RejectAsync(command, origin, audited, invalid, AuditOutcome.Invalid).ConfigureAwait(false);
            }

            // PLAN §3.1's lock-out guard: put the caller's own request through the real gate over the current and the candidate
            // config. If it currently reaches the admin app but the change would stop that, the change cuts the caller off;
            // `force` is the explicit override. A draft simulator run never commits, so it is not guarded.
            if (!simulation && origin.ViaAdminProxy && !command.Force && origin.AdminRequest is { } requestFacts)
            {
                RuntimeSnapshot before = await CurrentGuardSnapshotAsync(current, inventory, cancellationToken).ConfigureAwait(false);
                if (AdminAccessLost(current, candidate, before, snapshot, requestFacts, compilation.Warnings) is { } lockout)
                {
                    ConfigCommandResult refused = new(ConfigCommandStatus.Invalid, Changes: changes, Errors: [lockout]);
                    return await RejectAsync(command, origin, audited, refused, AuditOutcome.Invalid).ConfigureAwait(false);
                }
            }

            if (command.DryRun)
            {
                return new Execution(new ConfigCommandResult(ConfigCommandStatus.DryRun, Changes: changes, Version: version, Warnings: compilation.Warnings), Candidate: snapshot);
            }

            return await CommitAsync(context, origin, audited, latest, candidate, changes, revisionId, compilation, message ?? plan.DefaultMessage, version, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The commit transaction; null (rolled back) when the head is no longer the one the command was planned on.</summary>
    private async Task<Execution?> CommitAsync(
        ConfigDbContext context,
        CommandOrigin origin,
        Audited audited,
        StoredRevision? latest,
        ConfigDocument candidate,
        IReadOnlyList<ConfigChange> changes,
        ConfigRevisionId revisionId,
        ConfigCompilation compilation,
        string? message,
        long? version,
        CancellationToken cancellationToken)
    {
        byte[] document = CanonicalConfigJson.Serialize(candidate);
        string summary = JsonSerializer.Serialize(changes, ConfigJsonContext.Default.IReadOnlyListConfigChange);
        RevisionInfo info;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            // The transaction holds the database's write lock (BEGIN IMMEDIATE), so the head can't move after this.
            ConfigRevisionId? actualHead = await context.ConfigRevisions
                .OrderByDescending(r => r.Seq)
                .Select(r => (ConfigRevisionId?)r.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (actualHead != latest?.Info.Id)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                return null;
            }

            info = await _revisions.AppendAsync(context, new NewRevision(document, summary, origin.Actor, origin.Channel, message) { Id = revisionId }, cancellationToken)
                .ConfigureAwait(false);
            await ConfigProjection.WriteAsync(context, candidate, changes, info.Id, latest?.Info.Id, cancellationToken).ConfigureAwait(false);
            AuditEntry entry = Entry(origin, audited, AuditOutcome.Success) with
            {
                RevisionId = info.Id,
                Details = Json(w =>
                {
                    w.WriteNumber("seq", info.Seq);
                    w.WriteNumber("changes", changes.Count);
                }),
            };
            await _audit.AppendAsync(context, entry, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        RevisionRef revision = Ref(info);
        LogCommitted(revision.Seq, info.Id, audited.Action, origin.Actor, origin.Channel, changes.Count);
        PublishTicket ticket = new(revision, StartPublish(compilation.Snapshot!, compilation), new LastKnownGoodEntry(revision, document, info.DocumentHash));
        _latestPublish = ticket;
        Changes.Publish(new ConfigCommitted(revision, changes));
        return new Execution(new ConfigCommandResult(ConfigCommandStatus.Committed, revision, changes, Version: version, Warnings: compilation.Warnings), ticket);
    }

    /// <summary>
    /// The snapshot a lock-out probe uses for the current config: the live snapshot when it already serves an admin route, else
    /// the current document compiled with the same inventory (the test hosts publish only through their attached proxy). A
    /// compile failure means the guard cannot prove the caller has access now, so it does not refuse.
    /// </summary>
    private async Task<RuntimeSnapshot> CurrentGuardSnapshotAsync(
        ConfigDocument current, CertificateInventory inventory, CancellationToken cancellationToken)
    {
        RuntimeSnapshot live = _publisher.Store.Current;
        if (live.HostsByIndex.Any(static host => host.Routes.Any(static route => route.Admin)))
        {
            return live;
        }

        ConfigCompilation compilation = _compiler.Compile(current, ConfigRevisionId.Empty, inventory);
        return compilation.Snapshot ?? RuntimeSnapshot.Bootstrap;
    }

    /// <summary>
    /// Whether the caller's own request would still reach the admin app under <paramref name="candidate"/> (PLAN §3.1). The
    /// current and the compiled candidate are run through the real gate; only a request that currently forwards to the admin route
    /// and would stop doing so is a lock-out, so a condition that already exists is not blamed on this change.
    /// </summary>
    private static ConfigError? AdminAccessLost(
        ConfigDocument current,
        ConfigDocument candidate,
        RuntimeSnapshot before,
        RuntimeSnapshot after,
        AdminRequestFacts request,
        IReadOnlyList<ConfigWarning> warnings)
    {
        HostConfig? admin = current.Hosts.FirstOrDefault(static h => h is { Kind: HostKind.Admin, Enabled: true });
        if (admin is null || !ForwardsToAdmin(before, request))
        {
            return null;
        }

        string path = AdminHostPath(current, candidate, admin);
        if (!SameTrustedProxies(current.Settings.TrustedProxies, candidate.Settings.TrustedProxies))
        {
            return Lockout("/settings/trustedProxies", "changing the trusted proxies while the command arrives through the proxy");
        }

        HostConfig? adminAfter = candidate.Hosts.FirstOrDefault(static h => h.Kind == HostKind.Admin);
        if (adminAfter is not null
            && CommandErrors.PathOf(candidate, "hosts", adminAfter.Id.ToString()) is { } afterPath
            && warnings.Any(w => w.Code == ConfigWarningCodes.CertificateSpecificNoCoverage && IsAtOrBelow(w.Path, afterPath)))
        {
            return Lockout(path, "the admin host's certificate doesn't cover its names");
        }

        if (ForwardsToAdmin(after, request))
        {
            return null;
        }

        GateSimulation simulation = GateSimulator.Run(after, ProbeOf(request));
        string why = simulation.Outcome.Action switch
        {
            GateOutcomeAction.Forward => "the host would forward somewhere other than the admin app",
            GateOutcomeAction.Respond => $"the host would answer {simulation.Outcome.StatusCode} instead of forwarding to the admin app",
            _ => "the request would be rejected before reaching the admin app",
        };
        return Lockout(path, why);
    }

    private static bool ForwardsToAdmin(RuntimeSnapshot snapshot, AdminRequestFacts request)
    {
        GateSimulation simulation = GateSimulator.Run(snapshot, ProbeOf(request));
        return simulation.Outcome.Action == GateOutcomeAction.Forward && simulation.Upstream is { Admin: true };
    }

    private static GateProbe ProbeOf(AdminRequestFacts request) => new()
    {
        Https = request.Https,
        Host = request.Host,
        Path = "/",
        Method = "GET",
        ClientAddress = request.ClientIp ?? IPAddress.None,
        HttpVersion = ProbeHttpVersion.Http11,
        TlsVersion = request.Https ? SslProtocols.Tls13 : SslProtocols.Tls12,
    };

    private static ConfigError Lockout(string path, string why) => new(
        path,
        ConfigErrorCodes.HostAdminLockout,
        $"The request that sent this command would no longer reach the admin app: {why}. "
            + "Repeat it with force=true if that is what you mean, or make it on the admin port.");

    /// <summary>The admin host's pointer in the candidate when it exists, otherwise where it was in the current document.</summary>
    private static string AdminHostPath(ConfigDocument current, ConfigDocument candidate, HostConfig admin) =>
        CommandErrors.PathOf(candidate, "hosts", admin.Id.ToString())
            ?? CommandErrors.PathOf(current, "hosts", admin.Id.ToString())
            ?? "/hosts";

    private static bool SameTrustedProxies(TrustedProxySettings left, TrustedProxySettings right) =>
        left.Mode == right.Mode
        && string.Equals(left.Header, right.Header, StringComparison.Ordinal)
        && left.UseTrueClientIp == right.UseTrueClientIp
        && left.ForwardedForFallback == right.ForwardedForFallback
        && left.TrustForwardedProto == right.TrustForwardedProto
        && (left.Proxies ?? []).SequenceEqual(right.Proxies ?? [], StringComparer.Ordinal);

    private static bool IsAtOrBelow(string path, string prefix) =>
        path.StartsWith(prefix, StringComparison.Ordinal) && (path.Length == prefix.Length || path[prefix.Length] == '/');

    /// <summary>
    /// The errors for certificate bindings that <paramref name="candidate"/> introduces and that can't be used (PLAN §9.2):
    /// a host whose Specific binding is new or changed, or a changed default certificate, naming a certificate that is
    /// missing, expired, pending, failed or without a key. A binding the current config already has is left alone, so a
    /// certificate that expired since doesn't block unrelated changes (it stays a warning).
    /// </summary>
    private static List<ConfigError> IntroducedUnusableBindings(ConfigDocument current, ConfigDocument candidate, IReadOnlyList<ConfigWarning> warnings)
    {
        List<ConfigError> errors = [];
        foreach (ConfigWarning warning in warnings)
        {
            if (warning.Code == ConfigWarningCodes.CertificateDefaultUnusable)
            {
                if (candidate.Settings.DefaultCertificateId != current.Settings.DefaultCertificateId)
                {
                    errors.Add(new ConfigError(warning.Path, ConfigErrorCodes.SettingsDefaultCertificateUnusable, warning.Message));
                }
            }
            else if (warning.Code == ConfigWarningCodes.CertificateSpecificUnusable
                && CommandErrors.HostIndexOf(warning.Path) is { } index && index < candidate.Hosts.Count)
            {
                HostConfig host = candidate.Hosts[index];
                if (current.Hosts.FirstOrDefault(h => h.Id == host.Id)?.Certificate != host.Certificate)
                {
                    errors.Add(new ConfigError(warning.Path, ConfigErrorCodes.HostCertificateUnusable, warning.Message));
                }
            }
        }

        return errors;
    }

    /// <summary>The head moved twice: a conflict naming the real head and the object's current version.</summary>
    private async Task<Execution> RejectHeadMovedAsync(ConfigCommand command, CommandOrigin origin, Audited audited, CancellationToken cancellationToken)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            (StoredRevision? latest, ConfigDocument current) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            ConfigCommandResult conflict = new(
                ConfigCommandStatus.Conflict,
                Conflict: new ConfigConflict([], latest is null ? null : Ref(latest.Info), CurrentVersionOf(command, current)));
            return await RejectAsync(command, origin, audited, conflict, AuditOutcome.Conflict).ConfigureAwait(false);
        }
    }

    /// <summary>The warnings of the snapshot that is live (startup, a commit, a certificate change): hosts without
    /// certificate coverage, bindings to certificates that went bad. Set when a publish goes live, not when it is
    /// queued. Empty before the first one.</summary>
    public IReadOnlyList<ConfigWarning> Warnings => _warnings;

    /// <summary>
    /// Queues <paramref name="snapshot"/>. When the publish goes live, its warnings become <see cref="Warnings"/> and a
    /// republish is scheduled for when the certificate inventory it was compiled against next changes with the clock
    /// (<see cref="ConfigCompilation.ValidityChangesAt"/>), so a certificate that expires or becomes valid takes effect
    /// without any other trigger.
    /// </summary>
    private Task<PublishResult> StartPublish(RuntimeSnapshot snapshot, ConfigCompilation compilation)
    {
        Task<PublishResult> publishing;
        try
        {
            publishing = _publisher.PublishAsync(snapshot);
        }
        catch (ArgumentException ex)
        {
            // A bound candidate: a compiler bug. Reported like any failed publish, with a sentence and the trace ID rather than the
            // exception's own text (the log has it).
            LogPublishRefused(ex);
            return Task.FromResult(new PublishResult(
                PublishStatus.Failed,
                0,
                ["The candidate snapshot was refused by the publisher, so nothing went live. The details are in the server log under this response's trace ID."]));
        }

        _ = publishing.ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    if (task.Result.Status == PublishStatus.Published)
                    {
                        WentLiveWith(task.Result.Generation, compilation);
                    }

                    return;
                }

                // The publisher answered with a failed result for the failures it knows; only a cancel or a thrown fault gets
                // here, and either would otherwise leave the publish unrecorded.
                if (task.IsCanceled)
                {
                    LogPublishCancelled();
                }
                else
                {
                    LogPublishFaulted(task.Exception!.GetBaseException());
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return publishing;
    }

    /// <summary>A publish of generation <paramref name="generation"/> is live: record its warnings and schedule its
    /// validity timer, unless a later generation got there first.</summary>
    private void WentLiveWith(long generation, ConfigCompilation compilation)
    {
        lock (_liveLock)
        {
            if (generation < _liveGeneration || _disposed)
            {
                return;
            }

            _liveGeneration = generation;
            _warnings = compilation.Warnings;
            _validityTimer?.Dispose();
            _validityTimer = null;
            if (compilation.ValidityChangesAt is { } at)
            {
                TimeSpan due = at - _timeProvider.GetUtcNow();
                due = due < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : due > MaxTimerDelay ? MaxTimerDelay : due;
                _validityTimer = _timeProvider.CreateTimer(static state => ((ConfigService)state!).OnValidityChanged(), this, due, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void OnValidityChanged() => _ = RepublishForValidityAsync();

    private async Task<ConfigCommandResult> FinishPublishAsync(Execution execution, CommandOrigin origin)
    {
        long started = _timeProvider.GetTimestamp();
        TimeSpan Remaining()
        {
            TimeSpan left = _options.PublishWait - _timeProvider.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        PublishTicket own = execution.Publish!;
        ConfigCommandResult committed = execution.Result;
        if (await WaitAsync(own.Task, Remaining()).ConfigureAwait(false) is not { } result)
        {
            LogPublishPending(own.Revision.Seq, own.Revision.Id, _options.PublishWait);
            _ = AuditLatePublishAsync(own, origin);
            return committed with { Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [], own.Revision.Id) };
        }

        if (result.Status != PublishStatus.Superseded)
        {
            PublishOutcome outcome = Map(result, own.Revision.Id);
            if (outcome.Succeeded)
            {
                WentLive(own);
                return committed with { Publish = outcome };
            }

            await AuditPublishFailureAsync(own.Revision, outcome, origin).ConfigureAwait(false);
            return committed with { Status = ConfigCommandStatus.PublishFailed, Publish = outcome };
        }

        // Superseded: a newer commit's publish carries this change, so its outcome is this one's. Its owner audits it.
        PublishTicket followed = own;
        while (true)
        {
            // The newer ticket is set under the writer lock right after it is queued, so read it under the lock.
            if (!await _writer.WaitAsync(Remaining()).ConfigureAwait(false))
            {
                return committed with { Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [], followed.Revision.Id) };
            }

            PublishTicket? newer;
            try
            {
                newer = _latestPublish;
            }
            finally
            {
                _writer.Release();
            }

            if (newer is null || ReferenceEquals(newer, followed)
                || await WaitAsync(newer.Task, Remaining()).ConfigureAwait(false) is not { } latest)
            {
                return committed with { Publish = new PublishOutcome(PublishOutcomeStatus.Pending, 0, [], (newer ?? followed).Revision.Id) };
            }

            if (latest.Status == PublishStatus.Superseded)
            {
                followed = newer;
                continue;
            }

            PublishOutcome outcome = Map(latest, newer.Revision.Id);
            if (outcome.Succeeded)
            {
                WentLive(newer); // before this returns; its own command does it too
            }

            return committed with
            {
                Status = outcome.Succeeded ? ConfigCommandStatus.Committed : ConfigCommandStatus.PublishFailed,
                Publish = outcome,
            };
        }
    }

    private async Task<PublishResult?> WaitAsync(Task<PublishResult> publishing, TimeSpan timeout)
    {
        try
        {
            return await publishing.WaitAsync(timeout, _timeProvider).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>Follows a publish that was still pending when its command returned: on success, <see cref="WentLive"/>;
    /// on failure, logs and audits it.</summary>
    private async Task AuditLatePublishAsync(PublishTicket ticket, CommandOrigin origin)
    {
        PublishResult result = await ticket.Task.ConfigureAwait(false); // the publisher completes its tasks, never faults them
        if (result.Status == PublishStatus.Published)
        {
            WentLive(ticket);
            return;
        }

        if (result.Status == PublishStatus.Superseded)
        {
            return; // the newer publish's command reports it
        }

        await AuditPublishFailureAsync(ticket.Revision, Map(result, ticket.Revision.Id), origin).ConfigureAwait(false);
    }

    private async Task AuditPublishFailureAsync(RevisionRef revision, PublishOutcome outcome, CommandOrigin origin)
    {
        LogPublishFailed(revision.Seq, revision.Id, outcome.Status, string.Join("; ", outcome.Errors));
        AuditEntry entry = new(origin.Actor, origin.Channel, "config.publish", AuditOutcome.Failed)
        {
            Target = revision.Id.IsEmpty ? null : revision.Id.ToString(),
            RevisionId = revision.Id.IsEmpty ? null : revision.Id,
            ViaAdminProxy = origin.ViaAdminProxy,
            Details = Json(w =>
            {
                w.WriteString("status", DocumentNames.Of(outcome.Status));
                w.WriteStartArray("errors");
                foreach (string error in outcome.Errors.Take(MaxAuditedCodes))
                {
                    w.WriteStringValue(error.Length > MaxAuditedErrorLength ? error[..MaxAuditedErrorLength] : error);
                }

                w.WriteEndArray();
            }),
        };
        await TryAppendAuditAsync(entry).ConfigureAwait(false);
    }

    /// <summary>Audits a rejected command (not dry runs) and returns its result, whether or not the audit write works.</summary>
    private async Task<Execution> RejectAsync(ConfigCommand command, CommandOrigin origin, Audited audited, ConfigCommandResult result, AuditOutcome outcome)
    {
        if (!command.DryRun)
        {
            AuditEntry entry = Entry(origin, audited, outcome) with
            {
                Details = Json(w =>
                {
                    w.WriteString("status", DocumentNames.Of(result.Status));
                    string[] codes = [.. result.Errors.Concat(result.RelatedErrors).Select(e => e.Code).Distinct(StringComparer.Ordinal).Take(MaxAuditedCodes)];
                    if (codes.Length > 0)
                    {
                        w.WriteStartArray("errors");
                        foreach (string code in codes)
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
            await TryAppendAuditAsync(entry).ConfigureAwait(false);
        }

        return new Execution(result);
    }

    /// <summary>
    /// Appends an audit event in a transaction of its own, once the outcome it records is decided: without the
    /// caller's cancellation, and logging rather than throwing when the database refuses, so the outcome still reaches
    /// the caller. A fresh context from the factory, so it also works for a publish that completes later.
    /// </summary>
    internal async Task TryAppendAuditAsync(AuditEntry entry)
    {
        try
        {
            var context = await _contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    await _audit.AppendAsync(context, entry, CancellationToken.None).ConfigureAwait(false);
                    await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is DbException or DbUpdateException or InvalidOperationException)
        {
            // InvalidOperationException includes ObjectDisposedException: the container is shutting down.
            LogAuditFailed(ex, entry.Action, entry.Outcome, entry.Target);
        }
    }

    private static Plan Upsert<T>(
        RevisionRef? head,
        T sent,
        string list,
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        Func<T, long> versionOf,
        Func<IReadOnlyList<T>, ConfigDocument> withList)
        where T : class
    {
        string id = idOf(sent);
        T? existing = items.FirstOrDefault(item => idOf(item) == id);
        // The sent object goes last, so that a clash with another object (a name, an ID) is reported on it.
        ConfigDocument arranged = withList([.. items.Where(item => idOf(item) != id), sent]);
        long version = versionOf(sent);
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

    private static Plan Delete<T>(
        RevisionRef? head,
        string id,
        long version,
        IReadOnlyList<T> items,
        Func<T, string> idOf,
        Func<T, long> versionOf,
        Func<IReadOnlyList<T>, ConfigDocument> withList)
        where T : class
    {
        T? existing = items.FirstOrDefault(item => idOf(item) == id);
        if (existing is null)
        {
            return Plan.Stop(new(ConfigCommandStatus.NotFound), AuditOutcome.Conflict);
        }

        ConfigDocument arranged = withList([.. items.Where(item => idOf(item) != id)]);
        return versionOf(existing) == version
            ? new Plan(arranged)
            : Plan.Stop(Conflict(head, versionOf(existing)), AuditOutcome.Conflict, arranged);
    }

    private static Plan UpdateSettings(ConfigDocument current, RevisionRef? head, GlobalSettings sent)
    {
        ConfigDocument arranged = current with { Settings = sent };
        return current.Settings.Version == sent.Version
            ? new Plan(arranged, Own: new ObjectKey(SettingsList, string.Empty))
            : Plan.Stop(Conflict(head, current.Settings.Version), AuditOutcome.Conflict, arranged);
    }

    private static async Task<Plan> ReplaceAsync(
        ConfigDbContext context,
        ConfigDocument current,
        RevisionRef? head,
        ConfigDocument document,
        ConfigRevisionId? baseRevision,
        string? defaultMessage,
        CancellationToken cancellationToken)
    {
        ConfigRevisionId? based = baseRevision is { IsEmpty: true } ? null : baseRevision;
        if (based == head?.Id)
        {
            return new Plan(document, IsObjectCommand: false, DefaultMessage: defaultMessage);
        }

        IReadOnlyList<ConfigChange> sinceBase = [];
        if (based is { } id
            && await ConfigRevisionStore.GetAsync(context, id, cancellationToken).ConfigureAwait(false) is { } stored
            && CanonicalConfigJson.TryDeserialize(stored.Document.Span, out ConfigDocument? baseDocument, out _))
        {
            sinceBase = ConfigDiff.Compare(baseDocument, current);
        }

        ConfigCommandResult conflict = new(ConfigCommandStatus.Conflict, Conflict: new ConfigConflict(sinceBase, head));
        return Plan.Stop(conflict, AuditOutcome.Conflict, document) with { IsObjectCommand = false };
    }

    private static async Task<Plan> RollbackAsync(
        ConfigDbContext context, ConfigDocument current, RevisionRef? head, RollbackConfigCommand command, CancellationToken cancellationToken)
    {
        if (await ConfigRevisionStore.GetAsync(context, command.Target, cancellationToken).ConfigureAwait(false) is not { } target)
        {
            return Plan.Stop(new(ConfigCommandStatus.NotFound), AuditOutcome.Invalid) with { IsObjectCommand = false };
        }

        // A document this build no longer reads (it can't be newer: downgrades are unsupported) is invalid, not broken.
        if (!CanonicalConfigJson.TryDeserialize(target.Document.Span, out ConfigDocument? document, out ConfigError? error))
        {
            return Plan.Stop(new(ConfigCommandStatus.Invalid, Errors: [error]), AuditOutcome.Invalid) with { IsObjectCommand = false };
        }

        return await ReplaceAsync(context, current, head, document, command.BaseRevision, $"Rollback to {target.Info.Label}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static ConfigCommandResult Conflict(RevisionRef? head, long currentVersion) =>
        new(ConfigCommandStatus.Conflict, Conflict: new ConfigConflict([], head, currentVersion));

    /// <summary>
    /// An invalid result. Whole-document commands report <paramref name="errors"/> as they are. Object commands split
    /// them: the sent object's, relative to it; the rest, as related errors pointing into <paramref name="canonical"/>.
    /// </summary>
    private static ConfigCommandResult Invalid(
        Plan plan, ConfigDocument validated, ConfigDocument canonical, IReadOnlyList<ConfigError> errors, IReadOnlyList<ConfigChange> changes)
    {
        if (!plan.IsObjectCommand)
        {
            return new ConfigCommandResult(ConfigCommandStatus.Invalid, Changes: changes, Errors: errors);
        }

        string? ownPath = plan.Own switch
        {
            null => null,
            { List: SettingsList } => "/settings",
            { } own => CommandErrors.PathOf(validated, own.List, own.Id),
        };
        CommandErrors.IndexMap? toCanonical = ReferenceEquals(validated, canonical) ? null : CommandErrors.IndexMap.Between(validated, canonical);
        (List<ConfigError> mine, List<ConfigError> related) = CommandErrors.Split(errors, ownPath, toCanonical);
        return new ConfigCommandResult(ConfigCommandStatus.Invalid, Changes: changes, Errors: mine, RelatedErrors: related);
    }

    private static async Task<(StoredRevision? Latest, ConfigDocument Document)> LoadAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        StoredRevision? latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
        return latest is null
            ? (null, InitialDocument)
            : (latest, CanonicalConfigJson.Canonicalize(CanonicalConfigJson.Deserialize(latest.Document.Span)));
    }

    private static Audited Describe(ConfigCommand command)
    {
        static string Verb(long version) => version == 0 ? "create" : "update";

        Audited audited = command switch
        {
            UpsertHostCommand c => new($"config.host.{Verb(Required(c.Host).Version)}", c.Host.Id.ToString()),
            DeleteHostCommand c => new("config.host.delete", c.HostId.ToString()),
            UpsertUpstreamCommand c => new($"config.upstream.{Verb(Required(c.Upstream).Version)}", c.Upstream.Id.ToString()),
            DeleteUpstreamCommand c => new("config.upstream.delete", c.UpstreamId.ToString()),
            UpsertProfileCommand c => new($"config.profile.{Verb(Required(c.Profile).Version)}", c.Profile.Id.ToString()),
            DeleteProfileCommand c => new("config.profile.delete", c.ProfileId.ToString()),
            UpdateSettingsCommand => new("config.settings.update", null),
            ReplaceConfigCommand => new("config.replace", null),
            RollbackConfigCommand c => new("config.rollback", c.Target.ToString()),
            _ => throw new ArgumentException($"Unknown command {command.GetType().Name}.", nameof(command)),
        };
        return audited with { Force = command.Force };
    }

    private static string? MessageProblem(string? message)
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

    private static T Required<T>(T? part)
        where T : class =>
        part ?? throw new ArgumentException($"The command's {typeof(T).Name} is null.", nameof(part));

    private static AuditEntry Entry(CommandOrigin origin, Audited audited, AuditOutcome outcome) =>
        new(origin.Actor, origin.Channel, audited.Action, outcome)
        {
            Target = audited.Target,
            ClientIp = origin.ClientIp,
            Force = audited.Force,
            ViaAdminProxy = origin.ViaAdminProxy,
        };

    private static RevisionRef Ref(RevisionInfo info) => new(info.Id, info.Seq);

    private static PublishOutcome Map(PublishResult result, ConfigRevisionId revision) => new(
        result.Status switch
        {
            PublishStatus.Published => PublishOutcomeStatus.Published,
            PublishStatus.Invalid => PublishOutcomeStatus.Invalid,
            PublishStatus.Rejected => PublishOutcomeStatus.Rejected,
            PublishStatus.TimedOut => PublishOutcomeStatus.TimedOut,
            _ => PublishOutcomeStatus.Failed,
        },
        result.Generation,
        result.Errors,
        revision);

    /// <summary>The current version of the object <paramref name="command"/> edits; null for whole-document commands
    /// or when it doesn't exist.</summary>
    private static long? CurrentVersionOf(ConfigCommand command, ConfigDocument current) => command switch
    {
        UpsertHostCommand c => current.Hosts.FirstOrDefault(h => h.Id == c.Host.Id)?.Version,
        DeleteHostCommand c => current.Hosts.FirstOrDefault(h => h.Id == c.HostId)?.Version,
        UpsertUpstreamCommand c => current.Upstreams.FirstOrDefault(u => u.Id == c.Upstream.Id)?.Version,
        DeleteUpstreamCommand c => current.Upstreams.FirstOrDefault(u => u.Id == c.UpstreamId)?.Version,
        UpsertProfileCommand c => current.Profiles.FirstOrDefault(p => p.Id == c.Profile.Id)?.Version,
        DeleteProfileCommand c => current.Profiles.FirstOrDefault(p => p.Id == c.ProfileId)?.Version,
        UpdateSettingsCommand => current.Settings.Version,
        _ => null,
    };

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Committed config revision #{Seq} ({Revision}): {Action} by {Actor} via {Channel}, {Changes} change(s).")]
    private partial void LogCommitted(long seq, ConfigRevisionId revision, string action, Actor actor, Channel channel, int changes);

    [LoggerMessage(Level = LogLevel.Error, Message = "Config revision #{Seq} ({Revision}) is committed but publishing it failed ({Status}): {Errors}. The previous snapshot stays live; the change goes live with the next successful publish or start.")]
    private partial void LogPublishFailed(long seq, ConfigRevisionId revision, PublishOutcomeStatus status, string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "The publisher refused the candidate snapshot (an ArgumentException, so a compiler bug); nothing went live.")]
    private partial void LogPublishRefused(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't write the audit event {Action} ({Outcome}, target {Target}); the outcome stands.")]
    private partial void LogAuditFailed(Exception exception, string action, AuditOutcome outcome, string? target);

    [LoggerMessage(Level = LogLevel.Information, Message = "Another writer committed while {Action} was being prepared; preparing it again on the new head.")]
    private partial void LogHeadMoved(string action);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Config revision #{Seq} ({Revision}) is committed but not live after {Wait}; it is still queued for publishing.")]
    private partial void LogPublishPending(long seq, ConfigRevisionId revision, TimeSpan wait);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the config snapshot was cancelled; the previous snapshot stays live.")]
    private partial void LogPublishCancelled();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing the config snapshot threw instead of returning a result; the previous snapshot stays live.")]
    private partial void LogPublishFaulted(Exception exception);

    /// <summary>What a rejected command is audited as: its action name and target.</summary>
    private sealed record Audited(string Action, string? Target, bool Force = false);

    /// <summary>The top-level object an upsert sends: its list (or <see cref="SettingsList"/>) and ID string.</summary>
    private sealed record ObjectKey(string List, string Id);

    /// <summary>
    /// What a command does to the current document: the document <see cref="Arranged"/> for validation, or an
    /// <see cref="Early"/> result (a conflict still carries the arrangement, for its change list).
    /// </summary>
    private sealed record Plan(ConfigDocument? Arranged, bool IsObjectCommand = true, ObjectKey? Own = null, string? DefaultMessage = null)
    {
        public ConfigCommandResult? Early { get; init; }

        public AuditOutcome EarlyOutcome { get; init; }

        public static Plan Stop(ConfigCommandResult result, AuditOutcome outcome, ConfigDocument? arranged = null) =>
            new(arranged) { Early = result, EarlyOutcome = outcome };
    }

    /// <summary>A committed revision's queued publish.</summary>
    /// <param name="Revision">The revision it publishes (<see cref="ConfigRevisionId.Empty"/>, seq 0: the empty config).</param>
    /// <param name="Task">The publish.</param>
    /// <param name="LastKnownGood">The entry to record once it is live; null for the empty config.</param>
    /// <param name="FromDatabase">The config is the database's latest revision. False for a republish of the
    /// last-known-good document (the database can't be used): going live doesn't end a degraded start.</param>
    private sealed record PublishTicket(RevisionRef Revision, Task<PublishResult> Task, LastKnownGoodEntry? LastKnownGood, bool FromDatabase = true);

    /// <param name="Result">The command's result.</param>
    /// <param name="Publish">The queued publish of a committed command.</param>
    /// <param name="Candidate">A dry run's compiled snapshot (never published); the request simulator runs on it.</param>
    private readonly record struct Execution(ConfigCommandResult Result, PublishTicket? Publish = null, RuntimeSnapshot? Candidate = null);
}
