using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Issuance;
using Paduraksa.Core.Model;
using Paduraksa.Data;
using Paduraksa.Data.Notifications;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// Looks at every certificate once an hour and tells the channels subscribed to <see cref="NotificationEventKind.CertificateExpiring"/> when one
/// crosses 30, 14, 7 or 1 days. Every source counts: ACME, uploaded, self-signed, local-CA leaves and the local CA's root.
/// </summary>
/// <remarks>
/// <b>Once per threshold per chain.</b> What was sent is a row per (certificate, expiry, threshold), so a restart does not send it again and a renewal,
/// which brings a new expiry, starts the thresholds over. When a look finds several thresholds crossed (the first look after a long stop, or a channel added late),
/// only the smallest is reported and the larger ones are marked as sent too: three messages saying "30 days", "14 days" and "7 days" at once help nobody.
/// <b>Nothing is marked while nobody listens:</b> with no enabled channel subscribed to the event a threshold stays unsent, so a channel added
/// at 5 days still hears of the next one.
/// </remarks>
public sealed partial class CertificateExpiryMonitor : BackgroundService
{
    private static readonly TimeSpan FirstLookDelay = TimeSpan.FromMinutes(1);

    private readonly IDbContextFactory<ConfigDbContext> _contexts;
    private readonly CertificateService _certificates;
    private readonly INotificationEvents _events;
    private readonly NotificationOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    internal CertificateExpiryMonitor(
        IDbContextFactory<ConfigDbContext> contexts,
        CertificateService certificates,
        INotificationEvents events,
        NotificationOptions options,
        TimeProvider time,
        ILogger<CertificateExpiryMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _contexts = contexts;
        _certificates = certificates;
        _events = events;
        _options = options;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(FirstLookDelay, _time, stoppingToken).ConfigureAwait(false);
            using PeriodicTimer timer = new(_options.ExpiryCheckInterval, _time);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogLookFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    /// <summary>One look at every certificate; returns how many notices it sent.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _events.HasSubscribersAsync(NotificationEventKind.CertificateExpiring, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var now = _time.GetUtcNow();
        var sent = 0;
        var context = await _contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var notices = (await context.ExpiryNotices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false))
                .ToLookup(n => (n.CertificateId, n.NotAfter), n => n.Days);
            foreach (var certificate in await _certificates.ListAsync(null, cancellationToken).ConfigureAwait(false))
            {
                if (certificate.Status != CertificateStatus.Active)
                {
                    continue;
                }

                var notAfter = certificate.NotAfter.ToUnixTimeSeconds();
                var remaining = certificate.NotAfter - now;
                var crossed = _options.ExpiryThresholdDays.Where(d => remaining <= TimeSpan.FromDays(d)).Order().ToList();
                if (crossed.Count == 0)
                {
                    continue;
                }

                var already = notices[(certificate.Id, notAfter)].ToHashSet();
                if (already.Contains(crossed[0]))
                {
                    continue;
                }

                _events.Publish(NotificationText.CertificateExpiring(AcmeIssuanceService.CertificateLabel(certificate), certificate.Id, certificate.Source, certificate.NotAfter, now));
                sent++;
                foreach (var days in crossed.Where(d => !already.Contains(d)))
                {
                    context.ExpiryNotices.Add(new ExpiryNoticeRow { CertificateId = certificate.Id, NotAfter = notAfter, Days = days, SentAt = now });
                }

                // A renewal brings a new expiry: the notices of the chain it replaced are history.
                await context.ExpiryNotices.Where(n => n.CertificateId == certificate.Id && n.NotAfter != notAfter).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return sent;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The certificate expiry check failed; it tries again at the next look.")]
    private partial void LogLookFailed(Exception exception);
}
