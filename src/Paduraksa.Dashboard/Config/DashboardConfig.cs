using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Revisions;
using Paduraksa.Application.Simulation;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Dashboard.Config;

/// <summary>The Application services behind <see cref="IDashboardConfig"/>, one per circuit.</summary>
internal sealed partial class DashboardConfig(
    ConfigService config,
    DraftService drafts,
    DashboardCaller caller,
    AuthenticationStateProvider auth,
    RevisionQueryService revisions,
    ConfigImportService import,
    CertificateService certificates,
    RequestSimulator simulator,
    UserService users,
    CloudflareRangeReport cloudflareRanges,
    ILogger<DashboardConfig> logger) : IDashboardConfig
{
    public ConfigStoreStatus Status => config.Status;

    public IReadOnlyList<ConfigWarning> LiveWarnings => config.Warnings;

    public CloudflareRangeStatus CloudflareRanges => cloudflareRanges.Current;

    public Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default) => config.GetCurrentAsync(cancellationToken);

    public async Task<CurrentConfig> GetWorkingAsync(CancellationToken cancellationToken = default)
    {
        // The signed-in user as the circuit last saw them, not a session re-check: a page that refreshes in the background mustn't count as use.
        if (await OwnerOriginAsync().ConfigureAwait(false) is { } origin
            && await drafts.GetConfigAsync(origin, null, cancellationToken).ConfigureAwait(false) is { } working)
        {
            return new CurrentConfig(working.Document, working.Head);
        }

        return await config.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<DraftResult> GetDraftAsync(CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.GetAsync(origin, cancellationToken).ConfigureAwait(false)
            : new DraftResult(DraftStatus.Denied);

    public async Task<DraftResult> AddToDraftAsync(IReadOnlyList<ConfigCommand> commands, CancellationToken cancellationToken = default)
    {
        if (await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is not { } origin)
        {
            return new DraftResult(DraftStatus.Denied);
        }

        var created = false;
        var open = await drafts.GetAsync(origin, cancellationToken).ConfigureAwait(false);
        if (open.Status == DraftStatus.NoDraft)
        {
            open = await drafts.CreateAsync(origin, cancellationToken).ConfigureAwait(false);
            created = open.Status == DraftStatus.Succeeded;
        }

        if (open.Status != DraftStatus.Succeeded)
        {
            return open;
        }

        var added = await drafts.AddAsync(origin, open.Draft!.Id, commands, cancellationToken).ConfigureAwait(false);
        if (added.Status != DraftStatus.Succeeded && created)
        {
            await drafts.DiscardAsync(origin, open.Draft.Id, CancellationToken.None).ConfigureAwait(false);
        }

        return added;
    }

    public async Task<DraftResult> RemoveDraftEntryAsync(DraftId draft, int number, CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.RemoveEntryAsync(origin, draft, number, cancellationToken).ConfigureAwait(false)
            : new DraftResult(DraftStatus.Denied);

    public async Task<DraftResult> ReviewDraftAsync(DraftId draft, CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.ReviewAsync(origin, draft, cancellationToken: cancellationToken).ConfigureAwait(false)
            : new DraftResult(DraftStatus.Denied);

    public async Task<DraftResult> ApplyDraftAsync(DraftId draft, string? reviewToken, string? message, CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.ApplyAsync(origin, draft, reviewToken, message, cancellationToken: cancellationToken).ConfigureAwait(false)
            : new DraftResult(DraftStatus.Denied);

    public async Task<DraftResult> DiscardDraftAsync(DraftId draft, CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.DiscardAsync(origin, draft, cancellationToken).ConfigureAwait(false)
            : new DraftResult(DraftStatus.Denied);

    public async Task<IReadOnlyList<DraftNotice>> TakeDraftNoticesAsync(CancellationToken cancellationToken = default) =>
        await caller.OriginAsync(cancellationToken).ConfigureAwait(false) is { } origin
            ? await drafts.TakeNoticesAsync(origin, cancellationToken).ConfigureAwait(false)
            : [];

    private async Task<CommandOrigin?> OwnerOriginAsync() =>
        (await auth.GetAuthenticationStateAsync().ConfigureAwait(false)).User.AsDashboardUser()?.Session.OriginFor(Channel.Dashboard);

    public Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        config.ExecuteAsync(command, origin, cancellationToken);

    public async Task<ConfigExport> ExportAsync(CancellationToken cancellationToken = default) =>
        (await import.ExportAsync(cancellationToken: cancellationToken).ConfigureAwait(false))!;

    public Task<ConfigImportPreparation> PrepareImportAsync(ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        import.PrepareAsync(yaml, mode, origin, cancellationToken: cancellationToken);

    public Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default) =>
        revisions.ListWithChangesAsync(limit, after, cancellationToken);

    public Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default) =>
        revisions.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default) =>
        certificates.ListAsync(cancellationToken: cancellationToken);

    public async Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        var result = await users.ListAsync(origin, cancellationToken).ConfigureAwait(false);
        return result.Status == IdentityStatus.Succeeded ? result.Users : [];
    }

    public Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        simulator.SimulateAsync(target, request, origin, cancellationToken);

    public IDisposable Watch(Func<ConfigCommitted, Task> onCommitted)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        var subscription = config.Changes.Subscribe();
        _ = PumpAsync(subscription, onCommitted);
        return new Watching(subscription, drafts, this, onCommitted);
    }

    /// <summary>A commit's subscription and the caller's draft changes, as one handle.</summary>
    private sealed class Watching : IDisposable
    {
        private readonly ConfigChangeSubscription _commits;
        private readonly DraftService _drafts;
        private readonly Action<UserId> _onDraft;

        public Watching(ConfigChangeSubscription commits, DraftService drafts, DashboardConfig owner, Func<ConfigCommitted, Task> onCommitted)
        {
            _commits = commits;
            _drafts = drafts;
            _onDraft = changedFor => _ = owner.OnDraftChangedAsync(changedFor, onCommitted);
            drafts.Changed += _onDraft;
        }

        public void Dispose()
        {
            _drafts.Changed -= _onDraft;
            _commits.Dispose();
        }
    }

    private async Task OnDraftChangedAsync(UserId owner, Func<ConfigCommitted, Task> onCommitted)
    {
        try
        {
            var mine = (await auth.GetAuthenticationStateAsync().ConfigureAwait(false)).User.AsDashboardUser();
            if (mine is not null && mine.UserId == owner)
            {
                var head = (await config.GetCurrentAsync().ConfigureAwait(false)).Revision;
                await onCommitted(new ConfigCommitted(head ?? new RevisionRef(ConfigRevisionId.Empty, 0), [])).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogRefreshFailed(logger, ex, 0);
        }
    }

    /// <summary>Hands each commit to the callback until the subscription is disposed (which completes its reader).</summary>
    private async Task PumpAsync(ConfigChangeSubscription subscription, Func<ConfigCommitted, Task> onCommitted)
    {
        await foreach (var committed in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await onCommitted(committed).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A page that failed to refresh keeps what it shows; the next commit or a reload tries again.
                LogRefreshFailed(logger, ex, committed.Revision.Seq);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A dashboard page couldn't follow revision #{Seq}.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, long seq);
}
