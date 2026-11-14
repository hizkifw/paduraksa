using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Audit;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Identity;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The config write path: the one entry point every surface (dashboard, API, MCP, YAML import)
/// sends <see cref="ConfigCommand"/>s to. One writer at a time; reads (<see cref="GetCurrentAsync"/>) never wait.
/// </summary>
/// <remarks>
/// <para><b>One writer.</b> This process is the only writer of the config database, and the writer lock serializes its commands,
/// certificate changes and republishes. A command is authorized, planned on the latest revision, validated and compiled; a dry
/// run stops there, and a failure of any step persists nothing. The revision and its audit event commit in one transaction, and
/// the snapshot is queued with the publisher while the writer lock is still held, so snapshots go live in commit order. The
/// command then waits for the outcome outside the lock, at most <see cref="ConfigServiceOptions.PublishWait"/>. A second process
/// writing the same file isn't supported: the unique revision indexes make its commit fail rather than fork the history.</para>
/// <para><b>The empty config.</b> Before the first commit the config is <see cref="InitialDocument"/> (no objects,
/// default settings at version 0) and there is no revision: the first command creates revision #1. Startup writes
/// nothing, so every revision is someone's change.</para>
/// <para><b>Audit.</b> A commit's event is written in the commit's transaction. Rejected commands are audited too, in a
/// transaction of their own, and the rejection is reported whatever happens to that write (it runs without the caller's
/// cancellation, and a failure is logged). Dry runs and commands that change nothing are not audited: previews would
/// drown the log.</para>
/// <para><b>Parts.</b> <see cref="ConfigPipeline"/> runs a command's steps, <see cref="ConfigPublishing"/> carries a commit to
/// live, <see cref="ConfigRecovery"/> starts the store and republishes the live config, and <see cref="ConfigDrafts"/> serves
/// the request simulator. This class owns the writer lock and the public surface.</para>
/// </remarks>
public sealed class ConfigService : IDisposable
{
    /// <summary>Error code of a command message that is too long or not valid text.</summary>
    public const string MessageInvalidCode = "command.message.invalid";

    /// <summary>Error code of a command refused because the config store is unavailable (a degraded start).</summary>
    public const string StoreUnavailableCode = "config.store.unavailable";

    /// <summary>Error code of a stored revision that fails its integrity check.</summary>
    internal const string CorruptRevisionCode = "revision.corrupt";

    private readonly IDbContextFactory<ConfigDbContext> _contexts;

    // Never disposed: it holds no wait handle (AvailableWaitHandle is never used), and a command in flight when the
    // service is disposed must still be able to release it.
    private readonly SemaphoreSlim _writer = new(1, 1);

    private readonly ConfigAudit _audit;
    private readonly ConfigPublishing _publishing;
    private readonly ConfigPipeline _pipeline;
    private readonly ConfigRecovery _recovery;
    private readonly ILogger _logger;
    private OperationalChangeFeed? _operationalChanges;

