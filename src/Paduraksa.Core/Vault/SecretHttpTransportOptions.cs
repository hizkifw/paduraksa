namespace Paduraksa.Core.Vault;

/// <summary>
/// The transport settings of an authenticating handler (<see cref="ISecretHttpHandlerFactory"/>). The vault builds the
/// terminal <c>SocketsHttpHandler</c> itself, so the caller can only tune these knobs, not supply a handler that would see
/// the credential.
/// </summary>
/// <param name="ConnectTimeout">How long to wait for a TCP and TLS connection. Default 10 s.</param>
/// <param name="PooledConnectionLifetime">
/// The longest a pooled connection lives, which is how often DNS is looked up again (a provider that moves is picked up
/// within this time). Default 2 minutes.
/// </param>
/// <param name="PooledConnectionIdleTimeout">How long an idle pooled connection is kept. Default 30 s.</param>
/// <param name="ProxyAddress">An HTTP proxy to go through, or <see langword="null"/> (the default) to connect directly. The system proxy is never used.</param>
public sealed record SecretHttpTransportOptions(
    TimeSpan ConnectTimeout,
    TimeSpan PooledConnectionLifetime,
    TimeSpan PooledConnectionIdleTimeout,
    Uri? ProxyAddress)
{
    /// <summary>The defaults described on each member, with a direct connection.</summary>
    public static SecretHttpTransportOptions Default { get; } =
        new(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30), null);
}
