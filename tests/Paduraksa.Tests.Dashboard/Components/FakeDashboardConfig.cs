using System.Security.Claims;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Drafts;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Yaml;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Drafts;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Config;
using Paduraksa.Dashboard.Identity;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>The config pages' Application seam in memory: a document, scripted command results, revisions, a simulation answer.</summary>
internal sealed class FakeDashboardConfig : IDashboardConfig
{
    private readonly List<Func<ConfigCommitted, Task>> _watchers = [];

    public ConfigDocument Document { get; set; } = new();

    public RevisionRef? Revision { get; set; }

    /// <summary>What a command gets; by default a dry run passes with no changes and a real run commits as #1.</summary>
    public Func<ConfigCommand, ConfigCommandResult> Results { get; set; } = command => command.DryRun
        ? new ConfigCommandResult(ConfigCommandStatus.DryRun)
        : new ConfigCommandResult(ConfigCommandStatus.Committed, new RevisionRef(ConfigRevisionId.New(), 1));

    public List<ConfigCommand> Executed { get; } = [];

    public Dictionary<ConfigRevisionId, RevisionDetail> Revisions { get; } = [];

    public IReadOnlyList<CertificateSummary> Certificates { get; set; } = [];

    public SimulationResult? Simulation { get; set; }

    public List<SimulationTarget> Simulated { get; } = [];

    public int WatcherCount => _watchers.Count;

    public ConfigStoreStatus Status { get; set; } = new(ConfigSource.Database, null);

    public IReadOnlyList<ConfigWarning> LiveWarnings { get; set; } = [];

    public CloudflareRangeStatus CloudflareRanges { get; set; } = new(CloudflareRangeSource.BuiltIn, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 22, 0, null);

