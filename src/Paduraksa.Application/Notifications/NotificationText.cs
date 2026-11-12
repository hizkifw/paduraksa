using System.Globalization;
using Paduraksa.Core.Model;
using Paduraksa.Notifications;
using Paduraksa.Proxy.Yarp;

namespace Paduraksa.Application.Notifications;

/// <summary>
/// The words of every event: short, plain and specific (what happened to which object, since when, what to do). Names are inserted as they
/// are; each sender escapes them for its own medium.
/// </summary>
internal static class NotificationText
{
    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Described(DestinationReading destination, bool down) =>
        HealthTracker.ReasonOf(destination, down) is { } reason ? $"{destination.Address} ({reason})" : destination.Address;

    public static NotificationEvent Health(HealthChange change, string upstreamName, TimeSpan flapWindow, TimeSpan flapSettle)
    {
        ArgumentNullException.ThrowIfNull(change);
        var minutes = Number((int)flapWindow.TotalMinutes);
        var settle = Number((int)flapSettle.TotalMinutes);
        var scope = change.Scope == HealthScope.Upstream ? $"Upstream {upstreamName}" : $"{change.Address} of upstream {upstreamName}";
        var total = change.Readings.Count;
        var working = change.Readings.Count(d => d.Current != DestinationVerdict.Unhealthy);
        var down = change.Readings.Where(d => d.Current == DestinationVerdict.Unhealthy).Select(d => Described(d, down: true)).ToList();
        var state = change.Down ? "down" : "up";

        NotificationEventKind kind;
        string title;
        string body;
        NotificationSeverity severity;
        if (change.Flapping)
        {
            kind = change.Down ? Down(change) : Up(change);
            title = $"{scope} keeps changing state";
            body = $"It has changed state several times in {minutes} minutes and is {state} now. It is not reported again until it has been stable for {settle} minutes. Check the service and the network between Paduraksa and it.";
            severity = NotificationSeverity.Warning;
        }
        else if (change.Settled)
        {
            kind = change.Down ? Down(change) : Up(change);
            title = $"{scope} has settled: {state}";
            body = $"It has been stable for {settle} minutes and is {state}." + (change.Down ? " Check the service." : string.Empty);
            severity = change.Down ? NotificationSeverity.Critical : NotificationSeverity.Good;
        }
        else if (change.Scope == HealthScope.Upstream && change.Down)
        {
            kind = NotificationEventKind.UpstreamDown;
            title = $"Upstream {upstreamName} is down";
            body = $"No destination takes traffic since {Time(change.Since)}: {string.Join(", ", down)}. Check the services behind it; it recovers by itself once a check passes.";
            severity = NotificationSeverity.Critical;
        }
        else if (change.Scope == HealthScope.Upstream)
        {
            kind = NotificationEventKind.UpstreamUp;
            title = $"Upstream {upstreamName} is back up";
            body = $"{Number(working)} of {Number(total)} destinations take traffic again since {Time(change.Since)}."
                + (down.Count > 0 ? $" Still out: {string.Join(", ", down)}." : string.Empty);
            severity = NotificationSeverity.Good;
        }
        else if (change.Down)
        {
            kind = NotificationEventKind.DestinationDown;
            title = $"{change.Address} of upstream {upstreamName} is down";
            body = $"{change.Reason ?? "Its health check failed"} since {Time(change.Since)}. {Number(working)} of {Number(total)} destinations still take traffic. Check the service on that address.";
            severity = NotificationSeverity.Warning;
        }
        else
        {
            kind = NotificationEventKind.DestinationUp;
            title = $"{change.Address} of upstream {upstreamName} is back up";
            body = $"{change.Reason ?? "Its health check passes"} since {Time(change.Since)}. {Number(working)} of {Number(total)} destinations take traffic.";
            severity = NotificationSeverity.Good;
        }

        List<KeyValuePair<string, string>> data = [new("upstreamId", change.Upstream.ToString()), new("state", state)];
        if (change.Destination is { } destination)
        {
            data.Add(new("destinationId", destination.ToString()));
            data.Add(new("address", change.Address ?? string.Empty));
        }

        if (change.Reason is not null)
        {
            data.Add(new("reason", change.Reason));
        }

        return new NotificationEvent(kind, title, body, severity, change.Since, "/upstreams", data, (change.Destination is { } subject ? subject.ToString() : change.Upstream.ToString()));
    }

    private static NotificationEventKind Down(HealthChange change) =>
        change.Scope == HealthScope.Upstream ? NotificationEventKind.UpstreamDown : NotificationEventKind.DestinationDown;

    private static NotificationEventKind Up(HealthChange change) =>
        change.Scope == HealthScope.Upstream ? NotificationEventKind.UpstreamUp : NotificationEventKind.DestinationUp;

