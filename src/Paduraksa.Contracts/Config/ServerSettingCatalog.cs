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

    private const string SessionNote = "Existing sessions are held to the shorter time at their next check, so some may end at once.";

    public static readonly ServerSetting<long> AdminMaxFailedAttempts = new(
        "adminMaxFailedAttempts", "Admin:MaxFailedAttempts", ServerSettingGroup.SignIn, "Lockout", ServerSettingApply.Live,
        "Failed attempts before a lockout",
        "Consecutive wrong passwords or codes before an account refuses sign-in.",
        static s => s.AdminMaxFailedAttempts, static (s, v) => s with { AdminMaxFailedAttempts = v },
        5, 2, 100);

    public static readonly ServerSetting<TimeSpan> AdminLockoutDuration = new(
        "adminLockoutDuration", "Admin:LockoutDuration", ServerSettingGroup.SignIn, "Lockout", ServerSettingApply.Live,
        "Lockout",
        "How long a locked account refuses sign-in.",
        static s => s.AdminLockoutDuration, static (s, v) => s with { AdminLockoutDuration = v },
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));

    public static readonly ServerSetting<TimeSpan> AdminSessionLifetime = new(
        "adminSessionLifetime", "Admin:SessionLifetime", ServerSettingGroup.SignIn, "Sessions", ServerSettingApply.Live,
        "Session lifetime",
        "The longest a sign-in lasts, however active.",
        static s => s.AdminSessionLifetime, static (s, v) => s with { AdminSessionLifetime = v },
        TimeSpan.FromHours(12), TimeSpan.FromMinutes(5), TimeSpan.FromDays(30))
    {
        LoweredNote = SessionNote,
    };

    public static readonly ServerSetting<TimeSpan> AdminSessionIdleTimeout = new(
        "adminSessionIdleTimeout", "Admin:SessionIdleTimeout", ServerSettingGroup.SignIn, "Sessions", ServerSettingApply.Live,
        "Idle timeout",
        "A session unused for this long ends. At most the session lifetime.",
        static s => s.AdminSessionIdleTimeout, static (s, v) => s with { AdminSessionIdleTimeout = v },
        TimeSpan.FromHours(2), TimeSpan.FromMinutes(1), TimeSpan.FromDays(30))
    {
        LoweredNote = SessionNote,
    };

    public static readonly ServerSetting<TimeSpan> AdminPartialSessionLifetime = new(
        "adminPartialSessionLifetime", "Admin:PartialSessionLifetime", ServerSettingGroup.SignIn, "Sessions", ServerSettingApply.Live,
        "Time for the second factor",
        "How long a person has to give the authenticator code after the password.",
        static s => s.AdminPartialSessionLifetime, static (s, v) => s with { AdminPartialSessionLifetime = v },
        TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30))
    {
        LoweredNote = "A sign-in waiting for its code is held to the shorter time at its next step.",
    };

    public static readonly ServerSetting<long> AdminMinPasswordLength = new(
        "adminMinPasswordLength", "Admin:MinPasswordLength", ServerSettingGroup.SignIn, "Passwords", ServerSettingApply.Live,
        "Shortest password",
        "In characters, with no composition rules. Applies to new and changed passwords; stored ones keep working.",
        static s => s.AdminMinPasswordLength, static (s, v) => s with { AdminMinPasswordLength = v },
        12, 8, 128)
    {
        Note = static (before, after) => AdminMinPasswordLength!.Resolve(after) > AdminMinPasswordLength.Resolve(before)
            ? "The longer minimum applies to new and changed passwords only: nobody is signed out or asked to change theirs."
            : null,
    };

    public static readonly TextServerSetting AdminTotpIssuer = new(
        "adminTotpIssuer", "Admin:TotpIssuer", ServerSettingGroup.SignIn, "Passwords", ServerSettingApply.Live,
        "Authenticator name",
        "The issuer authenticator apps show for a new enrolment; existing ones keep the name they were enrolled with.",
        static s => s.AdminTotpIssuer, static (s, v) => s with { AdminTotpIssuer = v },
        "Paduraksa",
        static value => value.Length is 0 or > 64 || value.Contains(':', StringComparison.Ordinal) || value.Any(char.IsControl)
            ? "must be 1 to 64 characters without a colon or control characters"
            : null);

    public static readonly ServerSetting<long> AdminRateLimitPermitsPerIp = new(
        "adminRateLimitPermitsPerIp", "Admin:RateLimit:PermitsPerIp", ServerSettingGroup.SignIn, "Sign-in rate limit", ServerSettingApply.Live,
        "Attempts per address",
        "Sign-in, second-factor and setup attempts, successful or not, one client address may make per window. An IPv6 client is one /64.",
        static s => s.AdminRateLimitPermitsPerIp, static (s, v) => s with { AdminRateLimitPermitsPerIp = v },
        20, 1, 10_000);

    public static readonly ServerSetting<long> AdminRateLimitPermitsPerUser = new(
        "adminRateLimitPermitsPerUser", "Admin:RateLimit:PermitsPerUser", ServerSettingGroup.SignIn, "Sign-in rate limit", ServerSettingApply.Live,
        "Failures per username",
        "Failed attempts against one username per window; successful ones do not count.",
        static s => s.AdminRateLimitPermitsPerUser, static (s, v) => s with { AdminRateLimitPermitsPerUser = v },
        10, 1, 1000);

    public static readonly ServerSetting<TimeSpan> AdminRateLimitWindow = new(
        "adminRateLimitWindow", "Admin:RateLimit:Window", ServerSettingGroup.SignIn, "Sign-in rate limit", ServerSettingApply.Live,
        "Window",
        "The window both limits count in.",
        static s => s.AdminRateLimitWindow, static (s, v) => s with { AdminRateLimitWindow = v },
        TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));

    /// <summary>Every declaration, in the order the page shows them.</summary>
    public static IReadOnlyList<ServerSettingDefinition> All { get; } =
    [
        MetricsRetention, AnalyticsRetention, NotificationHistoryRetention, LogRetainedFiles, LogMaxAge, LogMaxTotalSize, LogMaxFileSize,
        AdminMaxFailedAttempts, AdminLockoutDuration, AdminSessionLifetime, AdminSessionIdleTimeout, AdminPartialSessionLifetime,
        AdminMinPasswordLength, AdminTotpIssuer, AdminRateLimitPermitsPerIp, AdminRateLimitPermitsPerUser, AdminRateLimitWindow,
    ];

    /// <summary>
    /// The rules between settings, over the values in force (each setting's own value or its default): a key and what is wrong, for each
    /// pair that can't be used together.
    /// </summary>
    public static IReadOnlyList<Func<ServerSettings, (string Key, string Problem)?>> Rules { get; } =
    [
        static s => AdminSessionIdleTimeout.Resolve(s) > AdminSessionLifetime.Resolve(s)
            ? (AdminSessionIdleTimeout.Key, $"the idle timeout ({ServerSettingText.Duration(AdminSessionIdleTimeout.Resolve(s))}) can't be longer than the session lifetime ({ServerSettingText.Duration(AdminSessionLifetime.Resolve(s))})")
            : null,
    ];

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
