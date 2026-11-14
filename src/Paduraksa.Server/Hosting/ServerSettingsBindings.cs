using Paduraksa.Application.Analytics;
using Paduraksa.Application.Metrics;
using Paduraksa.Application.Notifications;
using Paduraksa.Application.Settings;
using Paduraksa.Contracts.Config;
using Paduraksa.Core.Options;
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

    public ServerSettingsBindings(ServerSettingsState state, ServerLogFiles logFiles, PaduraksaOptionsSnapshot startup)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(logFiles);
        ArgumentNullException.ThrowIfNull(startup);
        _logFiles = logFiles;
        MetricRollups = new(startup.MetricRollups);
        Analytics = new(startup.Analytics);
        Notifications = new(startup.Notifications);
        Apply(state.Current);
        state.Changed += Apply;
    }

    public LiveOptions<MetricRollupOptions> MetricRollups { get; }

    public LiveOptions<AnalyticsOptions> Analytics { get; }

    public LiveOptions<NotificationOptions> Notifications { get; }

    private void Apply(ServerSettings values)
    {
        MetricRollups.Set(MetricRollups.Current with { Retention = ServerSettingCatalog.MetricsRetention.Resolve(values) });
        Analytics.Set(Analytics.Current with { Retention = ServerSettingCatalog.AnalyticsRetention.Resolve(values) });
        Notifications.Set(Notifications.Current with
        {
            HistoryRetention = ServerSettingCatalog.NotificationHistoryRetention.Resolve(values),
        });
        _logFiles.Retain(
            ServerSettingCatalog.LogMaxFileSize.Resolve(values),
            (int)ServerSettingCatalog.LogRetainedFiles.Resolve(values),
            ServerSettingCatalog.LogMaxAge.Resolve(values),
            ServerSettingCatalog.LogMaxTotalSize.Resolve(values));
    }
}

/// <summary>The consumers' options as the bootstrap configuration has them, before the server settings are laid over them.</summary>
internal sealed record PaduraksaOptionsSnapshot(MetricRollupOptions MetricRollups, AnalyticsOptions Analytics, NotificationOptions Notifications);
