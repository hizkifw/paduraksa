using System.Net;
using Microsoft.EntityFrameworkCore;
using Paduraksa.Core.Model;
using Paduraksa.Data;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// The two admin sign-in anomalies worth a notification, kept minimal (PLAN §9 "As built (M9): health state and notifications"):
/// <list type="bullet">
/// <item><b>An account is locked</b> by failed sign-ins (the lockout the identity core already decides).</item>
/// <item><b>An Owner or Admin signs in from a new address:</b> an address with no completed sign-in (a password-only sign-in, or a second factor
/// then completed) as that user in the last 30 days, for a user who has signed in before. A user's very first sign-in is not an anomaly, nobody else is
/// watched, and the address is compared as the audit stores it (IPv4-mapped IPv6 as IPv4).</item>
/// </list>
/// The answer comes from the audit log, so nothing is kept in memory and a restart forgets nothing.
/// </summary>
internal sealed class SignInWatch(INotificationEvents events, NotificationOptions options, TimeProvider time)
{
    /// <summary>The address as the audit log writes it.</summary>
    public static string AddressText(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    /// <summary>Whether this sign-in is one to report; read before the sign-in's own audit entry is written, so it does not count itself.</summary>
    public async Task<bool> IsNewAddressAsync(ConfigDbContext context, UserId user, IPAddress? address, CancellationToken cancellationToken)
    {
        if (address is null)
        {
            return false;
        }

        var privileged = await context.RoleAssignments.AsNoTracking()
            .AnyAsync(r => r.UserId == user && (r.Role == Role.Owner || r.Role == Role.Admin), cancellationToken).ConfigureAwait(false);
        if (!privileged)
        {
            return false;
        }

        var text = AddressText(address);
        var now = time.GetUtcNow();

        // Both reads are seeks on (ActorUserId, Action, Timestamp), bounded by time: they touch this user's sign-in rows in the window, never the table.
        // The LIKE only separates a completed password-only sign-in from a pending one, and runs over those rows.
        IQueryable<Data.Audit.AuditEvent> Completed(DateTimeOffset since) => context.AuditEvents.AsNoTracking()
            .Where(e => e.ActorUserId == user && e.Outcome == AuditOutcome.Success && e.Timestamp >= since
                && ((e.Action == "auth.login" && e.Details!.Contains("\"secondFactor\":\"none\"")) || e.Action == "auth.second_factor"));
        return await Completed(now - options.SignedInBeforeWindow).AnyAsync(cancellationToken).ConfigureAwait(false)
            && !await Completed(now - options.KnownAddressWindow).AnyAsync(e => e.ClientIp == text, cancellationToken).ConfigureAwait(false);
    }

    public void PublishNewAddress(string username, UserId user, IPAddress address) =>
        events.Publish(NotificationText.SignInNewAddress(username, user, AddressText(address), time.GetUtcNow()));

    public void PublishLockout(string username, UserId user, DateTimeOffset until) =>
        events.Publish(NotificationText.SignInLockout(username, user, until, time.GetUtcNow()));
}
