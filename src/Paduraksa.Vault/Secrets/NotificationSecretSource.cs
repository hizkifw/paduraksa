using Paduraksa.Core.Model;
using Paduraksa.Core.Vault;

namespace Paduraksa.Vault.Secrets;

/// <summary><see cref="INotificationSecretSource"/>: opens the secret, lends its pinned buffer to the callback and zeroes it afterwards.</summary>
internal sealed class NotificationSecretSource(SecretReader reader) : INotificationSecretSource
{
    public async ValueTask<bool> UseAsync(SecretId id, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> use, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(use);
        using var secret = await reader.OpenAsync(id, includePrevious: false, cancellationToken).ConfigureAwait(false);
        if (secret is not { Row.Kind: SecretKind.NotificationSecret })
        {
            return false;
        }

        await use(secret.Current.Memory, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
