using System.Net;
using Microsoft.Extensions.Options;

using Paduraksa.Admin.Auth;
using Paduraksa.Application.Certificates;
using Paduraksa.Application.Issuance;
using Paduraksa.Proxy.Net;

namespace Paduraksa.Server.Configuration;

/// <summary>
/// Startup validation for <see cref="PaduraksaOptions"/>. Runs once, before any listener is bound,
/// so a bad config fails fast with every problem listed instead of leaving a half-started process.
/// </summary>
/// <param name="allowEphemeralPorts">
/// Accept port 0 (OS-assigned). Only <see cref="Hosting.PaduraksaHostOptions.AllowEphemeralPorts"/> sets this;
/// it isn't reachable from configuration.
/// </param>
internal sealed class PaduraksaOptionsValidator(bool allowEphemeralPorts = false) : IValidateOptions<PaduraksaOptions>
{
    private const int MaxPort = 65535;

    public ValidateOptionsResult Validate(string? name, PaduraksaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];

        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory must not be empty.");
        }
        else if (options.DataDirectory.StartsWith('~'))
        {
            // Nothing expands '~' here (that's the shell's job); it would silently become <content root>/~/...
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory must not start with '~' (no home-directory expansion); use an absolute path, was '{options.DataDirectory}'.");
        }
        else if (options.DataDirectory.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:DataDirectory contains characters that aren't valid in a path.");
        }

        if (options.ShutdownTimeout <= TimeSpan.Zero)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:ShutdownTimeout must be positive.");
        }

        ValidateAdmin(options.Admin, failures);

        var listen = options.Listen;
        (string Name, int Port)[] ports =
        [
            ("Http", listen.Http),
            ("Https", listen.Https),
            ("Admin", listen.Admin),
            ("AdminLoopbackHttp", listen.AdminLoopbackHttp),
        ];

        foreach (var (portName, port) in ports)
        {
            // The loopback fallback is the only optional listener: 0 is "off" for it, never an OS-assigned port.
            var min = portName == "AdminLoopbackHttp" ? 0 : allowEphemeralPorts ? 0 : 1;
            if (port < min || port > MaxPort)
            {
                failures.Add($"{PaduraksaOptions.SectionName}:Listen:{portName} must be between {min} and {MaxPort}, was {port}.");
            }
        }

        // The admin listener may bind a different address than the public ones, but sharing a
        // port number between them is always a misconfiguration.
        var clashes = ports
            .Where(static p => p.Port > 0)
            .GroupBy(static p => p.Port)
            .Where(static g => g.Count() > 1);
        foreach (var clash in clashes)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen ports must be distinct; {string.Join(", ", clash.Select(static p => p.Name))} all use {clash.Key}.");
        }

        if (!IPAddress.TryParse(listen.AdminBind, out _))
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:AdminBind must be an IP address, was '{listen.AdminBind}'.");
        }

        if (listen.AdminAllowedSources is null)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:AdminAllowedSources must not be null.");
        }
        else
        {
            foreach (var source in listen.AdminAllowedSources.Where(static s => !AdminSourcePolicy.TryParseBlock(s, out _)))
            {
                failures.Add($"{PaduraksaOptions.SectionName}:Listen:AdminAllowedSources entry '{source}' is not an IP address or CIDR block (an IPv4-mapped IPv6 block must be written as IPv4).");
            }
        }

        if (listen.PublicHttpsPort is < 1 or > MaxPort)
        {
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:PublicHttpsPort must be between 1 and {MaxPort}, was {listen.PublicHttpsPort}.");
        }

        if (listen.Http3 && listen.Https == 0)
        {
            // Only reachable with ephemeral ports (tests): TCP and UDP must share one port, which 0 can't guarantee.
            failures.Add($"{PaduraksaOptions.SectionName}:Listen:Http3 needs a fixed Https port (TCP and UDP share it); turn it off with an OS-assigned port.");
        }

        ValidateCertificates(options, failures);
        ValidateAcme(options, failures);
        ValidateCloudflare(options, failures);
        ValidateLogs(options.Logs, failures);
        ValidateMetrics(options.Metrics, failures);
        ValidateNotifications(options.Notifications, failures);
        ValidateAccessLog(options, failures);

        ValidateProxyProtocol("HttpProxyProtocol", listen.HttpProxyProtocol, failures);
        ValidateProxyProtocol("HttpsProxyProtocol", listen.HttpsProxyProtocol, failures);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateCertificates(PaduraksaOptions options, List<string> failures)
    {
        var key = $"{PaduraksaOptions.SectionName}:Certificates";
        var certificates = options.Certificates;
        if (certificates is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        Bound(certificates.RecoveryStartupTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), $"{key}:RecoveryStartupTimeout", failures);
    }

    private static void ValidateAccessLog(PaduraksaOptions options, List<string> failures)
    {
        const string Key = PaduraksaOptions.SectionName + ":AccessLog";
        var accessLog = options.AccessLog;
        if (accessLog is null)
        {
            failures.Add($"{Key} must not be null.");
            return;
        }

        // The ring's capacity must be a power of two (its slot arithmetic is a mask), and a size that isn't one would only
        // surface as an exception in the middle of composition.
        var size = accessLog.RingSize;
        if (size < Proxy.Logging.AccessLogRing.MinimumCapacity || size > Proxy.Logging.AccessLogRing.MaximumCapacity || (size & (size - 1)) != 0)
        {
            failures.Add(
                $"{Key}:RingSize must be a power of two between {Proxy.Logging.AccessLogRing.MinimumCapacity} and {Proxy.Logging.AccessLogRing.MaximumCapacity}, was {size}.");
        }

        // The list itself can't be null (it is created with the options and never replaced), so only its entries are checked.
        for (var i = 0; i < accessLog.SensitiveQueryKeys.Count; i++)
        {
            var name = accessLog.SensitiveQueryKeys[i] ?? string.Empty;
            if (name.Length == 0 || name.Length > MaxSensitiveQueryKeyLength || name.Any(char.IsControl) || name.Contains('&', StringComparison.Ordinal)
                || name.Contains('=', StringComparison.Ordinal) || name.Contains(' ', StringComparison.Ordinal))
            {
                failures.Add(
                    $"{Key}:SensitiveQueryKeys:{i} must be a parameter name of 1 to {MaxSensitiveQueryKeyLength} characters without spaces, '&', '=' or control characters, was '{name}'.");
            }
        }
    }

    /// <summary>Longest accepted sensitive query-parameter name.</summary>
    internal const int MaxSensitiveQueryKeyLength = 64;

    private static void ValidateAcme(PaduraksaOptions options, List<string> failures)
    {
        var key = $"{PaduraksaOptions.SectionName}:Acme";
        var acme = options.Acme;
        if (acme is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        Bound(acme.SchedulerStartDelay, TimeSpan.Zero, TimeSpan.FromHours(1), $"{key}:SchedulerStartDelay", failures);
        Bound(acme.SchedulerPollInterval, TimeSpan.FromSeconds(1), TimeSpan.FromDays(1), $"{key}:SchedulerPollInterval", failures);
        Bound(acme.DnsPropagationDelay, TimeSpan.Zero, TimeSpan.FromHours(1), $"{key}:DnsPropagationDelay", failures);
        ValidateDnsPropagation(acme.DnsPropagation, $"{key}:DnsPropagation", failures);

        if (!string.IsNullOrWhiteSpace(acme.TrustedRootsFile))
        {
            // Cheap checks only; the file is read and parsed once, where it is used (IssuanceStore), which refuses what is not a root.
            try
            {
                FileInfo file = new(acme.TrustedRootsFile);
                if (!file.Exists)
                {
                    failures.Add($"{key}:TrustedRootsFile '{acme.TrustedRootsFile}' does not exist.");
                }
                else if (file.Length > MaxTrustedRootsFileBytes)
                {
                    failures.Add($"{key}:TrustedRootsFile '{acme.TrustedRootsFile}' is {file.Length} bytes; at most {MaxTrustedRootsFileBytes} are accepted.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add($"{key}:TrustedRootsFile '{acme.TrustedRootsFile}' can't be read: {ex.Message}");
            }
        }
    }

    private static void ValidateCloudflare(PaduraksaOptions options, List<string> failures)
    {
        var key = $"{PaduraksaOptions.SectionName}:Cloudflare";
        var cloudflare = options.Cloudflare;
        if (cloudflare?.RangeRefresh is not { } refresh)
        {
            failures.Add($"{key}:RangeRefresh must not be null.");
            return;
        }

        foreach (var (name, url) in new[] { ("ApiUrl", refresh.ApiUrl), ("TextV4Url", refresh.TextV4Url), ("TextV6Url", refresh.TextV6Url) })
        {
            // https always; http only to this machine, which is how a test serves a fake Cloudflare.
            if (url is null || !url.IsAbsoluteUri || !(url.Scheme == Uri.UriSchemeHttps || (url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)))
            {
                failures.Add($"{key}:RangeRefresh:{name} must be an absolute https URL (http only for a loopback host).");
            }
        }

        Bound(refresh.StartDelay, TimeSpan.Zero, TimeSpan.FromHours(1), $"{key}:RangeRefresh:StartDelay", failures);
        Bound(refresh.Interval, TimeSpan.FromMinutes(1), TimeSpan.FromDays(7), $"{key}:RangeRefresh:Interval", failures);

        if (!string.IsNullOrWhiteSpace(cloudflare.OriginPullCaFile))
        {
            // Cheap checks only; the file is parsed once, where it is used.
            try
            {
                FileInfo file = new(cloudflare.OriginPullCaFile);
                if (!file.Exists)
                {
                    failures.Add($"{key}:OriginPullCaFile '{cloudflare.OriginPullCaFile}' does not exist.");
                }
                else if (file.Length > MaxTrustedRootsFileBytes)
                {
                    failures.Add($"{key}:OriginPullCaFile '{cloudflare.OriginPullCaFile}' is {file.Length} bytes; at most {MaxTrustedRootsFileBytes} are accepted.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                failures.Add($"{key}:OriginPullCaFile '{cloudflare.OriginPullCaFile}' can't be read: {ex.Message}");
            }
        }
    }

    private static void ValidateDnsPropagation(DnsPropagationSettings? settings, string key, List<string> failures)
    {
        if (settings is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        Bound(settings.Timeout, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1), $"{key}:Timeout", failures);
        Bound(settings.InitialInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10), $"{key}:InitialInterval", failures);
        Bound(settings.MaxInterval, TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10), $"{key}:MaxInterval", failures);
        Bound(settings.QueryTimeout, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(30), $"{key}:QueryTimeout", failures);
        if (settings.MaxInterval < settings.InitialInterval)
        {
            failures.Add($"{key}:MaxInterval must not be below InitialInterval.");
        }

        if (settings.UnreachableRounds is < 1 or > 20)
        {
            failures.Add($"{key}:UnreachableRounds must be 1 to 20.");
        }

        if (settings.Resolvers is null || settings.Resolvers.Count > 8)
        {
            failures.Add($"{key}:Resolvers must be a list of at most 8 addresses.");
            return;
        }

        for (var i = 0; i < settings.Resolvers.Count; i++)
        {
            if (!IPEndPoint.TryParse(settings.Resolvers[i]?.Trim() ?? string.Empty, out _))
            {
                failures.Add($"{key}:Resolvers:{i} '{settings.Resolvers[i]}' is not an IP address (optionally with a port).");
            }
        }
    }

    /// <summary>The largest trusted-roots file accepted: a bundle of a few roots is some kilobytes.</summary>
    internal const int MaxTrustedRootsFileBytes = 1024 * 1024;

    private static void ValidateLogs(PaduraksaLogOptions? logs, List<string> failures)
    {
        const string Key = PaduraksaOptions.SectionName + ":Logs";
        if (logs is null)
        {
            failures.Add($"{Key} must not be null.");
            return;
        }

        if (logs.Directory is { Length: > 0 } directory)
        {
            if (directory.StartsWith('~'))
            {
                failures.Add($"{Key}:Directory must not start with '~' (no home-directory expansion); use an absolute path or a path under the data directory, was '{directory}'.");
            }
            else if (directory.AsSpan().IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                failures.Add($"{Key}:Directory contains characters that aren't valid in a path.");
            }
        }

        if (logs.ErrorLevel is not (LogLevel.Information or LogLevel.Warning or LogLevel.Error or LogLevel.Critical))
        {
            failures.Add($"{Key}:ErrorLevel must be Information, Warning, Error or Critical, was {logs.ErrorLevel}.");
        }

        if (logs.MaxFileSize is < MinLogFileSize or > MaxLogFileSize)
        {
            failures.Add($"{Key}:MaxFileSize must be between {MinLogFileSize} and {MaxLogFileSize} bytes, was {logs.MaxFileSize}.");
        }

        if (logs.RetainedFiles is < 1 or > 10_000)
        {
            failures.Add($"{Key}:RetainedFiles must be between 1 and 10 000, was {logs.RetainedFiles}.");
        }

        Bound(logs.MaxAge, TimeSpan.FromMinutes(1), TimeSpan.FromDays(3650), $"{Key}:MaxAge", failures);

        if (logs.MaxTotalSize is < 1024 * 1024 or > 1024L * 1024 * 1024 * 1024)
        {
            failures.Add($"{Key}:MaxTotalSize must be between {1024 * 1024} and {1024L * 1024 * 1024 * 1024} bytes, was {logs.MaxTotalSize}.");
        }
    }

    private static void ValidateMetrics(PaduraksaMetricsOptions? metrics, List<string> failures)
    {
        const string Key = PaduraksaOptions.SectionName + ":Metrics";
        if (metrics is null)
        {
            failures.Add($"{Key} must not be null.");
            return;
        }

        Bound(metrics.Retention, TimeSpan.FromHours(1), TimeSpan.FromDays(1825), $"{Key}:Retention", failures);
        Bound(metrics.WriteInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10), $"{Key}:WriteInterval", failures);
    }

    private static void ValidateNotifications(PaduraksaNotificationOptions? notifications, List<string> failures)
    {
        const string Key = PaduraksaOptions.SectionName + ":Notifications";
        if (notifications is null)
        {
            failures.Add($"{Key} must not be null.");
            return;
        }

        if (notifications.AdminUrl is { Length: > 0 } url
            && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
        {
            failures.Add($"{Key}:AdminUrl must be an http or https address without credentials, a query or a fragment, was '{url}'.");
        }
    }

    /// <summary>The smallest rotation size accepted: below one line's worth of bytes, every line would rotate the file again (thrashing).</summary>
    internal const long MinLogFileSize = 1024;

    /// <summary>The largest rotation size accepted (16 GiB): a bigger live file would be a stain on the dashboard's memory, not a log.</summary>
    internal const long MaxLogFileSize = 16L * 1024 * 1024 * 1024;

    private static void Bound(TimeSpan value, TimeSpan min, TimeSpan max, string name, List<string> failures)
    {
        if (value < min || value > max)
        {
            failures.Add($"{name} must be between {min} and {max}, was {value}.");
        }
    }

    private static void ValidateAdmin(AdminOptions? admin, List<string> failures)
    {
        const string Key = PaduraksaOptions.SectionName + ":Admin";
        if (admin is null)
        {
            failures.Add($"{Key} must not be null.");
            return;
        }

        static string Between<T>(T min, T max) => $"between {min} and {max}";

        void Int(string name, int value, int min, int max)
        {
            if (value < min || value > max)
            {
                failures.Add($"{Key}:{name} must be {Between(min, max)}, was {value}.");
            }
        }

        void Span(string name, TimeSpan value, TimeSpan min, TimeSpan max)
        {
            if (value < min || value > max)
            {
                failures.Add($"{Key}:{name} must be {Between(min, max)}, was {value}.");
            }
        }

        Int(nameof(AdminOptions.MaxFailedAttempts), admin.MaxFailedAttempts, 2, 100);
        Span(nameof(AdminOptions.LockoutDuration), admin.LockoutDuration, TimeSpan.FromMinutes(1), TimeSpan.FromHours(24));
        Span(nameof(AdminOptions.SessionLifetime), admin.SessionLifetime, TimeSpan.FromMinutes(5), TimeSpan.FromDays(30));
        Span(nameof(AdminOptions.SessionIdleTimeout), admin.SessionIdleTimeout, TimeSpan.FromMinutes(1), admin.SessionLifetime < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : admin.SessionLifetime);
        Span(nameof(AdminOptions.PartialSessionLifetime), admin.PartialSessionLifetime, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30));
        Int(nameof(AdminOptions.MinPasswordLength), admin.MinPasswordLength, 8, 128);
        Int(nameof(AdminOptions.PasswordIterations), admin.PasswordIterations, 100_000, 10_000_000);
        Span(nameof(AdminOptions.TouchInterval), admin.TouchInterval, TimeSpan.FromSeconds(1), TimeSpan.FromHours(1));
        if (admin.TouchInterval >= admin.SessionIdleTimeout)
        {
            // A last-seen time written less often than the idle limit is long would end live sessions.
            failures.Add($"{Key}:TouchInterval must be shorter than SessionIdleTimeout ({admin.SessionIdleTimeout}), was {admin.TouchInterval}.");
        }
        if (admin.RateLimit is null)
        {
            failures.Add($"{Key}:RateLimit must not be null.");
        }
        else
        {
            Int($"RateLimit:{nameof(AdminRateLimitOptions.PermitsPerIp)}", admin.RateLimit.PermitsPerIp, 1, 10_000);
            Int($"RateLimit:{nameof(AdminRateLimitOptions.PermitsPerUser)}", admin.RateLimit.PermitsPerUser, 1, 1000);
            Span($"RateLimit:{nameof(AdminRateLimitOptions.Window)}", admin.RateLimit.Window, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10));
        }

        if (admin.MaxRequestBodyBytes is < 1024 or > 16 * 1024 * 1024)
        {
            failures.Add($"{Key}:{nameof(AdminOptions.MaxRequestBodyBytes)} must be between 1024 and {16 * 1024 * 1024}, was {admin.MaxRequestBodyBytes}.");
        }

        if (string.IsNullOrWhiteSpace(admin.TotpIssuer) || admin.TotpIssuer.Length > 64 || admin.TotpIssuer.Contains(':', StringComparison.Ordinal) || admin.TotpIssuer.Any(char.IsControl))
        {
            failures.Add($"{Key}:TotpIssuer must be 1 to 64 characters without a colon or control characters.");
        }

        // The admin listener's certificate can only carry names a host name or an address can be; anything else is dropped
        // silently, which would leave the owner with a certificate that doesn't cover the name they typed.
        if (admin.CertificateNames.Count > AdminCertificateNames.MaxConfiguredNames)
        {
            failures.Add($"{Key}:{nameof(AdminOptions.CertificateNames)} lists {admin.CertificateNames.Count} names; at most {AdminCertificateNames.MaxConfiguredNames} are accepted (the process adds up to {AdminCertificateNames.AutomaticNames} of its own, and a certificate carries at most {AdminCertificateNames.MaxNames}).");
        }

        CertificateNameRules rules = new();
        for (var i = 0; i < admin.CertificateNames.Count; i++)
        {
            var name = admin.CertificateNames[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                failures.Add($"{Key}:{nameof(AdminOptions.CertificateNames)} entry {i} is empty.");
            }
            else if (name.Contains('%'))
            {
                // A zone/scope id (fe80::1%eth0) belongs to the interface, not to a certificate name.
                failures.Add($"{Key}:{nameof(AdminOptions.CertificateNames)} entry '{name}' has an IPv6 zone/scope id, which a certificate name can't carry.");
            }
            else if (name.StartsWith("*.", StringComparison.Ordinal))
            {
                failures.Add($"{Key}:{nameof(AdminOptions.CertificateNames)} entry '{name}' is a wildcard; the admin certificate covers exact names only.");
            }
            else if (rules.Normalize(name) is null)
            {
                failures.Add($"{Key}:{nameof(AdminOptions.CertificateNames)} entry '{name}' is not a DNS name or an IP address.");
            }
        }

        Span(nameof(AdminOptions.CertificateStartupTimeout), admin.CertificateStartupTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));

        Span(nameof(AdminOptions.SessionRecheckInterval), admin.SessionRecheckInterval, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5));
    }

    private static void ValidateProxyProtocol(string name, ProxyProtocolListenOptions? options, List<string> failures)
    {
        var key = $"{PaduraksaOptions.SectionName}:Listen:{name}";
        if (options is null)
        {
            failures.Add($"{key} must not be null.");
            return;
        }

        foreach (var source in options.TrustedSources)
        {
            if (!IpNetwork.TryParse(source ?? string.Empty, out _, out var error))
            {
                failures.Add($"{key}:TrustedSources has '{source}', which isn't an IP address or CIDR ({error}).");
            }
        }

        if (options.Enabled && options.TrustedSources.Count == 0)
        {
            failures.Add($"{key}:TrustedSources must list the load balancers' addresses when PROXY protocol is enabled.");
        }

        if (options.HeaderTimeout <= TimeSpan.Zero || options.HeaderTimeout > ProxyProtocolOptions.MaxHeaderTimeout)
        {
            failures.Add($"{key}:HeaderTimeout must be positive and at most {ProxyProtocolOptions.MaxHeaderTimeout}, was {options.HeaderTimeout}.");
        }
    }
}
