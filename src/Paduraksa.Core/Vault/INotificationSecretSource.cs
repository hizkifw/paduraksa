using Paduraksa.Core.Model;

namespace Paduraksa.Core.Vault;

/// <summary>
/// The one place a secret's value leaves the vault for code that sends something: a notification channel's webhook URL, token or
/// SMTP password has to be used by the sender, which no handler or MAC handle can do for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Only secrets of kind <see cref="SecretKind.NotificationSecret"/> open here; a DNS token, an HMAC key or anything
/// else answers as if it did not exist, so this cannot become a way to read the vault's other secrets. Nothing returns the value:
/// it is handed to <c>use</c> as a view of the vault's own pinned buffer, which is zeroed when <c>use</c> returns, so the caller
/// must finish with the bytes inside the callback and copy out only what it has to (a URL, a password string for the SMTP library).
/// </para>
/// </remarks>
public interface INotificationSecretSource
{
    /// <summary>
    /// Runs <paramref name="use"/> with the value of secret <paramref name="id"/>. Returns <see langword="false"/>, without calling it,
    /// when there is no such secret or it is not a <see cref="SecretKind.NotificationSecret"/>.
    /// </summary>
    ValueTask<bool> UseAsync(SecretId id, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> use, CancellationToken cancellationToken);
}
