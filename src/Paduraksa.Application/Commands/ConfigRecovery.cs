using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Compilation;
using Paduraksa.Application.Config;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.LastKnownGood;
using Paduraksa.Application.Revisions;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Ids;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Data;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Commands;

/// <summary>
/// The startup recompile and what stands behind it: brings the database up to date, loads the latest revision, compiles it and
/// queues the publish, with the last-known-good file as the fallback when the database or its latest revision can't be used, and
/// the republishes of the live config that no command asked for.
/// </summary>
/// <remarks>
/// <para><b>Never awaits the publish.</b> A snapshot with YARP content can only go live once the public app attaches
/// YARP, which starts after startup returns, so the publish is queued and followed in the background. The empty config needs
/// no YARP and is live at once, and is never written to the last-known-good file, which only holds a revision.</para>
/// <para><b>Restore.</b> A database without revisions next to a usable last-known-good file has lost its config: the
/// file's document is committed as revision #1 through the replace path, audited, and startup goes on from there. A
/// rejected or failed restore commit degrades the start on the last-known-good config (or the empty one).</para>
/// <para><b>Degraded starts.</b> When the database can't be opened or migrated, or its latest revision can't be read or
/// doesn't compile, the last-known-good document is compiled and published instead and the store's status says so.
/// Commands are refused only while the store itself is unusable; when just the compile failed they are accepted, and the
/// first one that goes live ends the degraded state. A failed or superseded startup publish falls back the same way, and a
/// damaged last-known-good file never stops a start.</para>
/// <para>With neither a usable database revision nor a usable last-known-good document there is nothing to serve: starting
/// throws <see cref="ConfigStartupException"/>.</para>
/// </remarks>
internal sealed partial class ConfigRecovery
{
    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly IConfigCompiler _compiler;
    private readonly CertificateInventoryLoader _certificates;
    private readonly LastKnownGoodFile _lastKnownGood;
    private readonly ConfigRevisionStore _revisions;
    private readonly ConfigPipeline _pipeline;
    private readonly ConfigPublishing _publishing;
    private readonly SemaphoreSlim _writer;
    private readonly IPasswordCredentials _passwords;
    private readonly ILogger _logger;

    public ConfigRecovery(
        IDbContextFactory<ConfigDbContext> contexts, IConfigCompiler compiler, CertificateInventoryLoader certificates,
        LastKnownGoodFile lastKnownGood, ConfigRevisionStore revisions, ConfigPipeline pipeline, ConfigPublishing publishing,
        SemaphoreSlim writer, IPasswordCredentials passwords, ILoggerFactory loggerFactory)
    {
        _contexts = contexts;
        _compiler = compiler;
        _certificates = certificates;
        _lastKnownGood = lastKnownGood;
        _revisions = revisions;
        _pipeline = pipeline;
        _publishing = publishing;
        _writer = writer;
        _passwords = passwords;
        _logger = loggerFactory.CreateLogger<ConfigRecovery>();
    }

    private int _started;
    private Task _startupPublish = Task.CompletedTask;

    // The latest publish ticket when startup ended (null, or the restore's): a commit since replaces it.
    private PublishTicket? _startupTicket;

    /// <summary>Completes once the startup publish has its outcome (and any fallback after a failed one has its own),
    /// logged and, on success, offered to the last-known-good file (tests: then <see cref="LastKnownGoodFile.FlushAsync"/>).
    /// Startup itself never waits for it.</summary>
    internal Task StartupPublishFollowed => Volatile.Read(ref _startupPublish);