    public Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CurrentConfig(Document, Revision));

    /// <summary>The open draft, as the service would hold it; null: none. Entries are replayed by hand: <see cref="WorkingDocument"/> is the head with them applied.</summary>
    public ConfigDraft? Draft { get; set; }

    /// <summary>What the pages read while a draft is open (the head with the draft applied); <see cref="Document"/> when null.</summary>
    public ConfigDocument? WorkingDocument { get; set; }

    /// <summary>Every call that added commands to the draft.</summary>
    public List<IReadOnlyList<ConfigCommand>> DraftAdds { get; } = [];

    /// <summary>What an add gets; by default the fake draft takes the commands (opening itself first), one entry per object.</summary>
    public Func<IReadOnlyList<ConfigCommand>, DraftResult>? AddResult { get; set; }

    /// <summary>What a review of the draft gets; by default a dry run that passes with no changes (and the token <see cref="TokenOf"/>), or <see cref="DraftStatus.Blocked"/> when every entry is blocked.</summary>
    public Func<ConfigDraft, DraftResult>? ReviewResult { get; set; }

    /// <summary>What an apply gets, given the draft and the token sent; by default the whole draft commits as #1 when the token is the review's, a changed-since-review answer
    /// (the fresh review) when it is not, and a refusal when an entry is blocked.</summary>
    public Func<ConfigDraft, string?, DraftResult>? ApplyResult { get; set; }

    public List<(DraftId Draft, string? Token, string? Message)> DraftApplies { get; } = [];

    /// <summary>The review token the fake gives a draft: its version, until a test moves it.</summary>
    public static string TokenOf(ConfigDraft draft) => $"t{draft.Version}";

    public int DraftDiscards { get; private set; }

    public List<DraftNotice> Notices { get; } = [];

    public Task<CurrentConfig> GetWorkingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new CurrentConfig(Draft is null ? Document : WorkingDocument ?? Document, Revision));

    public Task<DraftResult> GetDraftAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Draft is null ? new DraftResult(DraftStatus.NoDraft) : new DraftResult(DraftStatus.Succeeded, Draft));

    public Task<DraftResult> AddToDraftAsync(IReadOnlyList<ConfigCommand> commands, CancellationToken cancellationToken = default)
    {
        DraftAdds.Add(commands);
        if (AddResult is { } scripted)
        {
            return Task.FromResult(scripted(commands));
        }

        var entries = Draft?.Entries.ToList() ?? [];
        foreach (var command in commands)
        {
            var (target, action) = DraftTargets.Of(command)!.Value;
            var at = entries.FindIndex(e => e.Target == target);
            DraftEntry entry = new(at >= 0 ? entries[at].Number : entries.Count + 1, action, target, command, DateTimeOffset.UnixEpoch);
            if (at >= 0)
            {
                entries[at] = entry;
            }
            else
            {
                entries.Add(entry);
            }
        }

        Draft = (Draft ?? new ConfigDraft(DraftId.New(), 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddDays(30), [])) with
        {
            Entries = entries,
            Version = (Draft?.Version ?? 0) + 1,
        };
        return Task.FromResult(new DraftResult(DraftStatus.Succeeded, Draft));
    }

    public Task<DraftResult> RemoveDraftEntryAsync(DraftId draft, int number, CancellationToken cancellationToken = default)
    {
        if (Draft is null)
        {
            return Task.FromResult(new DraftResult(DraftStatus.NoDraft));
        }

        Draft = Draft with { Entries = [.. Draft.Entries.Where(e => e.Number != number)], Version = Draft.Version + 1 };
        return Task.FromResult(new DraftResult(DraftStatus.Succeeded, Draft));
    }

    /// <summary>How many times the draft was reviewed (a dry run through the draft service).</summary>
    public int DraftReviews { get; private set; }

    public Task<DraftResult> ReviewDraftAsync(DraftId draft, CancellationToken cancellationToken = default)
    {
        DraftReviews++;
        return Task.FromResult(Draft is null ? new DraftResult(DraftStatus.NoDraft) : ReviewResult?.Invoke(Draft) ?? Review(Draft));
    }

    private static DraftResult Review(ConfigDraft draft)
    {
        if (draft.Entries.Count == 0)
        {
            return new DraftResult(DraftStatus.NothingToApply, draft);
        }

        var blocked = draft.Entries.Any(static e => e.State == DraftEntryState.ChangedElsewhere);
        return blocked && draft.Entries.All(static e => e.State == DraftEntryState.ChangedElsewhere)
            ? new DraftResult(DraftStatus.Blocked, draft)
            : new DraftResult(DraftStatus.Succeeded, draft, new ConfigCommandResult(ConfigCommandStatus.DryRun), ReviewToken: blocked ? null : TokenOf(draft));
    }

    public Task<DraftResult> ApplyDraftAsync(DraftId draft, string? reviewToken, string? message, CancellationToken cancellationToken = default)
    {
        DraftApplies.Add((draft, reviewToken, message));
        if (Draft is null)
        {
            return Task.FromResult(new DraftResult(DraftStatus.NoDraft));
        }

        if (ApplyResult is { } scripted)
        {
            return Task.FromResult(scripted(Draft, reviewToken));
        }

        if (Draft.Entries.Any(static e => e.State == DraftEntryState.ChangedElsewhere))
        {
            return Task.FromResult(new DraftResult(DraftStatus.Blocked, Draft));
        }

        if (reviewToken != TokenOf(Draft))
        {
            return Task.FromResult(Review(Draft) with { Status = DraftStatus.ChangedSinceReview });
        }

        Draft = null;
        return Task.FromResult(new DraftResult(DraftStatus.Succeeded, null, new ConfigCommandResult(ConfigCommandStatus.Committed, new RevisionRef(ConfigRevisionId.New(), 1))));
    }

    public Task<DraftResult> DiscardDraftAsync(DraftId draft, CancellationToken cancellationToken = default)
    {
        DraftDiscards++;
        Draft = null;
        return Task.FromResult(new DraftResult(DraftStatus.Succeeded));
    }

    public Task<IReadOnlyList<DraftNotice>> TakeDraftNoticesAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DraftNotice> taken = [.. Notices];
        Notices.Clear();
        return Task.FromResult(taken);
    }

    /// <summary>Announces a change to the draft (made in another tab, say) to every page that watches.</summary>
    public Task DraftChangedAsync() => CommitAsync(new ConfigCommitted(Revision ?? new RevisionRef(ConfigRevisionId.Empty, 0), []));

    public Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        Executed.Add(command);
        return Task.FromResult(Results(command));
    }

    /// <summary>What an export gives.</summary>
    public ConfigExport Export { get; set; } = new("apiVersion: paduraksa.dev/v1\nkind: Config\n", null);

    /// <summary>What preparing an import gives; by default the command replacing the document with an empty one.</summary>
    public Func<ReadOnlyMemory<byte>, ConfigImportMode, ConfigImportPreparation> Prepared { get; set; } = (_, _) =>
        new ConfigImportPreparation(new ReplaceConfigCommand(new ConfigDocument(), null));

    public List<(string Yaml, ConfigImportMode Mode)> Imports { get; } = [];

    public Task<ConfigExport> ExportAsync(CancellationToken cancellationToken = default) => Task.FromResult(Export);

    public Task<ConfigImportPreparation> PrepareImportAsync(ReadOnlyMemory<byte> yaml, ConfigImportMode mode, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        Imports.Add((System.Text.Encoding.UTF8.GetString(yaml.Span), mode));
        return Task.FromResult(Prepared(yaml, mode));
    }

    public Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default) =>
        Task.FromResult<RevisionTimelinePage?>(new RevisionTimelinePage(
            [.. Revisions.Values.OrderByDescending(r => r.Revision.Seq).Select(r => new RevisionTimelineEntry(r.Revision, r.Changes))]));

    public Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Revisions.GetValueOrDefault(id));

    public Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Certificates);

    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserSummary>>([]);

    public Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        Simulated.Add(target);
        return Task.FromResult(Simulation ?? new SimulationResult(SimulationStatus.Unavailable, new SimulationSource(SimulationTargetKind.Live)));
    }

    public IDisposable Watch(Func<ConfigCommitted, Task> onCommitted)
    {
        _watchers.Add(onCommitted);
        return new Unwatch(() => _watchers.Remove(onCommitted));
    }

    /// <summary>Announces a commit to every page that watches.</summary>
    public async Task CommitAsync(ConfigCommitted committed)
    {
        foreach (var watcher in _watchers.ToArray())
        {
            await watcher(committed);
        }
    }

    private sealed class Unwatch(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}

