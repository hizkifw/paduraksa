using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Certificates;
using Paduraksa.Application.Commands;
using Paduraksa.Server.Configuration;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Registers the certificates the vault holds that the config database lost, in the core host after
/// <see cref="ConfigStartup"/> and <b>before the web apps start</b>. An awaited start step, bounded by
/// <c>Paduraksa:Certificates:RecoveryStartupTimeout</c>: past it the run continues in the background and the start goes on.
/// Registrations are quiet, then one publish is queued, so the first snapshot the listeners serve already includes what came
/// back; a stop is observed between certificates and never inside a commit. <b>Never stops the process</b>: a certificate
/// that can't be recovered is logged and stays missing, and a store that doesn't accept changes (a degraded start) is
/// skipped.
/// </summary>
internal sealed partial class CertificateRecoveryStartup(
    CertificateRecovery recovery,
    ConfigService config,
    CertificateRecoveryCompletion completion,
    PaduraksaOptions options,
    TimeProvider time,
    ILogger<CertificateRecoveryStartup> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private CancellationTokenSource? _run;
    private Task? _running;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        Task recovering = RecoverAsync(_run.Token);
        _running = recovering;

        TimeSpan timeout = options.Certificates.RecoveryStartupTimeout;
        using CancellationTokenSource timer = new();
        Task elapsed = Task.Delay(timeout, time, timer.Token);
        Task finished = await Task.WhenAny(recovering, elapsed).ConfigureAwait(false);
        await timer.CancelAsync().ConfigureAwait(false);

        if (finished == recovering || _run.IsCancellationRequested)
        {
            // The run finished, or the start was cancelled while waiting: its own cancellation surfaces here.
            await recovering.ConfigureAwait(false);
            return;
        }

        LogTimedOut(logger, timeout);
        _ = FollowAsync(recovering);
    }

    /// <summary>Stops the run before the vault and the config store are disposed; bounded by the shutdown budget.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_running is { } running)
        {
            try
            {
                await running.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                // Cancelled by the stop or the budget: the run observed it between certificates.
            }
        }
    }

    private async Task RecoverAsync(CancellationToken cancellationToken)
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
            if (result.Recovered.Count > 0)
            {
                // Queued, not awaited: the config's publish needs the public app's YARP, which starts after this host.
                await config.RepublishLiveAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The process was stopped: nothing failed.
        }
        catch (Exception ex)
        {
            LogFailed(logger, ex.Message, ex);
        }
        finally
        {
            completion.Complete(result);
        }
    }

    public void Dispose()
    {
        _run?.Dispose();
        _stop.Dispose();
    }

    /// <summary>Observes a run that outlived the start's timeout, so its cancellation does not become an unobserved task.</summary>
    private static async Task FollowAsync(Task recovering)
    {
        try
        {
            await recovering.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The process stopped after the start gave up waiting.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The config store does not accept changes, so the certificates the vault holds were not checked against the inventory.")]
    private static partial void LogSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking the vault's certificates against the inventory is taking longer than {Timeout}; the start goes on and the check continues in the background.")]
    private static partial void LogTimedOut(ILogger logger, TimeSpan timeout);

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
