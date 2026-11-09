using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Projection;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Proxy.Snapshot;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

public sealed partial class ConfigService
{
    /// <summary>Error code of a command refused because the config store is unavailable (a degraded start).</summary>
    public const string StoreUnavailableCode = "config.store.unavailable";

    private ConfigStoreStatus _status = ConfigStoreStatus.NotStarted;
    private int _started;
    private Task _startupPublish = Task.CompletedTask;

    // The latest publish ticket when startup ended (null, or the restore's): a commit since replaces it.
    private PublishTicket? _startupTicket;

    /// <summary>Where the running config came from and whether commands are accepted (see <see cref="StartAsync"/>).</summary>
    public ConfigStoreStatus Status => Volatile.Read(ref _status);

    /// <summary>Completes once the startup publish has its outcome (and any fallback after a failed one has its own),
    /// logged and, on success, offered to the last-known-good file (tests: then <see cref="LastKnownGoodFile.FlushAsync"/>).
    /// Startup itself never waits for it.</summary>
    internal Task StartupPublishFollowed => Volatile.Read(ref _startupPublish);

    /// <summary>
    /// The startup recompile: brings the database up to date (<see cref="ConfigDatabase.InitializeAsync"/>),
    /// loads the latest revision (none: <see cref="InitialDocument"/>), rewrites the projection if it doesn't show that
    /// revision, compiles it and queues the publish. Run once, before anything reads or writes config.
    /// </summary>
    /// <remarks>
    /// <para><b>Never awaits the publish.</b> A snapshot with YARP content can only go live once the public app attaches
    /// YARP, which starts after this returns: the publish is queued and followed in the background (logged; on success
    /// the document is written to the <see cref="LastKnownGoodFile"/>). The empty config needs no YARP and is live at
    /// once; it isn't written to the last-known-good file, which only ever holds a revision.</para>
    /// <para><b>Restore.</b> A database without revisions next to a usable last-known-good file has lost its config (a
    /// deleted or replaced file): the file's document is committed as revision #1 through the replace path (actor
    /// System, message "Restored from last-known-good #N (...)", audited) and startup goes on from there. If that commit is
    /// rejected or fails, the start is degraded on the last-known-good config (or, if that doesn't compile either, on the
    /// empty config), accepting commands.</para>
    /// <para><b>Degraded starts.</b> When the database can't be opened or migrated, or its latest revision can't be read
    /// (corrupt, unreadable by this build, damaged columns), or it doesn't compile, the last-known-good document is
    /// compiled and published instead, and <see cref="Status"/> says so. Commands are refused
    /// (<see cref="ConfigCommandStatus.Unavailable"/>) while the store itself is unusable. When only the compile failed,
    /// the database is fine and commands are accepted: they are planned on the latest revision as always (its errors
    /// show up on whatever they touch), and the first one that goes live ends the degraded state.</para>
    /// <para><b>A failed startup publish</b> (YARP rejects it, times out, ...) leaves no live config: if the last-known-good
    /// file holds another revision and no command has committed since, that is published instead, degraded and accepting
    /// commands. A superseded startup publish isn't a failure (a newer commit's publish carries the config).</para>
    /// <para>With neither a usable database revision nor a usable last-known-good document there is nothing to serve:
    /// this throws <see cref="ConfigStartupException"/>, and the process fails to start.</para>
    /// <para>A damaged last-known-good file never stops a start: it is logged as a warning, and replaced at the next
    /// publish.</para>
    /// </remarks>
    /// <exception cref="ConfigStartupException">Nothing can be served.</exception>
    /// <exception cref="InvalidOperationException">Already started.</exception>
    public async Task<ConfigStoreStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The config service has already been started.");
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ConfigStoreStatus status = await StartExclusiveAsync(cancellationToken).ConfigureAwait(false);
            _startupTicket = _latestPublish;
            Volatile.Write(ref _status, status);
            return status;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<ConfigStoreStatus> StartExclusiveAsync(CancellationToken cancellationToken)
    {
        DatabaseStart database = await LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        (LastKnownGoodEntry? lastGood, string? lastGoodProblem) = ReadLastKnownGood();

        if (database is { Problem: null, Latest: null } && lastGood is not null
            && await RestoreAsync(lastGood, cancellationToken).ConfigureAwait(false) is { } restored)
        {
            return restored;
        }

        if (database.Problem is not null)
        {
            return await StartOnLastKnownGoodAsync(database.Problem, acceptsCommands: false, lastGood, lastGoodProblem, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ConfigRevisionId revisionId = database.Latest?.Info.Id ?? ConfigRevisionId.Empty;
        ConfigCompilation compilation = await CompileWithCurrentInventoryAsync(database.Document!, revisionId, cancellationToken).ConfigureAwait(false);
        if (compilation.Snapshot is not { } snapshot)
        {
            string label = database.Latest is null ? "the empty config" : $"revision #{database.Latest.Info.Seq} ({database.Latest.Info.Id})";
            return await StartOnLastKnownGoodAsync(
                $"{label} doesn't compile: {Summarize(compilation.Errors)}", acceptsCommands: true, lastGood, lastGoodProblem, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ReportCertificateWarnings(compilation.Warnings);

        RevisionRef? revision = database.Latest is null ? null : Ref(database.Latest.Info);
        if (lastGood is not null && revision is not null && lastGood.Revision.Seq > revision.Seq)
        {
            LogLastKnownGoodAhead(lastGood.Revision.Seq, lastGood.Revision.Id, revision.Seq);
        }

        LastKnownGoodEntry? entry = database.Latest is null
            ? null
            : new LastKnownGoodEntry(revision!, database.Latest.Document, database.Latest.Info.DocumentHash);
        _startupPublish = FollowStartupPublishAsync(StartPublish(snapshot, compilation), revisionId, entry, lastGood);
        return new ConfigStoreStatus(ConfigSource.Database, revision);
    }

    /// <summary>The last-known-good file's entry, or null with why. A damaged file is logged; it never throws.</summary>
    private (LastKnownGoodEntry? Entry, string? Problem) ReadLastKnownGood()
    {
        if (_lastKnownGood is null)
        {
            return (null, "there is no last-known-good file");
        }

        if (!_lastKnownGood.IsPresent)
        {
            return (null, $"there is no {_lastKnownGood.Path}");
        }

        if (_lastKnownGood.TryRead(out LastKnownGoodEntry? entry, out string? problem))
        {
            return (entry, null);
        }

        LogLastKnownGoodDamaged(problem);
        return (null, problem);
    }

    /// <summary>
    /// Commits <paramref name="lastGood"/>'s document as the first revision of an empty database and queues its publish;
    /// null when it changes nothing (startup goes on as for an empty database). A rejected or failed commit is a degraded
    /// start on the last-known-good config, accepting commands.
    /// </summary>
    private async Task<ConfigStoreStatus?> RestoreAsync(LastKnownGoodEntry lastGood, CancellationToken cancellationToken)
    {
        string source = $"last-known-good #{lastGood.Revision.Seq} ({lastGood.Revision.Id})";
        ReplaceConfigCommand command = new(lastGood.Parsed!, BaseRevision: null) { Message = $"Restored from {source}" };
        string problem;
        try
        {
            Execution execution = await ExecuteExclusiveAsync(command, CommandOrigin.System, cancellationToken).ConfigureAwait(false);
            if (execution.Publish is { } ticket)
            {
                LogRestored(ticket.Revision.Seq, ticket.Revision.Id, source);
                _startupPublish = FollowStartupPublishAsync(ticket.Task, ticket.Revision.Id, ticket.LastKnownGood, lastGood);
                return new ConfigStoreStatus(ConfigSource.Database, ticket.Revision);
            }

            if (execution.Result.Status == ConfigCommandStatus.Unchanged)
            {
                return null;
            }

            problem = $"the database has no revisions, and restoring {source} was rejected ({DocumentNames.Of(execution.Result.Status)}: "
                + $"{Summarize([.. execution.Result.Errors, .. execution.Result.RelatedErrors])})";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The exception is logged; the status reason is a sentence a client may read.
            LogRestoreFailed(ex);
            problem = $"the database has no revisions, and restoring {source} failed";
        }

        return await StartOnLastKnownGoodAsync(problem, acceptsCommands: true, lastGood, lastGoodProblem: null, emptyIfUnusable: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A degraded start: publishes the last-known-good config (without awaiting it). Without one that compiles there is
    /// nothing to serve (<see cref="ConfigStartupException"/>), unless <paramref name="emptyIfUnusable"/>: then the
    /// database's empty config is served, still degraded.
    /// </summary>
    private async Task<ConfigStoreStatus> StartOnLastKnownGoodAsync(
        string problem, bool acceptsCommands, LastKnownGoodEntry? lastGood, string? lastGoodProblem, bool emptyIfUnusable = false, CancellationToken cancellationToken = default)
    {
        string? unusable = lastGoodProblem ?? "there is no last-known-good file";
        if (lastGood is not null)
        {
            // The last-known-good document, against the inventory as it is now.
            ConfigCompilation fallback = await CompileWithCurrentInventoryAsync(CanonicalConfigJson.Canonicalize(lastGood.Parsed!), lastGood.Revision.Id, cancellationToken).ConfigureAwait(false);
            if (fallback.Snapshot is { } snapshot)
            {
                if (acceptsCommands)
                {
                    LogDegradedAcceptingCommands(lastGood.Revision.Seq, lastGood.Revision.Id, problem);
                }
                else
                {
                    LogDegraded(lastGood.Revision.Seq, lastGood.Revision.Id, problem);
                }

                ReportCertificateWarnings(fallback.Warnings);

                // Already the file's content: nothing to write, and nothing else to fall back on.
                _startupPublish = FollowStartupPublishAsync(StartPublish(snapshot, fallback), lastGood.Revision.Id, entry: null, fallback: null);
                return new ConfigStoreStatus(ConfigSource.LastKnownGood, lastGood.Revision, problem, acceptsCommands);
            }

            unusable = $"its revision #{lastGood.Revision.Seq} ({lastGood.Revision.Id}) doesn't compile: {Summarize(fallback.Errors)}";
        }

        if (emptyIfUnusable
            && (await CompileWithCurrentInventoryAsync(InitialDocument, ConfigRevisionId.Empty, cancellationToken).ConfigureAwait(false)) is { Snapshot: { } empty } emptyCompilation)
        {
            string emptyProblem = $"{problem}; the last-known-good config can't be used either ({unusable})";
            LogDegradedOnEmpty(emptyProblem);
            _startupPublish = FollowStartupPublishAsync(StartPublish(empty, emptyCompilation), ConfigRevisionId.Empty, entry: null, fallback: null);
            return new ConfigStoreStatus(ConfigSource.Database, null, emptyProblem, AcceptsCommands: true);
        }

        // Logged once, by the host that fails to start.
        throw new ConfigStartupException($"No config can be served: {problem}; and the last-known-good config can't be used: {unusable}.");
    }

    /// <summary>Opens and migrates the database and reads the latest revision; a problem instead when it can't.</summary>
    private async Task<DatabaseStart> LoadLatestAsync(CancellationToken cancellationToken)
    {
        try
        {
            ConfigDatabaseInitialized initialized = await ConfigDatabase.InitializeAsync(_contexts, Int64OrderedIdGenerator.Shared, cancellationToken)
                .ConfigureAwait(false);
            if (initialized.AppliedMigrations.Count > 0)
            {
                LogMigrated(initialized.AppliedMigrations.Count, initialized.AppliedMigrations);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything (SQLite refusing the file, a full or read-only disk, a failed migration) means the same here.
            LogDatabaseFailed(ex);
            return new DatabaseStart(null, null, "the config database can't be opened or migrated");
        }

        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                StoredRevision? latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    return new DatabaseStart(null, InitialDocument, null);
                }

                if (!CanonicalConfigJson.TryDeserialize(latest.Document.Span, out ConfigDocument? document, out ConfigError? error))
                {
                    return new DatabaseStart(
                        null,
                        null,
                        $"revision #{latest.Info.Seq} ({latest.Info.Id}) can't be read by this build ({error.Code} at '{error.Path}': {error.Message})");
                }

                document = CanonicalConfigJson.Canonicalize(document);
                await HealProjectionAsync(context, latest, document, cancellationToken).ConfigureAwait(false);
                return new DatabaseStart(latest, document, null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A corrupt document, but also columns a converter can't read (a damaged ID or timestamp): the revision is
            // unusable whatever the exception says.
            LogDatabaseFailed(ex);
            return new DatabaseStart(null, null, "the latest config revision can't be read");
        }
    }

    /// <summary>
    /// Rewrites the projection whole when it doesn't show <paramref name="latest"/> (a crash can't cause that, the
    /// projection commits with its revision, but a restored or hand-edited database can), including when its settings
    /// row can't even be read. Any failure is logged and startup goes on: the projection only serves queries, and the
    /// next commit rewrites it whole anyway.
    /// </summary>
    private async Task HealProjectionAsync(ConfigDbContext context, StoredRevision latest, ConfigDocument document, CancellationToken cancellationToken)
    {
        try
        {
            ConfigRevisionId? reflected = await ConfigProjection.TryReflectedRevisionAsync(context, cancellationToken).ConfigureAwait(false);
            if (reflected == latest.Info.Id)
            {
                return;
            }

            var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                ConfigRevisionId? head = await context.ConfigRevisions
                    .OrderByDescending(r => r.Seq)
                    .Select(r => (ConfigRevisionId?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (head != latest.Info.Id)
                {
                    // Another writer committed meanwhile, and its commit brought the projection up to date.
                    await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                await ConfigProjection.WriteAsync(context, document, [], latest.Info.Id, parent: null, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            LogProjectionRewritten(latest.Info.Seq, latest.Info.Id, reflected?.ToString() ?? "nothing readable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.ChangeTracker.Clear();
            LogProjectionHealFailed(ex, latest.Info.Seq, latest.Info.Id);
        }
    }

    /// <summary>
    /// Logs how a startup publish went. On success, records <paramref name="entry"/> as last-known-good. On failure,
    /// publishes <paramref name="fallback"/> instead when it is another revision (see <see cref="StartAsync"/>).
    /// </summary>
    private async Task FollowStartupPublishAsync(Task<PublishResult> publish, ConfigRevisionId revision, LastKnownGoodEntry? entry, LastKnownGoodEntry? fallback)
    {
        PublishResult result = await publish.ConfigureAwait(false); // completed, never faulted
        string label = revision.IsEmpty ? "(none)" : revision.ToString();
        switch (result.Status)
        {
            case PublishStatus.Published:
                LogStartupPublished(label, result.Generation);
                if (entry is not null)
                {
                    _lastKnownGood?.Offer(entry);
                }

                return;
            case PublishStatus.Superseded:
                LogStartupPublishSuperseded(label);
                return;
            default:
                LogStartupPublishFailed(label, result.Status, result.Errors);
                if (fallback is not null && fallback.Revision.Id != revision)
                {
                    await PublishLastKnownGoodAfterFailureAsync(fallback, $"publishing revision {label} at startup failed ({result.Status}: {string.Join("; ", result.Errors)})")
                        .ConfigureAwait(false);
                }

                return;
        }
    }

    /// <summary>
    /// After a failed startup publish: publishes the last-known-good config, degraded and accepting commands, unless a
    /// command has committed since (its publish is newer) or the service is disposed. Under the writer lock, so no commit
    /// interleaves.
    /// </summary>
    private async Task PublishLastKnownGoodAfterFailureAsync(LastKnownGoodEntry lastGood, string problem)
    {
        Task<PublishResult> publish;
        await _writer.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || !ReferenceEquals(_latestPublish, _startupTicket))
            {
                return;
            }

            ConfigCompilation compilation = await CompileWithCurrentInventoryAsync(CanonicalConfigJson.Canonicalize(lastGood.Parsed!), lastGood.Revision.Id, CancellationToken.None).ConfigureAwait(false);
            if (compilation.Snapshot is not { } snapshot)
            {
                LogLastKnownGoodUnusableAfterFailure(lastGood.Revision.Seq, lastGood.Revision.Id, Summarize(compilation.Errors));
                return;
            }

            LogDegradedAcceptingCommands(lastGood.Revision.Seq, lastGood.Revision.Id, problem);
            Volatile.Write(ref _status, new ConfigStoreStatus(ConfigSource.LastKnownGood, lastGood.Revision, problem, AcceptsCommands: true));
            publish = StartPublish(snapshot, compilation);
        }
        finally
        {
            _writer.Release();
        }

        await FollowStartupPublishAsync(publish, lastGood.Revision.Id, entry: null, fallback: null).ConfigureAwait(false);
    }

    /// <summary>
    /// A commit's publish succeeded: records its document as last-known-good (in the background) and ends a degraded
    /// start that still accepted commands (the database's config is live again). Runs before the command that waited for
    /// the publish returns; idempotent, since a superseded command may run it for the newer publish too.
    /// </summary>
    private void WentLive(PublishTicket ticket)
    {
        if (ticket.LastKnownGood is { } entry)
        {
            _lastKnownGood?.Offer(entry);
        }

        ConfigStoreStatus status = Status;
        if (ticket.FromDatabase && !ticket.Revision.Id.IsEmpty && status.IsDegraded && status.AcceptsCommands
            && ReferenceEquals(Interlocked.CompareExchange(ref _status, new ConfigStoreStatus(ConfigSource.Database, ticket.Revision), status), status))
        {
            LogRecovered(ticket.Revision.Seq, ticket.Revision.Id);
        }
    }

    /// <summary>The live config compiled against the current inventory (see <see cref="CompileLiveAsync"/>).</summary>
    /// <param name="Compilation">A successful compilation.</param>
    /// <param name="Revision">The revision it is.</param>
    /// <param name="Entry">The last-known-good entry to record once live; null when it came from that file.</param>
    /// <param name="FromDatabase">It is the database's latest revision.</param>
    private sealed record LiveCompile(ConfigCompilation Compilation, RevisionRef Revision, LastKnownGoodEntry? Entry, bool FromDatabase);

    /// <summary>
    /// Compiles the config that is live, against the inventory as <paramref name="context"/> sees it, for a republish
    /// that no command asked for (a certificate change, a certificate's validity boundary passing). The live config is the
    /// database's latest revision, except when the start is degraded on the last-known-good file, or the database has no
    /// revision (it was lost): then it is the last-known-good document, and <b>never the empty config</b>. Null (logged)
    /// when there is nothing to republish: no last-known-good document, or the one to compile doesn't compile.
    /// </summary>
    private async Task<LiveCompile?> CompileLiveAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        if (Status.Source != ConfigSource.LastKnownGood)
        {
            StoredRevision? latest;
            ConfigDocument document;
            try
            {
                (latest, document) = await LoadAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRepublishSkipped("the latest revision can't be read", $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }

            if (latest is not null)
            {
                CertificateInventory inventory = await _certificates.LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
                ConfigCompilation compilation = _compiler.Compile(document, latest.Info.Id, inventory);
                if (compilation.Snapshot is null)
                {
                    LogRepublishSkipped($"revision #{latest.Info.Seq} doesn't compile", Summarize(compilation.Errors));
                    return null;
                }

                RevisionRef revision = Ref(latest.Info);
                return new LiveCompile(compilation, revision, new LastKnownGoodEntry(revision, latest.Document, latest.Info.DocumentHash), FromDatabase: true);
            }
        }

        (LastKnownGoodEntry? lastGood, string? problem) = ReadLastKnownGood();
        if (lastGood is null)
        {
            LogRepublishSkipped("there is no revision and no usable last-known-good document", problem ?? "nothing to republish");
            return null;
        }

        ConfigDocument lastGoodDocument = CanonicalConfigJson.Canonicalize(lastGood.Parsed!);
        ConfigCompilation fallback = _compiler.Compile(
            lastGoodDocument, lastGood.Revision.Id, await _certificates.LoadAsync(context, lastGoodDocument, cancellationToken).ConfigureAwait(false));
        if (fallback.Snapshot is null)
        {
            LogRepublishSkipped($"the last-known-good revision #{lastGood.Revision.Seq} doesn't compile", Summarize(fallback.Errors));
            return null;
        }

        return new LiveCompile(fallback, lastGood.Revision, Entry: null, FromDatabase: false);
    }

    /// <summary>Queues a <see cref="LiveCompile"/>'s publish (under the writer lock, like every publish).</summary>
    private PublishTicket PublishLive(LiveCompile live)
    {
        ReportCertificateWarnings(live.Compilation.Warnings);
        PublishTicket ticket = new(live.Revision, StartPublish(live.Compilation.Snapshot!, live.Compilation), live.Entry, live.FromDatabase);
        _latestPublish = ticket;
        return ticket;
    }

    /// <summary>
    /// A certificate's validity boundary passed (it expired or became valid): republishes the live config against the
    /// inventory as it now is, under the writer lock, so it is ordered with every other publish (the publisher coalesces
    /// them). Failures are logged and audited like any publish that nobody waits for.
    /// </summary>
    private async Task RepublishForValidityAsync()
    {
        try
        {
            PublishTicket? ticket = null;
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_disposed || Status is { AcceptsCommands: false })
                {
                    return;
                }

                var context = await _contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    if (await CompileLiveAsync(context, CancellationToken.None).ConfigureAwait(false) is { } live)
                    {
                        ticket = PublishLive(live);
                    }
                }
            }
            finally
            {
                _writer.Release();
            }

            if (ticket is not null)
            {
                await AuditLatePublishAsync(ticket, CommandOrigin.System).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogValidityRepublishFailed(ex);
        }
    }

    /// <summary>
    /// Recompiles the live config against the current inventory and <b>queues</b> its publish without waiting for it, for a
    /// startup step that changed the inventory before the web apps serve (<see cref="CertificateRecovery"/>). The publish is
    /// ordered with every other one and completes while the public app starts, so the first snapshot includes what changed;
    /// nothing here waits for YARP, which does not exist yet.
    /// </summary>
    public async Task RepublishLiveAsync()
    {
        try
        {
            PublishTicket? ticket = null;
            await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_disposed || Status is { AcceptsCommands: false })
                {
                    return;
                }

                var context = await _contexts.CreateDbContextAsync(CancellationToken.None).ConfigureAwait(false);
                await using (context.ConfigureAwait(false))
                {
                    if (await CompileLiveAsync(context, CancellationToken.None).ConfigureAwait(false) is { } live)
                    {
                        ticket = PublishLive(live);
                    }
                }
            }
            finally
            {
                _writer.Release();
            }

            if (ticket is not null)
            {
                _ = AuditLatePublishAsync(ticket, CommandOrigin.System);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRecoveryRepublishFailed(ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not republishing: {Reason} ({Detail}). Nothing was published; the next commit or start compiles with the current certificates.")]
    private partial void LogRepublishSkipped(string reason, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "Republishing after the startup certificate recovery failed.")]
    private partial void LogRecoveryRepublishFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Republishing after a certificate's validity changed failed.")]
    private partial void LogValidityRepublishFailed(Exception exception);

    /// <summary>
    /// Compiles <paramref name="document"/> against the certificate inventory as it is now, for the startup paths: a
    /// certificate table that can't be read gives an inventory of the built-in certificates only, never an exception
    /// (a start must not fail over certificates: hosts whose certificates are unknown degrade to the default certificate,
    /// with warnings). Unusable certificates are warnings, not errors, here (<see cref="IntroducedUnusableBindings"/> is
    /// for commands only).
    /// </summary>
    private async Task<ConfigCompilation> CompileWithCurrentInventoryAsync(ConfigDocument document, ConfigRevisionId revision, CancellationToken cancellationToken)
    {
        CertificateInventory inventory;
        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                inventory = await _certificates.LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogInventoryUnreadable(ex);
            inventory = await _certificates.LoadAsync(null, document, cancellationToken).ConfigureAwait(false);
        }

        return _compiler.Compile(document, revision, inventory);
    }

    private void ReportCertificateWarnings(IReadOnlyList<ConfigWarning> warnings)
    {
        if (warnings.Count > 0)
        {
            LogCertificateWarnings(warnings.Count, string.Join("; ", warnings.Take(MaxAuditedCodes).Select(w => $"{w.Path}: {w.Message}")));
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} certificate warning(s) in the config that is going live: {Warnings}")]
    private partial void LogCertificateWarnings(int count, string warnings);

    [LoggerMessage(Level = LogLevel.Error, Message = "The certificate inventory can't be read; compiling with the built-in certificates only. Hosts keep their config but lose certificates the vault would have served.")]
    private partial void LogInventoryUnreadable(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the last-known-good config into the empty database failed; the startup continues on the last-known-good file.")]
    private partial void LogRestoreFailed(Exception exception);

    /// <summary>A command refused because the store is unusable; not audited (the audit log lives in that store).</summary>
    private ConfigCommandResult Unavailable(ConfigCommand command, ConfigStoreStatus status)
    {
        LogRefused(command.GetType().Name, status.Problem ?? "unknown");
        return new ConfigCommandResult(
            ConfigCommandStatus.Unavailable,
            Errors:
            [
                new ConfigError(
                    string.Empty,
                    StoreUnavailableCode,
                    $"The config store is unavailable, so changes are refused: {status.Problem}. Paduraksa is serving the last-known-good config"
                    + $" ({status.Revision}); fix or restore the database and restart."),
            ]);
    }

    private static string Summarize(IReadOnlyList<ConfigError> errors) =>
        errors.Count == 0 ? "no reason given" : string.Join("; ", errors.Take(MaxAuditedCodes).Select(e => $"{e.Path}: {e.Message}"));

    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated the config database ({Count} migration(s): {Migrations}).")]
    private partial void LogMigrated(int count, IReadOnlyList<string> migrations);

    [LoggerMessage(Level = LogLevel.Error, Message = "The config database can't be used.")]
    private partial void LogDatabaseFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rewrote the config projection to show revision #{Seq} ({Revision}); it showed {Reflected}.")]
    private partial void LogProjectionRewritten(long seq, ConfigRevisionId revision, string reflected);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't rewrite the config projection for revision #{Seq} ({Revision}); queries may show stale config until the next commit rewrites it.")]
    private partial void LogProjectionHealFailed(Exception exception, long seq, ConfigRevisionId revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good file holds revision #{LastGoodSeq} ({LastGood}), newer than the database's latest (#{LatestSeq}): was the database replaced or restored? Serving the database's config, which is the source of truth; the file is replaced at the next publish.")]
    private partial void LogLastKnownGoodAhead(long lastGoodSeq, ConfigRevisionId lastGood, long latestSeq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good file is damaged and can't be a fallback: {Problem}. It is replaced at the next publish.")]
    private partial void LogLastKnownGoodDamaged(string problem);

    [LoggerMessage(Level = LogLevel.Critical, Message = "RESTORED: the config database had no revisions; committed the {Source} config as revision #{Seq} ({Revision}).")]
    private partial void LogRestored(long seq, ConfigRevisionId revision, string source);

    [LoggerMessage(Level = LogLevel.Critical, Message = "DEGRADED: serving the last-known-good config (revision #{Seq}, {Revision}) because {Problem}. Config changes are refused until the database is fixed or restored and Paduraksa restarted.")]
    private partial void LogDegraded(long seq, ConfigRevisionId revision, string problem);

    [LoggerMessage(Level = LogLevel.Critical, Message = "DEGRADED: serving the last-known-good config (revision #{Seq}, {Revision}) because {Problem}. Config changes are accepted; the first one that goes live ends the degraded state.")]
    private partial void LogDegradedAcceptingCommands(long seq, ConfigRevisionId revision, string problem);

    [LoggerMessage(Level = LogLevel.Critical, Message = "DEGRADED: serving the empty config because {Problem}. Config changes are accepted; the first one that goes live ends the degraded state.")]
    private partial void LogDegradedOnEmpty(string problem);

    [LoggerMessage(Level = LogLevel.Error, Message = "Can't fall back on the last-known-good config (revision #{Seq}, {Revision}) either: it doesn't compile ({Errors}).")]
    private partial void LogLastKnownGoodUnusableAfterFailure(long seq, ConfigRevisionId revision, string errors);

    [LoggerMessage(Level = LogLevel.Information, Message = "Published the startup snapshot (revision {Revision}, generation {Generation}).")]
    private partial void LogStartupPublished(string revision, long generation);

    [LoggerMessage(Level = LogLevel.Information, Message = "The startup snapshot (revision {Revision}) was superseded by a newer commit's before it went live.")]
    private partial void LogStartupPublishSuperseded(string revision);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Publishing the startup snapshot (revision {Revision}) failed ({Status}): {Errors}")]
    private partial void LogStartupPublishFailed(string revision, PublishStatus status, IReadOnlyList<string> errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recovered from a degraded start: revision #{Seq} ({Revision}) from the database is live.")]
    private partial void LogRecovered(long seq, ConfigRevisionId revision);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused {Command}: the config store is unavailable ({Problem}).")]
    private partial void LogRefused(string command, string problem);

    /// <summary>The database at startup: the latest revision (null: none yet) and its canonical document, or why not.</summary>
    private sealed record DatabaseStart(StoredRevision? Latest, ConfigDocument? Document, string? Problem);
}

/// <summary>Where the running config came from at startup.</summary>
public enum ConfigSource
{
    /// <summary><see cref="ConfigService.StartAsync"/> hasn't run (a process that only writes, like the CLI).</summary>
    NotStarted,

    /// <summary>The database's latest revision (or the empty config, before the first commit).</summary>
    Database,

    /// <summary>The last-known-good file: a degraded start.</summary>
    LastKnownGood,
}

/// <summary>The config store's state (<see cref="ConfigService.Status"/>).</summary>
/// <param name="Source">Where the running config came from.</param>
/// <param name="Revision">The revision startup published (null: the empty config, or not started), or the one whose
/// publish ended a degraded start.</param>
/// <param name="Problem">Why the start is degraded; null when it isn't.</param>
/// <param name="AcceptsCommands">False while the store itself is unusable: commands get
/// <see cref="ConfigCommandStatus.Unavailable"/>.</param>
public sealed record ConfigStoreStatus(ConfigSource Source, RevisionRef? Revision, string? Problem = null, bool AcceptsCommands = true)
{
    /// <summary>Before <see cref="ConfigService.StartAsync"/>.</summary>
    public static ConfigStoreStatus NotStarted { get; } = new(ConfigSource.NotStarted, null);

    /// <summary>A degraded start: not serving the database's latest config (see <see cref="Problem"/>).</summary>
    public bool IsDegraded => Problem is not null;
}

/// <summary>Startup found nothing to serve: neither the database's latest revision nor the last-known-good config can
/// be used (see <see cref="ConfigService.StartAsync"/>).</summary>
public sealed class ConfigStartupException : Exception
{
    public ConfigStartupException()
    {
    }

    public ConfigStartupException(string message)
        : base(message)
    {
    }

    public ConfigStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
