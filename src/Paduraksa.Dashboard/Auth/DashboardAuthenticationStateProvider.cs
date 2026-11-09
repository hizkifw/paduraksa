using System.Security.Claims;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Identity;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Auth;

/// <summary>
/// The dashboard's authentication state, one per page render and one per circuit. It starts from the request that rendered
/// the page or opened the circuit, whose user the admin app's authentication set from the session cookie (a <see cref="SessionIdentity"/>),
/// adds the user's permissions, and then keeps checking that the session is still live: in the background every
/// <see cref="DashboardOptions.SessionRecheckInterval"/>, and whenever a component or the circuit asks (<see cref="RecheckAsync"/>). When
/// the session is revoked or expires, its user is disabled or deleted, or a password change elsewhere ends it, the state becomes anonymous:
/// the router then sends the browser to the sign-in page and every service call is refused.
/// </summary>
/// <remarks>
/// The circuit never holds the cookie: it re-checks the session by its ID. A changed role or display name is picked up by the same check.
/// A check that fails for another reason (the database briefly unreachable) leaves the state as it was and is logged; the services refuse
/// on their own if they cannot work.
/// </remarks>
public sealed partial class DashboardAuthenticationStateProvider : AuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider, IDisposable
{
    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    private readonly IDashboardSessions _sessions;
    private readonly DashboardOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private Task<AuthenticationState> _state = Task.FromResult(Anonymous);
    private DashboardPrincipal? _user;
    private DateTimeOffset _lastCheck;
    private DateTimeOffset? _endedAt;
    private int _loopStarted;
    private int _disposed;

    public DashboardAuthenticationStateProvider(IDashboardSessions sessions, DashboardOptions options, TimeProvider time, ILogger<DashboardAuthenticationStateProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _sessions = sessions;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The signed-in user as last checked; null when anonymous or ended.</summary>
    public DashboardPrincipal? User => _user;

    /// <summary>When the session ended under this circuit (it was signed in, and no longer is); null while it is live or was never signed in.</summary>
    public DateTimeOffset? EndedAt => _endedAt;

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => _state;

    /// <summary>Called by the framework with the user of the request that rendered the page or opened the circuit.</summary>
    public void SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        ArgumentNullException.ThrowIfNull(authenticationStateTask);
        _state = AdoptAsync(authenticationStateTask);
        NotifyAuthenticationStateChanged(_state);
    }

    /// <summary>
    /// Re-checks the session now. <paramref name="countAsUse"/> is true for something the person did (a service call): it keeps an idle
    /// session alive, as a request with the cookie would. Returns the user, or null when anonymous or the session has ended (the state is
    /// then anonymous and the change is announced).
    /// </summary>
    /// <remarks>
    /// A check that fails for another reason than cancellation (the store busy, a brief I/O error) is logged and answers the user as last
    /// checked, unchanged: every action goes through here, and an exception would end the circuit.
    /// </remarks>
    public async Task<DashboardPrincipal?> RecheckAsync(bool countAsUse, CancellationToken cancellationToken = default)
    {
        try
        {
            return await CheckNowAsync(countAsUse, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ObjectDisposedException)
        {
            LogRecheckFailed(_logger, ex);
            return _user;
        }
    }

    /// <summary>Whether it is longer than <paramref name="interval"/> since the last check (a cheap gate for checks on every interaction).</summary>
    public bool IsRecheckDue(TimeSpan interval) => _user is not null && _time.GetUtcNow() - _lastCheck >= interval;

    /// <summary>Starts the background re-check (once, for a circuit; a page render that ends with its request needs none).</summary>
    public void StartBackgroundRecheck()
    {
        if (Interlocked.Exchange(ref _loopStarted, 1) == 0)
        {
            _ = RecheckPeriodicallyAsync(_stopping.Token);
        }
    }

    /// <summary>Stops the background re-check. Idempotent: the container holds this instance under two service types.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _stopping.Cancel();
        _stopping.Dispose();
        _checking.Dispose();
    }

    private async Task<DashboardPrincipal?> CheckNowAsync(bool countAsUse, CancellationToken cancellationToken)
    {
        await _state.ConfigureAwait(false);
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_user is not { } user)
            {
                return null;
            }

            SessionPrincipal? session = await _sessions.CheckAsync(user.SessionId, countAsUse, cancellationToken).ConfigureAwait(false);
            _lastCheck = _time.GetUtcNow();
            if (session is null)
            {
                End();
                return null;
            }

            IReadOnlySet<Permission> permissions = await _sessions.GetPermissionsAsync(session.UserId, cancellationToken).ConfigureAwait(false);
            if (Unchanged(user, session, permissions))
            {
                return user;
            }

            DashboardPrincipal updated = new(user.Session.With(session), permissions);
            _user = updated;
            _state = Task.FromResult(new AuthenticationState(updated));
            NotifyAuthenticationStateChanged(_state);
            return updated;
        }
        finally
        {
            _checking.Release();
        }
    }

    private async Task<AuthenticationState> AdoptAsync(Task<AuthenticationState> authenticationStateTask)
    {
        AuthenticationState state = await authenticationStateTask.ConfigureAwait(false);
        if (SessionIdentity.Of(state.User) is not { } identity)
        {
            _user = null;
            return Anonymous;
        }

        IReadOnlySet<Permission> permissions = await _sessions.GetPermissionsAsync(identity.Session.UserId, _stopping.Token).ConfigureAwait(false);
        _user = new DashboardPrincipal(identity, permissions);
        _lastCheck = _time.GetUtcNow();
        return new AuthenticationState(_user);
    }

    private async Task RecheckPeriodicallyAsync(CancellationToken stopping)
    {
        try
        {
            using PeriodicTimer timer = new(_options.SessionRecheckInterval, _time);
            while (await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false))
            {
                // A failed check is logged by RecheckAsync and keeps the last state; the next tick tries again.
                if (_user is not null && await RecheckAsync(countAsUse: false, stopping).ConfigureAwait(false) is null)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // The circuit is gone.
        }
    }

    private void End()
    {
        _user = null;
        _endedAt = _time.GetUtcNow();
        _state = Task.FromResult(Anonymous);
        LogEnded(_logger);
        NotifyAuthenticationStateChanged(_state);
    }

    private static bool Unchanged(DashboardPrincipal user, SessionPrincipal session, IReadOnlySet<Permission> permissions)
    {
        SessionPrincipal current = user.Session.Session;
        return current.UserId == session.UserId
            && string.Equals(current.Username, session.Username, StringComparison.Ordinal)
            && string.Equals(current.DisplayName, session.DisplayName, StringComparison.Ordinal)
            && current.Roles.SequenceEqual(session.Roles)
            && user.Permissions.SetEquals(permissions);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "A dashboard circuit's session ended (revoked, expired, or its user disabled); the page returns to the sign-in page.")]
    private static partial void LogEnded(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't re-check a dashboard circuit's session; it keeps its last state until the next check.")]
    private static partial void LogRecheckFailed(ILogger logger, Exception exception);
}
