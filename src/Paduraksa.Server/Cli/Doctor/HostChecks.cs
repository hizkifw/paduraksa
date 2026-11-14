using System.Globalization;
using System.Text.Json;

namespace Paduraksa.Server.Cli.Doctor;

/// <summary>libmsquic loads, so HTTP/3 can run.</summary>
internal sealed class Http3Check : IDoctorCheck
{
    public string Name => "HTTP/3";

    public bool NeedsOptions => true;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        CheckResult result;
        if (!context.Options!.Listen.Http3)
        {
            result = CheckResult.Info("http3", "HTTP/3 is switched off (Paduraksa__Listen__Http3=false).");
        }
        else if (context.Quic.IsSupported)
        {
            result = CheckResult.Ok("http3", "libmsquic loads: HTTP/3 is available.");
        }
        else
        {
            result = CheckResult.Warning(
                "http3",
                "libmsquic does not load, so HTTP/3 is off: the server serves HTTP/1.1 and HTTP/2 only and never sends Alt-Svc.",
                "Install libmsquic (the official image has it; on Debian or Ubuntu it comes from Microsoft's package feed), or set Paduraksa__Listen__Http3=false to say it is meant.");
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>([result]);
    }
}

/// <summary>The system clock agrees with a reference's HTTP <c>Date</c>. Optional: with no network it is skipped, never failed.</summary>
internal sealed class ClockCheck : IDoctorCheck
{
    /// <summary>The most a clock may differ before a TOTP code can be refused (a code is good for 30 seconds).</summary>
    internal static readonly TimeSpan WarnSkew = TimeSpan.FromSeconds(30);

    /// <summary>The most before certificate validity and ACME break.</summary>
    internal static readonly TimeSpan FailSkew = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public string Name => "clock";

    public bool NeedsOptions => false;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        if (!context.CheckClock)
        {
            return [CheckResult.Info("clock", "Not checked: it asks a public server for the time, so it runs only with --check-clock. ACME and sign-in codes depend on the clock being right.")];
        }

        var sent = context.Time.GetUtcNow();
        var answer = await context.Http.GetAsync(context.ClockUrl, trustAnyCertificate: false, bearerToken: null, Timeout, cancellationToken).ConfigureAwait(false);
        var received = context.Time.GetUtcNow();
        if (!answer.Answered)
        {
            return [CheckResult.Info("clock", $"Not checked: {context.ClockUrl.Host} could not be reached ({answer.Error}). That is normal without a network.")];
        }

        if (answer.Date is not { } reference)
        {
            return [CheckResult.Info("clock", $"Not checked: {context.ClockUrl.Host} sent no Date header.")];
        }

        // The reference's second was read somewhere inside the request: its midpoint is the best guess of when, and Date has whole-second resolution.
        var local = sent + ((received - sent) / 2);
        var skew = local - reference;
        var size = Format(skew.Duration());
        var side = skew > TimeSpan.Zero ? "ahead of" : "behind";
        var fix = "Turn on time synchronisation on the host (timedatectl set-ntp true, or chrony / systemd-timesyncd); a container uses its host's clock.";
        if (skew.Duration() > FailSkew)
        {
            return [CheckResult.Failure("clock", $"The system clock is {size} {side} {context.ClockUrl.Host}: ACME orders and certificate validity checks fail, and sign-in codes are refused.", fix)];
        }

        return skew.Duration() > WarnSkew
            ? [CheckResult.Warning("clock", $"The system clock is {size} {side} {context.ClockUrl.Host}: authenticator codes can be refused.", fix)]
            : [CheckResult.Ok("clock", $"The system clock is within {Format(WarnSkew)} of {context.ClockUrl.Host}.")];
    }

    private static string Format(TimeSpan span) =>
        span.TotalMinutes >= 1
            ? $"{((int)span.TotalMinutes).ToString(CultureInfo.InvariantCulture)} min {span.Seconds.ToString(CultureInfo.InvariantCulture)} s"
            : $"{((int)span.TotalSeconds).ToString(CultureInfo.InvariantCulture)} s";
}

/// <summary>A server answering on the admin loopback: its version, against this binary's.</summary>
internal sealed class RunningServerCheck : IDoctorCheck
{
    public string Name => "running server";

    public bool NeedsOptions => true;

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DoctorContext context, CancellationToken cancellationToken)
    {
        var admin = await context.RunningAdminAsync(cancellationToken).ConfigureAwait(false);
        if (!admin.IsPaduraksa)
        {
            return [CheckResult.Info("server", "No Paduraksa answers on the admin listener over loopback: it is not running, or its admin is bound to another address.")];
        }

        List<CheckResult> results = [];
        results.Add(admin.Status == 200
            ? CheckResult.Ok("server", $"A server is running ({admin.Uri} answers).")
            : CheckResult.Warning("server", $"A server answers at {admin.Uri} but reports itself unavailable ({admin.Status}).", "It has no config loaded yet (still starting), or failed to: read its log."));
        results.Add(await VersionAsync(context, admin, cancellationToken).ConfigureAwait(false));
        return results;
    }

    private static async Task<CheckResult> VersionAsync(DoctorContext context, AdminAnswer admin, CancellationToken cancellationToken)
    {
        if (context.ApiToken is null)
        {
            return CheckResult.Info("server-version", $"The running server's version is not compared: it needs an API token (--token-file, or PADURAKSA_TOKEN). This binary is {context.Version}.");
        }

        if (!admin.Uri!.IsLoopback)
        {
            // The token goes only where this process's own certificate shortcut is safe: the admin is bound to a specific address, not loopback.
            return CheckResult.Info("server-version", $"The running server's version is not compared: its admin listens on {admin.Uri.Host}, and an API token is only sent over loopback. This binary is {context.Version}.");
        }

        var uri = new UriBuilder(admin.Uri!.Scheme, admin.Uri.Host, admin.Uri.Port, "/api/v1/version").Uri;
        var answer = await context.Http.GetAsync(uri, trustAnyCertificate: true, context.ApiToken, DoctorCommands.Timeout, cancellationToken).ConfigureAwait(false);
        if (answer.Status != 200 || answer.Body is null)
        {
            return CheckResult.Warning(
                "server-version",
                answer.Answered ? $"The running server refused the version request ({answer.Status})." : $"The running server did not answer the version request: {answer.Error}.",
                "Give a valid API token (any scope) with --token-file or PADURAKSA_TOKEN.");
        }

        string? version;
        string? commit = null;
        try
        {
            using var document = JsonDocument.Parse(answer.Body);
            version = document.RootElement.GetProperty("version").GetString();
            commit = document.RootElement.TryGetProperty("commit", out var element) ? element.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return CheckResult.Warning("server-version", "The running server's version answer could not be read.", "Is --url or PADURAKSA_URL's admin address a Paduraksa?");
        }

        var running = commit is null ? version : $"{version} ({commit})";
        var own = context.Commit is null ? context.Version : $"{context.Version} ({context.Commit})";
        var same = string.Equals(version, context.Version, StringComparison.Ordinal)
            && (commit is null || context.Commit is null || string.Equals(commit, context.Commit, StringComparison.Ordinal));
        return same
            ? CheckResult.Ok("server-version", $"The running server is {running}, the same as this binary.")
            : CheckResult.Warning(
                "server-version",
                $"The running server is {running} but this binary is {own}.",
                "After an upgrade, restart the server so it runs the new version (docker compose up -d).");
    }
}
