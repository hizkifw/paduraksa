using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Registers the certificates the vault holds that the config database lost (PLAN §9.2), in the core host right after
/// <see cref="ConfigStartup"/>. <b>A background service, not a start step:</b> registering a certificate republishes the live config
/// and waits for it to go live, which for a config with routes needs the public app's YARP, and that app starts after the core host's
/// start has returned (the same reason <see cref="ConfigStartup"/> never awaits YARP). So it runs while the web apps start. The renewal
/// scheduler's start delay keeps it clear of anything that creates certificates. <b>Never stops the process</b>: a certificate that can't
/// be recovered is logged and stays missing from the inventory (as it was), and a store that doesn't accept changes (a degraded start)
/// is skipped.
/// </summary>
internal sealed partial class CertificateRecoveryStartup(CertificateRecovery recovery, ConfigService config, CertificateRecoveryCompletion completion, ILogger<CertificateRecoveryStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        CertificateRecoveryResult? result = null;
        try
        {
            if (config.Status is { AcceptsCommands: false })
            {
                LogSkipped(logger);
                return;
            }

            result = await recovery.RecoverAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailed(logger, ex.Message, ex);
        }
        finally
        {
            completion.Complete(result);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The config store does not accept changes, so the certificates the vault holds were not checked against the inventory.")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking the vault's certificates against the inventory failed: {Reason}")]
    private static partial void LogFailed(ILogger logger, string reason, Exception exception);
}

/// <summary>
/// Completes when <see cref="CertificateRecoveryStartup"/> has finished its one run (a shared instance, so tests and later startup steps can wait
/// for it instead of sleeping). The result is null when the run was skipped, failed or was cancelled by a stop.
/// </summary>
internal sealed class CertificateRecoveryCompletion
{
    private readonly TaskCompletionSource<CertificateRecoveryResult?> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<CertificateRecoveryResult?> Finished => _finished.Task;

    public void Complete(CertificateRecoveryResult? result) => _finished.TrySetResult(result);
}