/// <summary>The vault's own report of where its master key came from, as a page reads it.</summary>
internal sealed class FakeVaultLifecycle(KekSourceKind source = KekSourceKind.File, bool besideDatabase = false) : IVaultLifecycle
{
    public ValueTask<VaultStatus> InitializeAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new VaultStatus(source, besideDatabase));
}

/// <summary>Sets a bUnit context up for the config pages: a signed-in user in a role, the fake config, a live (interactive) renderer.</summary>
internal static class ConfigPages
{
    public static FakeDashboardConfig SignIn(DashboardTestContext context, Role role, TimeProvider? time = null) => SignIn(context, role, out _, time);

    /// <summary>As <see cref="SignIn(DashboardTestContext, Role, TimeProvider?)"/>, and hands back the signed-in session. <paramref name="time"/> is the page's clock (the system's when null); it must be given here, since the setup ends by building the services.</summary>
    public static FakeDashboardConfig SignIn(DashboardTestContext context, Role role, out SessionIdentity session, TimeProvider? time = null)
    {
        FakeDashboardConfig config = new();
        session = TestUsers.Session(role);
        TestSessions sessions = new(session.Session);
        DashboardAuthenticationStateProvider auth = new(
            sessions, new DashboardOptions(), TimeProvider.System, NullLogger<DashboardAuthenticationStateProvider>.Instance);
        auth.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(session))));
        context.Services.AddLogging();
        context.Services.AddSingleton<TimeProvider>(new HeldDebounceTimeProvider(time ?? TimeProvider.System));
        context.Services.AddSingleton(sessions);
        // Factories, so the context disposes the provider (and its background re-check) with it.
        context.Services.AddSingleton(_ => auth);
        context.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<DashboardAuthenticationStateProvider>());
        context.Services.AddSingleton<IDashboardConfig>(config);
        context.Services.TryAddSingleton<IVaultLifecycle>(new FakeVaultLifecycle());
        context.Services.TryAddSingleton<IDashboardIdentity>(new FakeDashboardIdentity());
        context.Services.AddSingleton(new Paduraksa.Application.Notifications.UpstreamHealthBoard());
        context.Services.AddScoped<DashboardCaller>();
        context.Services.AddScoped<ConfigUndo>();
        context.Services.AddScoped<DraftState>();
        context.Services.AddScoped<ReviewedDraft>();
        context.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return config;
    }

    /// <summary>The cascading authentication state of the signed-in user.</summary>
    public static Task<AuthenticationState> State(DashboardTestContext context) =>
        context.Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();
}

/// <summary>The signed-in user's session for the page tests: live until <see cref="Ended"/> is set.</summary>
internal sealed class TestSessions(SessionPrincipal principal) : IDashboardSessions
{
    /// <summary>The session was revoked or expired: the next check ends it.</summary>
    public bool Ended { get; set; }

    public Task<SessionPrincipal?> CheckAsync(SessionId session, bool countAsUse, CancellationToken cancellationToken) =>
        Task.FromResult<SessionPrincipal?>(Ended ? null : principal);

    public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(UserId user, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RolePermissions.Of(principal.Roles));
}
