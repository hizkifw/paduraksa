using System.Security.Claims;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Commands;
using Paduraksa.Application.Identity;
using Paduraksa.Contracts.Certificates;
using Paduraksa.Contracts.Commands;
using Paduraksa.Contracts.Config;
using Paduraksa.Contracts.Identity;
using Paduraksa.Contracts.Revisions;
using Paduraksa.Contracts.Simulation;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Auth;
using Paduraksa.Dashboard.Config;

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

    public int WatcherCount => _watchers.Count;

    public ConfigStoreStatus Status { get; set; } = new(ConfigSource.Database, null);

    public IReadOnlyList<ConfigWarning> LiveWarnings { get; set; } = [];

    public CloudflareRangeStatus CloudflareRanges { get; set; } = new(CloudflareRangeSource.BuiltIn, new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero), 22, 0, null);

    public Task<CurrentConfig> GetCurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(new CurrentConfig(Document, Revision));

    public Task<ConfigCommandResult> ExecuteAsync(ConfigCommand command, CommandOrigin origin, CancellationToken cancellationToken = default)
    {
        Executed.Add(command);
        return Task.FromResult(Results(command));
    }

    public Task<RevisionTimelinePage?> ListRevisionsAsync(int limit, ConfigRevisionId? after, CancellationToken cancellationToken = default) =>
        Task.FromResult<RevisionTimelinePage?>(new RevisionTimelinePage(
            [.. Revisions.Values.OrderByDescending(r => r.Revision.Seq).Select(r => new RevisionTimelineEntry(r.Revision, r.Changes))]));

    public Task<RevisionDetail?> GetRevisionAsync(ConfigRevisionId id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Revisions.GetValueOrDefault(id));

    public Task<IReadOnlyList<CertificateSummary>> ListCertificatesAsync(CancellationToken cancellationToken = default) => Task.FromResult(Certificates);

    public Task<IReadOnlyList<UserSummary>> ListUsersAsync(CommandOrigin origin, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<UserSummary>>([]);

    public Task<SimulationResult> SimulateAsync(SimulationTarget target, SimulationRequest request, CommandOrigin origin, CancellationToken cancellationToken = default) =>
        Task.FromResult(Simulation ?? new SimulationResult(SimulationStatus.Unavailable, new SimulationSource(SimulationTargetKind.Live)));

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
        context.Services.AddSingleton(time ?? TimeProvider.System);
        context.Services.AddSingleton(sessions);
        // Factories, so the context disposes the provider (and its background re-check) with it.
        context.Services.AddSingleton(_ => auth);
        context.Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<DashboardAuthenticationStateProvider>());
        context.Services.AddSingleton<IDashboardConfig>(config);
        context.Services.AddSingleton(new Paduraksa.Application.Notifications.UpstreamHealthBoard());
        context.Services.AddScoped<DashboardCaller>();
        context.Services.AddScoped<ConfigUndo>();
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
