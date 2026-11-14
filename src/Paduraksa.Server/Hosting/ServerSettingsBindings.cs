using Paduraksa.Application.Analytics;
using Paduraksa.Application.Cloudflare;
using Paduraksa.Application.Identity;
using Paduraksa.Application.Issuance;
using Paduraksa.Application.Metrics;
using Paduraksa.Application.Notifications;
using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Options;
using Paduraksa.Proxy.Logging;
using Paduraksa.Server.Logging;

namespace Paduraksa.Server.Hosting;

/// <summary>
/// Where each server setting that applies live goes (docs/design/config.md "Server settings"): the one place the values in force are handed to
/// the consumers' options, at composition and whenever they change. A consumer reads its <see cref="LiveOptions{T}"/> when it acts, so nothing
/// here waits.
/// </summary>
internal sealed class ServerSettingsBindings
{
    private readonly ServerLogFiles _logFiles;
    private readonly TimeSpan _touchInterval;

    public ServerSettingsBindings(ServerSettingsState state, ServerLogFiles logFiles, PaduraksaOptionsSnapshot startup)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logFiles);
        ArgumentNullException.ThrowIfNull(startup);
        _logFiles = logFiles;
        MetricRollups = new(startup.MetricRollups);
        Analytics = new(startup.Analytics);
        Notifications = new(startup.Notifications);
        Identity = new(startup.Identity);
        AccessLogFormat = new(startup.AccessLogFormat);
        CloudflareRanges = new(startup.CloudflareRanges);
        AcmeIssuance = new(startup.AcmeIssuance);
        _touchInterval = startup.Identity.TouchInterval;
        Apply(state.Current);
        state.Changed += Apply;
    }

    public LiveOptions<MetricRollupOptions> MetricRollups { get; }

    public LiveOptions<AnalyticsOptions> Analytics { get; }

    public LiveOptions<NotificationOptions> Notifications { get; }

    public LiveOptions<IdentityOptions> Identity { get; }

    public LiveOptions<AccessLogFormatOptions> AccessLogFormat { get; }

    public LiveOptions<CloudflareRangeRefreshOptions> CloudflareRanges { get; }

    public LiveOptions<AcmeIssuanceOptions> AcmeIssuance { get; }

    private void Apply(ServerSettings values)
    {
        MetricRollups.Set(MetricRollups.Current with { Retention = ServerSettingCatalog.MetricsRetention.Resolve(values) });
        Analytics.Set(Analytics.Current with { Retention = ServerSettingCatalog.AnalyticsRetention.Resolve(values) });
        Notifications.Set(Notifications.Current with
        {
            HistoryRetention = ServerSettingCatalog.NotificationHistoryRetention.Resolve(values),
            AdminUrl = ServerSettingCatalog.NotificationAdminUrl.Resolve(values) is { Length: > 0 } url ? new Uri(url.TrimEnd('/') + "/") : null,
            HealthHold = ServerSettingCatalog.NotificationHealthHold.Resolve(values),
            CoalesceWindow = ServerSettingCatalog.NotificationCoalesceWindow.Resolve(values),
            MaxDeliveryAge = ServerSettingCatalog.NotificationMaxDeliveryAge.Resolve(values),
        });
        CloudflareRanges.Set(CloudflareRanges.Current with
        {
            Enabled = ServerSettingCatalog.CloudflareRangeRefresh.Resolve(values),
            Interval = ServerSettingCatalog.CloudflareRangeInterval.Resolve(values),
        });
        AcmeIssuance.Set(AcmeIssuance.Current with
        {
            DnsPropagationDelay = ServerSettingCatalog.AcmeDnsPropagationDelay.Resolve(values),
            DnsPropagationCheck = ServerSettingCatalog.AcmeDnsPropagationCheck.Resolve(values),
        });
        AccessLogFormat.Set(AccessLogFormat.Current with { LogQueryString = ServerSettingCatalog.AccessLogQueryString.Resolve(values) });
        var idle = ServerSettingCatalog.AdminSessionIdleTimeout.Resolve(values);
        Identity.Set(Identity.Current with
        {
            MaxFailedAttempts = (int)ServerSettingCatalog.AdminMaxFailedAttempts.Resolve(values),
            LockoutDuration = ServerSettingCatalog.AdminLockoutDuration.Resolve(values),
            SessionLifetime = ServerSettingCatalog.AdminSessionLifetime.Resolve(values),
            SessionIdleTimeout = idle,
            PartialSessionLifetime = ServerSettingCatalog.AdminPartialSessionLifetime.Resolve(values),
            MinPasswordLength = (int)ServerSettingCatalog.AdminMinPasswordLength.Resolve(values),
            TotpIssuer = ServerSettingCatalog.AdminTotpIssuer.Resolve(values),

            // The last-seen time must be written more often than the idle limit is long, or live sessions end: the environment's touch interval
            // was checked against the environment's idle timeout, so a shorter one from the config halves it here.
            TouchInterval = _touchInterval < idle ? _touchInterval : idle / 2,
        });
        _logFiles.Retain(
            ServerSettingCatalog.LogMaxFileSize.Resolve(values),
            (int)ServerSettingCatalog.LogRetainedFiles.Resolve(values),
            ServerSettingCatalog.LogMaxAge.Resolve(values),
            ServerSettingCatalog.LogMaxTotalSize.Resolve(values));
    }
}

/// <summary>The consumers' options as the bootstrap configuration has them, before the server settings are laid over them.</summary>
internal sealed record PaduraksaOptionsSnapshot(MetricRollupOptions MetricRollups, AnalyticsOptions Analytics, NotificationOptions Notifications, IdentityOptions Identity, AccessLogFormatOptions AccessLogFormat, CloudflareRangeRefreshOptions CloudflareRanges, AcmeIssuanceOptions AcmeIssuance);
