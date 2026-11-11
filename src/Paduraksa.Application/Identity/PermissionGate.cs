using Paduraksa.Application.Audit;
using Paduraksa.Application.Commands;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Identity;

/// <summary>
/// The up-front permission check of the services that change things outside the config command pipeline (certificates, ACME
/// accounts and orders, DNS providers): they ask before anything happens, so a refused origin starts no CA order, creates no key and
/// leaves no pending row. A refusal is audited as <see cref="AuditOutcome.Denied"/>.
/// </summary>
internal static class PermissionGate
{
    /// <summary>
    /// True if <paramref name="origin"/> holds <paramref name="permission"/>. Always true while the config store is unavailable
    /// (a degraded start): the permissions live in that store, nothing is started in that state anyway, and the operation's own
    /// <c>Unavailable</c> answer is the honest one.
    /// </summary>
    public static async ValueTask<bool> MayAsync(
        IPermissionEvaluator permissions,
        ConfigService config,
        CommandOrigin origin,
        Permission permission,
        string action,
        string? target,
        CancellationToken cancellationToken)
    {
        if (config.Status is { AcceptsCommands: false })
        {
            return true;
        }

        if (await permissions.IsAllowedAsync(origin, permission, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        await config.TryAppendAuditAsync(
            new AuditEntry(origin.Actor, origin.Channel, action, AuditOutcome.Denied) { Target = target, ClientIp = origin.ClientIp }).ConfigureAwait(false);
        return false;
    }
}
