using Microsoft.Extensions.Logging;

using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The one "delete the vault secret of the record that was not created" step of the issuance flows: an ACME account's
/// external-account-binding key and a DNS credential are stored before the row that names them, and a change that fails afterwards must
/// take the secret back out, or it is unreachable for good.
/// </summary>
/// <remarks>
/// The deletion runs with <see cref="CancellationToken.None"/>, like the certificate flows' rollbacks, because the caller's request may
/// be exactly what ended: a cancelled request still has to clean up. A failure is logged and swallowed, so a cleanup never hides the
/// failure that caused it; only <see cref="OutOfMemoryException"/>, and a cancellation of the caller that made the delete give up, escape.
/// </remarks>
internal static partial class SecretCleanup
{
    /// <summary>Deletes <paramref name="secret"/>, logging (not throwing) a failure.</summary>
    /// <param name="secrets">The vault.</param>
    /// <param name="secret">The secret nothing will name.</param>
    /// <param name="logger">Where a failure is logged.</param>
    /// <param name="cancellationToken">The caller's token: an <see cref="OperationCanceledException"/> it caused is not swallowed.</param>
    public static async Task TryDeleteAsync(ISecretStore secrets, SecretId secret, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            _ = await secrets.DeleteAsync(secret, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            LogSecretNotDeleted(logger, secret, ex.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The vault secret {Secret} of a record that was not created could not be deleted ({Problem}); delete it with the vault command.")]
    private static partial void LogSecretNotDeleted(ILogger logger, SecretId secret, string problem);
}
