using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;

namespace Paduraksa.Dashboard.Auth;

/// <summary>
/// The way a page calls the Application services: it re-checks the session first (the call counts as use), so a page never acts on a
/// session that ended since the last background check, and it supplies the <see cref="CommandOrigin"/> (the user, the dashboard channel,
/// the client the circuit was opened from). Pages hide what the user may not do; the services still decide, and refuse, on their own.
/// </summary>
public sealed class DashboardCaller(DashboardAuthenticationStateProvider auth)
{
    /// <summary>The user, re-checked; null when the session has ended (the page is then on its way to the sign-in page).</summary>
    public Task<DashboardPrincipal?> CheckAsync(CancellationToken cancellationToken = default) =>
        auth.RecheckAsync(countAsUse: true, cancellationToken);

    /// <summary>The origin for a service call, after the re-check; null when the session has ended.</summary>
    public async Task<CommandOrigin?> OriginAsync(CancellationToken cancellationToken = default) =>
        (await CheckAsync(cancellationToken).ConfigureAwait(false))?.Session.OriginFor(Channel.Dashboard);

    /// <summary>Whether the re-checked user holds <paramref name="permission"/>.</summary>
    public async Task<bool> MayAsync(Permission permission, CancellationToken cancellationToken = default) =>
        (await CheckAsync(cancellationToken).ConfigureAwait(false))?.Has(permission) == true;
}
