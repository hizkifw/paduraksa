using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Application.Hosting;
using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The renewal scheduler: a background service that looks for ACME work every
/// <see cref="AcmeIssuanceOptions.SchedulerPollInterval"/> and hands it to <see cref="AcmeIssuanceService"/>. It decides nothing
/// about timing itself: every certificate's row says when it is due (<c>NextRenewalAt</c> from the ARI window or the
/// lifetime rule, <c>RetryAt</c> from the failure backoff, <c>AriCheckAt</c> for asking the CA again), so the same rules
/// hold after a restart. Driven by <see cref="TimeProvider"/>, so tests move the clock instead of waiting.
/// </summary>
/// <remarks>
/// <para>Per look, for each certificate that has an order, <see cref="AcmeIssuanceService.RunDueAsync"/> decides from a fresh read: an
/// unfinished order (a restart caught it) is resumed; a failed one is retried once <c>RetryAt</c> has passed; an issued one is renewed
/// once <c>NextRenewalAt</c> has passed, else its ARI window is refreshed once <c>AriCheckAt</c> has passed. Certificates are handled one at a time, so renewals do not
/// hit the CA in a burst. A failure is logged and recorded on the order and the certificate (by the service); it never stops the loop.</para>
/// </remarks>
public sealed partial class AcmeRenewalScheduler : BackgroundService
{
    private readonly AcmeIssuanceService _issuance;
    private readonly AcmeIssuanceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly IProcessReadiness? _readiness;
    private volatile bool _draining;
    private volatile bool _working;

    /// <summary>Completes when <see cref="ExecuteAsync"/> has begun, so a host that has started can rely on the loop running.</summary>
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The longest the scheduler pauses at once, whatever the options say (a pause longer than <see cref="Task.Delay(TimeSpan)"/> allows would throw and end the loop).</summary>
    internal static readonly TimeSpan MaxPause = TimeSpan.FromDays(1);

    /// <summary>The pause after a pause itself failed, so the loop neither spins nor ends.</summary>
    private static readonly TimeSpan FallbackPause = TimeSpan.FromMinutes(1);

    public AcmeRenewalScheduler(
        AcmeIssuanceService issuance,
        AcmeIssuanceOptions options,
        TimeProvider time,
        ILogger<AcmeRenewalScheduler> logger,
        IProcessReadiness? readiness = null)
    {
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _issuance = issuance;
        _options = options;
        _time = time;
        _logger = logger;
        _readiness = readiness;
    }

    /// <summary>One look at every order, doing what is due. Returns how many certificates it acted on (a renewal, retry, resume or ARI refresh).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var acted = 0;
        foreach (var order in await _issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_draining)
            {
                // Shutting down: the certificate being worked on was finished, no new one is started.
                break;
            }

            try
            {
                // The listing can be minutes old by the time a certificate's turn comes: the service decides from a fresh read.
                if (await _issuance.RunDueAsync(order.CertificateId, cancellationToken).ConfigureAwait(false))
                {
                    acted++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // One certificate's trouble must not stop the others, nor the loop.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogActFailed(ex, order.CertificateId);
            }
        }

        return acted;
    }

    /// <summary>
    /// Starts shutting down gracefully: no new look and no new certificate is started, and the one being worked on gets up to
    /// <paramref name="grace"/> to finish (an ACME validation in flight needs the public listener to answer, so this runs
    /// before it drains); after that, or when <paramref name="cancellationToken"/> fires, the work is cancelled like any stop (the order
    /// stays as it was and resumes at the next start). Does nothing when the scheduler never started.
    /// </summary>
    public async Task DrainAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        _draining = true;
        if (ExecuteTask is not { } running)
        {
            return;
        }

        if (_working)
        {
            try
            {
                await running.WaitAsync(grace, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // Out of time: StopAsync cancels what is left.
            }
        }

        await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the loop and waits until it has actually begun, so a host whose start has returned can rely on the scheduler
    /// running (its stop cancels a loop that is in progress, not a task that never started).
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _entered.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The start was cancelled while the loop was being scheduled; the stop that follows handles it.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _entered.TrySetResult();
        try
        {
            await PauseAsync(_options.SchedulerStartDelay, stoppingToken).ConfigureAwait(false);
            // The start delay is a heuristic; the listeners are a fact. An HTTP-01 validation that runs before the public
            // listener binds fails, and a permanent failure backs the certificate off for a long time, so wait for the
            // process to say it is ready (the core host starts before the web apps).
            if (_readiness is { } readiness)
            {
                await readiness.WaitUntilReadyAsync(stoppingToken).ConfigureAwait(false);
            }

            while (!stoppingToken.IsCancellationRequested && !_draining)
            {
                _working = true;
                if (_draining)
                {
                    // DrainAsync began between the loop test and the flag above and saw the loop idle: start nothing.
                    _working = false;
                    break;
                }

                try
                {
                    _ = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
#pragma warning disable CA1031 // The loop must outlive a failed look (the database was busy, say).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    LogLookFailed(ex);
                }
                finally
                {
                    _working = false;
                }

                if (!_draining)
                {
                    await PauseAsync(_options.SchedulerPollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    /// <summary>Waits <paramref name="wanted"/> (kept within 0 and <see cref="MaxPause"/>); a pause that fails for any other reason waits a minute instead of ending the loop.</summary>
    private async Task PauseAsync(TimeSpan wanted, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(wanted < TimeSpan.Zero ? TimeSpan.Zero : wanted > MaxPause ? MaxPause : wanted, _time, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The loop must outlive whatever a pause trips over.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogLookFailed(ex);
            await Task.Delay(FallbackPause, _time, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate {Certificate}: the scheduler could not act.")]
    private partial void LogActFailed(Exception exception, CertificateId certificate);

    [LoggerMessage(Level = LogLevel.Error, Message = "The renewal scheduler's look failed; trying again next time.")]
    private partial void LogLookFailed(Exception exception);
}
