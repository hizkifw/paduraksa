using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Core.Vault;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Brings the vault up first thing in the core host (it is registered before <see cref="ConfigStartup"/>, so hosted services start
/// in that order): resolves the key-encryption key, migrates <c>vault.db</c> and checks the key against it. <b>A failure stops
/// the process</b> (exit code 2, the cause in the Critical log entry): a wrong, missing or unsafe master key is an operator
/// problem that running without the vault would only hide, and the first certificate that needed a key would fail much later
/// and much less clearly.
/// </summary>
internal sealed partial class VaultStartup(IVaultLifecycle vault, ILogger<VaultStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            VaultStatus status = await vault.InitializeAsync(cancellationToken).ConfigureAwait(false);
            LogReady(logger, status.KekSource, status.KekBesideDatabase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Whatever it is (a wrong key, an unreadable or locked database, a data directory we can't write), the process stops
            // with it named: the vault is not optional.
            LogFailed(logger, ex.Message, ex);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(Level = LogLevel.Information, Message = "The vault is ready (master key source: {Source}, beside the database: {Beside}).")]
    private static partial void LogReady(ILogger logger, KekSourceKind source, bool beside);

    [LoggerMessage(Level = LogLevel.Critical, Message = "The vault can't start, so Paduraksa won't: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason, Exception exception);
}
