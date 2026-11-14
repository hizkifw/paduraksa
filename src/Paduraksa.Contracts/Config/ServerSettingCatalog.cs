namespace Paduraksa.Contracts.Config;

/// <summary>
/// Every server setting, declared once (docs/design/config.md "Server settings", "Adding a server setting"): its property on
/// <see cref="ServerSettings"/>, its configuration key, its group, when it applies, its words, its default and its bounds. The bounds are the
/// ones the server's options validator holds the configuration to, from these same declarations.
/// </summary>
public static class ServerSettingCatalog
{
    private const long Mebibyte = 1024 * 1024;
    private const long Gibibyte = 1024 * Mebibyte;

    public static readonly ServerSetting<TimeSpan> MetricsRetention = new(
        "metricsRetention", "Metrics:Retention", ServerSettingGroup.Retention, "Metrics", ServerSettingApply.Live,
        "Traffic and security history",
        "How long the minute rollups behind the overview, the analytics' traffic and the security statistics are kept. Older minutes are deleted hourly.",
        static s => s.MetricsRetention, static (s, v) => s with { MetricsRetention = v },
        TimeSpan.FromDays(90), TimeSpan.FromHours(1), TimeSpan.FromDays(1825))
    {
        LoweredNote = "older minutes are deleted at the next prune (within the hour) and can't be recovered.",
    };

    public static readonly ServerSetting<TimeSpan> AnalyticsRetention = new(
        "analyticsRetention", "Analytics:Retention", ServerSettingGroup.Retention, "Analytics", ServerSettingApply.Live,
        "Visitors, clients and paths",
        "How long the hourly analytics rollups are kept. Older hours are deleted hourly; the five-minute rows are kept two days.",
        static s => s.AnalyticsRetention, static (s, v) => s with { AnalyticsRetention = v },
        TimeSpan.FromDays(90), TimeSpan.FromHours(1), TimeSpan.FromDays(1825))
    {
        LoweredNote = "older hours are deleted at the next prune (within the hour) and can't be recovered.",
    };

    public static readonly ServerSetting<TimeSpan> NotificationHistoryRetention = new(
        "notificationHistoryRetention", "Notifications:HistoryRetention", ServerSettingGroup.Retention, "Notifications", ServerSettingApply.Live,
        "Notification history",
        "How long a sent event and its deliveries stay in the notification history.",
        static s => s.NotificationHistoryRetention, static (s, v) => s with { NotificationHistoryRetention = v },
        TimeSpan.FromDays(14), TimeSpan.FromHours(1), TimeSpan.FromDays(1825))
    {
        LoweredNote = "older events are deleted at the next prune (within the hour) and can't be recovered.",
    };

    public static readonly ServerSetting<long> LogRetainedFiles = new(
        "logRetainedFiles", "Logs:RetainedFiles", ServerSettingGroup.Retention, "Log files", ServerSettingApply.Live,
        "Rotated files kept",
        "How many rotated files the access log and the error log each keep, newest first.",
        static s => s.LogRetainedFiles, static (s, v) => s with { LogRetainedFiles = v },
        30, 1, 10_000)
    {
        LoweredNote = "the rotated files past it are deleted as soon as the change is live and can't be recovered.",
    };

    public static readonly ServerSetting<TimeSpan> LogMaxAge = new(
        "logMaxAge", "Logs:MaxAge", ServerSettingGroup.Retention, "Log files", ServerSettingApply.Live,
        "Oldest rotated file",
        "Rotated files older than this are deleted.",
        static s => s.LogMaxAge, static (s, v) => s with { LogMaxAge = v },
        TimeSpan.FromDays(30), TimeSpan.FromMinutes(1), TimeSpan.FromDays(3650))
    {
        LoweredNote = "older rotated files are deleted as soon as the change is live and can't be recovered.",
    };

    public static readonly ServerSetting<long> LogMaxTotalSize = new(
        "logMaxTotalSize", "Logs:MaxTotalSize", ServerSettingGroup.Retention, "Log files", ServerSettingApply.Live,
        "Rotated files in total",
        "The oldest rotated files are deleted until each log's rotated files fit in this.",
        static s => s.LogMaxTotalSize, static (s, v) => s with { LogMaxTotalSize = v },
        Gibibyte, Mebibyte, 1024 * Gibibyte, ServerSettingKind.Size)
    {
        LoweredNote = "the oldest rotated files are deleted as soon as the change is live until the rest fit, and can't be recovered.",
    };

    public static readonly ServerSetting<long> LogMaxFileSize = new(
        "logMaxFileSize", "Logs:MaxFileSize", ServerSettingGroup.Retention, "Log files", ServerSettingApply.Live,
        "Rotate at",
        "A log file is rotated when the next line would take it past this, and at midnight.",
        static s => s.LogMaxFileSize, static (s, v) => s with { LogMaxFileSize = v },

        // Below a line's worth of bytes every line would rotate the file again; above 16 GiB the live file is more than the dashboard's tail can read.
        64 * Mebibyte, 1024, 16 * Gibibyte, ServerSettingKind.Size);

    /// <summary>Every declaration, in the order the page shows them.</summary>
    public static IReadOnlyList<ServerSettingDefinition> All { get; } =
    [
        MetricsRetention, AnalyticsRetention, NotificationHistoryRetention, LogRetainedFiles, LogMaxAge, LogMaxTotalSize, LogMaxFileSize,
    ];

    /// <summary>
    /// The rules between settings, over the values in force (each setting's own value or its default): a key and what is wrong, for each
    /// pair that can't be used together.
    /// </summary>
    public static IReadOnlyList<Func<ServerSettings, (string Key, string Problem)?>> Rules { get; } = [];

    /// <summary>
    /// What the review drawer says about a change from <paramref name="before"/> to <paramref name="after"/> (both as stored; null fields at
    /// their defaults): a lowered value that deletes data, a setting's own note, and the settings that only apply after a restart.
    /// </summary>
    public static IReadOnlyList<string> Notices(ServerSettings? before, ServerSettings? after)
    {
        before ??= new();
        after ??= new();
        List<string> notices = [];
        foreach (var setting in All)
        {
            if (!setting.Differs(before, after))
            {
                continue;
            }

            if (setting.LoweredNote is { } lowered && setting.Lowered(before, after))
            {
                notices.Add($"{setting.Title} goes down from {ServerSettingText.Describe(setting.Kind, setting.ResolvedText(before))} to "
                    + $"{ServerSettingText.Describe(setting.Kind, setting.ResolvedText(after))}: {lowered}");
            }

            if (setting.Note?.Invoke(before, after) is { } note)
            {
                notices.Add(note);
            }

            if (setting.Apply == ServerSettingApply.Restart)
            {
                notices.Add($"{setting.Title} applies after a restart: the server runs with the value it started with until then.");
            }
        }

        return notices;
    }

    /// <summary>The declaration with <paramref name="key"/>, or null.</summary>
    public static ServerSettingDefinition? Find(string key)
    {
        foreach (var setting in All)
        {
            if (string.Equals(setting.Key, key, StringComparison.Ordinal))
            {
                return setting;
            }
        }

        return null;
    }
}