    /// <summary>Internal: the context factory is a Data type, which Application's public surface never shows.</summary>
    internal ConfigService(
        IDbContextFactory<ConfigDbContext> contexts,
        IConfigCompiler compiler,
        SnapshotPublisher publisher,
        IPermissionEvaluator permissions,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        ConfigServiceOptions options,
        LastKnownGoodFile lastKnownGood,
        IServerCertificateSource certificateSource,
        IBuiltInCertificates? builtInCertificates,
        ICertificateCatalog certificateCatalog,
        IPasswordCredentials passwordCredentials)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(compiler);
        ArgumentNullException.ThrowIfNull(publisher);
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(lastKnownGood);
        ArgumentNullException.ThrowIfNull(certificateSource);
        ArgumentNullException.ThrowIfNull(certificateCatalog);
        ArgumentNullException.ThrowIfNull(passwordCredentials);
        _contexts = contexts;
        _logger = loggerFactory.CreateLogger<ConfigService>();
        var revisions = new ConfigRevisionStore(timeProvider);
        var certificates = new CertificateInventoryLoader(certificateSource, builtInCertificates, timeProvider,
            loggerFactory.CreateLogger<CertificateInventoryLoader>());
        // Drafts and old revisions never load the vault's keys: what the vault describes decides what is usable, and the
        // inventory serves placeholder certificates.
        var simulationCertificates = CertificateInventoryLoader.ForSimulation(certificateCatalog, builtInCertificates,
            timeProvider, loggerFactory.CreateLogger<CertificateInventoryLoader>());
        _audit = new ConfigAudit(contexts, timeProvider, loggerFactory);
        _publishing = new ConfigPublishing(publisher, timeProvider, options, lastKnownGood, _audit, _writer, loggerFactory);
        _pipeline = new ConfigPipeline(contexts, compiler, certificates, simulationCertificates, publisher, permissions, revisions,
            _audit, _publishing, Changes, passwordCredentials, loggerFactory);
        _recovery = new ConfigRecovery(contexts, compiler, certificates, lastKnownGood, revisions, _pipeline, _publishing, _writer,
            passwordCredentials, loggerFactory);
        Drafts = new ConfigDrafts(contexts, compiler, simulationCertificates, _pipeline, _publishing, loggerFactory);
        _publishing.ValidityChanged += () => _ = _recovery.RepublishForValidityAsync();
    }

    /// <summary>The config before the first commit: no objects, default settings, every version 0.</summary>
    public static ConfigDocument InitialDocument => ConfigHead.Initial;

    /// <summary>One notification per committed revision.</summary>
    public ConfigChangeFeed Changes { get; } = new();

    /// <summary>Where the running config came from and whether commands are accepted (see <see cref="StartAsync"/>).</summary>
    public ConfigStoreStatus Status => _publishing.Status;

    /// <summary>The warnings of the snapshot that is live (startup, a commit, a certificate change): hosts without
    /// certificate coverage, bindings to certificates that went bad. Empty before the first one.</summary>
    public IReadOnlyList<ConfigWarning> Warnings => _publishing.Warnings;

    /// <summary>The server settings of the latest snapshot queued for publishing (docs/design/config.md "Server settings"); null when none sets any.</summary>
    public ServerSettings? ServerSettings => _publishing.Server;

    /// <summary>Raised when a queued snapshot changes the server settings; handlers run under the writer lock, so they only hand values on.</summary>
    public event Action<ServerSettings?>? ServerSettingsChanged
    {
        add => _publishing.ServerSettingsChanged += value;
        remove => _publishing.ServerSettingsChanged -= value;
    }

    /// <summary>Configs to compile without publishing them, for the request simulator.</summary>
    internal ConfigDrafts Drafts { get; }

    /// <summary>
    /// Runs <paramref name="body"/> holding the writer lock, with the pipeline: for a change that must read the head, decide and commit as one
    /// step (a draft's apply). A commit it returns is carried to live afterwards with <see cref="FinishAsync"/>, outside the lock, as
    /// <see cref="ExecuteAsync"/> does.
    /// </summary>
    internal async Task<T> UnderWriterAsync<T>(Func<ConfigPipeline, Task<T>> body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, this);
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await body(_pipeline).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }
    }

    /// <summary>Waits for the publish of a commit <see cref="UnderWriterAsync{T}"/> made, and says how it went (see <see cref="ConfigPublishing.FinishAsync"/>).</summary>
    internal Task<ConfigCommandResult> FinishAsync(Execution execution, CommandOrigin origin) => _publishing.FinishAsync(execution, origin);

    /// <summary>Completes once the startup publish has its outcome (tests); startup itself never waits for it.</summary>
    internal Task StartupPublishFollowed => _recovery.StartupPublishFollowed;

    /// <summary>
    /// The startup recompile (see <see cref="ConfigRecovery"/>): run once, before anything reads or writes config. Leaves the
    /// store usable or in the degraded state <see cref="Status"/> reports.
    /// </summary>
    /// <exception cref="ConfigStartupException">Neither the database nor the last-known-good file holds a config that can be served.</exception>
    public Task<ConfigStoreStatus> StartAsync(CancellationToken cancellationToken = default) =>
        _recovery.StartAsync(cancellationToken);

    /// <summary>The latest revision and its document (canonical, versions included); <see cref="InitialDocument"/>
    /// before the first commit.</summary>
    /// <exception cref="RevisionIntegrityException">The latest revision is corrupt.</exception>
    public async Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var (latest, document) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            return new CurrentConfig(document, latest is null ? null : ConfigHead.Ref(latest.Info));
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> for <paramref name="origin"/> (see the remarks). A rejected command is a result,
    /// not an exception; exceptions mean the database failed (nothing is committed then) or a programming error.
    /// <paramref name="cancellationToken"/> stops a command until it is decided; once rejected or committed, it reports
    /// that.
    /// </summary>
    public async Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(origin);
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, this);

        Execution execution;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the lock: startup sets it under the lock too.
            execution = Status is { AcceptsCommands: false } unusable
                ? new Execution(_publishing.Unavailable(command, unusable))
                : await _pipeline.ExecuteExclusiveAsync(command, origin, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writer.Release();
        }

        return execution.Publish is null
            ? execution.Result
            : await _publishing.FinishAsync(execution, origin).ConfigureAwait(false);
    }

    /// <summary>
    /// Compiles the live config against the current certificate inventory and queues its publish without waiting, for a
    /// startup step that changed the inventory before the web apps serve (<see cref="CertificateRecovery"/>). True when a publish
    /// was queued; false when nothing was (the store can't take commands, the live config can't be compiled, or the attempt
    /// failed, which is logged), so a caller that must see its change go live can try again.
    /// </summary>
    public Task<bool> RepublishLiveAsync() => _recovery.RepublishLiveAsync();

    /// <summary>Appends an audit event in a transaction of its own, once the outcome it records is decided (see
    /// <see cref="ConfigAudit.TryAppendAsync"/>).</summary>
    internal Task TryAppendAuditAsync(AuditEntry entry) => _audit.TryAppendAsync(entry);

    /// <summary>Stops new commands (they throw <see cref="ObjectDisposedException"/>); commands in flight finish.</summary>
    public void Dispose() => _publishing.Dispose();

    /// <summary>
    /// The operational change feed: one notification per committed, audited change made through
    /// <see cref="ApplyOperationalChangeAsync{T}"/> or <see cref="ApplyCertificateChangeAsync"/> (certificates, ACME accounts, DNS
    /// providers, ACME orders), so the dashboard's lists follow them without polling.
    /// </summary>
    public OperationalChangeFeed OperationalChanges =>
        LazyInitializer.EnsureInitialized(ref _operationalChanges, () => new OperationalChangeFeed(_logger));

    /// <summary>Tells the operational feed about a committed change that was audited.</summary>
    private void PublishOperational(AuditEntry? audit)
    {
        if (audit is not null)
        {
            OperationalChanges.Publish(new OperationalChange(audit.Action, audit.Target));
        }
    }


    /// <summary>
    /// Runs a certificate change (<see cref="Certificates.CertificateService"/>) on the config writer's lock, so it is
    /// ordered with config commands and with their publishes. In one transaction: <paramref name="change"/>
    /// edits the inventory tables and says what to audit; the audit event is appended. When
    /// <see cref="CertificateChangeOutcome.Republish"/> is set, the <b>live config is compiled again with the new
    /// inventory before the commit</b> (the transaction sees its own rows, as a command's compile sees its candidate),
    /// then everything commits and the result is published, without a new revision, exactly as a command's snapshot is;
    /// this waits for it like a command does.
    /// </summary>
    /// <remarks>
    /// Once the transaction commits the change has happened: nothing after it can fail the call or take the caller's
    /// cancellation. A change that is refused, or that doesn't affect serving, publishes nothing. If the live config can't
    /// be compiled (a degraded start whose database config is broken) the change stands, nothing is published and the
    /// result has no <see cref="CertificateChangeResult.Publish"/>; the next commit, start or validity change compiles
    /// with the inventory anyway, because every compile reads it fresh. A degraded start on the last-known-good file
    /// republishes that document, never the empty config, and doesn't end the degraded state.
    /// </remarks>
    internal async Task<CertificateChangeResult> ApplyCertificateChangeAsync(
        CommandOrigin origin,
        Func<CertificateChangeScope, Task<CertificateChangeOutcome>> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(change);
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, this);

        CertificateChangeResult result;
        PublishTicket? ticket = null;
        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publishing.Status is { AcceptsCommands: false } unusable)
            {
                _publishing.LogRefused("certificate change", unusable.Problem ?? "unknown");
                return new CertificateChangeResult(
                    CertificateChangeStatus.Unavailable,
                    Errors: [new ConfigError(string.Empty, StoreUnavailableCode, $"The config store is unavailable, so certificate changes are refused: {unusable.Problem}.")]);
            }

            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                CertificateChangeOutcome outcome;
                ConfigRecovery.LiveCompile? live = null;
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    // The transaction holds the write lock, so the latest revision can't move while the change looks at it.
                    var (_, document) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
                    outcome = await change(new CertificateChangeScope(context, document)).ConfigureAwait(false);
                    if (outcome.Audit is { } audit)
                    {
                        await _audit.AppendAsync(context, audit, cancellationToken).ConfigureAwait(false);
                    }

                    if (outcome.Republish)
                    {
                        live = await _recovery.CompileLiveAsync(context, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }

                // Committed: from here on the change is done, whatever happens to the publish.
                result = outcome.Result;
                PublishOperational(outcome.Audit);
                if (live is not null)
                {
                    ticket = _recovery.PublishLive(live);
                }
            }
        }
        finally
        {
            _writer.Release();
        }

        if (ticket is null)
        {
            return result;
        }

        var finished = await _publishing.FinishAsync(new Execution(new ConfigCommandResult(ConfigCommandStatus.Committed), ticket), origin).ConfigureAwait(false);
        return result with { Publish = finished.Publish };
    }

    /// <summary>
    /// Runs a change to an operational resource that is neither config nor serving metadata (ACME accounts, DNS
    /// providers, issuance state) on the config writer's lock: in one transaction <paramref name="change"/> edits the
    /// tables and says what to audit, the audit event is appended to the hash chain, and everything commits. Nothing is
    /// compiled or published and no revision is made. Going through the writer keeps these audit events ordered with
    /// every other one, so two writers can't fork the chain.
    /// </summary>
    /// <param name="change">Edits through the given context and returns the result with the audit entry (null for none).</param>
    /// <param name="unavailable">Makes the result for a degraded start (the config store is unusable); called with the reason, nothing runs.</param>
    /// <param name="cancellationToken">Cancels waiting for the writer and the work before the commit.</param>
    internal async Task<T> ApplyOperationalChangeAsync<T>(
        Func<ConfigDbContext, Task<(T Result, AuditEntry? Audit)>> change,
        Func<string, T> unavailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(unavailable);
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, this);

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_publishing.Status is { AcceptsCommands: false } unusable)
            {
                _publishing.LogRefused("operational change", unusable.Problem ?? "unknown");
                return unavailable(unusable.Problem ?? "unknown");
            }

            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var (result, audit) = await change(context).ConfigureAwait(false);
                    if (audit is not null)
                    {
                        await _audit.AppendAsync(context, audit, cancellationToken).ConfigureAwait(false);
                    }

                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    PublishOperational(audit);
                    return result;
                }
            }
        }
        finally
        {
            _writer.Release();
        }
    }
}