    /// <summary>
    /// The startup recompile: brings the database up to date, loads the latest revision, compiles it and queues the publish. Run once, before anything reads or writes config.
    /// </summary>
    /// <remarks>
    /// <para><b>Never awaits the publish.</b> A snapshot with YARP content can only go live once the public app attaches
    /// YARP, which starts after this returns, so the publish is queued and followed in the background. The empty config needs
    /// no YARP and is live at once, and is never written to the last-known-good file, which only holds a revision.</para>
    /// <para><b>Restore.</b> A database without revisions next to a usable last-known-good file has lost its config: the
    /// file's document is committed as revision #1 through the replace path, audited, and startup goes on from there. A
    /// rejected or failed restore commit degrades the start on the last-known-good config (or the empty one).</para>
    /// <para><b>Degraded starts.</b> When the database can't be opened or migrated, or its latest revision can't be read or
    /// doesn't compile, the last-known-good document is compiled and published instead and the status says so.
    /// Commands are refused only while the store itself is unusable; when just the compile failed they are accepted, and the
    /// first one that goes live ends the degraded state. A failed or superseded startup publish falls back the same way, and a
    /// damaged last-known-good file never stops a start.</para>
    /// <para>With neither a usable database revision nor a usable last-known-good document there is nothing to serve: this
    /// throws <see cref="ConfigStartupException"/>.</para>
    /// </remarks>
    public async Task<ConfigStoreStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_publishing.IsDisposed, nameof(ConfigService));
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            throw new InvalidOperationException("The config service has already been started.");
        }

        await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await StartExclusiveAsync(cancellationToken).ConfigureAwait(false);
            _startupTicket = _publishing.Latest;
            _publishing.SetStatus(status);
            return status;
        }
        finally
        {
            _writer.Release();
        }
    }

    private async Task<ConfigStoreStatus> StartExclusiveAsync(CancellationToken cancellationToken)
    {
        var database = await LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        var (lastGood, lastGoodProblem) = ReadLastKnownGood();

        if (database is { Problem: null, Latest: null } && lastGood is not null
            && await RestoreAsync(lastGood, cancellationToken).ConfigureAwait(false) is { } restored)
        {
            return restored;
        }

        if (database.Problem is not null)
        {
            return await StartOnLastKnownGoodAsync(database.Problem, acceptsCommands: false, lastGood, lastGoodProblem, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Before anything compiles the document: a conversion that took the passwords out of it must put them in the vault.
        await AccessListPasswords.ImportAsync(_passwords, database.Legacy, cancellationToken).ConfigureAwait(false);
        if (database is { Upgraded: true, Latest: not null } && await WriteUpgradedAsync(database, lastGood, cancellationToken).ConfigureAwait(false) is { } upgraded)
        {
            await DeleteOrphansAsync(database, cancellationToken).ConfigureAwait(false);
            return upgraded;
        }

        var revisionId = database.Latest?.Info.Id ?? ConfigRevisionId.Empty;
        var compilation = await CompileWithCurrentInventoryAsync(database.Document!, revisionId, cancellationToken).ConfigureAwait(false);
        if (compilation.Snapshot is not { } snapshot)
        {
            var label = database.Latest is null ? "the empty config" : $"revision #{database.Latest.Info.Seq} ({database.Latest.Info.Id})";
            return await StartOnLastKnownGoodAsync(
                $"{label} doesn't compile: {Summarize(compilation.Errors)}", acceptsCommands: true, lastGood, lastGoodProblem, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        ReportCertificateWarnings(compilation.Warnings);
        await DeleteOrphansAsync(database, cancellationToken).ConfigureAwait(false);

        var revision = database.Latest is null ? null : ConfigHead.Ref(database.Latest.Info);
        if (lastGood is not null && revision is not null && lastGood.Revision.Seq > revision.Seq)
        {
            LogLastKnownGoodAhead(lastGood.Revision.Seq, lastGood.Revision.Id, revision.Seq);
        }

        var entry = database.Latest is null
            ? null
            : new LastKnownGoodEntry(revision!, database.Latest.Document, database.Latest.Info.DocumentHash);
        _startupPublish = FollowStartupPublishAsync(_publishing.Start(snapshot, compilation), revisionId, entry, lastGood);
        return new ConfigStoreStatus(ConfigSource.Database, revision);
    }

    /// <summary>
    /// The live revision still had inline IP allow lists, or users' password hashes, which reading converted (to access lists, to credential IDs): stores the converted
    /// document as one revision (actor System, audited, saying what was converted), so the config that is running is the
    /// config that is stored. Every host, profile and list counts as touched, so their versions move. Null when the converted document can't be stored (it doesn't validate or compile): startup then runs on the converted document in memory, and the next change writes it.
    /// </summary>
    private async Task<ConfigStoreStatus?> WriteUpgradedAsync(DatabaseStart database, LastKnownGoodEntry? lastGood, CancellationToken cancellationToken)
    {
        var latest = database.Latest!;
        try
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var document = database.Document!;
                var changes = ConfigDiff.Compare(ConfigHead.Initial with { Settings = document.Settings with { Defaults = new HostSettings() } }, document);
                var stamped = ConfigVersions.Stamp(document, document, changes, latest.Info.Seq + 1);
                if (ConfigValidator.Validate(stamped) is { Count: > 0 } errors)
                {
                    LogUpgradeRefused(latest.Info.Seq, latest.Info.Id, Summarize(errors));
                    return null;
                }

                var candidate = CanonicalConfigJson.Canonicalize(stamped);
                var revisionId = _revisions.NextId();
                var compilation = await CompileWithCurrentInventoryAsync(candidate, revisionId, cancellationToken).ConfigureAwait(false);
                if (compilation.Snapshot is null)
                {
                    LogUpgradeRefused(latest.Info.Seq, latest.Info.Id, Summarize(compilation.Errors));
                    return null;
                }

                var message = UpgradeMessage(database.Upgrade, candidate);
                var execution = await _pipeline.CommitAsync(
                        context, CommandOrigin.System, new ConfigCommandPlanner.Audited("config.upgrade", null), candidate, changes, revisionId, compilation, message, version: null, cancellationToken)
                    .ConfigureAwait(false);
                var ticket = execution.Publish!;

                LogUpgraded(ticket.Revision.Seq, ticket.Revision.Id, latest.Info.Seq, candidate.AccessLists.Count);
                _startupPublish = FollowStartupPublishAsync(ticket.Task, ticket.Revision.Id, ticket.LastKnownGood, lastGood);
                ReportCertificateWarnings(compilation.Warnings);
                return new ConfigStoreStatus(ConfigSource.Database, ticket.Revision);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogUpgradeFailed(ex, latest.Info.Seq, latest.Info.Id);
            return null;
        }
    }

    // The vault's credentials the live config doesn't name (a change failed after setting a password). Only when the database's latest
    // revision is the live config: a start on the last-known-good file, or on no revision at all, doesn't know what is orphaned.
    private async Task DeleteOrphansAsync(DatabaseStart database, CancellationToken cancellationToken)
    {
        if (database is { Latest: not null, Document: { } document })
        {
            var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var held = await DraftService.PendingOfAllAsync(context, cancellationToken).ConfigureAwait(false);
                await AccessListPasswords.DeleteOrphansAsync(_passwords, document, held, _logger, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>What the startup revision says it converted, for each case and for both, within the message length.</summary>
    private static string UpgradeMessage(DocumentUpgrade upgrade, ConfigDocument document)
    {
        var users = upgrade.Credentials.Count;
        var passwords = $"{users} access-list {(users == 1 ? "password" : "passwords")} into the vault";
        if (!upgrade.AllowLists)
        {
            return $"Moved {passwords}";
        }

        var names = string.Join(", ", document.AccessLists.Select(static l => l.Name));
        var lists = $"Converted the inline IP allow lists to access lists: {names}";
        var both = upgrade.Passwords ? $"{lists}; moved {passwords}" : lists;
        if (both.Length <= ConfigRevisionStore.MaxMessageLength)
        {
            return both;
        }

        lists = $"Converted the inline IP allow lists to {document.AccessLists.Count} access lists";
        return upgrade.Passwords ? $"{lists}; moved {passwords}" : lists;
    }

    /// <summary>The last-known-good file's entry, or null with why. A damaged file is logged; it never throws.</summary>
    private (LastKnownGoodEntry? Entry, string? Problem) ReadLastKnownGood()
    {
        if (!_lastKnownGood.IsPresent)
        {
            return (null, $"there is no {_lastKnownGood.Path}");
        }

        if (_lastKnownGood.TryRead(out var entry, out var problem))
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
        var source = $"last-known-good #{lastGood.Revision.Seq} ({lastGood.Revision.Id})";
        await AccessListPasswords.ImportAsync(_passwords, lastGood.Legacy, cancellationToken).ConfigureAwait(false);
        ReplaceConfigCommand command = new(lastGood.Parsed!, BaseRevision: null) { Message = $"Restored from {source}" };
        string problem;
        try
        {
            var execution = await _pipeline.ExecuteExclusiveAsync(command, CommandOrigin.System, cancellationToken).ConfigureAwait(false);
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
        var unusable = lastGoodProblem ?? "there is no last-known-good file";
        if (lastGood is not null)
        {
            // The last-known-good document, against the inventory as it is now.
            await AccessListPasswords.ImportAsync(_passwords, lastGood.Legacy, cancellationToken).ConfigureAwait(false);
            var fallback = await CompileWithCurrentInventoryAsync(CanonicalConfigJson.Canonicalize(lastGood.Parsed!), lastGood.Revision.Id, cancellationToken).ConfigureAwait(false);
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
                _startupPublish = FollowStartupPublishAsync(_publishing.Start(snapshot, fallback), lastGood.Revision.Id, entry: null, fallback: null);
                return new ConfigStoreStatus(ConfigSource.LastKnownGood, lastGood.Revision, problem, acceptsCommands);
            }

            unusable = $"its revision #{lastGood.Revision.Seq} ({lastGood.Revision.Id}) doesn't compile: {Summarize(fallback.Errors)}";
        }

        if (emptyIfUnusable
            && (await CompileWithCurrentInventoryAsync(ConfigHead.Initial, ConfigRevisionId.Empty, cancellationToken).ConfigureAwait(false)) is { Snapshot: { } empty } emptyCompilation)
        {
            var emptyProblem = $"{problem}; the last-known-good config can't be used either ({unusable})";
            LogDegradedOnEmpty(emptyProblem);
            _startupPublish = FollowStartupPublishAsync(_publishing.Start(empty, emptyCompilation), ConfigRevisionId.Empty, entry: null, fallback: null);
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
            var initialized = await ConfigDatabase.InitializeAsync(_contexts, Int64OrderedIdGenerator.Shared, cancellationToken)
                .ConfigureAwait(false);
            if (initialized.AppliedMigrations.Count > 0)
            {
                LogMigrated(initialized.AppliedMigrations.Count, initialized.AppliedMigrations);
            }

            if (initialized.RestrictedFiles.Count > 0)
            {
                LogFilesRestricted(initialized.RestrictedFiles);
            }

            foreach (var failure in initialized.UnrestrictedFiles)
            {
                LogFileNotRestricted(failure.Path, System.Convert.ToString((int)failure.Mode, 8), failure.Reason);
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
                var latest = await ConfigRevisionStore.GetLatestAsync(context, cancellationToken).ConfigureAwait(false);
                if (latest is null)
                {
                    return new DatabaseStart(null, ConfigHead.Initial, null);
                }

                if (!CanonicalConfigJson.TryDeserialize(latest.Document.Span, out var document, out var error, out var upgrade))
                {
                    return new DatabaseStart(
                        null,
                        null,
                        $"revision #{latest.Info.Seq} ({latest.Info.Id}) can't be read by this build ({error.Code} at '{error.Path}': {error.Message})");
                }

                document = CanonicalConfigJson.Canonicalize(document);
                return new DatabaseStart(latest, document, null, upgrade);
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
    /// Logs how a startup publish went. On success, records <paramref name="entry"/> as last-known-good. On failure,
    /// publishes <paramref name="fallback"/> instead when it is another revision (see <see cref="StartAsync"/>).
    /// </summary>
    private async Task FollowStartupPublishAsync(Task<PublishResult> publish, ConfigRevisionId revision, LastKnownGoodEntry? entry, LastKnownGoodEntry? fallback)
    {
        var result = await publish.ConfigureAwait(false); // completed, never faulted
        var label = revision.IsEmpty ? "(none)" : revision.ToString();
        switch (result.Status)
        {
            case PublishStatus.Published:
                LogStartupPublished(label, result.Generation);
                if (entry is not null)
                {
                    _lastKnownGood.Offer(entry);
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
            if (_publishing.IsDisposed || !ReferenceEquals(_publishing.Latest, _startupTicket))
            {
                return;
            }

            await AccessListPasswords.ImportAsync(_passwords, lastGood.Legacy, CancellationToken.None).ConfigureAwait(false);
            var compilation = await CompileWithCurrentInventoryAsync(CanonicalConfigJson.Canonicalize(lastGood.Parsed!), lastGood.Revision.Id, CancellationToken.None).ConfigureAwait(false);
            if (compilation.Snapshot is not { } snapshot)
            {
                LogLastKnownGoodUnusableAfterFailure(lastGood.Revision.Seq, lastGood.Revision.Id, Summarize(compilation.Errors));
                return;
            }

            LogDegradedAcceptingCommands(lastGood.Revision.Seq, lastGood.Revision.Id, problem);
            _publishing.SetStatus(new ConfigStoreStatus(ConfigSource.LastKnownGood, lastGood.Revision, problem, AcceptsCommands: true));
            publish = _publishing.Start(snapshot, compilation);
        }
        finally
        {
            _writer.Release();
        }

        await FollowStartupPublishAsync(publish, lastGood.Revision.Id, entry: null, fallback: null).ConfigureAwait(false);
    }

    /// <summary>The live config compiled against the current inventory (see <see cref="CompileLiveAsync"/>).</summary>
    /// <param name="Compilation">A successful compilation.</param>
    /// <param name="Revision">The revision it is.</param>
    /// <param name="Entry">The last-known-good entry to record once live; null when it came from that file.</param>
    /// <param name="FromDatabase">It is the database's latest revision.</param>
    internal sealed record LiveCompile(ConfigCompilation Compilation, RevisionRef Revision, LastKnownGoodEntry? Entry, bool FromDatabase);

    /// <summary>
    /// Compiles the config that is live, against the inventory as <paramref name="context"/> sees it, for a republish no
    /// command asked for. The live config is the database's latest revision, except when the start is degraded on the
    /// last-known-good file or the database has no revision: then it is the last-known-good document, and never the empty
    /// config. Null (logged) when there is nothing to republish.
    /// </summary>
    internal async Task<LiveCompile?> CompileLiveAsync(ConfigDbContext context, CancellationToken cancellationToken)
    {
        if (_publishing.Status.Source != ConfigSource.LastKnownGood)
        {
            StoredRevision? latest;
            ConfigDocument document;
            try
            {
                (latest, document) = await ConfigHead.LoadAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRepublishSkipped("the latest revision can't be read", $"{ex.GetType().Name}: {ex.Message}");
                return null;
            }

            if (latest is not null)
            {
                var inventory = await _certificates.LoadAsync(context, document, cancellationToken).ConfigureAwait(false);
                var compilation = _compiler.Compile(document, latest.Info.Id, inventory);
                if (compilation.Snapshot is null)
                {
                    LogRepublishSkipped($"revision #{latest.Info.Seq} doesn't compile", Summarize(compilation.Errors));
                    return null;
                }

                var revision = ConfigHead.Ref(latest.Info);
                return new LiveCompile(compilation, revision, new LastKnownGoodEntry(revision, latest.Document, latest.Info.DocumentHash), FromDatabase: true);
            }
        }

        var (lastGood, problem) = ReadLastKnownGood();
        if (lastGood is null)
        {
            LogRepublishSkipped("there is no revision and no usable last-known-good document", problem ?? "nothing to republish");
            return null;
        }

        await AccessListPasswords.ImportAsync(_passwords, lastGood.Legacy, cancellationToken).ConfigureAwait(false);
        var lastGoodDocument = CanonicalConfigJson.Canonicalize(lastGood.Parsed!);
        var fallback = _compiler.Compile(
            lastGoodDocument, lastGood.Revision.Id, await _certificates.LoadAsync(context, lastGoodDocument, cancellationToken).ConfigureAwait(false));
        if (fallback.Snapshot is null)
        {
            LogRepublishSkipped($"the last-known-good revision #{lastGood.Revision.Seq} doesn't compile", Summarize(fallback.Errors));
            return null;
        }

        return new LiveCompile(fallback, lastGood.Revision, Entry: null, FromDatabase: false);
    }

    /// <summary>Queues a <see cref="LiveCompile"/>'s publish (under the writer lock, like every publish).</summary>
    internal PublishTicket PublishLive(LiveCompile live)
    {
        ReportCertificateWarnings(live.Compilation.Warnings);
        PublishTicket ticket = new(live.Revision, _publishing.Start(live.Compilation.Snapshot!, live.Compilation), live.Entry, live.FromDatabase);
        _publishing.Latest = ticket;
        return ticket;
    }

    /// <summary>
    /// A certificate's validity boundary passed (it expired or became valid): republishes the live config against the
    /// inventory as it now is, under the writer lock, so it is ordered with every other publish (the publisher coalesces
    /// them). Failures are logged and audited like any publish that nobody waits for.
    /// </summary>
    internal async Task RepublishForValidityAsync()
    {
        try
        {
            PublishTicket? ticket = null;
            await _writer.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_publishing.IsDisposed || _publishing.Status is { AcceptsCommands: false })
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
                await _publishing.AuditLateAsync(ticket, CommandOrigin.System).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogValidityRepublishFailed(ex);
        }
    }

    /// <summary>
    /// Recompiles the live config against the current inventory and queues its publish without waiting, for a startup step
    /// that changed the inventory before the web apps serve (<see cref="CertificateRecovery"/>): the publish completes while
    /// the public app starts, so the first snapshot includes what changed. True when a publish was queued; false when nothing
    /// was (the store can't take commands, the live config can't be compiled, or the attempt failed, which is logged), so a
    /// caller that must see its change go live can try again.
    /// </summary>
    public async Task<bool> RepublishLiveAsync()
    {
        try
        {
            PublishTicket? ticket = null;
            await _writer.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_publishing.IsDisposed || _publishing.Status is { AcceptsCommands: false })
                {
                    return false;
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

            if (ticket is null)
            {
                return false;
            }

            _ = _publishing.AuditLateAsync(ticket, CommandOrigin.System);
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRecoveryRepublishFailed(ex);
            return false;
        }
    }

    /// <summary>
    /// Compiles <paramref name="document"/> against the certificate inventory as it is now, for the startup paths: a
    /// certificate table that can't be read gives an inventory of the built-in certificates only, never an exception, because a
    /// start must not fail over certificates. Unusable certificates are warnings here, not errors.
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
            LogCertificateWarnings(warnings.Count, string.Join("; ", warnings.Take(ConfigAudit.MaxAuditedCodes).Select(w => $"{w.Path}: {w.Message}")));
        }
    }

    private static string Summarize(IReadOnlyList<ConfigError> errors) =>
        errors.Count == 0 ? "no reason given" : string.Join("; ", errors.Take(ConfigAudit.MaxAuditedCodes).Select(e => $"{e.Path}: {e.Message}"));

    /// <summary>The database at startup: the latest revision (null: none yet) and its canonical document, or why not.</summary>
    private sealed record DatabaseStart(StoredRevision? Latest, ConfigDocument? Document, string? Problem, DocumentUpgrade? Conversion = null)
    {
        /// <summary>What reading the latest revision converted: inline allow lists, inline password hashes.</summary>
        public DocumentUpgrade Upgrade => Conversion ?? DocumentUpgrade.None;

        public bool Upgraded => Upgrade.Upgraded;

        /// <summary>The password verifiers an earlier build kept in the latest revision (read back converted).</summary>
        public IReadOnlyList<LegacyCredential> Legacy => Upgrade.Credentials;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Not republishing: {Reason} ({Detail}). Nothing was published; the next commit or start compiles with the current certificates.")]
    private partial void LogRepublishSkipped(string reason, string detail);
    [LoggerMessage(Level = LogLevel.Error, Message = "Republishing after the startup certificate recovery failed.")]
    private partial void LogRecoveryRepublishFailed(Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Republishing after a certificate's validity changed failed.")]
    private partial void LogValidityRepublishFailed(Exception exception);
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Count} certificate warning(s) in the config that is going live: {Warnings}")]
    private partial void LogCertificateWarnings(int count, string warnings);
    [LoggerMessage(Level = LogLevel.Error, Message = "The certificate inventory can't be read; compiling with the built-in certificates only. Hosts keep their config but lose certificates the vault would have served.")]
    private partial void LogInventoryUnreadable(Exception exception);
    [LoggerMessage(Level = LogLevel.Error, Message = "Restoring the last-known-good config into the empty database failed; the startup continues on the last-known-good file.")]
    private partial void LogRestoreFailed(Exception exception);
    [LoggerMessage(Level = LogLevel.Information, Message = "Migrated the config database ({Count} migration(s): {Migrations}).")]
    private partial void LogMigrated(int count, IReadOnlyList<string> migrations);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The config database files {Files} could be read by other users; their mode is now 0600 (they hold password hashes and the audit log).")]
    private partial void LogFilesRestricted(IReadOnlyList<string> files);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tighten {File} from mode {Mode}: {Reason}. Others can read the config database (password hashes, the audit log) until its owner changes the file or the data directory's mode keeps them out.")]
    private partial void LogFileNotRestricted(string file, string mode, string reason);
    [LoggerMessage(Level = LogLevel.Error, Message = "The config database can't be used.")]
    private partial void LogDatabaseFailed(Exception exception);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good file holds revision #{LastGoodSeq} ({LastGood}), newer than the database's latest (#{LatestSeq}): was the database replaced or restored? Serving the database's config, which is the source of truth; the file is replaced at the next publish.")]
    private partial void LogLastKnownGoodAhead(long lastGoodSeq, ConfigRevisionId lastGood, long latestSeq);
    [LoggerMessage(Level = LogLevel.Warning, Message = "The last-known-good file is damaged and can't be a fallback: {Problem}. It is replaced at the next publish.")]
    private partial void LogLastKnownGoodDamaged(string problem);
    [LoggerMessage(Level = LogLevel.Warning, Message = "UPGRADED: revision #{Seq} ({Revision}) was written from revision #{From}, with its {Lists} inline IP allow list(s) converted to access lists.")]
    private partial void LogUpgraded(long seq, ConfigRevisionId revision, long from, int lists);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Revision #{Seq} ({Revision}) has inline IP allow lists, which this build reads as access lists, but the converted config wasn't stored: {Problem}. It is running as converted; the next change stores it.")]
    private partial void LogUpgradeRefused(long seq, ConfigRevisionId revision, string problem);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Storing the access lists converted from revision #{Seq} ({Revision}) failed; it is running as converted, and the next change stores it.")]
    private partial void LogUpgradeFailed(Exception exception, long seq, ConfigRevisionId revision);
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
}