    public static NotificationEvent CertificateExpiring(string name, CertificateId id, CertificateSource source, DateTimeOffset notAfter, DateTimeOffset now)
    {
        var remaining = notAfter - now;
        var days = (int)Math.Ceiling(remaining.TotalDays);
        var when = days <= 0 ? "has expired" : days == 1 ? "expires within a day" : $"expires in {Number(days)} days";
        var action = source switch
        {
            CertificateSource.Acme => "It is renewed automatically; if that is not happening, check the order on the Certificates page.",
            CertificateSource.Uploaded => "Upload a new certificate before then.",
            CertificateSource.SelfSigned => "Create a new one before then.",
            CertificateSource.LocalCaRoot => "Clients that trust this root need the new one; create it well before the date.",
            _ => "It is renewed automatically; if that is not happening, check the Certificates page.",
        };
        return new NotificationEvent(
            NotificationEventKind.CertificateExpiring,
            $"Certificate {name} {when}",
            $"Valid until {Date(notAfter)} {Time(notAfter)} ({SourceName(source)}). {action}",
            days <= 1 ? NotificationSeverity.Critical : days <= 7 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            now,
            "/certificates",
            [new("certificateId", id.ToString()), new("notAfter", notAfter.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)), new("days", Number(Math.Max(days, 0)))],
            id.ToString());
    }

    private static string SourceName(CertificateSource source) => source switch
    {
        CertificateSource.Acme => "ACME",
        CertificateSource.Uploaded => "uploaded",
        CertificateSource.SelfSigned => "self-signed",
        CertificateSource.LocalCa => "issued by the local CA",
        CertificateSource.LocalCaRoot => "the local CA's root",
        _ => "certificate",
    };

    public static NotificationEvent AcmeFailed(string name, CertificateId id, int attempts, string error, DateTimeOffset? retryAt, DateTimeOffset? notAfter, DateTimeOffset now) => new(
        NotificationEventKind.AcmeRenewalFailed,
        $"Issuing {name} failed",
        $"The CA said: {error}. "
        + (retryAt is { } retry ? $"Paduraksa retries at {Time(retry)}. " : string.Empty)
        + (notAfter is { } until ? $"The current certificate is valid until {Date(until)}. " : string.Empty)
        + "Check the order on the Certificates page.",
        NotificationSeverity.Warning,
        now,
        "/certificates",
        [new("certificateId", id.ToString()), new("attempts", Number(attempts))],
        id.ToString());

    public static NotificationEvent AcmeRecovered(string name, CertificateId id, DateTimeOffset notAfter, DateTimeOffset now) => new(
        NotificationEventKind.AcmeRenewalRecovered,
        $"{name} was issued after a failure",
        $"The new certificate is valid until {Date(notAfter)}.",
        NotificationSeverity.Good,
        now,
        "/certificates",
        [new("certificateId", id.ToString())],
        id.ToString());

    public static NotificationEvent SignInLockout(string username, UserId? user, DateTimeOffset until, DateTimeOffset now) => new(
        NotificationEventKind.SignInLockout,
        $"Account {username} is locked",
        $"Too many failed sign-ins; sign-in is refused until {Time(until)}. If that was not them, change the password once the lock ends and review the sessions.",
        NotificationSeverity.Warning,
        now,
        "/users",
        user is { } id ? [new("userId", id.ToString())] : null,
        user?.ToString());

    public static NotificationEvent SignInNewAddress(string username, UserId user, string address, DateTimeOffset now) => new(
        NotificationEventKind.SignInNewAddress,
        $"{username} signed in from a new address",
        $"{address} has not signed in as this account in the last 30 days. If it was not them, end the session on the Users page and change the password.",
        NotificationSeverity.Info,
        now,
        "/users",
        [new("userId", user.ToString()), new("address", address)],
        user.ToString());

    public static NotificationEvent Test(string label, DateTimeOffset now) => new(
        NotificationEventKind.Test,
        "Test from Paduraksa",
        $"If you can read this, the channel \"{label}\" works.",
        NotificationSeverity.Info,
        now,
        "/settings/notifications");

    /// <summary>
    /// Several events of one kind in one message: a storm (ten upstreams down at once) is one notification per channel, not ten.
    /// </summary>
    public static NotificationEvent Digest(IReadOnlyList<NotificationEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var first = events[0];
        const int Listed = 10;
        List<string> lines = [.. events.Take(Listed).Select(e => "- " + e.Title)];
        if (events.Count > Listed)
        {
            lines.Add($"- and {Number(events.Count - Listed)} more");
        }

        var worst = events.Max(e => e.Severity);
        return first with
        {
            Title = $"{Number(events.Count)} notifications: {Kind(first.Kind)}",
            Body = string.Join('\n', lines),
            Severity = worst,
            At = events.Min(e => e.At),
            Data = [new("count", Number(events.Count))],
        };
    }

    private static string Kind(NotificationEventKind kind) => kind switch
    {
        NotificationEventKind.UpstreamDown => "upstreams down",
        NotificationEventKind.UpstreamUp => "upstreams back up",
        NotificationEventKind.DestinationDown => "destinations down",
        NotificationEventKind.DestinationUp => "destinations back up",
        NotificationEventKind.CertificateExpiring => "certificates expiring",
        NotificationEventKind.AcmeRenewalFailed => "issuance failures",
        NotificationEventKind.AcmeRenewalRecovered => "issuances recovered",
        NotificationEventKind.SignInLockout => "sign-in lockouts",
        NotificationEventKind.SignInNewAddress => "new sign-in addresses",
        _ => "events",
    };
}
