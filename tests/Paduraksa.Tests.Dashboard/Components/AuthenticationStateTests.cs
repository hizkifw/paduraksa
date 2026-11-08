using System.Collections.Concurrent;
using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.Logging.Abstractions;

using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;
using Paduraksa.Dashboard;
using Paduraksa.Dashboard.Auth;

namespace Paduraksa.Tests.Dashboard.Components;

/// <summary>
/// The dashboard's authentication state (PLAN §12): it starts from the request's session, carries the user's permissions, and follows the
/// session: a revoked one makes the state anonymous (and is announced), changed roles change the permissions, and a circuit whose session
/// ended is torn down after its grace period.
/// </summary>
public sealed class AuthenticationStateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheRequestsSession_BecomesTheUser_WithItsPermissions()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Operator);
        using DashboardAuthenticationStateProvider auth = Provider(sessions);

        auth.SetAuthenticationState(StateOf(session));
        AuthenticationState state = await auth.GetAuthenticationStateAsync();

        DashboardPrincipal user = Assert.IsType<DashboardPrincipal>(state.User);
        Assert.True(user.Identity!.IsAuthenticated);
        Assert.Same(session, user.Session);
        Assert.True(user.Has(Permission.ConfigWrite));
        Assert.False(user.Has(Permission.UsersWrite));
        Assert.True(state.User.IsInRole("operator"));
    }

    [Fact]
    public async Task AnAnonymousRequest_StaysAnonymous()
    {
        using DashboardAuthenticationStateProvider auth = Provider(new FakeSessions());

        auth.SetAuthenticationState(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));

        Assert.False((await auth.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.Null(await auth.RecheckAsync(countAsUse: true, Ct));
        Assert.Null(auth.EndedAt);
    }

    [Fact]
    public async Task ARevokedSession_MakesTheStateAnonymous_AndSaysSo()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Owner);
        using DashboardAuthenticationStateProvider auth = Provider(sessions);
        auth.SetAuthenticationState(StateOf(session));
        await auth.GetAuthenticationStateAsync();
        List<Task<AuthenticationState>> announced = [];
        auth.AuthenticationStateChanged += announced.Add;

        sessions.Revoke(session);
        DashboardPrincipal? user = await auth.RecheckAsync(countAsUse: true, Ct);

        Assert.Null(user);
        Assert.NotNull(auth.EndedAt);
        Assert.False((await Assert.Single(announced)).User.Identity?.IsAuthenticated);
        Assert.False((await auth.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task ChangedRoles_ChangeThePermissions_AnUnchangedSessionChangesNothing()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Viewer);
        using DashboardAuthenticationStateProvider auth = Provider(sessions);
        auth.SetAuthenticationState(StateOf(session));
        await auth.GetAuthenticationStateAsync();
        int announced = 0;
        auth.AuthenticationStateChanged += _ => announced++;

        Assert.False((await auth.RecheckAsync(countAsUse: false, Ct))!.Has(Permission.ConfigWrite));
        Assert.Equal(0, announced);

        sessions.SetRoles(session, Role.Admin);
        DashboardPrincipal promoted = (await auth.RecheckAsync(countAsUse: false, Ct))!;

        Assert.True(promoted.Has(Permission.ConfigWrite));
        Assert.Equal(1, announced);
        Assert.Same(promoted, (await auth.GetAuthenticationStateAsync()).User);
    }

    [Fact]
    public async Task TheBackgroundCheck_EndsARevokedSession_WithoutCountingAsUse()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Owner);
        using DashboardAuthenticationStateProvider auth = Provider(sessions, recheck: TimeSpan.FromMilliseconds(50));
        auth.SetAuthenticationState(StateOf(session));
        await auth.GetAuthenticationStateAsync();
        TaskCompletionSource<AuthenticationState> ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // A check that runs while the revocation lands can read the session and then no permissions (the fake answers per user): that
        // announces a signed-in state first. The ending is the anonymous one.
        auth.AuthenticationStateChanged += async state =>
        {
            AuthenticationState announced = await state;
            if (announced.User.Identity?.IsAuthenticated != true)
            {
                ended.TrySetResult(announced);
            }
        };

        auth.StartBackgroundRecheck();
        await WaitUntilAsync(() => !sessions.Checks.IsEmpty);
        sessions.Revoke(session);
        AuthenticationState state = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.False(state.User.Identity?.IsAuthenticated);
        Assert.All(sessions.Checks, check => Assert.False(check.CountAsUse));
    }

    [Fact]
    public async Task AServiceCall_ReChecksTheSession_AndCountsAsUse()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Owner);
        using DashboardAuthenticationStateProvider auth = Provider(sessions);
        auth.SetAuthenticationState(StateOf(session));
        DashboardCaller caller = new(auth);

        Paduraksa.Application.Commands.CommandOrigin? origin = await caller.OriginAsync(Ct);

        Assert.NotNull(origin);
        Assert.Equal(Channel.Dashboard, origin.Channel);
        Assert.Equal(session.Session.UserId, origin.Actor.UserId);
        Assert.True(Assert.Single(sessions.Checks).CountAsUse);

        sessions.Revoke(session);
        Assert.Null(await caller.OriginAsync(Ct));
        Assert.False(await caller.MayAsync(Permission.ConfigRead, Ct));
    }

    [Fact]
    public async Task ACircuitWhoseSessionEnded_GetsItsGracePeriod_ThenIsTornDown()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Owner);
        ManualTime time = new();
        DashboardOptions options = new() { ActivityRecheckInterval = TimeSpan.FromSeconds(10), EndedCircuitGrace = TimeSpan.FromSeconds(10) };
        using DashboardAuthenticationStateProvider auth = Provider(sessions, time: time, options: options);
        auth.SetAuthenticationState(StateOf(session));
        await auth.GetAuthenticationStateAsync();
        int handled = 0;
        Func<CircuitInboundActivityContext, Task> handler =
            new SessionCircuitHandler(auth, options, time).CreateInboundActivityHandler(_ => { handled++; return Task.CompletedTask; });

        // Live, and checked recently: activity goes through without a check.
        await handler(null!);
        Assert.Equal(1, handled);
        Assert.Empty(sessions.Checks);

        // Revoked, and the check is due: the activity that finds out is dropped, the state ends.
        sessions.Revoke(session);
        time.Advance(TimeSpan.FromSeconds(11));
        await handler(null!);
        Assert.Equal(1, handled);
        Assert.NotNull(auth.EndedAt);
        Assert.False(Assert.Single(sessions.Checks).CountAsUse);

        // Within the grace period the browser may still talk (to follow the redirect); after it, the circuit is ended.
        time.Advance(TimeSpan.FromSeconds(5));
        await handler(null!);
        Assert.Equal(2, handled);
        time.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<SessionEndedException>(() => handler(null!));
    }

    [Fact]
    public async Task ACheckThatFails_KeepsTheLastState_AndTheCircuit()
    {
        FakeSessions sessions = new();
        SessionIdentity session = sessions.Add(Role.Operator);
        ManualTime time = new();
        DashboardOptions options = new() { ActivityRecheckInterval = TimeSpan.FromSeconds(10), EndedCircuitGrace = TimeSpan.FromSeconds(10) };
        using DashboardAuthenticationStateProvider auth = Provider(sessions, time: time, options: options);
        auth.SetAuthenticationState(StateOf(session));
        DashboardPrincipal before = Assert.IsType<DashboardPrincipal>((await auth.GetAuthenticationStateAsync()).User);
        int announced = 0;
        auth.AuthenticationStateChanged += _ => announced++;
        sessions.Failing = true;

        // A service call's check: the user as last checked, not an exception that would end the circuit.
        DashboardPrincipal? user = await new DashboardCaller(auth).CheckAsync(Ct);

        Assert.Same(before, user);
        Assert.Null(auth.EndedAt);
        Assert.Equal(0, announced);
        Assert.Same(before, (await auth.GetAuthenticationStateAsync()).User);

        // The circuit's activity check: the activity goes through on the last state.
        int handled = 0;
        Func<CircuitInboundActivityContext, Task> handler =
            new SessionCircuitHandler(auth, options, time).CreateInboundActivityHandler(_ => { handled++; return Task.CompletedTask; });
        time.Advance(TimeSpan.FromSeconds(11));
        await handler(null!);
        Assert.Equal(1, handled);
        Assert.Null(auth.EndedAt);

        // Once the store answers again, the next check sees what is true.
        sessions.Failing = false;
        sessions.Revoke(session);
        Assert.Null(await auth.RecheckAsync(countAsUse: true, Ct));
        Assert.NotNull(auth.EndedAt);
    }

    private static DashboardAuthenticationStateProvider Provider(
        FakeSessions sessions, TimeSpan? recheck = null, TimeProvider? time = null, DashboardOptions? options = null) =>
        new(
            sessions,
            options ?? new DashboardOptions { SessionRecheckInterval = recheck ?? TimeSpan.FromMinutes(1) },
            time ?? TimeProvider.System,
            NullLogger<DashboardAuthenticationStateProvider>.Instance);

    private static Task<AuthenticationState> StateOf(SessionIdentity session) =>
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(session)));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    /// <summary>Sessions in memory: each one live until revoked, with roles that can change.</summary>
    private sealed class FakeSessions : IDashboardSessions
    {
        private readonly ConcurrentDictionary<SessionId, SessionPrincipal?> _sessions = new();

        public ConcurrentQueue<(SessionId Session, bool CountAsUse)> Checks { get; } = new();

        /// <summary>Every check throws, as a busy or briefly unreachable database does.</summary>
        public bool Failing { get; set; }

        public SessionIdentity Add(params Role[] roles)
        {
            SessionIdentity identity = TestUsers.Session(roles);
            _sessions[identity.Session.SessionId] = identity.Session;
            return identity;
        }

        public void Revoke(SessionIdentity identity) => _sessions[identity.Session.SessionId] = null;

        public void SetRoles(SessionIdentity identity, params Role[] roles) =>
            _sessions[identity.Session.SessionId] = identity.Session with { Roles = roles };

        public Task<SessionPrincipal?> CheckAsync(SessionId session, bool countAsUse, CancellationToken cancellationToken)
        {
            Checks.Enqueue((session, countAsUse));
            if (Failing)
            {
                throw new InvalidOperationException("database is locked");
            }

            return Task.FromResult(_sessions.GetValueOrDefault(session));
        }

        public ValueTask<IReadOnlySet<Permission>> GetPermissionsAsync(UserId user, CancellationToken cancellationToken)
        {
            SessionPrincipal? principal = _sessions.Values.FirstOrDefault(s => s?.UserId == user);
            return ValueTask.FromResult(principal is null ? RolePermissions.Of([]) : RolePermissions.Of(principal.Roles));
        }
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
