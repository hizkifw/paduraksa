using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Paduraksa.Contracts.Issuance;
using Paduraksa.Core.Model;

namespace Paduraksa.Application.Issuance;

/// <summary>
/// The renewal scheduler (PLAN §9, §9.3): a background service that looks for ACME work every
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
/// <para>Notifications for failures arrive with the notification channels (M7); until then the dashboard shows the order's error.</para>
/// </remarks>
public sealed partial class AcmeRenewalScheduler : BackgroundService
{
    private readonly AcmeIssuanceService _issuance;
    private readonly AcmeIssuanceOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public AcmeRenewalScheduler(AcmeIssuanceService issuance, AcmeIssuanceOptions options, TimeProvider time, ILogger<AcmeRenewalScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(issuance);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _issuance = issuance;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>One look at every order, doing what is due. Returns how many certificates it acted on (a renewal, retry, resume or ARI refresh).</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        int acted = 0;
        foreach (AcmeOrderSummary order in await _issuance.ListOrdersAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                LogActFailed(order.CertificateId, ex.GetType().Name);
            }
        }

        return acted;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_options.SchedulerStartDelay, _time, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
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
                    LogLookFailed(ex.GetType().Name);
                }

                await Task.Delay(_options.SchedulerPollInterval, _time, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Certificate {Certificate}: the scheduler could not act ({ErrorType}).")]
    private partial void LogActFailed(CertificateId certificate, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "The renewal scheduler's look failed ({ErrorType}); trying again next time.")]
    private partial void LogLookFailed(string errorType);
}
